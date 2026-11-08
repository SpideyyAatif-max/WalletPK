using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using WalletPK.Common.Results;
using WalletPK.Web.Correlation;
using WalletPK.Web.Errors;
using WalletPK.Web.UnitTests.Infrastructure;

namespace WalletPK.Web.UnitTests.Errors;

public class ProblemDetailsTests
{
    private static Task<WebApplication> StartAppAsync(string environment = "Production") =>
        TestApp.StartAsync(
            app =>
            {
                app.MapGet("/errors/{type}", (string type) =>
                    new Error($"test.{type.ToLowerInvariant()}", $"A {type} error.", Enum.Parse<ErrorType>(type)).ToProblem());

                app.MapGet("/validation", () =>
                    Error.Validation(
                        "transfer.invalid",
                        "The transfer request is invalid.",
                        new Dictionary<string, string[]> { ["amount"] = ["Amount must be at least Rs. 1."] }).ToProblem());

                app.MapGet("/ok", () => Result.Success(42).ToHttpResult());
                app.MapGet("/no-content", () => Result.Success().ToHttpResult());
                app.MapGet("/boom", IResult () => throw new InvalidOperationException("secret connection string"));
            },
            environment);

    [Theory]
    [InlineData(nameof(ErrorType.Validation), HttpStatusCode.BadRequest)]
    [InlineData(nameof(ErrorType.Unauthorized), HttpStatusCode.Unauthorized)]
    [InlineData(nameof(ErrorType.Forbidden), HttpStatusCode.Forbidden)]
    [InlineData(nameof(ErrorType.NotFound), HttpStatusCode.NotFound)]
    [InlineData(nameof(ErrorType.Conflict), HttpStatusCode.Conflict)]
    [InlineData(nameof(ErrorType.BusinessRule), HttpStatusCode.UnprocessableEntity)]
    [InlineData(nameof(ErrorType.Unavailable), HttpStatusCode.ServiceUnavailable)]
    [InlineData(nameof(ErrorType.TooManyRequests), HttpStatusCode.TooManyRequests)]
    [InlineData(nameof(ErrorType.Failure), HttpStatusCode.InternalServerError)]
    public async Task Each_error_type_maps_to_its_http_status(string type, HttpStatusCode expected)
    {
        await using var app = await StartAppAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(new Uri($"/errors/{type}", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await ReadJsonAsync(response);

        response.StatusCode.ShouldBe(expected);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        body.GetProperty("code").GetString().ShouldBe($"test.{type.ToLowerInvariant()}");
        body.GetProperty("status").GetInt32().ShouldBe((int)expected);
    }

    [Fact]
    public async Task Every_problem_carries_trace_and_correlation_ids()
    {
        await using var app = await StartAppAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(CorrelationIdMiddleware.HeaderName, "corr-42");

        using var response = await client.GetAsync(new Uri("/errors/BusinessRule", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await ReadJsonAsync(response);

        body.GetProperty("correlationId").GetString().ShouldBe("corr-42");
        body.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
        body.GetProperty("instance").GetString().ShouldBe("GET /errors/BusinessRule");
    }

    [Fact]
    public async Task Validation_errors_list_the_failing_fields()
    {
        await using var app = await StartAppAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(new Uri("/validation", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await ReadJsonAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        body.GetProperty("code").GetString().ShouldBe("transfer.invalid");
        body.GetProperty("errors").GetProperty("amount")[0].GetString().ShouldBe("Amount must be at least Rs. 1.");
    }

    [Fact]
    public async Task Successful_results_become_200_or_204()
    {
        await using var app = await StartAppAsync();
        using var client = app.GetTestClient();

        using var ok = await client.GetAsync(new Uri("/ok", UriKind.Relative), TestContext.Current.CancellationToken);
        using var noContent = await client.GetAsync(new Uri("/no-content", UriKind.Relative), TestContext.Current.CancellationToken);

        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ok.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("42");
        noContent.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Unhandled_exceptions_return_a_safe_500_in_production()
    {
        await using var app = await StartAppAsync("Production");
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(new Uri("/boom", UriKind.Relative), TestContext.Current.CancellationToken);
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        raw.ShouldNotContain("secret connection string");
        raw.ShouldNotContain("InvalidOperationException");
        raw.ShouldContain("correlationId");
    }

    [Fact]
    public async Task Unhandled_exceptions_show_the_message_in_development()
    {
        await using var app = await StartAppAsync("Development");
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(new Uri("/boom", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await ReadJsonAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        body.GetProperty("detail").GetString().ShouldBe("secret connection string");
    }

    [Fact]
    public async Task Unknown_routes_return_a_problem_404()
    {
        await using var app = await StartAppAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(new Uri("/does-not-exist", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
}
