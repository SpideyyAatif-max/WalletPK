using System.Net;
using WalletPK.MockProviders.Tests.Infrastructure;
using static WalletPK.MockProviders.Tests.Infrastructure.TestKit;

namespace WalletPK.MockProviders.Tests;

/// <summary>M11.4: the mock SMS and email gateways, idempotent by the caller's message ID.</summary>
[Collection(MockProvidersCollection.Name)]
public sealed class MessagingTests(MockProvidersFactory factory)
{
    private static string NewNumber() => $"0300{Random.Shared.Next(1_000_000, 9_999_999)}";

    [Fact]
    public async Task A_text_sent_twice_with_the_same_id_is_sent_once_and_a_reused_id_with_other_text_is_refused()
    {
        using var client = factory.Client();
        var id = NewId();
        var number = NewNumber();

        using var first = await client.PostJsonAsync("/sms/v1/messages", new { messageId = id, to = number, text = "WalletPK: آپ کو Rs. 250.00 موصول ہوئے۔" });
        using var again = await client.PostJsonAsync("/sms/v1/messages", new { messageId = id, to = number, text = "WalletPK: آپ کو Rs. 250.00 موصول ہوئے۔" });
        using var different = await client.PostJsonAsync("/sms/v1/messages", new { messageId = id, to = number, text = "Something else" });

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var reference = (await first.JsonAsync()).GetProperty("reference").GetString();
        (await again.JsonAsync()).GetProperty("reference").GetString().ShouldBe(reference);
        different.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var sent = await client.GetJsonAsync($"/sms/v1/messages?to={number}");
        var text = sent.EnumerateArray().ShouldHaveSingleItem();
        text.GetProperty("to").GetString().ShouldBe($"+92{number[1..]}");
        text.GetProperty("text").GetString().ShouldBe("WalletPK: آپ کو Rs. 250.00 موصول ہوئے۔");
    }

    [Fact]
    public async Task Bad_numbers_addresses_and_empty_messages_are_refused()
    {
        using var client = factory.Client();

        using var badNumber = await client.PostJsonAsync("/sms/v1/messages", new { messageId = NewId(), to = "12345", text = "Hi" });
        using var noText = await client.PostJsonAsync("/sms/v1/messages", new { messageId = NewId(), to = NewNumber(), text = "" });
        using var badAddress = await client.PostJsonAsync("/email/v1/messages", new { messageId = NewId(), to = "not-an-address", subject = "Hi", body = "Hi" });
        using var noId = await client.PostJsonAsync("/email/v1/messages", new { to = "a@b.pk", subject = "Hi", body = "Hi" });

        badNumber.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        noText.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        badAddress.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        noId.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Emails_are_kept_by_address_and_the_gateways_need_the_client_key()
    {
        using var client = factory.Client();
        var address = $"user{Random.Shared.Next(100_000, 999_999)}@Example.pk";

        using var sent = await client.PostJsonAsync("/email/v1/messages", new { messageId = NewId(), to = address, subject = "WalletPK: Account upgraded", body = "Your account is now L2." });
        sent.StatusCode.ShouldBe(HttpStatusCode.OK);

        var inbox = await client.GetJsonAsync($"/email/v1/messages?to={address.ToLowerInvariant()}");
        inbox.EnumerateArray().ShouldHaveSingleItem().GetProperty("subject").GetString().ShouldBe("WalletPK: Account upgraded");

        using var anonymous = factory.CreateClient();
        using var refused = await anonymous.PostJsonAsync("/sms/v1/messages", new { messageId = NewId(), to = NewNumber(), text = "Hi" });
        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
