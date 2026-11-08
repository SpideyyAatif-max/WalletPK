using System.Buffers.Text;
using System.Globalization;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using WalletPK.Common.Identifiers;
using WalletPK.Identity.Application.Abstractions;
using WalletPK.Identity.Application.Authentication;
using WalletPK.Identity.Domain.Devices;
using WalletPK.Identity.Domain.Users;
using WalletPK.Identity.Infrastructure.Security;

namespace WalletPK.Identity.UnitTests.Infrastructure;

public class JwtTokenTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);

    private static SigningKeyProvider TemporaryKey() =>
        new(new JwtSigningOptions { CreateSigningKeyIfMissing = true }, new TestHostEnvironment(Path.GetTempPath()), NullLogger<SigningKeyProvider>.Instance);

    private static TokenValidationParameters ValidationFor(SigningKeyProvider keys) => new()
    {
        ValidIssuer = "walletpk-identity",
        ValidAudience = "walletpk-api",
        IssuerSigningKeys = keys.ValidationKeys,
        ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        ValidateLifetime = false, // fixed test clock; expiry is checked separately below
    };

    [Fact]
    public async Task Access_tokens_are_rs256_signed_and_carry_the_walletpk_claims()
    {
        using var keys = TemporaryKey();
        var issuer = new JwtAccessTokenIssuer(keys, new JwtSigningOptions(), new AuthOptions());
        var userId = UserId.New();
        var deviceId = DeviceId.New();

        var issued = issuer.Issue(new AccessTokenClaims(userId, deviceId, KycTier.L1, UserRole.Customer, null), _now);
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(issued.Token, ValidationFor(keys));

        result.IsValid.ShouldBeTrue(result.Exception?.Message);
        var token = (JsonWebToken)result.SecurityToken;
        token.Alg.ShouldBe("RS256");
        token.Kid.ShouldBe(keys.KeyId);
        token.Subject.ShouldBe(userId.ToString());
        token.GetClaim("device_id").Value.ShouldBe(deviceId.ToString());
        token.GetClaim("kyc_tier").Value.ShouldBe("L1");
        token.GetClaim("role").Value.ShouldBe("customer");
        token.Id.ShouldNotBeNullOrEmpty();
        token.TryGetClaim("cooling_off_until", out _).ShouldBeFalse();
        token.ValidTo.ShouldBe(_now.AddMinutes(15).UtcDateTime);
        issued.ExpiresAt.ShouldBe(_now.AddMinutes(15));
    }

    [Fact]
    public void A_device_in_cooling_off_gets_the_end_time_as_a_claim()
    {
        using var keys = TemporaryKey();
        var issuer = new JwtAccessTokenIssuer(keys, new JwtSigningOptions(), new AuthOptions());
        var until = _now.AddHours(24);

        var issued = issuer.Issue(new AccessTokenClaims(UserId.New(), DeviceId.New(), KycTier.L0, UserRole.Customer, until), _now);

        new JsonWebToken(issued.Token).GetClaim("cooling_off_until").Value.ShouldBe(until.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task A_token_signed_by_another_key_is_rejected()
    {
        using var keys = TemporaryKey();
        using var otherKeys = TemporaryKey();
        var issued = new JwtAccessTokenIssuer(otherKeys, new JwtSigningOptions(), new AuthOptions())
            .Issue(new AccessTokenClaims(UserId.New(), DeviceId.New(), KycTier.L0, UserRole.Customer, null), _now);

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(issued.Token, ValidationFor(keys));

        result.IsValid.ShouldBeFalse();
    }

    [Fact]
    public void The_jwks_publishes_only_the_public_key_with_a_thumbprint_kid()
    {
        using var keys = TemporaryKey();

        var set = keys.JsonWebKeySet["keys"].ShouldBeOfType<Dictionary<string, string>[]>();
        var jwk = set.ShouldHaveSingleItem();

        jwk["kty"].ShouldBe("RSA");
        jwk["alg"].ShouldBe("RS256");
        jwk["use"].ShouldBe("sig");
        jwk["kid"].ShouldBe(keys.KeyId);
        jwk.Keys.ShouldBe(new[] { "kty", "use", "alg", "kid", "n", "e" }, ignoreOrder: true); // no "d", "p", "q": nothing private
        SigningKeyProvider.ComputeThumbprint(jwk["n"], jwk["e"]).ShouldBe(keys.KeyId);
        Base64Url.DecodeFromChars(jwk["n"]).Length.ShouldBe(256); // 2048-bit modulus
    }

    [Fact]
    public void Matches_the_rfc_7638_example_thumbprint()
    {
        // The example key and expected thumbprint from RFC 7638, section 3.1.
        const string n = "0vx7agoebGcQSuuPiLJXZptN9nndrQmbXEps2aiAFbWhM78LhWx4cbbfAAtVT86zwu1RK7aPFFxuhDR1L6tSoc_BJECPebWKRXjBZCiFV4n3oknjhMstn64tZ_2W-5JsGY4Hc5n9yBXArwl93lqt7_RN5w6Cf0h4QyQ5v-65YGjQR0_FDW2QvzqY368QQMicAtaSqzs8KJZgnYb9c7d0zgdAZHzu6qMQvRL5hajrn1n91CbOpbISD08qNLyrdkt-bFTWhAI4vMQFh6WeZu0fM4lFd2NcRwr3XPksINHaQ-G_xBniIqbw0Ls1jF44-csFCur-kEgU8awapJzKnqDKgw";
        const string e = "AQAB";

        SigningKeyProvider.ComputeThumbprint(n, e).ShouldBe("NzbLsXh8uDCcd-6MNwXF4W_7noWXFZAfHkxZsRGC9Xs");
    }

    [Fact]
    public void A_key_file_is_created_once_and_then_reused_so_tokens_survive_restarts()
    {
        var folder = Path.Combine(Path.GetTempPath(), "walletpk-tests", Guid.NewGuid().ToString("N"));
        var options = new JwtSigningOptions { SigningKeyPath = "keys/signing.pem", CreateSigningKeyIfMissing = true };
        var environment = new TestHostEnvironment(folder);

        try
        {
            using var first = new SigningKeyProvider(options, environment, NullLogger<SigningKeyProvider>.Instance);
            using var second = new SigningKeyProvider(options, environment, NullLogger<SigningKeyProvider>.Instance);

            File.Exists(Path.Combine(folder, "keys", "signing.pem")).ShouldBeTrue();
            File.ReadAllText(Path.Combine(folder, "keys", "signing.pem")).ShouldStartWith("-----BEGIN PRIVATE KEY-----");
            second.KeyId.ShouldBe(first.KeyId);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_missing_key_file_stops_the_service_unless_creation_is_allowed()
    {
        var options = new JwtSigningOptions { SigningKeyPath = "/does/not/exist/" + Guid.NewGuid().ToString("N") + ".pem" };

        var error = Should.Throw<InvalidOperationException>(() =>
            new SigningKeyProvider(options, new TestHostEnvironment(Path.GetTempPath()), NullLogger<SigningKeyProvider>.Instance));
        error.Message.ShouldContain("does not exist");
    }

    private sealed class TestHostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";

        public string ApplicationName { get; set; } = "WalletPK.Identity.UnitTests";

        public string ContentRootPath { get; set; } = contentRoot;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
