using System.Text;
using WalletPK.Web.Security;

namespace WalletPK.Web.UnitTests.Security;

public class WebhookSignatureTests
{
    private const string Secret = "raast-shared-secret";
    private const long Timestamp = 1_794_135_411;
    private static readonly byte[] _body = Encoding.UTF8.GetBytes("""{"transferId":"t-1","status":"SETTLED"}""");

    [Fact]
    public void A_correct_signature_verifies()
    {
        var signature = WebhookSignature.Compute(Secret, Timestamp, _body);

        signature.ShouldStartWith("sha256=");
        signature.Length.ShouldBe("sha256=".Length + 64);
        WebhookSignature.Verify(Secret, Timestamp, _body, signature).ShouldBeTrue();
    }

    [Fact]
    public void A_changed_body_fails()
    {
        var signature = WebhookSignature.Compute(Secret, Timestamp, _body);
        var tampered = Encoding.UTF8.GetBytes("""{"transferId":"t-1","status":"FAILED"}""");

        WebhookSignature.Verify(Secret, Timestamp, tampered, signature).ShouldBeFalse();
    }

    [Fact]
    public void A_replayed_body_with_a_new_timestamp_fails()
    {
        var signature = WebhookSignature.Compute(Secret, Timestamp, _body);

        WebhookSignature.Verify(Secret, Timestamp + 600, _body, signature).ShouldBeFalse();
    }

    [Theory]
    [InlineData("other-secret")]
    [InlineData("raast-shared-secreT")]
    public void A_different_secret_fails(string otherSecret)
    {
        var signature = WebhookSignature.Compute(otherSecret, Timestamp, _body);

        WebhookSignature.Verify(Secret, Timestamp, _body, signature).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256=deadbeef")]
    public void Missing_or_malformed_signatures_fail(string? signature)
    {
        WebhookSignature.Verify(Secret, Timestamp, _body, signature).ShouldBeFalse();
    }
}
