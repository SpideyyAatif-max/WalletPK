namespace WalletPK.Ledger.Infrastructure.ReadModels;

/// <summary>Read-model settings, from "Ledger:ReadModels".</summary>
public sealed class ReadModelOptions
{
    public const string SectionName = "Ledger:ReadModels";

    /// <summary>Run the projection (the consumer that fills the read models). Queries work either way.</summary>
    public bool ProjectionEnabled { get; set; } = true;

    /// <summary>How often each instance checks whether a rebuild started a new generation.</summary>
    public int GenerationPollMilliseconds { get; set; } = 2_000;

    /// <summary>The projection's Kafka consumer group is "{prefix}-g{generation}".</summary>
    public string ConsumerGroupPrefix { get; set; } = "ledger-read-models";

    public bool IsValid() =>
        GenerationPollMilliseconds >= 100 && !string.IsNullOrWhiteSpace(ConsumerGroupPrefix);

    /// <summary>The consumer group that projects <paramref name="generation"/>.</summary>
    public string ConsumerGroupFor(long generation) => $"{ConsumerGroupPrefix}-g{generation}";

    /// <summary>The generation a consumer group projects, or null when the name is not one of ours.</summary>
    public long? GenerationOf(string consumerGroup)
    {
        ArgumentNullException.ThrowIfNull(consumerGroup);
        var prefix = ConsumerGroupPrefix + "-g";
        return consumerGroup.StartsWith(prefix, StringComparison.Ordinal)
            && long.TryParse(consumerGroup.AsSpan(prefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var generation)
            ? generation
            : null;
    }
}
