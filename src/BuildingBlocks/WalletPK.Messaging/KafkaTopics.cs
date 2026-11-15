namespace WalletPK.Messaging;

/// <summary>
/// Every WalletPK Kafka topic. Must match ops/kafka/create-topics.sh, which creates them
/// (a unit test checks the two stay in sync).
/// </summary>
public static class KafkaTopics
{
    public const string IdentityEvents = "identity.events";
    public const string LedgerEvents = "ledger.events";

    /// <summary>Requests to the Ledger from Sagas: place, capture or release a hold (M6).</summary>
    public const string LedgerCommands = "ledger.commands";

    /// <summary>The Ledger's answers to <see cref="LedgerCommands"/>, keyed by hold ID.</summary>
    public const string LedgerReplies = "ledger.replies";
    public const string PaymentsEvents = "payments.events";
    public const string BillsEvents = "bills.events";
    public const string TopupEvents = "topup.events";
    public const string AgentsEvents = "agents.events";
    public const string FraudAlerts = "fraud.alerts";
    public const string NotificationsEvents = "notifications.events";

    public const string PaymentsDeadLetter = "payments.dlq";
    public const string BillsDeadLetter = "bills.dlq";
    public const string TopupDeadLetter = "topup.dlq";

    public const string SimulationMetrics = "sim.metrics";

    public static IReadOnlyList<string> All { get; } =
    [
        IdentityEvents, LedgerEvents, LedgerCommands, LedgerReplies, PaymentsEvents, BillsEvents, TopupEvents, AgentsEvents,
        FraudAlerts, NotificationsEvents, PaymentsDeadLetter, BillsDeadLetter, TopupDeadLetter,
        SimulationMetrics,
    ];
}
