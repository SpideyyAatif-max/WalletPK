using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using WalletPK.Identity.Application.Abstractions;

namespace WalletPK.Identity.Infrastructure.Security;

/// <summary>
/// AES-256-GCM field encryption. GCM is authenticated: any change to the stored value, or decrypting
/// it for a different owner (associated data), fails instead of returning garbage.
/// <para>Stored format: <c>v1.&lt;keyId&gt;.&lt;nonce&gt;.&lt;ciphertext+tag&gt;</c> (Base64url parts).
/// A fresh random 96-bit nonce per value means equal CNICs never produce equal ciphertexts.</para>
/// </summary>
internal sealed class AesGcmFieldEncryptor : IFieldEncryptor
{
    private const string FormatVersion = "v1";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly Dictionary<string, byte[]> _keys;
    private readonly string _activeKeyId;

    public AesGcmFieldEncryptor(EncryptionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.HasValidKeys())
        {
            throw new InvalidOperationException("Encryption:Keys must hold 32-byte Base64 keys and include Encryption:ActiveKeyId.");
        }

        _keys = options.Keys.ToDictionary(pair => pair.Key, pair => Convert.FromBase64String(pair.Value), StringComparer.Ordinal);
        _activeKeyId = options.ActiveKeyId;
    }

    public string Encrypt(string plaintext, string associatedData)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(associatedData);

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var sealedData = new byte[plain.Length + TagSize];

        using (var aes = new AesGcm(_keys[_activeKeyId], TagSize))
        {
            aes.Encrypt(nonce, plain, sealedData.AsSpan(0, plain.Length), sealedData.AsSpan(plain.Length), Encoding.UTF8.GetBytes(associatedData));
        }

        return string.Join('.', FormatVersion, _activeKeyId, Base64Url.EncodeToString(nonce), Base64Url.EncodeToString(sealedData));
    }

    public string Decrypt(string ciphertext, string associatedData)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(associatedData);

        var parts = ciphertext.Split('.');
        if (parts.Length != 4 || parts[0] != FormatVersion || !_keys.TryGetValue(parts[1], out var key))
        {
            throw new CryptographicException("Unknown ciphertext format or encryption key.");
        }

        byte[] nonce;
        byte[] sealedData;
        try
        {
            nonce = Base64Url.DecodeFromChars(parts[2]);
            sealedData = Base64Url.DecodeFromChars(parts[3]);
        }
        catch (FormatException exception)
        {
            throw new CryptographicException("The ciphertext is corrupt.", exception);
        }

        if (nonce.Length != NonceSize || sealedData.Length < TagSize)
        {
            throw new CryptographicException("The ciphertext is corrupt.");
        }

        var plain = new byte[sealedData.Length - TagSize];
        using (var aes = new AesGcm(key, TagSize))
        {
            // Throws AuthenticationTagMismatchException (a CryptographicException) on any tampering.
            aes.Decrypt(nonce, sealedData.AsSpan(0, plain.Length), sealedData.AsSpan(plain.Length), plain, Encoding.UTF8.GetBytes(associatedData));
        }

        return Encoding.UTF8.GetString(plain);
    }
}

/// <summary>HMAC-SHA256 blind index with a dedicated key (separate from the encryption keys).</summary>
internal sealed class HmacBlindIndex : IBlindIndex
{
    private readonly byte[] _key;

    public HmacBlindIndex(EncryptionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.HasValidBlindIndexKey())
        {
            throw new InvalidOperationException("Encryption:BlindIndexKey must be Base64 of at least 32 bytes.");
        }

        _key = Convert.FromBase64String(options.BlindIndexKey);
    }

    public string Compute(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(value)));
    }
}
