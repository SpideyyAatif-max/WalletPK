using System.Reflection;
using NetArchTest.Rules;

namespace WalletPK.ArchitectureTests;

/// <summary>The services and their layers, loaded from the referenced projects.</summary>
public static class Layers
{
    public static readonly string[] Services = ["Identity", "Ledger", "Payments", "Bills", "TopupMerchant", "Agents", "Fraud", "Notifications"];

    public static readonly string[] BuildingBlocks =
        ["Common", "Contracts", "Messaging", "Messaging.Kafka", "Observability", "Persistence", "Resilience", "Sagas", "Web"];

    public static Assembly Load(string service, string layer) => Assembly.Load($"WalletPK.{service}.{layer}");

    public static Assembly BuildingBlock(string name) => Assembly.Load($"WalletPK.{name}");

    public static Types TypesOf(string service, string layer) => Types.InAssembly(Load(service, layer));

    public static TheoryData<string> ServiceData() => [.. Services];

    public static TheoryData<string> BuildingBlockData() => [.. BuildingBlocks];

    /// <summary>A failed rule, listing the offending types.</summary>
    public static void ShouldPass(this NetArchTest.Rules.TestResult result, string because)
    {
        ArgumentNullException.ThrowIfNull(result);
        result.IsSuccessful.ShouldBeTrue($"{because}. Offending types: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }
}
