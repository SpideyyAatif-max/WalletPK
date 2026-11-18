using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using WalletPK.Payments.Application.Raast;
using WalletPK.Payments.Domain;
using WalletPK.Payments.Infrastructure.Raast;

namespace WalletPK.Payments.UnitTests;

/// <summary>M6.5: every way a Raast call can end, through the real per-bank pipelines (with a fake network).</summary>
public sealed class RaastGatewayTests : IDisposable
{
    private static readonly RaastBank Hbl = RaastBanks.ByCode("HBL")!;
    private static readonly RaastBank Mcb = RaastBanks.ByCode("MCB")!;

    private readonly FakeNetwork _network = new();
    private readonly TestMeterFactory _meters = new();
    private readonly RaastResilience _resilience;
    private readonly RaastGateway _gateway;
    private readonly HttpClient _http;

    public RaastGatewayTests()
    {
        var options = new RaastOptions
        {
            BaseUrl = new Uri("http://raast.test/"),
            ApiKey = "key",
            Resilience = new RaastResilienceOptions
            {
                MaxConcurrentCalls = 1,
                MaxQueuedCalls = 0,
                AttemptTimeoutMilliseconds = 200,
                TotalTimeoutMilliseconds = 2_000,
                MaxRetries = 2,
                RetryBaseDelayMilliseconds = 1,
                MinimumThroughput = 4,
                FailureRatio = 0.5,
                SamplingSeconds = 30,
                BreakSeconds = 30,
            },
        };
        _resilience = new RaastResilience(options, NullLoggerFactory.Instance, _meters);
        _http = new HttpClient(_network) { BaseAddress = options.BaseUrl };
        _gateway = new RaastGateway(_http, _resilience, new RaastMetrics(_meters), NullLogger<RaastGateway>.Instance);
    }

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private Task<RaastSendResult> SendAsync(RaastBank bank) =>
        _gateway.SendAsync(new RaastSendRequest(Guid.NewGuid(), 10_000, "PK36SCBL0000001123456702", bank, "WalletPK", null), Cancel);

    [Fact]
    public async Task An_accepted_transfer_returns_its_reference()
    {
        _network.Respond(HttpStatusCode.Accepted, """{"transferId":"x","raastReference":"RAAST1","status":"Pending","reason":null}""");

        var result = await SendAsync(Hbl);

        result.Outcome.ShouldBe(RaastCallOutcome.Ok);
        result.RaastReference.ShouldBe("RAAST1");
        _network.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task A_bank_outage_is_unavailable_at_once_without_retrying()
    {
        _network.Respond(HttpStatusCode.ServiceUnavailable, """{"code":"provider.unavailable"}""");

        var result = await SendAsync(Hbl);

        result.Outcome.ShouldBe(RaastCallOutcome.Unavailable);
        _network.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Server_errors_are_retried_and_then_reported_as_unknown()
    {
        _network.Respond(HttpStatusCode.InternalServerError, """{"code":"provider.error"}""");

        var result = await SendAsync(Hbl);

        result.Outcome.ShouldBe(RaastCallOutcome.Unknown);
        _network.Calls.ShouldBe(3);
    }

    [Fact]
    public async Task A_retry_that_succeeds_hides_the_failure()
    {
        _network.Respond(HttpStatusCode.BadGateway, "", times: 1);
        _network.Respond(HttpStatusCode.Accepted, """{"transferId":"x","raastReference":"RAAST2","status":"Pending","reason":null}""");

        (await SendAsync(Hbl)).RaastReference.ShouldBe("RAAST2");
        _network.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Refusals_carry_raasts_error_code_and_not_found_is_its_own_outcome()
    {
        _network.Respond(HttpStatusCode.BadRequest, """{"code":"raast.amount_out_of_range","detail":"Too much."}""", times: 1);
        _network.Respond(HttpStatusCode.NotFound, """{"code":"raast.transfer_not_found"}""");

        var refused = await SendAsync(Hbl);
        var missing = await _gateway.GetStatusAsync(Guid.NewGuid(), Hbl, Cancel);

        refused.ShouldSatisfyAllConditions(
            () => refused.Outcome.ShouldBe(RaastCallOutcome.Rejected),
            () => refused.Code.ShouldBe("raast.amount_out_of_range"),
            () => refused.Detail.ShouldBe("Too much."));
        missing.Outcome.ShouldBe(RaastCallOutcome.NotFound);
        _network.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task A_connection_that_cannot_be_made_is_unavailable_but_a_dropped_one_is_unknown()
    {
        _network.Throw(new HttpRequestException(HttpRequestError.ConnectionError, "refused"));
        (await SendAsync(Hbl)).Outcome.ShouldBe(RaastCallOutcome.Unavailable);

        _network.Throw(new HttpRequestException(HttpRequestError.ResponseEnded, "dropped"));
        (await SendAsync(Mcb)).Outcome.ShouldBe(RaastCallOutcome.Unknown);
    }

    [Fact]
    public async Task A_bank_that_does_not_answer_times_out_as_unknown()
    {
        _network.Hang();

        var result = await SendAsync(Hbl);

        result.Outcome.ShouldBe(RaastCallOutcome.Unknown);
        result.Detail.ShouldBe("timed out");
        _network.Calls.ShouldBe(3);
    }

    [Fact]
    public async Task A_failing_bank_opens_its_own_circuit_only()
    {
        _network.Respond(HttpStatusCode.ServiceUnavailable, "");
        for (var i = 0; i < 4; i++)
        {
            (await SendAsync(Hbl)).Outcome.ShouldBe(RaastCallOutcome.Unavailable);
        }

        var calls = _network.Calls;
        var fastFail = await SendAsync(Hbl);

        fastFail.Outcome.ShouldBe(RaastCallOutcome.Unavailable);
        fastFail.Detail!.ShouldContain("circuit open");
        _network.Calls.ShouldBe(calls);

        var banks = _resilience.GetAll();
        banks.Single(bank => bank.Code == "HBL").ShouldSatisfyAllConditions(
            bank => bank.Status.ShouldBe("Down"),
            bank => bank.Circuit.ShouldBe("Open"),
            bank => bank.Since.ShouldNotBeNull());
        banks.Where(bank => bank.Code != "HBL").ShouldAllBe(bank => bank.Status == "Available");

        _network.Respond(HttpStatusCode.Accepted, """{"transferId":"x","raastReference":"RAAST3","status":"Pending","reason":null}""");
        (await SendAsync(Mcb)).Outcome.ShouldBe(RaastCallOutcome.Ok);
    }

    [Fact]
    public async Task A_call_that_timed_out_before_the_circuit_opened_is_unknown_not_unavailable()
    {
        // Earlier calls trip the breaker half way; this call's first try timed out (it may have arrived),
        // so even though its retry meets an open circuit, the outcome must be "ask first", not "not done".
        _network.Respond(HttpStatusCode.ServiceUnavailable, "", times: 3);
        for (var i = 0; i < 3; i++)
        {
            (await SendAsync(Hbl)).Outcome.ShouldBe(RaastCallOutcome.Unavailable);
        }

        _network.Hang();
        var result = await SendAsync(Hbl);

        result.Outcome.ShouldBe(RaastCallOutcome.Unknown);
        result.Detail!.ShouldContain("circuit open");
    }

    [Fact]
    public async Task A_full_bulkhead_refuses_more_calls_to_that_bank()
    {
        var release = new TaskCompletionSource();
        _network.Block(release.Task);

        var first = SendAsync(Hbl);
        await _network.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), Cancel);
        var second = await SendAsync(Hbl);

        second.Outcome.ShouldBe(RaastCallOutcome.Unavailable);
        second.Detail!.ShouldContain("too many calls");
        release.SetResult();
        (await first).Outcome.ShouldBe(RaastCallOutcome.Ok);
    }

    public void Dispose()
    {
        _http.Dispose();
        _network.Dispose();
        _meters.Dispose();
    }

    /// <summary>A scripted network: answers in order (the last answer repeats), throws, hangs or blocks.</summary>
    private sealed class FakeNetwork : HttpMessageHandler
    {
        private readonly Queue<(Func<CancellationToken, Task<HttpResponseMessage>> Answer, int Times)> _script = new();
        private int _calls;

        public int Calls => _calls;

        public TaskCompletionSource FirstCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Respond(HttpStatusCode status, string body, int times = int.MaxValue) =>
            Add(_ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }), times);

        public void Throw(Exception exception) => Add(_ => Task.FromException<HttpResponseMessage>(exception), int.MaxValue);

        public void Hang() => Add(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException();
        }, int.MaxValue);

        public void Block(Task until) => Add(async token =>
        {
            await until.WaitAsync(token);
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent("""{"transferId":"x","raastReference":"RAAST4","status":"Pending","reason":null}""", Encoding.UTF8, "application/json"),
            };
        }, int.MaxValue);

        /// <summary>Queues an answer; an answer that repeats for ever replaces the previous one.</summary>
        private void Add(Func<CancellationToken, Task<HttpResponseMessage>> answer, int times)
        {
            lock (_script)
            {
                var kept = _script.Where(entry => entry.Times != int.MaxValue).ToList();
                _script.Clear();
                kept.ForEach(_script.Enqueue);
                _script.Enqueue((answer, times));
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            FirstCallStarted.TrySetResult();
            lock (_script)
            {
                var (answer, times) = _script.Peek();
                if (times != int.MaxValue)
                {
                    _script.Dequeue();
                    if (times > 1)
                    {
                        // Put the remainder back at the front.
                        var rest = _script.ToList();
                        _script.Clear();
                        _script.Enqueue((answer, times - 1));
                        rest.ForEach(_script.Enqueue);
                    }
                }

                return answer(cancellationToken);
            }
        }
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = [];

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options);
            _meters.Add(meter);
            return meter;
        }

        public void Dispose() => _meters.ForEach(meter => meter.Dispose());
    }
}
