namespace WalletPK.ArchitectureTests;

/// <summary>
/// Clean Architecture inside each service (M0.2): Domain ← Application ← Infrastructure ← Api.
/// The project references already stop an inner layer from naming an outer one; these rules also keep
/// frameworks out of the inner layers.
/// </summary>
public class CleanArchitectureTests
{
    [Theory]
    [MemberData(nameof(Layers.ServiceData), MemberType = typeof(Layers))]
    public void The_domain_depends_on_no_other_layer_and_no_framework(string service)
    {
        Layers.TypesOf(service, "Domain").ShouldNot().HaveDependencyOnAny(
                $"WalletPK.{service}.Application",
                $"WalletPK.{service}.Infrastructure",
                $"WalletPK.{service}.Api",
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore",
                "Npgsql",
                "Confluent.Kafka",
                "StackExchange.Redis",
                "WalletPK.Persistence",
                "WalletPK.Messaging.Kafka",
                "WalletPK.Web")
            .GetResult()
            .ShouldPass($"{service}.Domain must be plain C# (entities, value objects, rules)");
    }

    [Theory]
    [MemberData(nameof(Layers.ServiceData), MemberType = typeof(Layers))]
    public void The_application_layer_uses_ports_not_infrastructure(string service)
    {
        Layers.TypesOf(service, "Application").ShouldNot().HaveDependencyOnAny(
                $"WalletPK.{service}.Infrastructure",
                $"WalletPK.{service}.Api",
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore",
                "Npgsql",
                "Confluent.Kafka",
                "StackExchange.Redis",
                "WalletPK.Messaging.Kafka")
            .GetResult()
            .ShouldPass($"{service}.Application must reach databases, brokers and HTTP only through its ports");
    }

    [Theory]
    [MemberData(nameof(Layers.ServiceData), MemberType = typeof(Layers))]
    public void Infrastructure_does_not_depend_on_the_api(string service)
    {
        Layers.TypesOf(service, "Infrastructure").ShouldNot().HaveDependencyOn($"WalletPK.{service}.Api")
            .GetResult()
            .ShouldPass($"{service}.Infrastructure must not know its host");
    }

    [Theory]
    [MemberData(nameof(Layers.ServiceData), MemberType = typeof(Layers))]
    public void Endpoints_go_through_the_application_not_the_database(string service)
    {
        Layers.TypesOf(service, "Api")
            .That().ResideInNamespace($"WalletPK.{service}.Api.Endpoints")
            .ShouldNot().HaveDependencyOnAny("Microsoft.EntityFrameworkCore", "Npgsql", "Confluent.Kafka", "StackExchange.Redis")
            .GetResult()
            .ShouldPass($"{service}'s endpoints must call application services, not storage or brokers");
    }

    [Theory]
    [MemberData(nameof(Layers.ServiceData), MemberType = typeof(Layers))]
    public void Infrastructure_implementations_are_internal_or_sealed(string service)
    {
        Layers.TypesOf(service, "Infrastructure")
            .That().AreClasses().And().AreNotAbstract().And().AreNotStatic().And().DoNotResideInNamespaceEndingWith(".Migrations").And().DoNotHaveNameMatching("^<")
            .Should().BeSealed()
            .GetResult()
            .ShouldPass($"{service}.Infrastructure classes are not designed for inheritance (generated migrations aside)");
    }

    [Fact]
    public void The_rules_see_real_dependencies()
    {
        // A control: if assembly loading or matching broke, every "should not" rule above would pass vacuously.
        Layers.TypesOf("Ledger", "Infrastructure").That().HaveDependencyOn("Microsoft.EntityFrameworkCore").GetTypes().ShouldNotBeEmpty();
        Layers.TypesOf("Ledger", "Application").That().HaveDependencyOn("WalletPK.Ledger.Domain").GetTypes().ShouldNotBeEmpty();
        Layers.TypesOf("Payments", "Api").That().ResideInNamespace("WalletPK.Payments.Api.Endpoints").GetTypes().ShouldNotBeEmpty();
    }
}
