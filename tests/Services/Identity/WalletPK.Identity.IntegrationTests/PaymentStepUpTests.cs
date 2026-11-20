using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WalletPK.Identity.Infrastructure.Persistence;
using WalletPK.Identity.IntegrationTests.Infrastructure;

namespace WalletPK.Identity.IntegrationTests;

/// <summary>Step-up OTP for payments (M10.4, FR-5.1): a code to confirm a payment the fraud engine stepped up.</summary>
[Collection(IdentityCollection.Name)]
public class PaymentStepUpTests(IdentityApiFactory factory)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_code_sent_to_the_phone_confirms_one_payment_from_this_device_for_the_fraud_service()
    {
        using var client = factory.CreateClient();
        var session = await TestAccounts.SignUpAndSignInAsync(factory, client, Cancel);

        using var startRequest = session.Request(HttpMethod.Post, "/api/identity/step-up");
        using var start = await client.SendAsync(startRequest, Cancel);
        start.StatusCode.ShouldBe(HttpStatusCode.OK, await start.Content.ReadAsStringAsync(Cancel));
        (await start.Content.ReadFromJsonAsync<JsonElement>(Cancel)).GetProperty("expiresInSeconds").GetInt32().ShouldBeGreaterThan(0);
        var code = factory.Sms.LatestCodeFor(session.E164);

        using (var wrongRequest = session.Request(HttpMethod.Post, "/api/identity/step-up/verify", new { code = code == "000000" ? "111111" : "000000" }))
        using (var wrong = await client.SendAsync(wrongRequest, Cancel))
        {
            wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using var verifyRequest = session.Request(HttpMethod.Post, "/api/identity/step-up/verify", new { code });
        using var verify = await client.SendAsync(verifyRequest, Cancel);
        verify.StatusCode.ShouldBe(HttpStatusCode.OK, await verify.Content.ReadAsStringAsync(Cancel));
        var validUntil = (await verify.Content.ReadFromJsonAsync<JsonElement>(Cancel)).GetProperty("validUntil").GetDateTimeOffset();
        validUntil.ShouldBeInRange(DateTimeOffset.UtcNow.AddMinutes(4), DateTimeOffset.UtcNow.AddMinutes(6));

        // Single use: the same code again is refused.
        using (var againRequest = session.Request(HttpMethod.Post, "/api/identity/step-up/verify", new { code }))
        using (var again = await client.SendAsync(againRequest, Cancel))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var message = await db.OutboxMessages.SingleAsync(m => m.PartitionKey == session.UserId.ToString() && m.EventType == "identity.payment_step_up_confirmed", Cancel);
        message.Topic.ShouldBe("identity.events");
        using var payload = JsonDocument.Parse(message.Payload);
        payload.RootElement.GetProperty("userId").GetGuid().ShouldBe(session.UserId);
        payload.RootElement.GetProperty("deviceId").GetGuid().ShouldBe(session.DeviceId);
    }

    [Fact]
    public async Task Step_up_needs_a_signed_in_user()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(new Uri("/api/identity/step-up", UriKind.Relative), null, Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
