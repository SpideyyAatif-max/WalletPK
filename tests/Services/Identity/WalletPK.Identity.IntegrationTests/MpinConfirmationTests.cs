using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using WalletPK.Identity.IntegrationTests.Infrastructure;

namespace WalletPK.Identity.IntegrationTests;

/// <summary>The MPIN on the payment confirmation screen (proposal 12.1): a short-lived token for one payment.</summary>
[Collection(IdentityCollection.Name)]
public class MpinConfirmationTests(IdentityApiFactory factory)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_right_mpin_gives_a_token_for_this_user_device_and_payment_that_is_not_an_access_token()
    {
        using var client = factory.CreateClient();
        var session = await TestAccounts.SignUpAndSignInAsync(factory, client, Cancel);

        using var request = session.Request(HttpMethod.Post, "/api/identity/mpin/confirm", new { mpin = TestAccounts.Mpin, idempotencyKey = "pay-0001-abcd" });
        using var response = await client.SendAsync(request, Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Cancel));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Cancel);
        body.GetProperty("expiresAt").GetDateTimeOffset().ShouldBeInRange(DateTimeOffset.UtcNow.AddMinutes(4), DateTimeOffset.UtcNow.AddMinutes(6));
        var token = new JsonWebToken(body.GetProperty("confirmationToken").GetString());
        token.Audiences.ShouldBe(["walletpk-confirmation"]);
        token.Subject.ShouldBe(session.UserId.ToString());
        token.GetClaim("device_id").Value.ShouldBe(session.DeviceId.ToString());
        token.GetClaim("binding").Value.ShouldBe("pay-0001-abcd");

        // It is not an access token: Identity's own endpoints refuse it.
        using var misuse = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/identity/me", UriKind.Relative));
        misuse.Headers.Authorization = new("Bearer", token.EncodedToken);
        using var refused = await client.SendAsync(misuse, Cancel);
        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Wrong_mpins_count_towards_the_login_lockout()
    {
        using var client = factory.CreateClient();
        var session = await TestAccounts.SignUpAndSignInAsync(factory, client, Cancel);

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            using var wrongRequest = session.Request(HttpMethod.Post, "/api/identity/mpin/confirm", new { mpin = "11111", idempotencyKey = "pay-0002-abcd" });
            using var wrong = await client.SendAsync(wrongRequest, Cancel);
            wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            var problem = await wrong.Content.ReadFromJsonAsync<JsonElement>(Cancel);
            problem.GetProperty("code").GetString().ShouldBe("identity.incorrect_mpin");
            problem.GetProperty("detail").GetString()!.ShouldContain($"{5 - attempt} attempt(s) left");
        }

        using (var fifthRequest = session.Request(HttpMethod.Post, "/api/identity/mpin/confirm", new { mpin = "11111", idempotencyKey = "pay-0002-abcd" }))
        using (var fifth = await client.SendAsync(fifthRequest, Cancel))
        {
            fifth.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await fifth.Content.ReadFromJsonAsync<JsonElement>(Cancel)).GetProperty("code").GetString().ShouldBe("identity.account_locked");
        }

        // Locked: even the right MPIN is refused now.
        using var rightRequest = session.Request(HttpMethod.Post, "/api/identity/mpin/confirm", new { mpin = TestAccounts.Mpin, idempotencyKey = "pay-0002-abcd" });
        using var right = await client.SendAsync(rightRequest, Cancel);
        right.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_payment_key_is_required_and_signing_in_is_required()
    {
        using var client = factory.CreateClient();
        var session = await TestAccounts.SignUpAndSignInAsync(factory, client, Cancel);

        using (var noKeyRequest = session.Request(HttpMethod.Post, "/api/identity/mpin/confirm", new { mpin = TestAccounts.Mpin }))
        using (var noKey = await client.SendAsync(noKeyRequest, Cancel))
        {
            noKey.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using var anonymous = await client.PostAsJsonAsync(new Uri("/api/identity/mpin/confirm", UriKind.Relative), new { mpin = TestAccounts.Mpin, idempotencyKey = "k-0001-abcd" }, Cancel);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
