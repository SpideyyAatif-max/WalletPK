using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WalletPK.Identity.IntegrationTests.Infrastructure;

namespace WalletPK.Identity.IntegrationTests;

/// <summary>Every service publishes an OpenAPI document (M14.A6): the web client and the docs are generated from it.</summary>
[Collection(IdentityCollection.Name)]
public class OpenApiDocumentTests(IdentityApiFactory factory)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_service_serves_its_openapi_document_without_signing_in()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var document = await response.Content.ReadFromJsonAsync<JsonElement>(Cancel);
        document.GetProperty("openapi").GetString().ShouldStartWith("3.");
        document.GetProperty("info").GetProperty("title").GetString().ShouldBe("WalletPK.Identity");
        document.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("Bearer", out _).ShouldBeTrue();

        var paths = document.GetProperty("paths");
        paths.TryGetProperty("/api/identity/limits", out var limits).ShouldBeTrue();
        // Endpoints returning a Result describe their success body and their ProblemDetails errors.
        var responses = limits.GetProperty("get").GetProperty("responses");
        responses.GetProperty("200").GetProperty("content").GetProperty("application/json").TryGetProperty("schema", out _).ShouldBeTrue();
        responses.TryGetProperty("404", out var notFound).ShouldBeTrue();
        notFound.GetProperty("content").TryGetProperty("application/problem+json", out _).ShouldBeTrue();

        // Numbers are numbers (the API never writes them as strings), so generated clients type them as numbers.
        var minorUnits = document.GetProperty("components").GetProperty("schemas").GetProperty("Money").GetProperty("properties").GetProperty("minorUnits");
        minorUnits.GetProperty("type").ValueKind.ShouldBe(JsonValueKind.String);
        minorUnits.GetProperty("type").GetString().ShouldBe("integer");
    }
}
