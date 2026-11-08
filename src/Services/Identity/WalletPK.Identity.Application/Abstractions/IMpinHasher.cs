namespace WalletPK.Identity.Application.Abstractions;

/// <summary>One-way hashing for MPINs (Argon2id with a server-side pepper in production).</summary>
public interface IMpinHasher
{
    string Hash(string mpin);

    bool Verify(string mpin, string hash);
}
