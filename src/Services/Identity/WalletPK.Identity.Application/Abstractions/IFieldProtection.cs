namespace WalletPK.Identity.Application.Abstractions;

/// <summary>
/// Encrypts sensitive fields (CNIC now, IBANs later) before they are stored: AES-256-GCM in production.
/// </summary>
public interface IFieldEncryptor
{
    /// <param name="plaintext">The value to protect.</param>
    /// <param name="associatedData">
    /// Context the ciphertext is bound to, for example the owner's user ID. Decryption with any other
    /// context fails, so a ciphertext copied into another user's row is useless.
    /// </param>
    string Encrypt(string plaintext, string associatedData);

    /// <exception cref="System.Security.Cryptography.CryptographicException">The value was tampered with or the context is wrong.</exception>
    string Decrypt(string ciphertext, string associatedData);
}

/// <summary>
/// A keyed hash (HMAC-SHA256) of a sensitive value, used to find equal values (for example a CNIC
/// already linked to another account) without storing or decrypting the value itself.
/// </summary>
public interface IBlindIndex
{
    string Compute(string value);
}
