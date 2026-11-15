using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using WalletPK.Identity.Application.Abstractions;
using WalletPK.Identity.Domain.Kyc;
using WalletPK.Identity.Infrastructure.Nadra;

namespace WalletPK.Identity.UnitTests.Infrastructure;

/// <summary>M5.5: Identity's client for NADRA (the mock provider over HTTP).</summary>
public sealed class HttpNadraClientTests
{
    private static readonly Cnic _cnic = Cnic.Create("35202-1234567-1").Value;

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        public HttpRequestMessage? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return answer(request);
        }
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return Json(HttpStatusCode.OK, """{"result":"Verified"}""");
        }
    }

    private static (HttpNadraClient Client, Handler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var handler = new Handler(answer);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://mock-providers.test/"), Timeout = TimeSpan.FromSeconds(5) };
        return (new HttpNadraClient(http, NullLogger<HttpNadraClient>.Instance), handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Theory]
    [InlineData("""{"result":"Verified","reference":"NADRA-ABC123","nameMatchScore":0.97}""", NadraOutcome.Verified, "NADRA-ABC123")]
    [InlineData("""{"result":"NotFound","reference":null}""", NadraOutcome.NotFound, null)]
    [InlineData("""{"result":"Mismatch","reference":null,"nameMatchScore":0.41}""", NadraOutcome.Mismatch, null)]
    [InlineData("""{"result":"Something new"}""", NadraOutcome.Unavailable, null)]
    public async Task Answers_map_to_outcomes(string json, NadraOutcome outcome, string? reference)
    {
        var (client, handler) = Create(_ => Json(HttpStatusCode.OK, json));

        var result = await client.VerifyAsync(_cnic, "Ali Khan", new DateOnly(1990, 1, 31), TestContext.Current.CancellationToken);

        result.ShouldBe(new NadraVerification(outcome, reference));
        handler.Request!.RequestUri!.AbsolutePath.ShouldBe("/nadra/v1/verifications");
        handler.Body.ShouldBe("""{"cnic":"3520212345671","fullName":"Ali Khan","dateOfBirth":"1990-01-31"}""");
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Anything_but_a_clear_answer_is_unavailable(HttpStatusCode status)
    {
        var (client, _) = Create(_ => Json(status, "{}"));

        (await client.VerifyAsync(_cnic, "Ali Khan", new DateOnly(1990, 1, 31), TestContext.Current.CancellationToken))
            .Outcome.ShouldBe(NadraOutcome.Unavailable);
    }

    [Fact]
    public async Task A_dropped_connection_or_a_timeout_is_unavailable()
    {
        var (dropped, _) = Create(_ => throw new HttpRequestException("Connection reset"));
        using var slowHttp = new HttpClient(new SlowHandler()) { BaseAddress = new Uri("http://mock-providers.test/"), Timeout = TimeSpan.FromMilliseconds(50) };
        var slow = new HttpNadraClient(slowHttp, NullLogger<HttpNadraClient>.Instance);

        (await dropped.VerifyAsync(_cnic, "Ali Khan", new DateOnly(1990, 1, 31), TestContext.Current.CancellationToken)).Outcome.ShouldBe(NadraOutcome.Unavailable);
        (await slow.VerifyAsync(_cnic, "Ali Khan", new DateOnly(1990, 1, 31), TestContext.Current.CancellationToken)).Outcome.ShouldBe(NadraOutcome.Unavailable);
    }

    [Theory]
    [InlineData("Simulated", "", "", true)]
    [InlineData("Http", "http://mock-providers:8080/", "key", true)]
    [InlineData("Http", "", "key", false)]
    [InlineData("Http", "http://mock-providers:8080/", "", false)]
    [InlineData("Carrier pigeon", "", "", false)]
    public void Options_need_a_url_and_key_for_http(string mode, string url, string key, bool valid) =>
        new NadraOptions { Mode = mode, BaseUrl = url, ApiKey = key }.IsValid().ShouldBe(valid);
}
