using System.Globalization;
using System.Text.Json;
using WalletPK.Common.Results;
using WalletPK.Contracts.Fraud;
using WalletPK.Contracts.Ledger;
using WalletPK.Fraud.Domain;

namespace WalletPK.Fraud.Application;

public static class FraudErrors
{
    public static readonly Error CaseNotFound = Error.NotFound("fraud.case_not_found", "No such case.");
    public static readonly Error CaseClosed = Error.Conflict("fraud.case_closed", "This case is closed.");
    public static readonly Error RuleNotFound = Error.NotFound("fraud.rule_not_found", "No such rule.");
    public static readonly Error RuleExists = Error.Conflict("fraud.rule_exists", "A rule with this ID already exists.");
    public static readonly Error InvalidRuleId = Error.Validation("fraud.invalid_rule_id", "A rule ID is 3 to 40 lowercase letters, digits and dashes.");
    public static readonly Error InvalidName = Error.Validation("fraud.invalid_name", "A rule name is 3 to 100 characters.");
    public static readonly Error InvalidResolution = Error.Validation("fraud.invalid_resolution", "The resolution is \"ConfirmedFraud\" or \"FalsePositive\".");
    public static readonly Error InvalidNote = Error.Validation("fraud.invalid_note", "A note is 1 to 2,000 characters.");
    public static readonly Error ReasonRequired = Error.Validation("fraud.reason_required", "Give a reason (3 to 200 characters); it goes in the audit log.");
    public static readonly Error AlreadyFrozen = Error.Conflict("fraud.already_frozen", "This user's wallets are already frozen.");
    public static readonly Error NotFrozen = Error.Conflict("fraud.not_frozen", "This user's wallets are not frozen.");

    public static Error InvalidRule(IReadOnlyList<string> problems) =>
        Error.Validation("fraud.invalid_rule", string.Join(" ", problems));
}

/// <summary>
/// Freezing and unfreezing (M10.6): asks the Ledger to freeze or unfreeze all of the user's wallets
/// (FreezeWallets / UnfreezeWallets through the outbox), remembers it, writes the audit log and announces
/// it on fraud.alerts. The Ledger's answer confirms it.
/// </summary>
public sealed class AccountService(IFraudStore store, TimeProvider timeProvider)
{
    public Task<Result<AccountFlag>> FreezeAsync(Guid userId, string? reason, string by, CancellationToken cancellationToken) =>
        store.InTransactionAsync(async () =>
        {
            if (Reason(reason) is not { } why)
            {
                return Result.Failure<AccountFlag>(FraudErrors.ReasonRequired);
            }

            await store.LockUsersAsync([userId], cancellationToken);
            if (await store.FlagAsync(userId, cancellationToken) is { Frozen: true })
            {
                return FraudErrors.AlreadyFrozen;
            }

            return Result.Success(await StageAsync(userId, frozen: true, why, by, cancellationToken));
        }, cancellationToken);

    public Task<Result<AccountFlag>> UnfreezeAsync(Guid userId, string? reason, string by, CancellationToken cancellationToken) =>
        store.InTransactionAsync(async () =>
        {
            if (Reason(reason) is not { } why)
            {
                return Result.Failure<AccountFlag>(FraudErrors.ReasonRequired);
            }

            await store.LockUsersAsync([userId], cancellationToken);
            if (await store.FlagAsync(userId, cancellationToken) is not { Frozen: true })
            {
                return FraudErrors.NotFrozen;
            }

            return Result.Success(await StageAsync(userId, frozen: false, why, by, cancellationToken));
        }, cancellationToken);

    /// <summary>Freezes inside the caller's transaction, unless already frozen (rules and rings). The caller holds the user's lock.</summary>
    internal async Task FreezeIfNeededAsync(Guid userId, string reason, string by, CancellationToken cancellationToken)
    {
        if (await store.FlagAsync(userId, cancellationToken) is not { Frozen: true })
        {
            await StageAsync(userId, frozen: true, reason, by, cancellationToken);
        }
    }

    /// <summary>Unfreezes inside the caller's transaction (closing a case as a false positive). The caller holds the user's lock.</summary>
    internal Task UnfreezeInTransactionAsync(Guid userId, string reason, string by, CancellationToken cancellationToken) =>
        StageAsync(userId, frozen: false, reason, by, cancellationToken);

    public Task<AccountFlag?> StatusAsync(Guid userId, CancellationToken cancellationToken) => store.FlagAsync(userId, cancellationToken);

    private async Task<AccountFlag> StageAsync(Guid userId, bool frozen, string reason, string by, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var flag = await store.FlagAsync(userId, cancellationToken);
        if (flag is null)
        {
            flag = new AccountFlag { UserId = userId };
            store.Add(flag);
        }

        flag.Frozen = frozen;
        flag.Reason = reason;
        flag.ChangedBy = by;
        flag.ChangedAt = now;
        flag.RequestId = Guid.CreateVersion7();
        flag.ConfirmedAt = null;
        store.Publish(frozen ? new FreezeWallets(flag.RequestId, userId, reason) : new UnfreezeWallets(flag.RequestId, userId, reason), flag.RequestId.ToString());
        store.Publish(new AccountFreezeChanged(userId, frozen, reason, by, now), null);
        await store.AuditAsync(by, frozen ? "account.freeze" : "account.unfreeze", $"user:{userId}", JsonSerializer.Serialize(new { reason }), now, cancellationToken);
        return flag;
    }

    private static string? Reason(string? reason) => reason?.Trim() is { Length: >= 3 and <= 200 } clean ? clean : null;
}

/// <summary>
/// Alerts and the cases they open (M10.6): a payment held for review or blocked opens (or joins) the
/// user's case; a block also freezes the user's wallets; an allowed payment above the alert level is only
/// an alert. A mule ring opens a ring case and freezes the mule and the senders who were refused.
/// </summary>
public sealed class AlertService(IFraudStore store, AccountService accounts, TimeProvider timeProvider)
{
    public Task PaymentAsync(ScoreInput input, RiskResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(result);
        return store.InTransactionAsync(async () =>
        {
            // One payment's case and freeze at a time per user (a ring may be freezing the same user).
            await store.LockUsersAsync([input.UserId], cancellationToken);
            var now = timeProvider.GetUtcNow();
            var severity = result.Action switch { RiskAction.Block => "High", RiskAction.Review => "Medium", _ => "Low" };
            var summary = string.Create(
                CultureInfo.InvariantCulture,
                $"{input.TransactionType} of Rs. {input.AmountPaisa / 100m:#,0.##}{(input.Counterparty is null ? "" : $" to {input.Counterparty}")}: {RiskPolicy.Describe(result)}");
            FraudCase? fraudCase = null;
            if (result.Action is RiskAction.Review or RiskAction.Block)
            {
                fraudCase = await CaseForAsync(input.UserId, "Payment", [input.UserId], $"Payment {result.Action.ToString().ToLowerInvariant()}ed by rules {string.Join(", ", result.Rules)}", now, cancellationToken);
            }

            var alert = new FraudAlert
            {
                Id = Guid.CreateVersion7(),
                UserId = input.UserId,
                TransactionId = input.TransactionId,
                Kind = "Payment",
                Severity = severity,
                Score = result.Score,
                Action = result.Action.ToString(),
                Rules = [.. result.Rules],
                Summary = summary,
                TransactionType = input.TransactionType,
                AmountPaisa = input.AmountPaisa,
                Counterparty = input.Counterparty,
                CaseId = fraudCase?.Id,
                CreatedAt = now,
            };
            store.Add(alert);
            store.Publish(Raised(alert), input.TransactionId.ToString());

            if (result.Action == RiskAction.Block)
            {
                await accounts.FreezeIfNeededAsync(input.UserId, $"Blocked by rules {string.Join(", ", result.Rules)} (case {fraudCase!.Number})", "rules", cancellationToken);
            }

            return true;
        }, cancellationToken);
    }

    /// <summary>A mule ring around <paramref name="muleUserId"/>: one case, one alert, freezes.</summary>
    public Task RingAsync(Guid muleUserId, string recipient, IReadOnlyList<Guid> senders, IReadOnlyList<Guid> flaggedSenders, CancellationToken cancellationToken) =>
        store.InTransactionAsync(async () =>
        {
            Guid[] subjects = [muleUserId, .. senders.Where(sender => sender != muleUserId)];
            await store.LockUsersAsync(subjects, cancellationToken);
            var now = timeProvider.GetUtcNow();
            var fraudCase = await CaseForAsync(muleUserId, "Ring", subjects, $"Mule ring: {senders.Count} senders to {recipient} in minutes, {flaggedSenders.Count} of them refused for risk", now, cancellationToken);
            var alert = new FraudAlert
            {
                Id = Guid.CreateVersion7(),
                UserId = muleUserId,
                Kind = "Ring",
                Severity = "High",
                Score = 100,
                Action = "Freeze",
                Rules = ["mule-ring"],
                Summary = $"{senders.Count} users paid {recipient} within minutes and {flaggedSenders.Count} of them were refused for risk: the wallet and those senders are frozen.",
                Counterparty = recipient,
                CaseId = fraudCase.Id,
                CreatedAt = now,
            };
            store.Add(alert);
            store.Publish(Raised(alert), null);

            await accounts.FreezeIfNeededAsync(muleUserId, $"Mule ring (case {fraudCase.Number})", "ring-detector", cancellationToken);
            foreach (var sender in flaggedSenders.Where(sender => sender != muleUserId))
            {
                await accounts.FreezeIfNeededAsync(sender, $"Sender in a mule ring (case {fraudCase.Number})", "ring-detector", cancellationToken);
            }

            return true;
        }, cancellationToken);

    /// <summary>The user's open case of this kind, or a new one.</summary>
    private async Task<FraudCase> CaseForAsync(Guid userId, string kind, Guid[] subjects, string title, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await store.OpenCaseAsync(userId, kind, cancellationToken) is { } open)
        {
            open.Subjects = [.. open.Subjects.Union(subjects)];
            open.UpdatedAt = now;
            return open;
        }

        var fraudCase = new FraudCase
        {
            Id = Guid.CreateVersion7(),
            Number = await store.NextCaseNumberAsync(cancellationToken),
            UserId = userId,
            Subjects = subjects,
            Kind = kind,
            Title = title.Length <= 200 ? title : title[..200],
            CreatedAt = now,
            UpdatedAt = now,
        };
        store.Add(fraudCase);
        store.Publish(new FraudCaseChanged(fraudCase.Id, userId, kind, fraudCase.Status, null, null, now), null);
        return fraudCase;
    }

    private static FraudAlertRaised Raised(FraudAlert alert) =>
        new(alert.Id, alert.UserId, alert.TransactionId, alert.Kind, alert.Severity, alert.Score, alert.Action, alert.Rules, alert.Summary, alert.CaseId, alert.CreatedAt);
}

/// <summary>
/// Mule rings (M10.5): when several users pay one wallet within minutes and enough of them were refused
/// for risk, the wallet is a mule collecting a ring's stolen money. Fed by the scorer (every P2P payment
/// scored) and by the ledger's posted entries (payments that went through).
/// </summary>
public sealed class RingDetector(IFeatureStore features, AlertService alerts, FraudOptions options)
{
    /// <returns>True when this observation completed a ring (a case was opened).</returns>
    public async Task<bool> ObserveAsync(RingObservation observation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var window = TimeSpan.FromMinutes(options.RingWindowMinutes);
        var senders = await features.SendersAsync(observation.Recipient, observation.At - window);
        if (senders.Count < options.RingMinSenders)
        {
            return false;
        }

        var flagged = await features.FlaggedAsync(senders);
        if (flagged.Count < options.RingMinFlagged
            || await features.OwnerOfAsync(observation.Recipient) is not { } mule
            || !await features.ClaimRingAsync(observation.Recipient, TimeSpan.FromHours(1)))
        {
            return false;
        }

        await alerts.RingAsync(mule, observation.Recipient, senders, flagged, cancellationToken);
        return true;
    }
}

public sealed record CaseView(FraudCase Case, IReadOnlyList<FraudAlert> Alerts, IReadOnlyList<CaseNote> Notes);

/// <summary>Case management for compliance officers (M10.7): list, read, assign, note, close.</summary>
public sealed class CaseService(IFraudStore store, AccountService accounts, TimeProvider timeProvider)
{
    public Task<IReadOnlyList<FraudCase>> ListAsync(string? status, int? limit, CancellationToken cancellationToken) =>
        store.CasesAsync(status, Math.Clamp(limit ?? 50, 1, 200), cancellationToken);

    public async Task<Result<CaseView>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await store.CaseAsync(id, cancellationToken) is not { } fraudCase)
        {
            return FraudErrors.CaseNotFound;
        }

        return new CaseView(fraudCase, await store.AlertsAsync(null, id, null, 200, cancellationToken), await store.NotesAsync(id, cancellationToken));
    }

    /// <summary>Opens a case by hand, for a user a compliance officer is worried about.</summary>
    public Task<Result<CaseView>> OpenAsync(Guid userId, string? title, string by, CancellationToken cancellationToken) =>
        store.InTransactionAsync(async () =>
        {
            var clean = title?.Trim();
            if (clean is not { Length: >= 3 and <= 200 })
            {
                return Result.Failure<CaseView>(Error.Validation("fraud.invalid_title", "A case title is 3 to 200 characters."));
            }

            var now = timeProvider.GetUtcNow();
            var fraudCase = new FraudCase
            {
                Id = Guid.CreateVersion7(),
                Number = await store.NextCaseNumberAsync(cancellationToken),
                UserId = userId,
                Subjects = [userId],
                Kind = "Manual",
                Title = clean,
                AssignedTo = by,
                Status = "InReview",
                CreatedAt = now,
                UpdatedAt = now,
            };
            store.Add(fraudCase);
            store.Publish(new FraudCaseChanged(fraudCase.Id, userId, fraudCase.Kind, fraudCase.Status, null, by, now), null);
            await store.AuditAsync(by, "case.open", $"case:{fraudCase.Number}", JsonSerializer.Serialize(new { userId, title = clean }), now, cancellationToken);
            return Result.Success(new CaseView(fraudCase, [], []));
        }, cancellationToken);

    public Task<Result<CaseView>> AssignAsync(Guid id, string to, CancellationToken cancellationToken) =>
        ChangeAsync(id, to, "case.assign", (fraudCase, now) =>
        {
            fraudCase.AssignedTo = to;
            fraudCase.Status = "InReview";
            return Task.FromResult<Error?>(null);
        }, new { to }, cancellationToken);

    public Task<Result<CaseView>> NoteAsync(Guid id, string? text, string by, CancellationToken cancellationToken)
    {
        var clean = text?.Trim();
        if (clean is not { Length: >= 1 and <= 2_000 })
        {
            return Task.FromResult(Result.Failure<CaseView>(FraudErrors.InvalidNote));
        }

        return ChangeAsync(id, by, "case.note", (fraudCase, now) =>
        {
            store.Add(new CaseNote { Id = Guid.CreateVersion7(), CaseId = fraudCase.Id, Author = by, Text = clean, CreatedAt = now });
            return Task.FromResult<Error?>(null);
        }, new { length = clean.Length }, cancellationToken, keepStatus: true);
    }

    /// <summary>Closes a case: confirmed fraud, or a false positive (and then, if asked, its subjects' wallets are unfrozen).</summary>
    public Task<Result<CaseView>> CloseAsync(Guid id, string? resolution, bool unfreeze, string? note, string by, CancellationToken cancellationToken)
    {
        if (resolution is not ("ConfirmedFraud" or "FalsePositive"))
        {
            return Task.FromResult(Result.Failure<CaseView>(FraudErrors.InvalidResolution));
        }

        return ChangeAsync(id, by, "case.close", async (fraudCase, now) =>
        {
            fraudCase.Status = "Closed";
            fraudCase.Resolution = resolution;
            fraudCase.ClosedAt = now;
            if (!string.IsNullOrWhiteSpace(note))
            {
                store.Add(new CaseNote { Id = Guid.CreateVersion7(), CaseId = fraudCase.Id, Author = by, Text = note.Trim()[..Math.Min(note.Trim().Length, 2_000)], CreatedAt = now });
            }

            if (unfreeze && resolution == "FalsePositive")
            {
                await store.LockUsersAsync(fraudCase.Subjects, cancellationToken);
                foreach (var subject in fraudCase.Subjects)
                {
                    if (await store.FlagAsync(subject, cancellationToken) is { Frozen: true })
                    {
                        await accounts.UnfreezeInTransactionAsync(subject, $"Case {fraudCase.Number} closed as a false positive", by, cancellationToken);
                    }
                }
            }

            return null;
        }, new { resolution, unfreeze }, cancellationToken);
    }

    private Task<Result<CaseView>> ChangeAsync(Guid id, string by, string action, Func<FraudCase, DateTimeOffset, Task<Error?>> change, object details, CancellationToken cancellationToken, bool keepStatus = false) =>
        store.InTransactionAsync(async () =>
        {
            if (await store.CaseAsync(id, cancellationToken) is not { } fraudCase)
            {
                return Result.Failure<CaseView>(FraudErrors.CaseNotFound);
            }

            if (fraudCase.Status == "Closed")
            {
                return FraudErrors.CaseClosed;
            }

            var now = timeProvider.GetUtcNow();
            if (await change(fraudCase, now) is { } error)
            {
                return error;
            }

            fraudCase.UpdatedAt = now;
            if (!keepStatus)
            {
                store.Publish(new FraudCaseChanged(fraudCase.Id, fraudCase.UserId, fraudCase.Kind, fraudCase.Status, fraudCase.Resolution, fraudCase.AssignedTo, now), null);
            }

            await store.AuditAsync(by, action, $"case:{fraudCase.Number}", JsonSerializer.Serialize(details), now, cancellationToken);
            await store.SaveChangesAsync(cancellationToken);
            return Result.Success(new CaseView(fraudCase, await store.AlertsAsync(null, id, null, 200, cancellationToken), await store.NotesAsync(id, cancellationToken)));
        }, cancellationToken);
}

public sealed record RuleView(string Id, string Name, string Description, string Definition, bool Enabled, int Version, bool BuiltIn, DateTimeOffset UpdatedAt, string UpdatedBy);

/// <param name="Rule">The rule being tried (from the editor), or null to try the rules in force.</param>
/// <param name="Features">Feature values; missing ones are left out (conditions on them are false).</param>
public sealed record RuleTestRequest(string? Rule, Dictionary<string, JsonElement>? Features);

/// <summary>
/// The rule editor (M10.2): list, create, change, switch on and off, and try rules, all without a redeploy.
/// A change is checked first, then saved with its version and an audit entry, then announced so every
/// instance reloads its rules within a moment.
/// </summary>
public sealed class RuleService(IFraudStore store, IRuleCache cache, FraudOptions options, TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<RuleView>> ListAsync(CancellationToken cancellationToken) =>
        [.. (await store.RulesAsync(cancellationToken)).Select(View)];

    public async Task<Result<RuleView>> GetAsync(string id, CancellationToken cancellationToken) =>
        await store.RuleAsync(id, cancellationToken) is { } rule ? View(rule) : FraudErrors.RuleNotFound;

    public async Task<Result<RuleView>> CreateAsync(string? id, string? name, string? description, string? definition, string by, CancellationToken cancellationToken)
    {
        if (id is null || !System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9][a-z0-9-]{2,39}$"))
        {
            return FraudErrors.InvalidRuleId;
        }

        if (name?.Trim() is not { Length: >= 3 and <= 100 } cleanName)
        {
            return FraudErrors.InvalidName;
        }

        var (_, errors) = RiskRule.Parse(id, cleanName, definition);
        if (errors.Count > 0)
        {
            return FraudErrors.InvalidRule(errors);
        }

        var result = await store.InTransactionAsync(async () =>
        {
            if (await store.RuleAsync(id, cancellationToken) is not null)
            {
                return Result.Failure<RuleView>(FraudErrors.RuleExists);
            }

            var now = timeProvider.GetUtcNow();
            var rule = new RuleRecord { Id = id, Name = cleanName, Description = description?.Trim() ?? "", Definition = definition!, UpdatedAt = now, UpdatedBy = by };
            store.Add(rule);
            await store.AuditAsync(by, "rule.create", $"rule:{id}", JsonSerializer.Serialize(new { name = cleanName, definition }), now, cancellationToken);
            return Result.Success(View(rule));
        }, cancellationToken);
        return await AnnouncedAsync(result);
    }

    /// <summary>Changes a rule's definition, name, description or whether it is on; anything left null stays.</summary>
    public async Task<Result<RuleView>> UpdateAsync(string id, string? name, string? description, string? definition, bool? enabled, string by, CancellationToken cancellationToken)
    {
        if (name is not null && name.Trim() is not { Length: >= 3 and <= 100 })
        {
            return FraudErrors.InvalidName;
        }

        if (definition is not null && RiskRule.Parse(id, name ?? id, definition).Errors is { Count: > 0 } errors)
        {
            return FraudErrors.InvalidRule(errors);
        }

        var result = await store.InTransactionAsync(async () =>
        {
            if (await store.RuleAsync(id, cancellationToken) is not { } rule)
            {
                return Result.Failure<RuleView>(FraudErrors.RuleNotFound);
            }

            var now = timeProvider.GetUtcNow();
            rule.Name = name?.Trim() ?? rule.Name;
            rule.Description = description?.Trim() ?? rule.Description;
            rule.Definition = definition ?? rule.Definition;
            rule.Enabled = enabled ?? rule.Enabled;
            rule.Version++;
            rule.UpdatedAt = now;
            rule.UpdatedBy = by;
            await store.AuditAsync(by, "rule.update", $"rule:{id}", JsonSerializer.Serialize(new { version = rule.Version, rule.Enabled, definition }), now, cancellationToken);
            return Result.Success(View(rule));
        }, cancellationToken);
        return await AnnouncedAsync(result);
    }

    /// <summary>A dry run: what a rule (or the rules in force) says about some feature values. Nothing is saved.</summary>
    public Result<RiskResult> Test(RuleTestRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var vector = new FeatureVector();
        foreach (var (name, value) in request.Features ?? [])
        {
            switch (FeatureCatalog.Find(name)?.Kind)
            {
                case FeatureKind.Number when value.ValueKind == JsonValueKind.Number:
                    vector.Set(name, value.GetDouble());
                    break;
                case FeatureKind.Bool when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    vector.Set(name, value.GetBoolean());
                    break;
                case FeatureKind.Text when value.ValueKind == JsonValueKind.String:
                    vector.Set(name, value.GetString()!);
                    break;
                default:
                    return Error.Validation("fraud.invalid_feature", $"\"{name}\" is not a feature, or its value has the wrong type.");
            }
        }

        var policy = options.Policy();
        if (request.Rule is null)
        {
            return policy.Evaluate(cache.Rules, vector);
        }

        var (rule, errors) = RiskRule.Parse("test", "Test", request.Rule);
        return rule is null ? FraudErrors.InvalidRule(errors) : policy.Evaluate([rule], vector);
    }

    private async Task<Result<RuleView>> AnnouncedAsync(Result<RuleView> result)
    {
        if (result.IsSuccess)
        {
            await cache.AnnounceChangeAsync();
        }

        return result;
    }

    private static RuleView View(RuleRecord rule) =>
        new(rule.Id, rule.Name, rule.Description, rule.Definition, rule.Enabled, rule.Version, rule.BuiltIn, rule.UpdatedAt, rule.UpdatedBy);
}

/// <summary>The audit log (FR-5.3): read it, and check its hash chain.</summary>
public sealed class AuditService(IFraudStore store)
{
    public Task<IReadOnlyList<AuditEntry>> ListAsync(long? after, int? limit, CancellationToken cancellationToken) =>
        store.AuditTrailAsync(after, Math.Clamp(limit ?? 100, 1, 1_000), cancellationToken);

    /// <summary>Recomputes every hash; the first entry whose hash or link is wrong, if any.</summary>
    public async Task<(bool Intact, long Entries, long? BrokenAt)> VerifyAsync(CancellationToken cancellationToken)
    {
        var previous = AuditChain.Genesis;
        long count = 0;
        long? after = null;
        while (true)
        {
            var page = await store.AuditTrailAsync(after, 1_000, cancellationToken);
            foreach (var entry in page)
            {
                count++;
                if (entry.PreviousHash != previous || AuditChain.Hash(entry.PreviousHash, entry.Actor, entry.Action, entry.Subject, entry.Details, entry.At) != entry.Hash)
                {
                    return (false, count, entry.Sequence);
                }

                previous = entry.Hash;
            }

            if (page.Count < 1_000)
            {
                return (true, count, null);
            }

            after = page[^1].Sequence;
        }
    }
}
