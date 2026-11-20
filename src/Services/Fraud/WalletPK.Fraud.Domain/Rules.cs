using System.Globalization;
using System.Text.Json;

namespace WalletPK.Fraud.Domain;

/// <summary>
/// What happens to a payment (FR-5.1), mildest first: let it through, ask the user to confirm it with a
/// one-time code, hold it for a compliance officer, or block it and freeze the wallets.
/// </summary>
public enum RiskAction
{
    Allow = 0,
    StepUp = 1,
    Review = 2,
    Block = 3,
}

/// <summary>A condition over the features of one payment, compiled from a rule's JSON.</summary>
public abstract class Condition
{
    public abstract bool Evaluate(FeatureVector features);
}

internal sealed class AllCondition(IReadOnlyList<Condition> children) : Condition
{
    public override bool Evaluate(FeatureVector features) => children.All(child => child.Evaluate(features));
}

internal sealed class AnyCondition(IReadOnlyList<Condition> children) : Condition
{
    public override bool Evaluate(FeatureVector features) => children.Any(child => child.Evaluate(features));
}

internal sealed class NotCondition(Condition child) : Condition
{
    public override bool Evaluate(FeatureVector features) => !child.Evaluate(features);
}

internal sealed class NumberCondition(string feature, string op, double value, double upper) : Condition
{
    public override bool Evaluate(FeatureVector features)
    {
        if (features[feature] is not double actual)
        {
            return false;
        }

        return op switch
        {
            "gt" => actual > value,
            "gte" => actual >= value,
            "lt" => actual < value,
            "lte" => actual <= value,
            "eq" => actual.Equals(value),
            "neq" => !actual.Equals(value),
            "between" => actual >= value && actual <= upper,
            _ => false,
        };
    }
}

internal sealed class BoolCondition(string feature, bool negate, bool value) : Condition
{
    public override bool Evaluate(FeatureVector features) => features[feature] is bool actual && (actual == value) != negate;
}

internal sealed class TextCondition(string feature, bool negate, IReadOnlySet<string> values) : Condition
{
    public override bool Evaluate(FeatureVector features) => features[feature] is string actual && values.Contains(actual) != negate;
}

/// <summary>
/// One risk rule (M10.2), from JSON. Example:
/// <code>{
///   "types": ["P2PTransfer", "BankTransfer"],
///   "when": { "all": [ { "feature": "device_age_hours", "op": "lt", "value": 24 },
///                      { "feature": "amount_paisa", "op": "gte", "value": 1000000 } ] },
///   "score": 50,
///   "action": "Review",
///   "reason": "A large payment from a new phone needs a check."
/// }</code>
/// <c>when</c> nests <c>all</c>, <c>any</c> and <c>not</c> over conditions on features from the
/// <see cref="FeatureCatalog"/>. Numbers: gt, gte, lt, lte, eq, neq, between ([low, high]); true/false: eq,
/// neq; text: eq, neq, in, not_in. A matching rule adds its score (all scores together, at most 100) and,
/// when it has one, forces at least its action. <c>types</c> limits the rule to some payment types.
/// </summary>
public sealed class RiskRule
{
    private RiskRule(string id, string name, Condition when, int score, RiskAction? action, string? reason, IReadOnlySet<string>? types)
    {
        Id = id;
        Name = name;
        When = when;
        Score = score;
        Action = action;
        Reason = reason;
        Types = types;
    }

    public string Id { get; }

    public string Name { get; }

    public Condition When { get; }

    public int Score { get; }

    public RiskAction? Action { get; }

    public string? Reason { get; }

    public IReadOnlySet<string>? Types { get; }

    public bool Matches(FeatureVector features)
    {
        ArgumentNullException.ThrowIfNull(features);
        return (Types is null || features[FeatureCatalog.TransactionType] is string type && Types.Contains(type)) && When.Evaluate(features);
    }

    /// <summary>Parses and checks a rule; the errors say exactly what is wrong, for the rule editor.</summary>
    public static (RiskRule? Rule, IReadOnlyList<string> Errors) Parse(string id, string name, string? definition)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(definition))
        {
            return (null, ["The definition is empty."]);
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(definition);
            root = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            return (null, [$"Not valid JSON: {exception.Message}"]);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return (null, ["The definition must be a JSON object."]);
        }

        var score = 0;
        if (root.TryGetProperty("score", out var scoreElement))
        {
            if (scoreElement.ValueKind != JsonValueKind.Number || !scoreElement.TryGetInt32(out score) || score is < 0 or > 100)
            {
                errors.Add("\"score\" must be a whole number from 0 to 100.");
            }
        }

        RiskAction? action = null;
        if (root.TryGetProperty("action", out var actionElement) && actionElement.ValueKind != JsonValueKind.Null)
        {
            if (actionElement.ValueKind == JsonValueKind.String && Enum.TryParse<RiskAction>(actionElement.GetString(), ignoreCase: false, out var parsed) && parsed != RiskAction.Allow)
            {
                action = parsed;
            }
            else
            {
                errors.Add("\"action\" must be \"StepUp\", \"Review\", \"Block\" or null.");
            }
        }

        if (score == 0 && action is null && errors.Count == 0)
        {
            errors.Add("A rule needs a \"score\" above 0, an \"action\", or both.");
        }

        string? reason = null;
        if (root.TryGetProperty("reason", out var reasonElement) && reasonElement.ValueKind != JsonValueKind.Null)
        {
            reason = reasonElement.ValueKind == JsonValueKind.String ? reasonElement.GetString() : null;
            if (reason is null || reason.Length > 200)
            {
                errors.Add("\"reason\" must be text of at most 200 characters.");
            }
        }

        HashSet<string>? types = null;
        if (root.TryGetProperty("types", out var typesElement) && typesElement.ValueKind != JsonValueKind.Null)
        {
            types = ReadStrings(typesElement, "types", errors);
            foreach (var type in types ?? [])
            {
                if (!RiskPolicy.TransactionTypes.Contains(type))
                {
                    errors.Add($"\"types\": unknown payment type \"{type}\" (known: {string.Join(", ", RiskPolicy.TransactionTypes)}).");
                }
            }
        }

        Condition? when = null;
        if (!root.TryGetProperty("when", out var whenElement))
        {
            errors.Add("\"when\" is missing.");
        }
        else
        {
            when = ParseCondition(whenElement, "when", errors, depth: 0);
        }

        foreach (var property in root.EnumerateObject())
        {
            if (property.Name is not ("types" or "when" or "score" or "action" or "reason"))
            {
                errors.Add($"Unknown property \"{property.Name}\".");
            }
        }

        return errors.Count == 0 ? (new RiskRule(id, name, when!, score, action, reason, types), errors) : (null, errors);
    }

    private static Condition? ParseCondition(JsonElement element, string path, List<string> errors, int depth)
    {
        if (depth > 8)
        {
            errors.Add($"{path}: nested too deeply (at most 8 levels).");
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path}: a condition must be an object.");
            return null;
        }

        if (element.TryGetProperty("all", out var all))
        {
            return Group(all, $"{path}.all", errors, depth, children => new AllCondition(children));
        }

        if (element.TryGetProperty("any", out var any))
        {
            return Group(any, $"{path}.any", errors, depth, children => new AnyCondition(children));
        }

        if (element.TryGetProperty("not", out var not))
        {
            var child = ParseCondition(not, $"{path}.not", errors, depth + 1);
            return child is null ? null : new NotCondition(child);
        }

        return ParseLeaf(element, path, errors);
    }

    private static Condition? Group(JsonElement array, string path, List<string> errors, int depth, Func<IReadOnlyList<Condition>, Condition> make)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0)
        {
            errors.Add($"{path}: must be a non-empty list of conditions.");
            return null;
        }

        var children = new List<Condition>();
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (ParseCondition(item, $"{path}[{index++}]", errors, depth + 1) is { } child)
            {
                children.Add(child);
            }
        }

        return children.Count == array.GetArrayLength() ? make(children) : null;
    }

    private static Condition? ParseLeaf(JsonElement element, string path, List<string> errors)
    {
        var name = element.TryGetProperty("feature", out var featureElement) && featureElement.ValueKind == JsonValueKind.String ? featureElement.GetString() : null;
        var op = element.TryGetProperty("op", out var opElement) && opElement.ValueKind == JsonValueKind.String ? opElement.GetString() : null;
        if (FeatureCatalog.Find(name) is not { } feature)
        {
            errors.Add($"{path}: unknown feature \"{name}\". Use \"all\", \"any\", \"not\", or a feature from the catalog.");
            return null;
        }

        if (!element.TryGetProperty("value", out var value))
        {
            errors.Add($"{path}: \"value\" is missing.");
            return null;
        }

        switch (feature.Kind)
        {
            case FeatureKind.Number when op is "gt" or "gte" or "lt" or "lte" or "eq" or "neq":
                if (value.ValueKind == JsonValueKind.Number)
                {
                    return new NumberCondition(feature.Name, op, value.GetDouble(), 0);
                }

                errors.Add($"{path}: {feature.Name} is a number; \"value\" must be a number.");
                return null;
            case FeatureKind.Number when op == "between":
                if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 2
                    && value[0].ValueKind == JsonValueKind.Number && value[1].ValueKind == JsonValueKind.Number
                    && value[0].GetDouble() <= value[1].GetDouble())
                {
                    return new NumberCondition(feature.Name, op, value[0].GetDouble(), value[1].GetDouble());
                }

                errors.Add($"{path}: \"between\" needs [low, high] with low <= high.");
                return null;
            case FeatureKind.Bool when op is "eq" or "neq":
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return new BoolCondition(feature.Name, op == "neq", value.GetBoolean());
                }

                errors.Add($"{path}: {feature.Name} is true/false; \"value\" must be true or false.");
                return null;
            case FeatureKind.Text when op is "eq" or "neq":
                if (value.ValueKind == JsonValueKind.String)
                {
                    return new TextCondition(feature.Name, op == "neq", new HashSet<string>([value.GetString()!], StringComparer.Ordinal));
                }

                errors.Add($"{path}: {feature.Name} is text; \"value\" must be text.");
                return null;
            case FeatureKind.Text when op is "in" or "not_in":
                var values = ReadStrings(value, path, errors);
                return values is { Count: > 0 } ? new TextCondition(feature.Name, op == "not_in", values) : null;
            default:
                errors.Add($"{path}: operator \"{op}\" does not apply to {feature.Name} ({feature.Kind.ToString().ToLowerInvariant()}).");
                return null;
        }
    }

    private static HashSet<string>? ReadStrings(JsonElement element, string path, List<string> errors)
    {
        if (element.ValueKind != JsonValueKind.Array || element.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
        {
            errors.Add($"{path}: must be a list of text values.");
            return null;
        }

        return [.. element.EnumerateArray().Select(item => item.GetString()!)];
    }
}

/// <param name="Score">0 to 100.</param>
/// <param name="Action">What to do with the payment.</param>
/// <param name="Rules">The rules that matched, by ID.</param>
/// <param name="Reason">A sentence for the customer when it is not allowed.</param>
public sealed record RiskResult(int Score, RiskAction Action, IReadOnlyList<string> Rules, string? Reason);

/// <summary>
/// Turns matched rules into a decision (M10.3): the scores add up (at most 100); at or above the step-up
/// threshold the user must confirm the payment with a one-time code, at or above the review threshold it is
/// held for review, at or above the block threshold it is blocked; a rule with its own action forces at
/// least that.
/// </summary>
public sealed class RiskPolicy(int reviewAt = 60, int blockAt = 90, int stepUpAt = 30)
{
    public static IReadOnlyList<string> TransactionTypes { get; } = ["P2PTransfer", "BankTransfer", "BillPayment", "MobileTopup", "MerchantPayment", "CashOut"];

    public int ReviewAt { get; } = reviewAt;

    public int BlockAt { get; } = blockAt;

    public int StepUpAt { get; } = stepUpAt;

    public RiskResult Evaluate(IReadOnlyList<RiskRule> rules, FeatureVector features)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var matched = rules.Where(rule => rule.Matches(features)).ToList();
        var score = Math.Min(100, matched.Sum(rule => rule.Score));
        var action = score >= BlockAt ? RiskAction.Block : score >= ReviewAt ? RiskAction.Review : score >= StepUpAt ? RiskAction.StepUp : RiskAction.Allow;
        foreach (var rule in matched)
        {
            if (rule.Action is { } forced && forced > action)
            {
                action = forced;
            }
        }

        var reason = action == RiskAction.Allow
            ? null
            : matched.Where(rule => rule.Reason is not null).OrderByDescending(rule => rule.Action ?? RiskAction.Allow).ThenByDescending(rule => rule.Score).Select(rule => rule.Reason).FirstOrDefault();
        return new RiskResult(score, action, [.. matched.Select(rule => rule.Id)], reason);
    }

    /// <summary>
    /// The decision once the user has confirmed with a one-time code: a step-up becomes an allow (the
    /// matched rules stay on record); a review or block is not something a code can lift.
    /// </summary>
    public static RiskResult Confirmed(RiskResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Action == RiskAction.StepUp ? result with { Action = RiskAction.Allow, Reason = "Confirmed with a one-time code." } : result;
    }

    /// <summary>The hour (0-23) in Pakistan for an instant.</summary>
    public static double HourInPakistan(DateTimeOffset instant) => instant.ToOffset(TimeSpan.FromHours(5)).Hour;

    public static string Describe(RiskResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return string.Create(CultureInfo.InvariantCulture, $"score {result.Score}, {result.Action}, rules: {(result.Rules.Count == 0 ? "none" : string.Join(", ", result.Rules))}");
    }
}

/// <summary>
/// The audit log's hash chain (FR-5.3): each entry's hash covers its content and the previous entry's
/// hash, so changing or removing any entry breaks every hash after it.
/// </summary>
public static class AuditChain
{
    /// <summary>The "previous hash" of the first entry.</summary>
    public static readonly string Genesis = new('0', 64);

    /// <summary>To the microsecond, as PostgreSQL keeps it, so a stored entry hashes the same when read back.</summary>
    public static DateTimeOffset Truncate(DateTimeOffset at) => new(at.UtcTicks - (at.UtcTicks % 10), TimeSpan.Zero);

    public static string Hash(string previousHash, string actor, string action, string subject, string details, DateTimeOffset at)
    {
        var text = string.Join('\u001f', previousHash, actor, action, subject, details, at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    }
}
