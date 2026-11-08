using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using WalletPK.Identity.Application.Abstractions;

namespace WalletPK.Identity.IntegrationTests.Infrastructure;

/// <summary>A registered user signed in on one device, as returned by <see cref="TestAccounts.SignUpAndSignInAsync"/>.</summary>
/// <param name="LocalNumber">The number as typed, for example "03021234567".</param>
/// <param name="UserId">The user's ID.</param>
/// <param name="DeviceId">The trusted device's ID.</param>
/// <param name="AccessToken">A valid access token.</param>
/// <param name="RefreshToken">The matching refresh token.</param>
public sealed record TestSession(string LocalNumber, Guid UserId, Guid DeviceId, string AccessToken, string RefreshToken)
{
    public string E164 => "+92" + LocalNumber[1..];

    public HttpRequestMessage Request(HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }
}

/// <summary>Creates users through the public API, exactly as the app would.</summary>
public static class TestAccounts
{
    public const string Mpin = "40291";

    public static string NewLocalNumber(string prefix = "0302") => prefix + RandomNumberGenerator.GetInt32(1_000_000, 10_000_000);

    /// <summary>Registers a new number, then logs in on a new device and completes the OTP step.</summary>
    public static async Task<TestSession> SignUpAndSignInAsync(IdentityApiFactory factory, HttpClient client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(client);

        var number = NewLocalNumber();
        var e164 = "+92" + number[1..];

        using var start = await client.PostAsJsonAsync(new Uri("/api/identity/auth/register", UriKind.Relative), new { phoneNumber = number }, cancellationToken);
        start.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        using var verify = await client.PostAsJsonAsync(
            new Uri("/api/identity/auth/register/verify-otp", UriKind.Relative),
            new { phoneNumber = number, code = factory.Sms.LatestCodeFor(e164) },
            cancellationToken);
        var registrationToken = (await verify.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("registrationToken").GetString();
        using var complete = await client.PostAsJsonAsync(
            new Uri("/api/identity/auth/register/set-mpin", UriKind.Relative),
            new { registrationToken, mpin = Mpin, confirmMpin = Mpin },
            cancellationToken);
        complete.StatusCode.ShouldBe(HttpStatusCode.Created);

        return await SignInOnNewDeviceAsync(factory, client, number, cancellationToken);
    }

    /// <summary>Logs an existing user in on a brand-new device (OTP step included).</summary>
    public static async Task<TestSession> SignInOnNewDeviceAsync(IdentityApiFactory factory, HttpClient client, string number, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(number);

        var e164 = "+92" + number[1..];
        await factory.ClearOtpCooldownAsync(OtpPurpose.DeviceVerification, e164);

        using var login = await client.PostAsJsonAsync(
            new Uri("/api/identity/auth/login", UriKind.Relative),
            new { phoneNumber = number, mpin = Mpin, installationId = Guid.NewGuid().ToString(), deviceName = "Test phone" },
            cancellationToken);
        login.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var challengeId = (await login.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("challengeId").GetString();

        using var verify = await client.PostAsJsonAsync(
            new Uri("/api/identity/auth/login/verify-device", UriKind.Relative),
            new { challengeId, code = factory.Sms.LatestCodeFor(e164) },
            cancellationToken);
        verify.StatusCode.ShouldBe(HttpStatusCode.OK);
        var tokens = await verify.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        return new TestSession(
            number,
            tokens.GetProperty("userId").GetGuid(),
            tokens.GetProperty("deviceId").GetGuid(),
            tokens.GetProperty("accessToken").GetString()!,
            tokens.GetProperty("refreshToken").GetString()!);
    }

    /// <summary>A random, well-formed CNIC. Prefixes 70000 / 71111 / 72222 trigger the simulated NADRA failures.</summary>
    public static string NewCnic(string prefix = "") =>
        prefix.Length > 0
            ? prefix + RandomNumberGenerator.GetInt32(10_000_000, 100_000_000)
            : "3" + RandomNumberGenerator.GetInt32(1000, 10000) + RandomNumberGenerator.GetInt32(10_000_000, 100_000_000);
}
