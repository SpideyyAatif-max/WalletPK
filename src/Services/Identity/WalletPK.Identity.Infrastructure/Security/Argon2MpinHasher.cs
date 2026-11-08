using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using WalletPK.Identity.Application.Abstractions;

namespace WalletPK.Identity.Infrastructure.Security;

/// <summary>
/// Argon2id (memory-hard, the current OWASP recommendation) over HMAC-SHA256(pepper, MPIN), with a
/// random 16-byte salt per user. The stored string is self-describing (PHC format), so cost
/// parameters can be raised later without breaking existing hashes:
/// <c>$argon2id$v=19$m=19456,t=2,p=1$&lt;salt&gt;$&lt;hash&gt;</c>
/// </summary>
internal sealed class Argon2MpinHasher(MpinHashingOptions options) : IMpinHasher
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public string Hash(string mpin)
    {
        ArgumentException.ThrowIfNullOrEmpty(mpin);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Compute(mpin, salt, options.MemoryKiB, options.Iterations, options.Parallelism);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"$argon2id$v=19$m={options.MemoryKiB},t={options.Iterations},p={options.Parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    public bool Verify(string mpin, string hash)
    {
        if (string.IsNullOrEmpty(mpin) || string.IsNullOrEmpty(hash))
        {
            return false;
        }

        // ["", "argon2id", "v=19", "m=..,t=..,p=..", salt, hash]
        var parts = hash.Split('$');
        if (parts.Length != 6 || parts[1] != "argon2id" || !TryParseCost(parts[3], out var memory, out var iterations, out var parallelism))
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[4]);
            var expected = Convert.FromBase64String(parts[5]);
            var actual = Compute(mpin, salt, memory, iterations, parallelism);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            // Corrupt or tampered hash (bad Base64, salt too short, ...): never a match.
            return false;
        }
    }

    private byte[] Compute(string mpin, byte[] salt, int memoryKiB, int iterations, int parallelism)
    {
        var peppered = HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.Pepper), Encoding.UTF8.GetBytes(mpin));

        using var argon2 = new Argon2id(peppered)
        {
            Salt = salt,
            MemorySize = memoryKiB,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };

        return argon2.GetBytes(HashBytes);
    }

    private static bool TryParseCost(string text, out int memory, out int iterations, out int parallelism)
    {
        memory = iterations = parallelism = 0;
        foreach (var pair in text.Split(','))
        {
            var keyValue = pair.Split('=');
            if (keyValue.Length != 2 || !int.TryParse(keyValue[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return false;
            }

            switch (keyValue[0])
            {
                case "m": memory = value; break;
                case "t": iterations = value; break;
                case "p": parallelism = value; break;
                default: return false;
            }
        }

        return memory > 0 && iterations > 0 && parallelism > 0;
    }
}
