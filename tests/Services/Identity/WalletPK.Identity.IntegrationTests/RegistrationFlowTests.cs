using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WalletPK.Identity.Domain.Users;
using WalletPK.Identity.Infrastructure.Persistence;
using WalletPK.Identity.IntegrationTests.Infrastructure;

namespace WalletPK.Identity.IntegrationTests;

[Collection(IdentityCollection.Name)]
public class RegistrationFlowTests(IdentityApiFactory factory)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>A fresh random number per test, so tests never collide (cooldowns, duplicates).</summary>
    private static string NewLocalNumber() => "0300" + RandomNumberGenerator.GetInt32(1_000_000, 10_000_000);

    private static string ToE164(string localNumber) => "+92" + localNumber[1..];

    private static Uri Url(string path) => new($"/api/identity/auth/register{path}", UriKind.Relative);

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Cancel);
        return body.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private async Task<string> VerifiedTokenAsync(HttpClient client, string number)
    {
        using var start = await client.PostAsJsonAsync(Url("/"), new { phoneNumber = number }, Cancel);
        start.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var code = factory.Sms.LatestCodeFor(ToE164(number));
        using var verify = await client.PostAsJsonAsync(Url("/verify-otp"), new { phoneNumber = number, code }, Cancel);
        verify.StatusCode.ShouldBe(HttpStatusCode.OK);

        var ticket = await verify.Content.ReadFromJsonAsync<JsonElement>(Cancel);
        return ticket.GetProperty("registrationToken").GetString().ShouldNotBeNull();
    }

    [Fact]
    public async Task A_new_user_can_register_end_to_end()
    {
        using var client = factory.CreateClient();
        var number = NewLocalNumber();

        // 1. Phone number -> OTP
        using var start = await client.PostAsJsonAsync(Url("/"), new { phoneNumber = number }, Cancel);
        var challenge = await start.Content.ReadFromJsonAsync<JsonElement>(Cancel);
        start.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        challenge.GetProperty("phoneNumber").GetString().ShouldBe($"+92300*****{number[^2..]}");
        challenge.GetProperty("expiresInSeconds").GetInt32().ShouldBe(120);

        // 2. OTP -> registration token
        var code = factory.Sms.LatestCodeFor(ToE164(number));
        using var verify = await client.PostAsJsonAsync(Url("/verify-otp"), new { phoneNumber = number, code }, Cancel);
        var ticket = await verify.Content.ReadFromJsonAsync<JsonElement>(Cancel);
        verify.StatusCode.ShouldBe(HttpStatusCode.OK);
        var token = ticket.GetProperty("registrationToken").GetString();

        // 3. Token + MPIN -> account
        using var complete = await client.PostAsJsonAsync(Url("/set-mpin"), new { registrationToken = token, mpin = "40291", confirmMpin = "40291" }, Cancel);
        var user = await complete.Content.ReadFromJsonAsync<JsonElement>(Cancel);
        complete.StatusCode.ShouldBe(HttpStatusCode.Created);
        user.GetProperty("kycTier").GetString().ShouldBe("L0");
        var userId = user.GetProperty("userId").GetGuid();
        complete.Headers.Location!.ToString().ShouldBe($"/api/identity/users/{userId}");

        // The row in PostgreSQL holds a salted Argon2id hash, never the MPIN.
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var phone = PhoneNumber.Create(number).Value;
        var saved = await db.Users.SingleAsync(u => u.PhoneNumber == phone, Cancel);
        saved.Id.Value.ShouldBe(userId);
        saved.MpinHash.ShouldStartWith("$argon2id$");
        saved.MpinHash.ShouldNotContain("40291");
        saved.Status.ShouldBe(UserStatus.Active);
    }

    [Fact]
    public async Task A_registration_token_works_only_once()
    {
        using var client = factory.CreateClient();
        var token = await VerifiedTokenAsync(client, NewLocalNumber());

        using var first = await client.PostAsJsonAsync(Url("/set-mpin"), new { registrationToken = token, mpin = "40291", confirmMpin = "40291" }, Cancel);
        using var second = await client.PostAsJsonAsync(Url("/set-mpin"), new { registrationToken = token, mpin = "40291", confirmMpin = "40291" }, Cancel);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(second)).ShouldBe("registration.session_expired");
    }

    [Fact]
    public async Task A_registered_number_cannot_sign_up_again()
    {
        using var client = factory.CreateClient();
        var number = NewLocalNumber();
        var token = await VerifiedTokenAsync(client, number);
        using var created = await client.PostAsJsonAsync(Url("/set-mpin"), new { registrationToken = token, mpin = "40291", confirmMpin = "40291" }, Cancel);

        // Same number in a different format is still the same number.
        using var again = await client.PostAsJsonAsync(Url("/"), new { phoneNumber = ToE164(number) }, Cancel);

        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(again)).ShouldBe("identity.phone_already_registered");
    }

    [Fact]
    public async Task Five_wrong_codes_destroy_the_otp()
    {
        using var client = factory.CreateClient();
        var number = NewLocalNumber();
        using var start = await client.PostAsJsonAsync(Url("/"), new { phoneNumber = number }, Cancel);
        var realCode = factory.Sms.LatestCodeFor(ToE164(number));
        var wrongCode = realCode == "000000" ? "111111" : "000000";

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            using var wrong = await client.PostAsJsonAsync(Url("/verify-otp"), new { phoneNumber = number, code = wrongCode }, Cancel);
            wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await ErrorCodeAsync(wrong)).ShouldBe("otp.incorrect");
        }

        using var fifth = await client.PostAsJsonAsync(Url("/verify-otp"), new { phoneNumber = number, code = wrongCode }, Cancel);
        using var tooLate = await client.PostAsJsonAsync(Url("/verify-otp"), new { phoneNumber = number, code = realCode }, Cancel);

        fifth.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await ErrorCodeAsync(fifth)).ShouldBe("otp.too_many_attempts");
        (await ErrorCodeAsync(tooLate)).ShouldBe("otp.expired");
    }

    [Fact]
    public async Task A_new_code_cannot_be_requested_immediately()
    {
        using var client = factory.CreateClient();
        var number = NewLocalNumber();

        using var first = await client.PostAsJsonAsync(Url("/"), new { phoneNumber = number }, Cancel);
        using var second = await client.PostAsJsonAsync(Url("/"), new { phoneNumber = number }, Cancel);

        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        second.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await ErrorCodeAsync(second)).ShouldBe("otp.resend_too_soon");
    }

    [Fact]
    public async Task Invalid_numbers_and_weak_mpins_are_rejected_with_clear_codes()
    {
        using var client = factory.CreateClient();

        using var badNumber = await client.PostAsJsonAsync(Url("/"), new { phoneNumber = "042-3576-1234" }, Cancel);
        badNumber.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(badNumber)).ShouldBe("phone.invalid");

        var token = await VerifiedTokenAsync(client, NewLocalNumber());
        using var weak = await client.PostAsJsonAsync(Url("/set-mpin"), new { registrationToken = token, mpin = "12345", confirmMpin = "12345" }, Cancel);
        using var strong = await client.PostAsJsonAsync(Url("/set-mpin"), new { registrationToken = token, mpin = "40291", confirmMpin = "40291" }, Cancel);

        (await ErrorCodeAsync(weak)).ShouldBe("mpin.too_simple");
        strong.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Readiness_reports_postgres_and_redis_healthy()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), Cancel);
        var report = await response.Content.ReadFromJsonAsync<JsonElement>(Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var checks = report.GetProperty("checks").EnumerateArray()
            .ToDictionary(check => check.GetProperty("name").GetString() ?? string.Empty, check => check.GetProperty("status").GetString());
        checks["postgres"].ShouldBe("Healthy");
        checks["redis"].ShouldBe("Healthy");
    }
}
