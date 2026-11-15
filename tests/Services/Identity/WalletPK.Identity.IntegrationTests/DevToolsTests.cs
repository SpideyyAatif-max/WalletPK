using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using WalletPK.Identity.Infrastructure.Sms;
using WalletPK.Identity.IntegrationTests.Infrastructure;

namespace WalletPK.Identity.IntegrationTests;

/// <summary>The Development-only SMS inbox the web app reads OTPs from (M4.7).</summary>
[Collection(IdentityCollection.Name)]
public class DevToolsTests(IdentityApiFactory factory)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task In_development_the_inbox_shows_the_messages_sent_to_a_number_newest_first()
    {
        var inbox = factory.Services.GetRequiredService<DevSmsInbox>();
        inbox.Add("+923007654321", "Your WalletPK code is 111111");
        inbox.Add("+923007654321", "Your WalletPK code is 222222");
        inbox.Add("+923001111111", "Someone else's code");
        using var client = factory.CreateClient();

        // Any common way of writing the number finds the same inbox.
        var messages = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/identity/dev/sms?phoneNumber=0300%207654321", UriKind.Relative), Cancel);

        messages.EnumerateArray().Select(message => message.GetProperty("message").GetString())
            .ShouldBe(["Your WalletPK code is 222222", "Your WalletPK code is 111111"]);
        messages[0].GetProperty("to").GetString().ShouldBe("+923007654321");

        using var invalid = await client.GetAsync(new Uri("/api/identity/dev/sms?phoneNumber=12", UriKind.Relative), Cancel);
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Outside_development_the_inbox_does_not_exist()
    {
        await using var staging = factory.WithWebHostBuilder(builder => builder
            .UseEnvironment("Staging")
            // Same settings as the tests normally run with; only the environment name differs.
            .ConfigureAppConfiguration(configuration =>
            {
                var appsettings = configuration.Sources.ToList().FindIndex(source => source is JsonConfigurationSource { Path: "appsettings.json" });
                configuration.Sources.Insert(appsettings + 1, new JsonConfigurationSource { Path = "appsettings.Development.json", Optional = false });
            }));
        using var client = staging.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/identity/dev/sms?phoneNumber=03007654321", UriKind.Relative), Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public void The_inbox_keeps_the_last_ten_messages_per_number()
    {
        var inbox = new DevSmsInbox(TimeProvider.System);
        for (var i = 1; i <= 12; i++)
        {
            inbox.Add("+923009999999", $"message {i}");
        }

        inbox.For("+923009999999").Select(message => message.Message).ShouldBe([.. Enumerable.Range(3, 10).Reverse().Select(i => $"message {i}")]);
        inbox.For("+923008888888").ShouldBeEmpty();
    }
}
