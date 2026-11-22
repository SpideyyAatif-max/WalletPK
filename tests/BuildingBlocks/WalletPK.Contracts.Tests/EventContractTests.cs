using System.Reflection;
using System.Text.RegularExpressions;
using WalletPK.Messaging;
using WalletPK.Messaging.Serialization;

namespace WalletPK.Contracts.Tests;

/// <summary>
/// Kafka event contracts (M15.5). Producers and consumers deploy separately, so a recorded event may only
/// gain optional fields; renaming, removing or retyping a field, or adding a required one, needs a new
/// version of the event.
/// </summary>
public partial class EventContractTests
{
    private const string File = "events.json";

    [GeneratedRegex("^[a-z]+\\.[a-z][a-z0-9_]*$")]
    private static partial Regex EventName();

    [Fact]
    public void Every_event_has_a_unique_name_a_version_and_a_known_topic()
    {
        var topics = typeof(KafkaTopics).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet();

        var events = SchemaDescriber.Events();

        events.Count.ShouldBe(SchemaDescriber.EventTypes().Count, "two events share a name");
        events.Values.ShouldAllBe(schema => EventName().IsMatch(schema.Name), "names are lowercase \"<area>.<fact>\"");
        events.Values.ShouldAllBe(schema => schema.Version >= 1);
        events.Values.Where(schema => !topics.Contains(schema.Topic)).Select(schema => $"{schema.Name} → {schema.Topic}").ShouldBeEmpty("every topic is one of KafkaTopics");

        // The registry every service builds from this assembly accepts all of them.
        Should.NotThrow(() => new IntegrationEventRegistry().RegisterFromAssembly(SchemaDescriber.Contracts));
    }

    [Fact]
    public void Recorded_events_change_only_in_backward_compatible_ways()
    {
        var recorded = Snapshots.Read<SortedDictionary<string, EventSchema>>(File);
        recorded.ShouldNotBeNull($"Snapshots/{File} is missing: run the tests with {Snapshots.UpdateVariable}=1 to record the contracts");

        Breaking(recorded, SchemaDescriber.Events()).ShouldBeEmpty();
    }

    [Fact]
    public void The_recorded_events_are_up_to_date()
    {
        var current = SchemaDescriber.Events();
        var recorded = Snapshots.Read<SortedDictionary<string, EventSchema>>(File);
        if (Snapshots.Updating)
        {
            var breaking = recorded is null ? [] : Breaking(recorded, current);
            breaking.ShouldBeEmpty("not recorded: raise the event's Version for a breaking change");
            Snapshots.Write(File, current);
            return;
        }

        recorded.ShouldNotBeNull();
        Snapshots.Normalized(current).ShouldBe(
            Snapshots.Normalized(recorded),
            $"The events changed compatibly but are not recorded. Run the tests with {Snapshots.UpdateVariable}=1 and commit Snapshots/{File}.");
    }

    /// <summary>What a consumer built against <paramref name="recorded"/> would trip over.</summary>
    internal static List<string> Breaking(SortedDictionary<string, EventSchema> recorded, SortedDictionary<string, EventSchema> current)
    {
        var problems = new List<string>();
        foreach (var (name, before) in recorded)
        {
            if (!current.TryGetValue(name, out var now))
            {
                problems.Add($"{name}: removed (consumers may still subscribe to it)");
                continue;
            }

            if (now.Version < before.Version)
            {
                problems.Add($"{name}: version went down from {before.Version} to {now.Version}");
            }

            if (now.Version != before.Version)
            {
                continue;
            }

            if (now.Topic != before.Topic)
            {
                problems.Add($"{name}: moved from {before.Topic} to {now.Topic}");
            }

            foreach (var (field, shape) in before.Fields)
            {
                if (!now.Fields.TryGetValue(field, out var nowShape))
                {
                    problems.Add($"{name}.{field}: removed");
                }
                else if (nowShape != shape)
                {
                    problems.Add($"{name}.{field}: {shape} → {nowShape}");
                }
            }

            foreach (var (field, shape) in now.Fields.Where(field => !before.Fields.ContainsKey(field.Key)))
            {
                if (!shape.EndsWith('?'))
                {
                    problems.Add($"{name}.{field}: added as required ({shape}); older producers do not send it, so make it nullable");
                }
            }
        }

        return problems;
    }
}
