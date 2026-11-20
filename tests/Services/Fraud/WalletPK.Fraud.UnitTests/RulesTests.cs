using WalletPK.Fraud.Application;
using WalletPK.Fraud.Domain;

namespace WalletPK.Fraud.UnitTests;

public class RuleParsingTests
{
    private static RiskRule Rule(string json)
    {
        var (rule, errors) = RiskRule.Parse("test", "Test", json);
        errors.ShouldBeEmpty();
        return rule!;
    }

    private static IReadOnlyList<string> Errors(string json) => RiskRule.Parse("test", "Test", json).Errors;

    [Fact]
    public void Every_built_in_rule_parses()
    {
        foreach (var seed in BuiltInRules.All)
        {
            RiskRule.Parse(seed.Id, seed.Name, seed.Definition).Errors.ShouldBeEmpty(seed.Id);
        }

        BuiltInRules.All.Select(seed => seed.Id).ShouldBeUnique();
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("{ not json", "Not valid JSON")]
    [InlineData("[]", "JSON object")]
    [InlineData("""{ "score": 10 }""", "\"when\" is missing")]
    [InlineData("""{ "when": { "feature": "count_60s", "op": "gte", "value": 3 } }""", "needs a \"score\"")]
    [InlineData("""{ "when": { "feature": "colour", "op": "eq", "value": "red" }, "score": 5 }""", "unknown feature \"colour\"")]
    [InlineData("""{ "when": { "feature": "count_60s", "op": "in", "value": [1] }, "score": 5 }""", "operator \"in\" does not apply")]
    [InlineData("""{ "when": { "feature": "count_60s", "op": "gte", "value": "ten" }, "score": 5 }""", "must be a number")]
    [InlineData("""{ "when": { "feature": "recipient_known", "op": "eq", "value": 1 }, "score": 5 }""", "must be true or false")]
    [InlineData("""{ "when": { "feature": "amount_paisa", "op": "between", "value": [9, 1] }, "score": 5 }""", "low <= high")]
    [InlineData("""{ "when": { "all": [] }, "score": 5 }""", "non-empty list")]
    [InlineData("""{ "when": { "feature": "count_60s", "op": "gte", "value": 3 }, "score": 500 }""", "0 to 100")]
    [InlineData("""{ "when": { "feature": "count_60s", "op": "gte", "value": 3 }, "action": "Allow" }""", "\"Review\", \"Block\"")]
    [InlineData("""{ "when": { "feature": "count_60s", "op": "gte", "value": 3 }, "score": 5, "types": ["Teleport"] }""", "unknown payment type")]
    [InlineData("""{ "when": { "feature": "count_60s", "op": "gte", "value": 3 }, "score": 5, "colour": "red" }""", "Unknown property \"colour\"")]
    public void Mistakes_are_explained(string json, string expected) =>
        string.Join(" ", Errors(json)).ShouldContain(expected);

    [Fact]
    public void Conditions_nest_all_any_and_not()
    {
        var rule = Rule("""
            {
              "when": { "all": [
                { "feature": "amount_paisa", "op": "between", "value": [100, 1000] },
                { "any": [
                  { "feature": "transaction_type", "op": "in", "value": ["CashOut", "BankTransfer"] },
                  { "not": { "feature": "recipient_known", "op": "eq", "value": true } }
                ] }
              ] },
              "score": 10
            }
            """);

        static FeatureVector Payment(double amount, string type, bool known) => new FeatureVector()
            .Set(FeatureCatalog.AmountPaisa, amount).Set(FeatureCatalog.TransactionType, type).Set(FeatureCatalog.RecipientKnown, known);

        rule.Matches(Payment(500, "CashOut", known: true)).ShouldBeTrue();
        rule.Matches(Payment(500, "P2PTransfer", known: false)).ShouldBeTrue();
        rule.Matches(Payment(500, "P2PTransfer", known: true)).ShouldBeFalse();
        rule.Matches(Payment(5_000, "CashOut", known: false)).ShouldBeFalse();
    }

    [Fact]
    public void A_condition_on_a_missing_feature_is_false() =>
        Rule("""{ "when": { "feature": "count_60s", "op": "lt", "value": 3 }, "score": 5 }""").Matches(new FeatureVector()).ShouldBeFalse();

    [Fact]
    public void Types_limit_a_rule_to_some_payments()
    {
        var rule = Rule("""{ "types": ["CashOut"], "when": { "feature": "amount_paisa", "op": "gt", "value": 0 }, "score": 5 }""");

        rule.Matches(new FeatureVector().Set(FeatureCatalog.AmountPaisa, 10).Set(FeatureCatalog.TransactionType, "CashOut")).ShouldBeTrue();
        rule.Matches(new FeatureVector().Set(FeatureCatalog.AmountPaisa, 10).Set(FeatureCatalog.TransactionType, "P2PTransfer")).ShouldBeFalse();
    }
}

public class RiskPolicyTests
{
    private static readonly IReadOnlyList<RiskRule> Rules =
    [
        .. BuiltInRules.All.Select(seed => RiskRule.Parse(seed.Id, seed.Name, seed.Definition).Rule!),
    ];

    private static readonly DateTimeOffset Noon = new(2026, 11, 20, 7, 0, 0, TimeSpan.Zero); // 12:00 in Pakistan

    private static ScoreInput Payment(long amount, string type = "P2PTransfer", string? to = "+923001112222") =>
        new(Guid.NewGuid(), Guid.NewGuid(), "device-1", type, amount, to);

    private static FeatureSnapshot Snapshot(
        int recentPayments = 0,
        long eachPaisa = 10_000,
        double? deviceHours = null,
        double? accountHours = 24 * 365,
        bool recipientKnown = true,
        int otherSenders = 0,
        bool flagged = false) =>
        new(
            [.. Enumerable.Range(0, recentPayments).Select(i => (Noon.AddSeconds(-1 - i), eachPaisa))],
            deviceHours is { } device ? Noon.AddHours(-device) : null,
            null,
            accountHours is { } account ? Noon.AddHours(-account) : null,
            recipientKnown,
            otherSenders,
            flagged);

    private static RiskResult Score(ScoreInput input, FeatureSnapshot snapshot) =>
        new RiskPolicy().Evaluate(Rules, FeatureBuilder.Build(input, snapshot, Noon));

    [Fact]
    public void An_ordinary_payment_is_allowed_with_no_rules()
    {
        var result = Score(Payment(150_000), Snapshot(recentPayments: 2));

        result.Score.ShouldBe(0);
        result.Action.ShouldBe(RiskAction.Allow);
        result.Rules.ShouldBeEmpty();
        result.Reason.ShouldBeNull();
    }

    [Fact]
    public void The_tenth_payment_in_a_minute_is_held_for_review()
    {
        Score(Payment(10_000), Snapshot(recentPayments: 8)).Action.ShouldBe(RiskAction.Allow);

        var tenth = Score(Payment(10_000), Snapshot(recentPayments: 9));

        tenth.Action.ShouldBe(RiskAction.Review);
        tenth.Rules.ShouldBe(["velocity-burst"]);
        tenth.Reason!.ShouldContain("Too many payments");
    }

    [Fact]
    public void A_large_payment_from_a_new_phone_on_an_old_account_is_held_but_not_on_a_new_account()
    {
        Score(Payment(1_500_000), Snapshot(deviceHours: 2, accountHours: 24 * 30)).Action.ShouldBe(RiskAction.Review);
        Score(Payment(1_500_000), Snapshot(deviceHours: 2, accountHours: 3)).Action.ShouldBe(RiskAction.Allow);
        Score(Payment(500_000), Snapshot(deviceHours: 2, accountHours: 24 * 30)).Action.ShouldBe(RiskAction.Allow);
    }

    [Fact]
    public void A_third_payment_from_a_swapped_sim_within_the_hour_is_blocked()
    {
        var result = Score(Payment(500_000), Snapshot(recentPayments: 2, eachPaisa: 500_000, deviceHours: 1));

        result.Action.ShouldBe(RiskAction.Block);
        result.Rules.ShouldContain("sim-swap-drain");
        result.Score.ShouldBe(90);
    }

    [Fact]
    public void Scores_add_up_and_cross_the_review_threshold()
    {
        // Fan-in (40) and a refusal in the last hour (30): 70, review.
        var result = Score(Payment(20_000), Snapshot(otherSenders: 4, flagged: true));

        result.Score.ShouldBe(70);
        result.Action.ShouldBe(RiskAction.Review);
        result.Rules.ShouldBe(["mule-fan-in", "repeat-after-flag"], ignoreOrder: true);
    }

    [Fact]
    public void A_large_payment_to_a_new_recipient_at_3am_needs_a_one_time_code()
    {
        var threeAm = new DateTimeOffset(2026, 11, 19, 22, 0, 0, TimeSpan.Zero);
        var result = new RiskPolicy().Evaluate(Rules, FeatureBuilder.Build(Payment(3_000_000), Snapshot(recipientKnown: false), threeAm));

        result.Score.ShouldBe(55);
        result.Action.ShouldBe(RiskAction.StepUp);
        result.Rules.ShouldBe(["new-recipient-large", "odd-hours-large"], ignoreOrder: true);
    }

    [Fact]
    public void A_large_payment_at_3am_to_someone_known_is_allowed()
    {
        var threeAm = new DateTimeOffset(2026, 11, 19, 22, 0, 0, TimeSpan.Zero);
        var result = new RiskPolicy().Evaluate(Rules, FeatureBuilder.Build(Payment(3_000_000), Snapshot(recipientKnown: true), threeAm));

        result.Score.ShouldBe(25);
        result.Action.ShouldBe(RiskAction.Allow);
    }

    [Theory]
    [InlineData(29, RiskAction.Allow)]
    [InlineData(30, RiskAction.StepUp)]
    [InlineData(59, RiskAction.StepUp)]
    [InlineData(60, RiskAction.Review)]
    [InlineData(90, RiskAction.Block)]
    public void The_thresholds_pick_the_action(int score, RiskAction expected)
    {
        var rule = RiskRule.Parse("s", "S", $$"""{ "when": { "feature": "amount_paisa", "op": "gt", "value": 0 }, "score": {{score}} }""").Rule!;

        new RiskPolicy().Evaluate([rule], FeatureBuilder.Build(Payment(1_000), Snapshot(), Noon)).Action.ShouldBe(expected);
    }

    [Fact]
    public void A_one_time_code_lifts_a_step_up_but_not_a_review_or_a_block()
    {
        var stepUp = new RiskResult(35, RiskAction.StepUp, ["new-recipient-large"], "Confirm it");

        var confirmed = RiskPolicy.Confirmed(stepUp);

        confirmed.Action.ShouldBe(RiskAction.Allow);
        confirmed.Rules.ShouldBe(["new-recipient-large"]);
        RiskPolicy.Confirmed(stepUp with { Action = RiskAction.Review }).Action.ShouldBe(RiskAction.Review);
        RiskPolicy.Confirmed(stepUp with { Action = RiskAction.Block }).Action.ShouldBe(RiskAction.Block);
    }

    [Fact]
    public void A_rule_can_ask_for_a_one_time_code()
    {
        var (rule, errors) = RiskRule.Parse("otp", "OTP", """{ "when": { "feature": "amount_paisa", "op": "gte", "value": 100 }, "action": "StepUp" }""");

        errors.ShouldBeEmpty();
        new RiskPolicy().Evaluate([rule!], FeatureBuilder.Build(Payment(1_000), Snapshot(), Noon)).Action.ShouldBe(RiskAction.StepUp);
    }

    [Fact]
    public void A_rule_action_forces_at_least_that_action_and_the_total_is_capped_at_100()
    {
        var block = RiskRule.Parse("b", "B", """{ "when": { "feature": "amount_paisa", "op": "gt", "value": 0 }, "score": 70, "action": "Block" }""").Rule!;
        var more = RiskRule.Parse("m", "M", """{ "when": { "feature": "amount_paisa", "op": "gt", "value": 0 }, "score": 70 }""").Rule!;

        var result = new RiskPolicy().Evaluate([block, more], new FeatureVector().Set(FeatureCatalog.AmountPaisa, 1));

        result.Score.ShouldBe(100);
        result.Action.ShouldBe(RiskAction.Block);
    }

    [Fact]
    public void Unknown_ages_count_as_old()
    {
        var vector = FeatureBuilder.Build(Payment(100), Snapshot(deviceHours: null, accountHours: null), Noon);

        vector[FeatureCatalog.DeviceAgeHours].ShouldBe(FeatureCatalog.UnknownAgeHours);
        vector[FeatureCatalog.AccountAgeHours].ShouldBe(FeatureCatalog.UnknownAgeHours);
        vector[FeatureCatalog.Count60s].ShouldBe(1d);
        vector[FeatureCatalog.RecipientSenders1h].ShouldBe(1d);
        vector[FeatureCatalog.HourPkt].ShouldBe(12d);
    }
}

public class AuditChainTests
{
    [Fact]
    public void Each_hash_covers_the_entry_and_the_one_before()
    {
        var at = AuditChain.Truncate(DateTimeOffset.UtcNow);
        var first = AuditChain.Hash(AuditChain.Genesis, "user:1", "account.freeze", "user:2", "{}", at);
        var second = AuditChain.Hash(first, "user:1", "account.unfreeze", "user:2", "{}", at);

        first.Length.ShouldBe(64);
        AuditChain.Hash(AuditChain.Genesis, "user:1", "account.freeze", "user:2", "{}", at).ShouldBe(first);
        AuditChain.Hash(AuditChain.Genesis, "user:1", "account.freeze", "user:3", "{}", at).ShouldNotBe(first);
        AuditChain.Hash(AuditChain.Genesis, "user:1", "account.unfreeze", "user:2", "{}", at).ShouldNotBe(second);
    }

    [Fact]
    public void Times_are_kept_to_the_microsecond() =>
        AuditChain.Truncate(new DateTimeOffset(638_000_000_000_000_007, TimeSpan.Zero)).UtcTicks.ShouldBe(638_000_000_000_000_000);
}
