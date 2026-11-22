namespace WalletPK.Contracts.Tests;

/// <summary>The compatibility rules themselves, on made-up schemas.</summary>
public class CompatibilityRuleTests
{
    private static SortedDictionary<string, EventSchema> Events(int version, params (string Field, string Shape)[] fields) =>
        new(StringComparer.Ordinal)
        {
            ["test.thing_happened"] = new("test.thing_happened", version, "test.events", "Test", new(fields.ToDictionary(field => field.Field, field => field.Shape), StringComparer.Ordinal)),
        };

    [Fact]
    public void Removing_retyping_or_adding_a_required_field_breaks_consumers_unless_the_version_is_raised()
    {
        var before = Events(1, ("id", "uuid"), ("amountPaisa", "integer"));

        EventContractTests.Breaking(before, Events(1, ("id", "uuid"), ("amountPaisa", "integer"), ("note", "string?"))).ShouldBeEmpty();
        EventContractTests.Breaking(before, Events(1, ("id", "uuid"))).ShouldHaveSingleItem().ShouldContain("amountPaisa: removed");
        EventContractTests.Breaking(before, Events(1, ("id", "string"), ("amountPaisa", "integer"))).ShouldHaveSingleItem().ShouldContain("uuid → string");
        EventContractTests.Breaking(before, Events(1, ("id", "uuid"), ("amountPaisa", "integer"), ("fee", "integer"))).ShouldHaveSingleItem().ShouldContain("added as required");
        EventContractTests.Breaking(before, Events(2, ("id", "string"))).ShouldBeEmpty();
        EventContractTests.Breaking(before, []).ShouldHaveSingleItem().ShouldContain("removed");
    }

    [Fact]
    public void A_proto_field_keeps_its_number_name_and_type()
    {
        static SortedDictionary<string, ProtoFile> Proto(params (int Number, string Field)[] fields) =>
            new(StringComparer.Ordinal)
            {
                ["x.proto"] = new("x.proto", "x.v1", new(StringComparer.Ordinal) { ["Svc.Get"] = "x.v1.Req -> x.v1.Res" },
                    new(StringComparer.Ordinal) { ["x.v1.Req"] = new("x.v1.Req", new(fields.ToDictionary(field => field.Number, field => field.Field))) }),
            };

        var before = Proto((1, "user_id string"), (2, "amount int64"));

        ProtoContractTests.Breaking(before, Proto((1, "user_id string"), (2, "amount int64"), (3, "note string"))).ShouldBeEmpty();
        ProtoContractTests.Breaking(before, Proto((1, "user_id string"))).ShouldHaveSingleItem().ShouldContain("field 2");
        ProtoContractTests.Breaking(before, Proto((1, "user_id string"), (2, "amount_paisa int64"))).ShouldHaveSingleItem().ShouldContain("amount int64 → amount_paisa int64");
    }
}
