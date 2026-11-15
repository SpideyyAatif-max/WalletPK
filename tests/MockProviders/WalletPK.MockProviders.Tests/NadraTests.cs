using System.Net;
using System.Net.Http.Json;
using WalletPK.MockProviders.Tests.Infrastructure;
using static WalletPK.MockProviders.Tests.Infrastructure.TestKit;

namespace WalletPK.MockProviders.Tests;

/// <summary>M5.5: mock NADRA.</summary>
[Collection(MockProvidersCollection.Name)]
public sealed class NadraTests(MockProvidersFactory factory)
{
    private async Task<(HttpStatusCode Status, string? Result, double? Score)> VerifyAsync(string cnic, string name, string dateOfBirth)
    {
        using var client = factory.Client();
        using var response = await client.PostJsonAsync("/nadra/v1/verifications", new { cnic, fullName = name, dateOfBirth });
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return (response.StatusCode, null, null);
        }

        var body = await response.JsonAsync();
        return (response.StatusCode,
            body.GetProperty("result").GetString(),
            body.GetProperty("nameMatchScore").ValueKind == System.Text.Json.JsonValueKind.Number ? body.GetProperty("nameMatchScore").GetDouble() : null);
    }

    private static string RandomCnic() => $"35202{Random.Shared.Next(1_000_000, 9_999_999)}{Random.Shared.Next(0, 10)}";

    [Fact]
    public async Task The_test_prefixes_work_as_in_identity()
    {
        (await VerifyAsync("70000-1234567-1", "Ali Khan", "1990-01-01")).Result.ShouldBe("NotFound");
        (await VerifyAsync("71111-1234567-1", "Ali Khan", "1990-01-01")).Result.ShouldBe("Mismatch");
        (await VerifyAsync("72222-1234567-1", "Ali Khan", "1990-01-01")).Status.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await VerifyAsync(RandomCnic(), "Anyone At All", "1990-01-01")).Result.ShouldBe("Verified");
    }

    [Fact]
    public async Task A_stored_record_must_match_by_name_and_exact_date_of_birth()
    {
        var cnic = RandomCnic();
        using var admin = factory.Admin();
        using (var stored = await admin.PutAsJsonAsync(new Uri($"/admin/nadra/citizens/{cnic}", UriKind.Relative), new { fullName = "Muhammad Ali Khan", dateOfBirth = "1992-03-14" }, Cancel))
        {
            stored.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await VerifyAsync(cnic, "muhammad ali khan", "1992-03-14")).Result.ShouldBe("Verified");
        (await VerifyAsync(cnic, "Mohammad Ali Khan", "1992-03-14")).Result.ShouldBe("Verified");
        (await VerifyAsync(cnic, "Ali Khan Muhammad", "1992-03-14")).Result.ShouldBe("Verified");
        (await VerifyAsync(cnic, "Muhammad Ali Khaan", "1992-03-14")).Result.ShouldBe("Verified");

        var stranger = await VerifyAsync(cnic, "Ayesha Siddiqui", "1992-03-14");
        stranger.Result.ShouldBe("Mismatch");
        stranger.Score!.Value.ShouldBeLessThan(0.85);
        (await VerifyAsync(cnic, "Muhammad Ali Khan", "1992-03-15")).Result.ShouldBe("Mismatch");

        using (var cancelled = await admin.PutAsJsonAsync(new Uri($"/admin/nadra/citizens/{cnic}", UriKind.Relative), new { fullName = "Muhammad Ali Khan", dateOfBirth = "1992-03-14", status = "Cancelled" }, Cancel))
        {
            cancelled.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await VerifyAsync(cnic, "Muhammad Ali Khan", "1992-03-14")).Result.ShouldBe("NotFound");
        (await admin.GetJsonAsync($"/admin/nadra/citizens/{cnic}")).GetProperty("status").GetString().ShouldBe("Cancelled");
    }

    [Fact]
    public async Task Malformed_requests_are_refused()
    {
        (await VerifyAsync("12345", "Ali Khan", "1990-01-01")).Status.ShouldBe(HttpStatusCode.BadRequest);
        (await VerifyAsync(RandomCnic(), " ", "1990-01-01")).Status.ShouldBe(HttpStatusCode.BadRequest);
    }
}
