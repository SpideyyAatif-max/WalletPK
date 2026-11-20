namespace WalletPK.Fraud.Domain;

/// <summary>The type of a feature's value, which decides the operators a rule may use on it.</summary>
public enum FeatureKind
{
    Number,
    Bool,
    Text,
}

/// <param name="Name">How rules name it ("count_60s").</param>
/// <param name="Kind">Number, true/false or text.</param>
/// <param name="Description">What it measures.</param>
public sealed record FeatureDefinition(string Name, FeatureKind Kind, string Description);

/// <summary>
/// Every feature a rule can test (M10.1). The numbers come from Redis windows that the scorer keeps per
/// user, device and recipient; the current payment counts in the windows ("the 10th transfer in 60 s"
/// has count_60s = 10).
/// </summary>
public static class FeatureCatalog
{
    public const string AmountPaisa = "amount_paisa";
    public const string TransactionType = "transaction_type";
    public const string HourPkt = "hour_pkt";
    public const string AccountAgeHours = "account_age_hours";
    public const string DeviceAgeHours = "device_age_hours";
    public const string DeviceCoolingOff = "device_cooling_off";
    public const string RecipientKnown = "recipient_known";
    public const string Count60s = "count_60s";
    public const string Count1h = "count_1h";
    public const string Count24h = "count_24h";
    public const string Sum1hPaisa = "sum_1h_paisa";
    public const string Sum24hPaisa = "sum_24h_paisa";
    public const string RecipientSenders1h = "recipient_senders_1h";
    public const string FlaggedRecently = "flagged_recently";

    /// <summary>Used for an age that is not known (a device or account from before the Fraud service saw it): treated as old.</summary>
    public const double UnknownAgeHours = 100_000;

    public static IReadOnlyList<FeatureDefinition> All { get; } =
    [
        new(AmountPaisa, FeatureKind.Number, "What leaves the wallet, in paisa."),
        new(TransactionType, FeatureKind.Text, "P2PTransfer, BankTransfer, BillPayment, MobileTopup, MerchantPayment or CashOut."),
        new(HourPkt, FeatureKind.Number, "Hour of the day in Pakistan, 0-23."),
        new(AccountAgeHours, FeatureKind.Number, "Hours since the user registered (100000 when not known)."),
        new(DeviceAgeHours, FeatureKind.Number, "Hours since this device was trusted (100000 when not known)."),
        new(DeviceCoolingOff, FeatureKind.Bool, "The device is in its new-device cooling-off period."),
        new(RecipientKnown, FeatureKind.Bool, "The user has paid this recipient before (within 90 days)."),
        new(Count60s, FeatureKind.Number, "The user's payments in the last 60 seconds, this one included."),
        new(Count1h, FeatureKind.Number, "The user's payments in the last hour, this one included."),
        new(Count24h, FeatureKind.Number, "The user's payments in the last 24 hours, this one included."),
        new(Sum1hPaisa, FeatureKind.Number, "What the user paid in the last hour, this one included."),
        new(Sum24hPaisa, FeatureKind.Number, "What the user paid in the last 24 hours, this one included."),
        new(RecipientSenders1h, FeatureKind.Number, "Different users who paid this recipient in the last hour, this one included."),
        new(FlaggedRecently, FeatureKind.Bool, "One of the user's payments was held or blocked for risk in the last hour."),
    ];

    private static readonly Dictionary<string, FeatureDefinition> _byName = All.ToDictionary(feature => feature.Name, StringComparer.Ordinal);

    public static FeatureDefinition? Find(string? name) => name is not null && _byName.TryGetValue(name, out var feature) ? feature : null;
}

/// <summary>The feature values for one payment, by name; numbers are doubles, true/false are bools, text is strings.</summary>
public sealed class FeatureVector
{
    private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);

    public FeatureVector Set(string name, double value)
    {
        _values[name] = value;
        return this;
    }

    public FeatureVector Set(string name, bool value)
    {
        _values[name] = value;
        return this;
    }

    public FeatureVector Set(string name, string value)
    {
        _values[name] = value;
        return this;
    }

    public object? this[string name] => _values.GetValueOrDefault(name);

    public IReadOnlyDictionary<string, object> Values => _values;
}
