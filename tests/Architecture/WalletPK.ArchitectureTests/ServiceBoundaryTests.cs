using NetArchTest.Rules;

namespace WalletPK.ArchitectureTests;

/// <summary>
/// Microservice boundaries: a service talks to another only through WalletPK.Contracts (events, commands,
/// gRPC) and HTTP, never by using its code; the building blocks know no service.
/// </summary>
public class ServiceBoundaryTests
{
    [Theory]
    [MemberData(nameof(Layers.ServiceData), MemberType = typeof(Layers))]
    public void A_service_uses_no_other_services_code(string service)
    {
        string[] others = [.. Layers.Services.Where(other => other != service).Select(other => $"WalletPK.{other}.")];
        foreach (var layer in new[] { "Domain", "Application", "Infrastructure", "Api" })
        {
            Layers.TypesOf(service, layer).ShouldNot().HaveDependencyOnAny(others)
                .GetResult()
                .ShouldPass($"{service}.{layer} must reach other services only through contracts");
        }
    }

    [Theory]
    [MemberData(nameof(Layers.BuildingBlockData), MemberType = typeof(Layers))]
    public void Building_blocks_know_no_service(string buildingBlock)
    {
        Types.InAssembly(Layers.BuildingBlock(buildingBlock))
            .ShouldNot().HaveDependencyOnAny([.. Layers.Services.Select(service => $"WalletPK.{service}."), "WalletPK.Gateway"])
            .GetResult()
            .ShouldPass($"WalletPK.{buildingBlock} is shared by every service and must not depend on one");
    }

    [Fact]
    public void Contracts_are_plain_messages()
    {
        Types.InAssembly(Layers.BuildingBlock("Contracts"))
            .ShouldNot().HaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Confluent.Kafka", "WalletPK.Persistence", "WalletPK.Messaging.Kafka", "WalletPK.Web", "WalletPK.Sagas")
            .GetResult()
            .ShouldPass("WalletPK.Contracts is what services agree on; it carries no infrastructure");
    }

    [Fact]
    public void The_gateway_routes_and_uses_no_service_code()
    {
        Types.InAssembly(System.Reflection.Assembly.Load("WalletPK.Gateway"))
            .ShouldNot().HaveDependencyOnAny([.. Layers.Services.Select(service => $"WalletPK.{service}.")])
            .GetResult()
            .ShouldPass("The gateway only forwards requests");
    }
}
