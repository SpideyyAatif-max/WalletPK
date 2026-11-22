namespace WalletPK.Contracts.Tests;

/// <summary>
/// gRPC contracts (identity/v1/limits.proto, fraud/v1/risk.proto). Field numbers are the wire format: a field
/// may be added, never renumbered, renamed or retyped, and a removed number must stay reserved.
/// </summary>
public class ProtoContractTests
{
    private const string File = "protos.json";

    [Fact]
    public void Recorded_protos_change_only_in_backward_compatible_ways()
    {
        var recorded = Snapshots.Read<SortedDictionary<string, ProtoFile>>(File);
        recorded.ShouldNotBeNull($"Snapshots/{File} is missing: run the tests with {Snapshots.UpdateVariable}=1 to record the contracts");

        Breaking(recorded, SchemaDescriber.Protos()).ShouldBeEmpty();
    }

    [Fact]
    public void The_recorded_protos_are_up_to_date()
    {
        var current = SchemaDescriber.Protos();
        current.Keys.ShouldBe(["fraud/v1/risk.proto", "identity/v1/limits.proto"]);
        var recorded = Snapshots.Read<SortedDictionary<string, ProtoFile>>(File);
        if (Snapshots.Updating)
        {
            (recorded is null ? [] : Breaking(recorded, current)).ShouldBeEmpty("not recorded: add a new message, method or package version instead");
            Snapshots.Write(File, current);
            return;
        }

        recorded.ShouldNotBeNull();
        Snapshots.Normalized(current).ShouldBe(
            Snapshots.Normalized(recorded),
            $"The protos changed compatibly but are not recorded. Run the tests with {Snapshots.UpdateVariable}=1 and commit Snapshots/{File}.");
    }

    internal static List<string> Breaking(SortedDictionary<string, ProtoFile> recorded, SortedDictionary<string, ProtoFile> current)
    {
        var problems = new List<string>();
        foreach (var (name, before) in recorded)
        {
            if (!current.TryGetValue(name, out var now))
            {
                problems.Add($"{name}: removed");
                continue;
            }

            foreach (var (method, signature) in before.Methods)
            {
                if (!now.Methods.TryGetValue(method, out var nowSignature))
                {
                    problems.Add($"{method}: removed");
                }
                else if (nowSignature != signature)
                {
                    problems.Add($"{method}: {signature} → {nowSignature}");
                }
            }

            foreach (var (message, fields) in before.Messages)
            {
                if (!now.Messages.TryGetValue(message, out var nowMessage))
                {
                    problems.Add($"{message}: removed");
                    continue;
                }

                foreach (var (number, field) in fields.Fields)
                {
                    if (!nowMessage.Fields.TryGetValue(number, out var nowField))
                    {
                        problems.Add($"{message} field {number} ({field}): removed; keep it, or mark the number reserved and record that");
                    }
                    else if (nowField != field)
                    {
                        problems.Add($"{message} field {number}: {field} → {nowField}");
                    }
                }
            }
        }

        return problems;
    }
}
