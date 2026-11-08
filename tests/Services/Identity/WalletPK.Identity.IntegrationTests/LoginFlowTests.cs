using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using WalletPK.Identity.Application.Abstractions;
using WalletPK.Identity.IntegrationTests.Infrastructure;

namespace WalletPK.Identity.IntegrationTests;

[Collection(IdentityCollection.Name)]
public class LoginFlowTests(IdentityApiFactory factory)
{
    private const string Mpin = "40291";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static string NewLocalNumber() => "0301" + RandomNumberGenerator.GetInt32(1_000_000, 10_000_000);

    private static string NewInstallationId() => Guid.NewGuid().ToString();

    private static string ToE164(string localNumber) => "+92" + localNumber[1..];

    private static Uri Url(string path) => new(path, UriKind.Relative);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Cancel);

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await JsonAsync(response);
        return body.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>Creates an account through the public registration API.</summary>
    private async Task<string> RegisterAsync(HttpClient client)
    {
        var number = NewLocalNumber();
        using var start = await client.PostAsJsonAsync(Url("/api/identity/auth/register"), new { phoneNumber = number }, Cancel);
        var code = factory.Sms.LatestCodeFor(ToE164(number));
        using var verify = await client.PostAsJsonAsync(Url("/api/identity/auth/register/verify-otp"), new { phoneNumber = number, code }, Cancel);
        var token = (await JsonAsync(verify)).GetProperty("registrationToken").GetString();
        using var complete = await client.PostAsJsonAsync(
            Url("/api/identity/auth/register/set-mpin"), new { registrationToken = token, mpin = Mpin, confirmMpin = Mpin }, Cancel);
        complete.StatusCode.ShouldBe(HttpStatusCode.Created);
        return number;
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string number, string installationId, string mpin = Mpin) =>
        client.PostAsJsonAsync(
            Url("/api/identity/auth/login"),
            new { phoneNumber = number, mpin, installationId, deviceName = "Integration test phone" },
            Cancel);

    /// <summary>Logs in on a new device and completes the OTP step; returns the token response.</summary>
    private async Task<JsonElement> SignInOnNewDeviceAsync(HttpClient client, string number, string installationId)
    {
        await factory.ClearOtpCooldownAsync(OtpPurpose.DeviceVerification, ToE164(number));

        using var login = await LoginAsync(client, number, installationId);
        login.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var challengeId = (await JsonAsync(login)).GetProperty("challengeId").GetString();

        var code = factory.Sms.LatestCodeFor(ToE164(number));
        using var verify = await client.PostAsJsonAsync(Url("/api/identity/auth/login/verify-device"), new { challengeId, code }, Cancel);
        verify.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await JsonAsync(verify);
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, JsonElement tokens)
    {
        var request = new HttpRequestMessage(method, Url(path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return request;
    }

    [Fact]
    public async Task A_user_logs_in_on_a_new_device_with_an_otp_and_then_reaches_their_profile()
    {
        using var client = factory.CreateClient();
        var number = await RegisterAsync(client);
        var installationId = NewInstallationId();

        // 1. New device: the MPIN is right, but an OTP is required (202 + challenge).
        await factory.ClearOtpCooldownAsync(OtpPurpose.DeviceVerification, ToE164(number));
        using var login = await LoginAsync(client, number, installationId);
        var challenge = await JsonAsync(login);
        login.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        challenge.GetProperty("phoneNumber").GetString().ShouldBe($"+92301*****{number[^2..]}");

        // 2. OTP -> tokens; the device is now trusted.
        var code = factory.Sms.LatestCodeFor(ToE164(number));
        using var verify = await client.PostAsJsonAsync(
            Url("/api/identity/auth/login/verify-device"),
            new { challengeId = challenge.GetProperty("challengeId").GetString(), code },
            Cancel);
        var tokens = await JsonAsync(verify);
        verify.StatusCode.ShouldBe(HttpStatusCode.OK);
        tokens.GetProperty("tokenType").GetString().ShouldBe("Bearer");
        tokens.GetProperty("expiresIn").GetInt32().ShouldBe(900);
        tokens.GetProperty("coolingOffUntil").ValueKind.ShouldBe(JsonValueKind.Null);

        // 3. The access token opens the protected profile endpoint.
        using var meRequest = Authorized(HttpMethod.Get, "/api/identity/me", tokens);
        using var me = await client.SendAsync(meRequest, Cancel);
        var profile = await JsonAsync(me);
        me.StatusCode.ShouldBe(HttpStatusCode.OK);
        profile.GetProperty("userId").GetString().ShouldBe(tokens.GetProperty("userId").GetString());
        profile.GetProperty("kycTier").GetString().ShouldBe("L0");
        profile.GetProperty("role").GetString().ShouldBe("customer");
        profile.GetProperty("currentDevice").GetProperty("isCurrent").GetBoolean().ShouldBeTrue();
        profile.GetProperty("currentDevice").GetProperty("name").GetString().ShouldBe("Integration test phone");

        // 4. The same device logs in again with the MPIN alone (200, no OTP).
        using var again = await LoginAsync(client, number, installationId);
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await JsonAsync(again)).GetProperty("accessToken").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Protected_endpoints_reject_missing_and_forged_tokens()
    {
        using var client = factory.CreateClient();

        using var anonymous = await client.GetAsync(Url("/api/identity/me"), Cancel);

        using var forgedRequest = new HttpRequestMessage(HttpMethod.Get, Url("/api/identity/devices"));
        forgedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "eyJhbGciOiJub25lIn0.eyJzdWIiOiJ4In0.");
        using var forged = await client.SendAsync(forgedRequest, Cancel);

        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        forged.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_tokens_rotate_and_a_replayed_one_ends_the_session()
    {
        using var client = factory.CreateClient();
        var number = await RegisterAsync(client);
        var first = await SignInOnNewDeviceAsync(client, number, NewInstallationId());

        using var refresh = await client.PostAsJsonAsync(
            Url("/api/identity/auth/refresh"), new { refreshToken = first.GetProperty("refreshToken").GetString() }, Cancel);
        var second = await JsonAsync(refresh);
        refresh.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.GetProperty("refreshToken").GetString().ShouldNotBe(first.GetProperty("refreshToken").GetString());

        // Someone replays the first (already used) refresh token: the whole session family is revoked.
        using var replay = await client.PostAsJsonAsync(
            Url("/api/identity/auth/refresh"), new { refreshToken = first.GetProperty("refreshToken").GetString() }, Cancel);
        using var afterReplay = await client.PostAsJsonAsync(
            Url("/api/identity/auth/refresh"), new { refreshToken = second.GetProperty("refreshToken").GetString() }, Cancel);

        replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ErrorCodeAsync(replay)).ShouldBe("auth.refresh_token_reused");
        afterReplay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ErrorCodeAsync(afterReplay)).ShouldBe("auth.invalid_refresh_token");
    }

    [Fact]
    public async Task Logout_ends_the_session()
    {
        using var client = factory.CreateClient();
        var number = await RegisterAsync(client);
        var tokens = await SignInOnNewDeviceAsync(client, number, NewInstallationId());
        var refreshToken = tokens.GetProperty("refreshToken").GetString();

        using var logout = await client.PostAsJsonAsync(Url("/api/identity/auth/logout"), new { refreshToken }, Cancel);
        using var refresh = await client.PostAsJsonAsync(Url("/api/identity/auth/refresh"), new { refreshToken }, Cancel);

        logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        refresh.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Five_wrong_mpins_lock_the_account()
    {
        using var client = factory.CreateClient();
        var number = await RegisterAsync(client);
        var installationId = NewInstallationId();

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            using var wrong = await LoginAsync(client, number, installationId, mpin: "11223");
            wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await ErrorCodeAsync(wrong)).ShouldBe("auth.invalid_credentials");
        }

        using var fifth = await LoginAsync(client, number, installationId, mpin: "11223");
        using var correctButLocked = await LoginAsync(client, number, installationId);

        fifth.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(fifth)).ShouldBe("identity.account_locked");
        correctButLocked.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_second_device_is_in_cooling_off_and_a_device_can_be_revoked()
    {
        using var client = factory.CreateClient();
        var number = await RegisterAsync(client);
        var firstPhone = await SignInOnNewDeviceAsync(client, number, NewInstallationId());
        var secondPhone = await SignInOnNewDeviceAsync(client, number, NewInstallationId());

        secondPhone.GetProperty("coolingOffUntil").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddHours(23));

        using var listRequest = Authorized(HttpMethod.Get, "/api/identity/devices", secondPhone);
        using var list = await client.SendAsync(listRequest, Cancel);
        var devices = (await JsonAsync(list)).EnumerateArray().ToList();
        devices.Count.ShouldBe(2);
        devices.Count(device => device.GetProperty("isCurrent").GetBoolean()).ShouldBe(1);

        // From the new phone, remove the old one (for example, it was lost).
        var oldDeviceId = firstPhone.GetProperty("deviceId").GetString();
        using var revokeRequest = Authorized(HttpMethod.Delete, $"/api/identity/devices/{oldDeviceId}", secondPhone);
        using var revoke = await client.SendAsync(revokeRequest, Cancel);
        using var oldRefresh = await client.PostAsJsonAsync(
            Url("/api/identity/auth/refresh"), new { refreshToken = firstPhone.GetProperty("refreshToken").GetString() }, Cancel);

        revoke.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        oldRefresh.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Tokens_verify_against_the_published_jwks_exactly_as_the_gateway_does()
    {
        using var client = factory.CreateClient();
        var number = await RegisterAsync(client);
        var tokens = await SignInOnNewDeviceAsync(client, number, NewInstallationId());

        // Discovery document -> JWKS URL -> public keys.
        using var discovery = await client.GetAsync(Url("/.well-known/openid-configuration"), Cancel);
        var document = await JsonAsync(discovery);
        document.GetProperty("issuer").GetString().ShouldBe("walletpk-identity");
        var jwksUri = new Uri(document.GetProperty("jwks_uri").GetString()!);
        jwksUri.AbsolutePath.ShouldBe("/.well-known/jwks.json");

        using var jwksResponse = await client.GetAsync(Url(jwksUri.AbsolutePath), Cancel);
        jwksResponse.Headers.CacheControl!.MaxAge.ShouldBe(TimeSpan.FromMinutes(5));
        var keySet = new JsonWebKeySet(await jwksResponse.Content.ReadAsStringAsync(Cancel));
        keySet.Keys.ShouldHaveSingleItem().HasPrivateKey.ShouldBeFalse();

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(
            tokens.GetProperty("accessToken").GetString()!,
            new TokenValidationParameters
            {
                ValidIssuer = "walletpk-identity",
                ValidAudience = "walletpk-api",
                IssuerSigningKeys = keySet.GetSigningKeys(),
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            });

        result.IsValid.ShouldBeTrue(result.Exception?.Message);
        result.Claims["sub"].ShouldBe(tokens.GetProperty("userId").GetString());
        result.Claims["device_id"].ShouldBe(tokens.GetProperty("deviceId").GetString());
        result.Claims["kyc_tier"].ShouldBe("L0");
    }
}
