namespace WalletPK.Identity.Infrastructure.Security;

/// <summary>MPIN hashing settings, from the "MpinHashing" configuration section.</summary>
public sealed class MpinHashingOptions
{
    public const string SectionName = "MpinHashing";

    /// <summary>
    /// Secret mixed in before hashing. A 5-digit MPIN has only 100,000 possibilities, so without a
    /// pepper (stored outside the database) a stolen database could be brute-forced in minutes.
    /// At least 32 characters; never commit a real one.
    /// </summary>
    public string Pepper { get; set; } = string.Empty;

    /// <summary>Argon2id memory in KiB (OWASP minimum recommendation: 19456).</summary>
    public int MemoryKiB { get; set; } = 19_456;

    public int Iterations { get; set; } = 2;

    public int Parallelism { get; set; } = 1;
}
