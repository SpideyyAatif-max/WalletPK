using System.Text.Json;
using WalletPK.Common.Identifiers;

namespace WalletPK.Common.UnitTests.Identifiers;

public class StronglyTypedIdTests
{
    [Fact]
    public void New_creates_unique_version_7_ids()
    {
        var first = WalletId.New();
        var second = WalletId.New();

        first.ShouldNotBe(second);
        first.Value.Version.ShouldBe(7);
    }

    [Fact]
    public void Ids_with_the_same_guid_are_equal()
    {
        var guid = Guid.CreateVersion7();

        UserId.From(guid).ShouldBe(new UserId(guid));
    }

    [Fact]
    public void Serialises_to_json_as_a_plain_string()
    {
        var id = TransactionId.New();

        var json = JsonSerializer.Serialize(new Payload(id));
        var roundTripped = JsonSerializer.Deserialize<Payload>(json);

        json.ShouldBe($$"""{"Id":"{{id.Value}}"}""");
        roundTripped.ShouldNotBeNull();
        roundTripped.Id.ShouldBe(id);
    }

    [Fact]
    public void TryParse_accepts_guids_and_rejects_anything_else()
    {
        var id = WalletId.New();

        WalletId.TryParse(id.ToString(), null, out var parsed).ShouldBeTrue();
        parsed.ShouldBe(id);
        WalletId.TryParse("not-a-guid", null, out _).ShouldBeFalse();
        WalletId.TryParse(null, null, out _).ShouldBeFalse();
    }

    [Fact]
    public void Empty_is_the_default_value()
    {
        WalletId.Empty.Value.ShouldBe(Guid.Empty);
    }

    public sealed record Payload(TransactionId Id);
}
