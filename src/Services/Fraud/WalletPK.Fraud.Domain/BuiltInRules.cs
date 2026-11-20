namespace WalletPK.Fraud.Domain;

/// <param name="Id">Stable ID ("velocity-burst").</param>
/// <param name="Name">For the rule list.</param>
/// <param name="Description">What it catches.</param>
/// <param name="Definition">The rule's JSON.</param>
public sealed record RuleSeed(string Id, string Name, string Description, string Definition);

/// <summary>
/// The rules WalletPK starts with (M10.3), seeded once into the rules table, where compliance officers
/// can change or switch them off without a redeploy. Scores add up: review at 60, block at 90.
/// </summary>
public static class BuiltInRules
{
    public static IReadOnlyList<RuleSeed> All { get; } =
    [
        new(
            "velocity-burst",
            "Velocity: 10 payments in 60 seconds",
            "Ten or more payments in a minute: a script or someone draining the wallet.",
            """
            {
              "when": { "feature": "count_60s", "op": "gte", "value": 10 },
              "score": 60,
              "action": "Review",
              "reason": "Too many payments in a short time. Our team will check them; please try again later."
            }
            """),
        new(
            "new-device-large",
            "New device and a large payment",
            "Rs. 10,000 or more from a phone trusted less than 24 hours ago, on an account older than a day (a SIM swap looks like this).",
            """
            {
              "types": ["P2PTransfer", "BankTransfer", "CashOut", "MerchantPayment"],
              "when": { "all": [
                { "feature": "device_age_hours", "op": "lt", "value": 24 },
                { "feature": "account_age_hours", "op": "gte", "value": 24 },
                { "feature": "amount_paisa", "op": "gte", "value": 1000000 }
              ] },
              "score": 60,
              "reason": "A large payment from a new phone needs a check by our team."
            }
            """),
        new(
            "sim-swap-drain",
            "SIM-swap drain",
            "A phone trusted less than 6 hours ago sends a third payment within the hour, or a big total: block and freeze.",
            """
            {
              "types": ["P2PTransfer", "BankTransfer", "CashOut"],
              "when": { "all": [
                { "feature": "device_age_hours", "op": "lt", "value": 6 },
                { "feature": "account_age_hours", "op": "gte", "value": 24 },
                { "any": [
                  { "feature": "count_1h", "op": "gte", "value": 3 },
                  { "feature": "sum_1h_paisa", "op": "gte", "value": 2500000 }
                ] }
              ] },
              "score": 90,
              "action": "Block",
              "reason": "For your safety your wallet has been frozen. Please call WalletPK support."
            }
            """),
        new(
            "mule-fan-in",
            "Many senders to one wallet",
            "Five or more different users paid the same wallet in an hour: it may be a mule collecting stolen money.",
            """
            {
              "types": ["P2PTransfer"],
              "when": { "feature": "recipient_senders_1h", "op": "gte", "value": 5 },
              "score": 40,
              "reason": "Many people have paid this wallet in the last hour. Make sure you know who you are paying."
            }
            """),
        new(
            "odd-hours-large",
            "Large payment at night",
            "Rs. 25,000 or more between 1 am and 5 am Pakistan time.",
            """
            {
              "when": { "all": [
                { "feature": "hour_pkt", "op": "between", "value": [1, 4] },
                { "feature": "amount_paisa", "op": "gte", "value": 2500000 }
              ] },
              "score": 25
            }
            """),
        new(
            "new-recipient-large",
            "Large payment to a new recipient",
            "Rs. 25,000 or more to someone the user has never paid.",
            """
            {
              "types": ["P2PTransfer", "BankTransfer"],
              "when": { "all": [
                { "feature": "recipient_known", "op": "eq", "value": false },
                { "feature": "amount_paisa", "op": "gte", "value": 2500000 }
              ] },
              "score": 30,
              "reason": "This is a large payment to someone you have not paid before."
            }
            """),
        new(
            "repeat-after-flag",
            "Trying again after a refusal",
            "Another payment within the hour after one was held or blocked for risk.",
            """
            {
              "when": { "feature": "flagged_recently", "op": "eq", "value": true },
              "score": 30
            }
            """),
    ];
}
