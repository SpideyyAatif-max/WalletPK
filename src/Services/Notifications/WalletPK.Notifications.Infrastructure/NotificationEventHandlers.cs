using WalletPK.Common.Primitives;
using WalletPK.Contracts.Agents;
using WalletPK.Contracts.Bills;
using WalletPK.Contracts.Fraud;
using WalletPK.Contracts.Identity;
using WalletPK.Contracts.Ledger;
using WalletPK.Contracts.Payments;
using WalletPK.Contracts.TopupMerchant;
using WalletPK.Messaging.Abstractions;
using WalletPK.Notifications.Application;
using WalletPK.Notifications.Domain;
using static WalletPK.Notifications.Domain.MessageRenderer;

namespace WalletPK.Notifications.Infrastructure;

/// <summary>
/// What every other service's events mean for users and staff (M11.2). Money movement is told from the
/// Ledger's wallet entries (one per wallet per journal, for every kind of payment), so a payment is
/// announced once; the payment services' own events add what the Ledger cannot know: failures, requests
/// and status changes for live screens. Fraud and dead-letter events feed the Ops live feed.
/// </summary>
internal sealed class NotificationEventHandlers(Notifier notifier, Recipients recipients, INotificationStore store, RealtimeOutbox outbox) :
    IIntegrationEventHandler<UserRegistered>,
    IIntegrationEventHandler<DeviceBound>,
    IIntegrationEventHandler<KycUpgraded>,
    IIntegrationEventHandler<WalletOpened>,
    IIntegrationEventHandler<WalletEntryPosted>,
    IIntegrationEventHandler<P2PTransferChanged>,
    IIntegrationEventHandler<BankTransferChanged>,
    IIntegrationEventHandler<MoneyRequestChanged>,
    IIntegrationEventHandler<BillPaymentChanged>,
    IIntegrationEventHandler<BillDueReminder>,
    IIntegrationEventHandler<BillPaymentDeadLettered>,
    IIntegrationEventHandler<TopUpChanged>,
    IIntegrationEventHandler<TopUpDeadLettered>,
    IIntegrationEventHandler<MerchantPaymentChanged>,
    IIntegrationEventHandler<AgentStatusChanged>,
    IIntegrationEventHandler<AgentTransactionChanged>,
    IIntegrationEventHandler<AgentFloatLow>,
    IIntegrationEventHandler<FraudAlertRaised>,
    IIntegrationEventHandler<AccountFreezeChanged>,
    IIntegrationEventHandler<FraudCaseChanged>
{
    private const string Failed = "Failed";

    // ---- Identity ----

    public async Task HandleAsync(UserRegistered integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        await recipients.EnsureAsync(integrationEvent.UserId, integrationEvent.PhoneNumber, cancellationToken);
        await NotifyAsync(integrationEvent.UserId, TemplateCatalog.Welcome, "welcome", integrationEvent.RegisteredAt, cancellationToken, ("phone", integrationEvent.PhoneNumber));
    }

    /// <summary>A device with cooling-off is a second (or later) device: the owner should hear about it.</summary>
    public Task HandleAsync(DeviceBound integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return integrationEvent.CoolingOffUntil is not { } until
            ? Task.CompletedTask
            : NotifyAsync(
                integrationEvent.UserId, TemplateCatalog.NewDevice, $"device:{integrationEvent.DeviceId}:{integrationEvent.BoundAt.UtcTicks}", integrationEvent.BoundAt, cancellationToken,
                ("at", DateTime(integrationEvent.BoundAt)), ("until", DateTime(until)));
    }

    public Task HandleAsync(KycUpgraded integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return NotifyAsync(
            integrationEvent.UserId, TemplateCatalog.KycUpgraded, $"kyc:{integrationEvent.NewTier}", integrationEvent.UpgradedAt, cancellationToken, ("tier", integrationEvent.NewTier));
    }

    // ---- Ledger ----

    public async Task HandleAsync(WalletOpened integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        if (integrationEvent.WalletType == "CustomerWallet" && integrationEvent.WalletNumber is { } number)
        {
            await recipients.EnsureAsync(integrationEvent.OwnerId, number, cancellationToken);
        }
    }

    /// <summary>Every new entry on a wallet: a live push of the new balance, and a notification.</summary>
    public async Task HandleAsync(WalletEntryPosted integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        if (integrationEvent.OwnerId is not { } owner)
        {
            return;
        }

        var customer = integrationEvent.WalletType == "CustomerWallet";
        if (customer && integrationEvent.WalletNumber is { } number)
        {
            await recipients.EnsureAsync(owner, number, cancellationToken);
        }

        var balance = Money.FromPaisa(integrationEvent.BalanceAfterPaisa);
        var credit = integrationEvent.Direction == "Credit";
        var line = new LiveStatementLine(
            integrationEvent.Sequence,
            integrationEvent.TransactionId,
            integrationEvent.JournalType,
            WalletEntryTitles.For(integrationEvent.JournalType, integrationEvent.Description, integrationEvent.Direction, integrationEvent.CounterpartyWalletNumber),
            credit ? "In" : "Out",
            Money.FromPaisa(integrationEvent.AmountPaisa),
            balance,
            integrationEvent.PostedAt);
        var live = new LiveWalletEntry(integrationEvent.WalletId, integrationEvent.WalletType, integrationEvent.Sequence, balance, line);
        outbox.Push(realtime => realtime.EntryPostedAsync(owner, live));

        (string Name, string Value)[] parameters;
        string template;
        if (integrationEvent.JournalType == "P2PTransfer" && integrationEvent.CounterpartyWalletNumber is { } other)
        {
            template = credit ? TemplateCatalog.MoneyReceived : TemplateCatalog.MoneySent;
            parameters = [("amount", Rupees(integrationEvent.AmountPaisa)), ("balance", Rupees(integrationEvent.BalanceAfterPaisa)), ("counterparty", WalletEntryTitles.Mask(other))];
        }
        else
        {
            template = credit ? TemplateCatalog.WalletCredited : TemplateCatalog.WalletDebited;
            var phrase = $"journal:{integrationEvent.JournalType}";
            parameters =
            [
                ("amount", Rupees(integrationEvent.AmountPaisa)),
                ("balance", Rupees(integrationEvent.BalanceAfterPaisa)),
                ("what", Phrases.All.ContainsKey(phrase) ? Phrase(phrase) : integrationEvent.Description),
            ];
        }

        // Merchants and agents see every payment in the app; texting them for each one would be noise.
        await notifier.NotifyAsync(
            new NotifyRequest(owner, template, Parameters(parameters), $"entry:{integrationEvent.WalletId}:{integrationEvent.Sequence}", integrationEvent.PostedAt, AllowSms: customer),
            cancellationToken);
    }

    // ---- Payments ----

    public Task HandleAsync(P2PTransferChanged integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return ChangedAsync(
            integrationEvent.SenderUserId, "p2p", integrationEvent.TransferId, integrationEvent.Status, null, integrationEvent.Detail, integrationEvent.ChangedAt, cancellationToken,
            ("amount", Rupees(integrationEvent.AmountPaisa)), ("counterparty", WalletEntryTitles.Mask(integrationEvent.ToWalletNumber)));
    }

    public Task HandleAsync(BankTransferChanged integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return ChangedAsync(
            integrationEvent.UserId, "bank", integrationEvent.TransferId, integrationEvent.Status, integrationEvent.Step, integrationEvent.Detail, integrationEvent.ChangedAt, cancellationToken,
            ("amount", Rupees(integrationEvent.AmountPaisa)), ("bank", integrationEvent.ToBank));
    }

    /// <summary>A new request goes to the payer (if they have a wallet); its answer goes back to the requester.</summary>
    public async Task HandleAsync(MoneyRequestChanged integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        var update = new PaymentUpdate("request", integrationEvent.RequestId, integrationEvent.Status, null);
        outbox.Push(realtime => realtime.PaymentChangedAsync(integrationEvent.RequesterUserId, update));
        var amount = ("amount", Rupees(integrationEvent.AmountPaisa));
        switch (integrationEvent.Status)
        {
            case "Pending" when await store.RecipientByPhoneAsync(integrationEvent.PayerWalletNumber, cancellationToken) is { } payer:
                outbox.Push(realtime => realtime.PaymentChangedAsync(payer.UserId, update));
                await NotifyAsync(
                    payer.UserId, TemplateCatalog.RequestReceived, $"request:{integrationEvent.RequestId}:received", integrationEvent.ChangedAt, cancellationToken,
                    amount, ("requester", WalletEntryTitles.Mask(integrationEvent.RequesterWalletNumber)));
                break;
            case "Paid":
                await NotifyAsync(
                    integrationEvent.RequesterUserId, TemplateCatalog.RequestPaid, $"request:{integrationEvent.RequestId}:paid", integrationEvent.ChangedAt, cancellationToken,
                    amount, ("payer", WalletEntryTitles.Mask(integrationEvent.PayerWalletNumber)));
                break;
            case "Declined":
                await NotifyAsync(
                    integrationEvent.RequesterUserId, TemplateCatalog.RequestDeclined, $"request:{integrationEvent.RequestId}:declined", integrationEvent.ChangedAt, cancellationToken,
                    amount, ("payer", WalletEntryTitles.Mask(integrationEvent.PayerWalletNumber)));
                break;
        }
    }

    // ---- Bills, top-ups, merchants ----

    public Task HandleAsync(BillPaymentChanged integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return ChangedAsync(
            integrationEvent.UserId, "bill", integrationEvent.PaymentId, integrationEvent.Status, integrationEvent.Step, integrationEvent.Detail, integrationEvent.ChangedAt, cancellationToken,
            ("amount", Rupees(integrationEvent.AmountPaisa)), ("biller", integrationEvent.Biller));
    }

    public Task HandleAsync(BillDueReminder integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return NotifyAsync(
            integrationEvent.UserId, TemplateCatalog.BillDue, $"bill_due:{integrationEvent.SavedBillerId}:{integrationEvent.BillingMonth}", context.OccurredAt, cancellationToken,
            ("biller", integrationEvent.Biller),
            ("name", integrationEvent.Nickname ?? integrationEvent.ConsumerNumber),
            ("month", Month(integrationEvent.BillingMonth)),
            ("amount", Rupees(integrationEvent.AmountPaisa)),
            ("due", Date(integrationEvent.DueDate)));
    }

    public Task HandleAsync(BillPaymentDeadLettered integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        Ops("dead_letter", "High", $"Bill payment stuck: {integrationEvent.Biller} {integrationEvent.ConsumerNumber}, {Rupees(integrationEvent.AmountPaisa)}",
            $"{integrationEvent.LastState}: {integrationEvent.Reason}", integrationEvent.UserId, integrationEvent.DeadLetteredAt);
        return Task.CompletedTask;
    }

    public Task HandleAsync(TopUpChanged integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return ChangedAsync(
            integrationEvent.UserId, "topup", integrationEvent.TopUpId, integrationEvent.Status, integrationEvent.Step, integrationEvent.Detail, integrationEvent.ChangedAt, cancellationToken,
            ("amount", Rupees(integrationEvent.AmountPaisa)), ("network", integrationEvent.Network));
    }

    public Task HandleAsync(TopUpDeadLettered integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        Ops("dead_letter", "High", $"Top-up stuck: {integrationEvent.Network} {integrationEvent.Msisdn}, {Rupees(integrationEvent.AmountPaisa)}",
            $"{integrationEvent.LastState}: {integrationEvent.Reason}", integrationEvent.UserId, integrationEvent.DeadLetteredAt);
        return Task.CompletedTask;
    }

    public Task HandleAsync(MerchantPaymentChanged integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return ChangedAsync(
            integrationEvent.PayerUserId, "merchant", integrationEvent.PaymentId, integrationEvent.Status, null, "The merchant payment could not be completed.", integrationEvent.ChangedAt, cancellationToken,
            ("amount", Rupees(integrationEvent.AmountPaisa)));
    }

    // ---- Agents ----

    public async Task HandleAsync(AgentStatusChanged integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        await recipients.SetAgentOwnerAsync(integrationEvent.AgentId, integrationEvent.OwnerUserId, cancellationToken);
        var reason = ("reason", integrationEvent.Reason is { Length: > 0 } given ? Sentence(given) : Phrase("no_reason"));
        var code = ("code", integrationEvent.AgentCode);
        var subject = $"agent:{integrationEvent.AgentId}";
        switch (integrationEvent.Status)
        {
            case "Active":
                await NotifyAsync(integrationEvent.OwnerUserId, TemplateCatalog.AgentApproved, $"{subject}:active", integrationEvent.ChangedAt, cancellationToken, code);
                break;
            case "Suspended":
                await NotifyAsync(integrationEvent.OwnerUserId, TemplateCatalog.AgentSuspended, $"{subject}:suspended:{integrationEvent.ChangedAt.UtcTicks}", integrationEvent.ChangedAt, cancellationToken, code, reason);
                break;
            case "Rejected":
                await NotifyAsync(integrationEvent.OwnerUserId, TemplateCatalog.AgentRejected, $"{subject}:rejected", integrationEvent.ChangedAt, cancellationToken, code, reason);
                break;
        }
    }

    /// <summary>Cash-in or cash-out at an agent: both the customer's and the agent's screens refresh.</summary>
    public async Task HandleAsync(AgentTransactionChanged integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        var update = new PaymentUpdate("agent", integrationEvent.TransactionId, integrationEvent.Status, integrationEvent.Kind);
        if (integrationEvent.CustomerUserId is { } customer)
        {
            outbox.Push(realtime => realtime.PaymentChangedAsync(customer, update));
        }

        if (await store.AgentOwnerAsync(integrationEvent.AgentId, cancellationToken) is { } owner)
        {
            outbox.Push(realtime => realtime.PaymentChangedAsync(owner, update));
        }
    }

    /// <summary>At most one low-float message an hour per agent.</summary>
    public async Task HandleAsync(AgentFloatLow integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        await recipients.SetAgentOwnerAsync(integrationEvent.AgentId, integrationEvent.OwnerUserId, cancellationToken);
        await NotifyAsync(
            integrationEvent.OwnerUserId, TemplateCatalog.AgentFloatLow, $"float_low:{integrationEvent.AgentId}:{integrationEvent.At.UtcTicks / TimeSpan.TicksPerHour}", integrationEvent.At, cancellationToken,
            ("float", Rupees(integrationEvent.FloatPaisa)), ("threshold", Rupees(integrationEvent.ThresholdPaisa)));
        Ops("agent.float_low", "Medium", $"Agent float low: {Rupees(integrationEvent.FloatPaisa)} (alert at {Rupees(integrationEvent.ThresholdPaisa)})", null, integrationEvent.OwnerUserId, integrationEvent.At);
    }

    // ---- Fraud ----

    public Task HandleAsync(FraudAlertRaised integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        Ops("fraud.alert", integrationEvent.Severity, integrationEvent.Summary,
            $"{integrationEvent.Kind}: score {integrationEvent.Score}, {integrationEvent.Action}; rules {string.Join(", ", integrationEvent.Rules)}",
            integrationEvent.UserId, integrationEvent.RaisedAt);
        return Task.CompletedTask;
    }

    /// <summary>The customer is told their wallet is on hold, never why (no tipping off); staff see the reason.</summary>
    public async Task HandleAsync(AccountFreezeChanged integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        var frozen = integrationEvent.Frozen;
        await NotifyAsync(
            integrationEvent.UserId, frozen ? TemplateCatalog.AccountFrozen : TemplateCatalog.AccountUnfrozen,
            $"freeze:{integrationEvent.ChangedAt.UtcTicks}:{frozen}", integrationEvent.ChangedAt, cancellationToken);
        Ops(frozen ? "account.frozen" : "account.unfrozen", frozen ? "High" : "Low", $"Account {(frozen ? "frozen" : "unfrozen")} by {integrationEvent.By}",
            integrationEvent.Reason, integrationEvent.UserId, integrationEvent.ChangedAt);
    }

    public Task HandleAsync(FraudCaseChanged integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        Ops("fraud.case", "Low", $"{integrationEvent.Kind} case {integrationEvent.Status.ToLowerInvariant()}",
            integrationEvent.Resolution ?? (integrationEvent.AssignedTo is { } assignee ? $"Assigned to {assignee}" : null), integrationEvent.UserId, integrationEvent.ChangedAt);
        return Task.CompletedTask;
    }

    // ---- Helpers ----

    /// <summary>A payment changed: the payer's screen refreshes live; a failure is also a notification.</summary>
    private async Task ChangedAsync(
        Guid userId, string kind, Guid id, string status, string? step, string detail, DateTimeOffset at, CancellationToken cancellationToken, params (string Name, string Value)[] parameters)
    {
        var update = new PaymentUpdate(kind, id, status, step);
        outbox.Push(realtime => realtime.PaymentChangedAsync(userId, update));
        if (status == Failed)
        {
            await NotifyAsync(
                userId, TemplateCatalog.PaymentFailed, $"{kind}:{id}:failed", at, cancellationToken,
                [.. parameters, ("what", Phrase($"failed:{kind}")), ("reason", Sentence(detail))]);
        }
    }

    private Task NotifyAsync(Guid userId, string template, string dedupeKey, DateTimeOffset occurredAt, CancellationToken cancellationToken, params (string Name, string Value)[] parameters) =>
        notifier.NotifyAsync(new NotifyRequest(userId, template, Parameters(parameters), dedupeKey, occurredAt), cancellationToken);

    private void Ops(string kind, string severity, string title, string? detail, Guid? userId, DateTimeOffset at)
    {
        var opsEvent = new OpsEvent(Guid.CreateVersion7(), kind, severity, title, detail, userId, at);
        outbox.Push(realtime => realtime.OpsAsync(opsEvent));
    }

    private static Dictionary<string, string> Parameters((string Name, string Value)[] parameters) =>
        parameters.ToDictionary(parameter => parameter.Name, parameter => parameter.Value, StringComparer.Ordinal);

    /// <summary>A reason as a sentence, so the text after it reads right.</summary>
    private static string Sentence(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length == 0 || trimmed[^1] is '.' or '!' or '?' or '۔' ? trimmed : $"{trimmed}.";
    }
}
