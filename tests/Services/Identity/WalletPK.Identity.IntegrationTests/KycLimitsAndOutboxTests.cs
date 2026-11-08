using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using WalletPK.Common.Identifiers;
using WalletPK.Contracts.Identity.V1;
using WalletPK.Identity.Infrastructure.Persistence;
using WalletPK.Identity.IntegrationTests.Infrastructure;

namespace WalletPK.Identity.IntegrationTests;

[Collection(IdentityCollection.Name)]
public class KycLimitsAndOutboxTests(IdentityApiFactory factory)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Cancel);
        return body.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private async Task<HttpResponseMessage> SubmitKycAsync(HttpClient client, TestSession session, string cnic)
    {
        using var request = session.Request(HttpMethod.Post, "/api/identity/kyc", new { cnic, fullName = "Ayesha Khan", dateOfBirth = "1998-04-21" });
        return await client.SendAsync(request, Cancel);
    }

    /// <summary>A gRPC client talking to the in-memory test server over HTTP/2, as the Ledger will.</summary>
    private Limits.LimitsClient LimitsClient()
    {
        var channel = GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = factory.Server.CreateHandler() });
        return new Limits.LimitsClient(channel);
    }

    private static ReserveLimitRequest Reserve(TestSession session, string transactionId, long rupees, Guid? deviceId = null) => new()
    {
        TransactionId = transactionId,
        UserId = session.UserId.ToString(),
        AmountPaisa = rupees * 100,
        TransactionType = TransactionType.P2PTransfer,
        DeviceId = deviceId?.ToString() ?? string.Empty,
    };

    // ---------------- KYC (M2.5) ----------------

    [Fact]
    public async Task Kyc_upgrades_to_l1_stores_the_cnic_encrypted_and_the_next_token_says_l1()
    {
        using var client = factory.CreateClient();
        var session = await TestAccounts.SignUpAndSignInAsync(factory, client, Cancel);
        var cnic = TestAccounts.NewCnic();

        using var submit = await SubmitKycAsync(client, session, cnic);
        var status = await submit.Content.ReadFromJsonAsync<JsonElement>(Cancel);
        submit.StatusCode.ShouldBe(HttpStatusCode.OK);
        status.GetProperty("kycTier").GetString().ShouldBe("L1");
        status.GetProperty("cnicMasked").GetString().ShouldBe($"{cnic[..5]}-*******-{cnic[^1]}");

        using var getRequest = session.Request(HttpMethod.Get, "/api/identity/kyc");
        using var get = await client.SendAsync(getRequest, Cancel);
        (await get.Content.ReadFromJsonAsync<JsonElement>(Cancel)).GetProperty("verified").GetBoolean().ShouldBeTrue();

        // In PostgreSQL: ciphertext, a blind index and the masked form; never the CNIC itself.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            var userId = UserId.From(session.UserId);
            var profile = await db.KycProfiles.SingleAsync(p => p.Id == userId, Cancel);
            profile.CnicCiphertext.ShouldStartWith("v1.dev1.");
            profile.CnicCiphertext.ShouldNotContain(cnic);
            profile.CnicLookupHash.ShouldNotContain(cnic);
        }

        // Refreshing the session issues an access token with the new tier.
        using var refresh = await client.PostAsJsonAsync(new Uri("/api/identity/auth/refresh", UriKind.Relative), new { refreshToken = session.RefreshToken }, Cancel);
        var accessToken = (await refresh.Content.ReadFromJsonAsync<JsonElement>(Cancel)).GetProperty("accessToken").GetString()!;
        new JsonWebToken(accessToken).GetClaim("kyc_tier").Value.ShouldBe("L1");
    }

    [Fact]
    public async Task One_cnic_can_verify_only_one_account()
    {
        using var client = factory.CreateClient();
        var first = await TestAccounts.SignUpAndSignInAsync(factory, client, Cancel);
        var second = await TestAccounts.SignUpAndSignInAsync(factory, client, Cancel);
        var cnic = TestAccounts.NewCnic();

        using var ok = await SubmitKycAsync(client, first, cnic);
        using var duplicate = await SubmitKycAsync(client, second, cnic);

        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(duplicate)).ShouldBe("kyc.cnic_already_used");
    }

    [Fact]
    public async Task Nadra_failures_are_reported_clearly_and_change_nothing()
    {
        using var client = factory.CreateClient();
        var session = await TestAccounts.SignUpAndSignInAsync(factory, client, Cancel);

        using var notFound = await SubmitKycAsync(client, session, TestAccounts.NewCnic("70000"));
        using var outage = await SubmitKycAsync(client, session, TestAccounts.NewCnic("72222"));
        using var anonymous = await client.PostAsJsonAsync(new Uri("/api/identity/kyc", UriKind.Relative), new { cnic = TestAccounts.NewCnic() }, Cancel);

        notFound.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ErrorCodeAsync(notFound)).ShouldBe("kyc.not_verified");
        outage.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await ErrorCodeAsync(outage)).ShouldBe("kyc.nadra_unavailable");
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ---------------- Limits over gRPC (M2.6) ----------------

    [Fact]
    public async Task The_ledger_can_reserve_and_release_limits_over_grpc()
    {
        using var client = factory.CreateClient();
        var session = await TestAccounts.SignUpAndSignInAsync(factory, client, Cancel);
        var limits = LimitsClient();
        var prefix = Guid.NewGuid().ToString("N");

        var check = await limits.CheckLimitAsync(
            new CheckLimitRequest { UserId = session.UserId.ToString(), AmountPaisa = 20_000_00, TransactionType = TransactionType.BillPayment },
            cancellationToken: Cancel);
        var first = await limits.ReserveLimitAsync(Reserve(session, prefix + "-1", 10_000), cancellationToken: Cancel);
        var retry = await limits.ReserveLimitAsync(Reserve(session, prefix + "-1", 10_000), cancellationToken: Cancel);
        var second = await limits.ReserveLimitAsync(Reserve(session, prefix + "-2", 10_000), cancellationToken: Cancel);
        var third = await limits.ReserveLimitAsync(Reserve(session, prefix + "-3", 10_000), cancellationToken: Cancel);

        check.Decision.ShouldBe(LimitDecision.ExceedsPerTransaction);
        check.KycTier.ShouldBe("L0");
        first.Decision.ShouldBe(LimitDecision.Allowed);
        retry.DailyRemainingPaisa.ShouldBe(15_000_00); // the retry counted nothing
        second.DailyRemainingPaisa.ShouldBe(5_000_00);
        third.Decision.ShouldBe(LimitDecision.ExceedsDaily);

        var released = await limits.ReleaseLimitAsync(new ReleaseLimitRequest { TransactionId = prefix + "-2" }, cancellationToken: Cancel);
        var afterRelease = await limits.ReserveLimitAsync(Reserve(session, prefix + "-4", 10_000), cancellationToken: Cancel);

        released.Released.ShouldBeTrue();
        afterRelease.Decision.ShouldBe(LimitDecision.Allowed);

        // The customer's limits screen shows the same numbers.
        using var summaryRequest = session.Request(HttpMethod.Get, "/api/identity/limits");
        using var summary = await client.SendAsync(summaryRequest, Cancel);
        var daily = (await summary.Content.ReadFromJsonAsync<JsonElement>(Cancel)).GetProperty("daily");
        daily.GetProperty("used").GetProperty("minorUnits").GetInt64().ShouldBe(20_000_00);
        daily.GetProperty("remaining").GetProperty("minorUnits").GetInt64().ShouldBe(5_000_00);
    }

    [Fact]
    public async Task A_new_device_is_capped_during_cooling_off()
    {
        using var client = factory.CreateClient();
        var firstPhone = await TestAccounts.SignUpAndSignInAsync(factory, client, Cancel);
        var secondPhone = await TestAccounts.SignInOnNewDeviceAsync(factory, client, firstPhone.LocalNumber, Cancel);
        var limits = LimitsClient();

        var fromNewPhone = await limits.ReserveLimitAsync(Reserve(secondPhone, Guid.NewGuid().ToString(), 6_000, secondPhone.DeviceId), cancellationToken: Cancel);
        var fromOldPhone = await limits.ReserveLimitAsync(Reserve(firstPhone, Guid.NewGuid().ToString(), 6_000, firstPhone.DeviceId), cancellationToken: Cancel);

        fromNewPhone.Decision.ShouldBe(LimitDecision.CoolingOff);
        fromNewPhone.PerTransactionLimitPaisa.ShouldBe(5_000_00);
        fromOldPhone.Decision.ShouldBe(LimitDecision.Allowed);
    }

    [Fact]
    public async Task Invalid_grpc_requests_get_invalid_argument()
    {
        using var client = factory.CreateClient();
        var limits = LimitsClient();

        var badUser = await Should.ThrowAsync<RpcException>(async () =>
            await limits.CheckLimitAsync(new CheckLimitRequest { UserId = "nope", AmountPaisa = 100, TransactionType = TransactionType.P2PTransfer }, cancellationToken: Cancel));
        var noType = await Should.ThrowAsync<RpcException>(async () =>
            await limits.CheckLimitAsync(new CheckLimitRequest { UserId = Guid.NewGuid().ToString(), AmountPaisa = 100 }, cancellationToken: Cancel));

        badUser.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        noType.StatusCode.ShouldBe(StatusCode.InvalidArgument);
    }

    // ---------------- Outbox (M2.7) ----------------

    [Fact]
    public async Task Every_public_event_is_written_to_the_outbox_in_the_same_transaction()
    {
        using var client = factory.CreateClient();
        var session = await TestAccounts.SignUpAndSignInAsync(factory, client, Cancel);
        using var kyc = await SubmitKycAsync(client, session, TestAccounts.NewCnic());
        kyc.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var messages = await db.OutboxMessages
            .Where(message => message.PartitionKey == session.UserId.ToString())
            .OrderBy(message => message.OccurredAt)
            .ToListAsync(Cancel);

        messages.Select(message => message.EventType)
            .ShouldBe(new[] { "identity.user_registered", "identity.device_bound", "identity.kyc_upgraded" });
        messages.ShouldAllBe(message => message.Topic == "identity.events" && message.PublishedAt == null && message.CorrelationId != null);

        using var payload = JsonDocument.Parse(messages[0].Payload);
        payload.RootElement.GetProperty("phoneNumber").GetString().ShouldBe(session.E164);
    }
}
