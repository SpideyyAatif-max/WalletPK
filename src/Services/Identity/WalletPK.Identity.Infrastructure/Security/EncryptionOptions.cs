namespace WalletPK.Identity.Infrastructure.Security;

/// <summary>
/// Field-encryption keys, from the "Encryption" configuration section. In production they come from a
/// secret store, never from appsettings.json.
/// <para>
/// Key rotation: add a new key under <see cref="Keys"/> and point <see cref="ActiveKeyId"/> at it.
/// New values use the new key; old values still decrypt because each ciphertext names its key.
/// </para>
/// </summary>
public sealed class EncryptionOptions
{
    public const string SectionName = "Encryption";

    /// <summary>Key ID → Base64 of a random 32-byte (256-bit) AES key.</summary>
    public Dictionary<string, string> Keys { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The key used to encrypt new values.</summary>
    public string ActiveKeyId { get; set; } = string.Empty;

    /// <summary>Base64 of a random 32-byte HMAC key for blind indexes. Changing it means re-indexing every row.</summary>
    public string BlindIndexKey { get; set; } = string.Empty;

    /// <summary>True when every key is valid Base64 of 32 bytes, key IDs are simple, and the active key exists.</summary>
    public bool HasValidKeys() =>
        Keys.Count > 0 &&
        Keys.ContainsKey(ActiveKeyId) &&
        Keys.All(pair =>
            pair.Key.Length is > 0 and <= 16 &&
            pair.Key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') &&
            DecodedLength(pair.Value) == 32);

    public bool HasValidBlindIndexKey() => DecodedLength(BlindIndexKey) >= 32;

    private static int DecodedLength(string base64)
    {
        try
        {
            return string.IsNullOrWhiteSpace(base64) ? 0 : Convert.FromBase64String(base64).Length;
        }
        catch (FormatException)
        {
            return 0;
        }
    }
}
