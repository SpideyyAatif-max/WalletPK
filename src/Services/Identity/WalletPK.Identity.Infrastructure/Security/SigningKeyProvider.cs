using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace WalletPK.Identity.Infrastructure.Security;

/// <summary>
/// Holds the RSA key that signs access tokens (RS256). Only the Identity service has the private key;
/// everyone else (the gateway, other services) verifies tokens with the public key published at
/// <c>/.well-known/jwks.json</c>. The key ID ("kid") is the key's RFC 7638 thumbprint, so a token's
/// header names exactly which key signed it, and keys can be rotated without downtime.
/// </summary>
public sealed partial class SigningKeyProvider : IDisposable
{
    private const int KeySizeBits = 2048;

    private readonly RSA _rsa;

    public SigningKeyProvider(JwtSigningOptions options, IHostEnvironment environment, ILogger<SigningKeyProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);

        _rsa = LoadOrCreate(options, environment.ContentRootPath, logger);

        var publicParameters = _rsa.ExportParameters(includePrivateParameters: false);
        var modulus = Base64Url.EncodeToString(publicParameters.Modulus);
        var exponent = Base64Url.EncodeToString(publicParameters.Exponent);
        KeyId = ComputeThumbprint(modulus, exponent);

        SigningCredentials = new SigningCredentials(new RsaSecurityKey(_rsa) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256);
        ValidationKeys = [new RsaSecurityKey(publicParameters) { KeyId = KeyId }];

        // Built by hand from the public parameters only, so the private key can never leak into the JWKS.
        JsonWebKeySet = new Dictionary<string, object>
        {
            ["keys"] = new[]
            {
                new Dictionary<string, string>
                {
                    ["kty"] = "RSA",
                    ["use"] = "sig",
                    ["alg"] = SecurityAlgorithms.RsaSha256,
                    ["kid"] = KeyId,
                    ["n"] = modulus,
                    ["e"] = exponent,
                },
            },
        };
    }

    public string KeyId { get; }

    public SigningCredentials SigningCredentials { get; }

    /// <summary>Public keys accepted when validating tokens (more than one during a key rotation).</summary>
    public IReadOnlyList<SecurityKey> ValidationKeys { get; }

    /// <summary>The public keys as a JSON Web Key Set document.</summary>
    public IReadOnlyDictionary<string, object> JsonWebKeySet { get; }

    public void Dispose() => _rsa.Dispose();

    /// <summary>RFC 7638: SHA-256 over the required members in alphabetical order, without spaces.</summary>
    internal static string ComputeThumbprint(string modulus, string exponent)
    {
        var canonical = $$"""{"e":"{{exponent}}","kty":"RSA","n":"{{modulus}}"}""";
        return Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static RSA LoadOrCreate(JwtSigningOptions options, string contentRoot, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(options.SigningKeyPath))
        {
            if (!options.CreateSigningKeyIfMissing)
            {
                throw new InvalidOperationException("Jwt:SigningKeyPath is not configured.");
            }

            LogTemporaryKey(logger);
            return RSA.Create(KeySizeBits);
        }

        var path = Path.GetFullPath(options.SigningKeyPath, contentRoot);
        if (File.Exists(path))
        {
            var rsa = RSA.Create();
            rsa.ImportFromPem(File.ReadAllText(path));
            LogLoadedKey(logger, path);
            return rsa;
        }

        if (!options.CreateSigningKeyIfMissing)
        {
            throw new InvalidOperationException($"The token signing key file '{path}' does not exist.");
        }

        var created = RSA.Create(KeySizeBits);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, created.ExportPkcs8PrivateKeyPem());
        LogCreatedKey(logger, path);
        return created;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Using a temporary token signing key; tokens will stop working when the service restarts")]
    private static partial void LogTemporaryKey(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Loaded the token signing key from {Path}")]
    private static partial void LogLoadedKey(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Created a new development token signing key at {Path}")]
    private static partial void LogCreatedKey(ILogger logger, string path);
}
