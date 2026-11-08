using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using WalletPK.Web.Correlation;
using WalletPK.Web.UnitTests.Infrastructure;

namespace WalletPK.Web.UnitTests.Correlation;

public class CorrelationIdTests
{
    private static Task<WebApplication> StartAppAsync() =>
        TestApp.StartAsync(app => app.MapGet("/whoami", () => CorrelationContext.Current ?? "none"));

    [Fact]
    public async Task Creates_a_correlation_id_when_the_request_has_none()
    {
        await using var app = await StartAppAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(new Uri("/whoami", UriKind.Relative), TestContext.Current.CancellationToken);

        var header = response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single();
        header.Length.ShouldBe(32);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe(header);
    }

    [Fact]
    public async Task Reuses_a_valid_incoming_correlation_id()
    {
        await using var app = await StartAppAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(CorrelationIdMiddleware.HeaderName, "mobile-app-7f3a");

        using var response = await client.GetAsync(new Uri("/whoami", UriKind.Relative), TestContext.Current.CancellationToken);

        response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single().ShouldBe("mobile-app-7f3a");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("mobile-app-7f3a");
    }

    [Theory]
    [InlineData("has spaces")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("this-correlation-id-is-far-too-long-to-be-accepted-by-walletpk-0123456789")]
    public void Unsafe_incoming_ids_are_replaced(string incoming)
    {
        var resolved = CorrelationIdMiddleware.Resolve(incoming);

        resolved.ShouldNotBe(incoming);
        resolved.Length.ShouldBe(32);
    }

    [Fact]
    public async Task Outgoing_http_calls_carry_the_current_correlation_id()
    {
        var capture = new CapturingHandler();
        using var handler = new CorrelationIdDelegatingHandler { InnerHandler = capture };
        using var client = new HttpClient(handler);

        CorrelationContext.Current = "abc-123";
        try
        {
            using var response = await client.GetAsync(new Uri("http://bank.test/transfer"), TestContext.Current.CancellationToken);
        }
        finally
        {
            CorrelationContext.Current = null;
        }

        capture.LastRequest.ShouldNotBeNull();
        capture.LastRequest.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single().ShouldBe("abc-123");
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
