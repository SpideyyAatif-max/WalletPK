using WalletPK.Agents.Domain;
using WalletPK.Common.Primitives;
using WalletPK.Common.Results;
using WalletPK.Contracts.Agents;
using WalletPK.Contracts.Ledger;
using WalletPK.Sagas;

namespace WalletPK.Agents.Application;

/// <summary>Who is calling: from the access token.</summary>
/// <param name="UserId">The user.</param>
/// <param name="WalletNumber">Their wallet number (the phone number in the token, E.164).</param>
/// <param name="DeviceId">Their device.</param>
/// <param name="KycTier">"L0", "L1" or "L2".</param>
public sealed record Caller(Guid UserId, string? WalletNumber, string? DeviceId, string? KycTier);

/// <summary>
/// Agents (M9): applying and approval, cash-in, the customer's cash-out request and the agent taking it,
/// float moves, and what the agent has earned.
/// </summary>
public sealed class AgentService(
    IAgentStore store,
    ISagaEngine engine,
    ISagaReader reader,
    ISagaLists lists,
    AgentTransactionSaga transactions,
    AgentOptions options,
    TimeProvider timeProvider)
{
    // ---- Onboarding (M9.1) ----

    /// <summary>
    /// Applies to be an agent. With AutoApprove the float is opened at once (OpenAgentWallet, through the
    /// outbox); otherwise the application waits for operations.
    /// </summary>
    public async Task<Result<AgentView>> ApplyAsync(Caller caller, string? shopName, string? city, string? area, string? correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (Tier(caller.KycTier) < Tier(options.MinimumKycTier))
        {
            return AgentErrors.KycTooLow;
        }

        if (await store.ByOwnerAsync(caller.UserId, cancellationToken) is not null)
        {
            return AgentErrors.AlreadyAgent;
        }

        var name = shopName?.Trim();
        if (name is not { Length: >= AgentRules.MinShopNameLength and <= AgentRules.MaxShopNameLength })
        {
            return AgentErrors.InvalidShopName;
        }

        var cleanCity = city?.Trim();
        if (cleanCity is not { Length: >= 2 and <= AgentRules.MaxPlaceLength })
        {
            return AgentErrors.InvalidCity;
        }

        var cleanArea = string.IsNullOrWhiteSpace(area) ? null : area.Trim()[..Math.Min(area.Trim().Length, AgentRules.MaxPlaceLength)];

        // Six digits: pick another ID in the rare case its code is taken.
        var id = Guid.CreateVersion7();
        while (await store.ByCodeAsync(CodeFor(id), cancellationToken) is not null)
        {
            id = Guid.CreateVersion7();
        }

        var now = timeProvider.GetUtcNow();
        var agent = new Agent
        {
            Id = id,
            OwnerUserId = caller.UserId,
            Code = CodeFor(id),
            ShopName = name,
            City = cleanCity,
            Area = cleanArea,
            CreatedAt = now,
            UpdatedAt = now,
        };
        store.Add(agent);
        if (options.AutoApprove)
        {
            Open(agent, correlationId);
        }

        await store.SaveChangesAsync(cancellationToken);
        return await ViewAsync(agent, cancellationToken);
    }

    public async Task<Result<AgentView>> MeAsync(Guid ownerUserId, CancellationToken cancellationToken) =>
        await store.ByOwnerAsync(ownerUserId, cancellationToken) is { } agent ? await ViewAsync(agent, cancellationToken) : AgentErrors.NotAgent;

    /// <summary>Sets the float level below which the agent is alerted.</summary>
    public async Task<Result<AgentView>> SetLowFloatAsync(Guid ownerUserId, long paisa, CancellationToken cancellationToken)
    {
        if (await store.ByOwnerAsync(ownerUserId, cancellationToken) is not { } agent)
        {
            return AgentErrors.NotAgent;
        }

        if (paisa is < 0 or > 100_000_000)
        {
            return AgentErrors.InvalidThreshold;
        }

        agent.LowFloatPaisa = paisa;
        agent.UpdatedAt = timeProvider.GetUtcNow();
        await store.SaveChangesAsync(cancellationToken);
        return await ViewAsync(agent, cancellationToken);
    }

    /// <summary>Active agents in a city, for customers looking for one.</summary>
    public async Task<IReadOnlyList<AgentListing>> FindAsync(string? city, CancellationToken cancellationToken) =>
        [.. (await store.ActiveAsync(string.IsNullOrWhiteSpace(city) ? null : city.Trim(), 50, cancellationToken)).Select(Listing)];

    public async Task<Result<AgentListing>> ByCodeAsync(string? code, CancellationToken cancellationToken) =>
        code is not null && await store.ByCodeAsync(code.Trim().ToUpperInvariant(), cancellationToken) is { Status: AgentStatus.Active } agent
            ? Listing(agent)
            : AgentErrors.UnknownAgent;

    // ---- Operations: approve, reject, suspend, reactivate ----

    public async Task<IReadOnlyList<AgentView>> PendingAsync(CancellationToken cancellationToken)
    {
        var pending = await store.WithStatusAsync(AgentStatus.PendingApproval, 100, cancellationToken);
        var views = new List<AgentView>(pending.Count);
        foreach (var agent in pending)
        {
            views.Add(await ViewAsync(agent, cancellationToken));
        }

        return views;
    }

    public Task<Result<AgentView>> ApproveAsync(Guid agentId, string? correlationId, CancellationToken cancellationToken) =>
        ChangeAsync(agentId, AgentStatus.PendingApproval, agent => Open(agent, correlationId), null, cancellationToken);

    public Task<Result<AgentView>> RejectAsync(Guid agentId, string? reason, CancellationToken cancellationToken) =>
        ChangeAsync(agentId, AgentStatus.PendingApproval, agent => agent.Status = AgentStatus.Rejected, reason, cancellationToken);

    public Task<Result<AgentView>> SuspendAsync(Guid agentId, string? reason, CancellationToken cancellationToken) =>
        ChangeAsync(agentId, AgentStatus.Active, agent => agent.Status = AgentStatus.Suspended, reason, cancellationToken);

    public Task<Result<AgentView>> ReactivateAsync(Guid agentId, CancellationToken cancellationToken) =>
        ChangeAsync(agentId, AgentStatus.Suspended, agent => agent.Status = AgentStatus.Active, null, cancellationToken);

    private async Task<Result<AgentView>> ChangeAsync(Guid agentId, string from, Action<Agent> change, string? reason, CancellationToken cancellationToken)
    {
        if (await store.ByIdAsync(agentId, cancellationToken) is not { } agent)
        {
            return AgentErrors.NotAgent;
        }

        if (agent.Status != from)
        {
            return AgentErrors.WrongStatus;
        }

        change(agent);
        agent.StatusReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()[..Math.Min(reason.Trim().Length, 200)];
        agent.UpdatedAt = timeProvider.GetUtcNow();
        store.Publish(new AgentStatusChanged(agent.Id, agent.OwnerUserId, agent.Code, agent.Status, agent.StatusReason, agent.UpdatedAt), null);
        await store.SaveChangesAsync(cancellationToken);
        return await ViewAsync(agent, cancellationToken);
    }

    /// <summary>Asks the Ledger for the float; the agent is Active when it answers.</summary>
    private void Open(Agent agent, string? correlationId)
    {
        agent.Status = AgentStatus.Opening;
        store.Publish(new OpenAgentWallet(agent.Id, agent.OwnerUserId, agent.Code, agent.ShopName), correlationId);
    }

    // ---- Cash-in (M9.2) ----

    /// <summary>The agent takes cash and deposits it to the customer's wallet from its float.</summary>
    public async Task<Result<AgentTransactionView>> CashInAsync(Caller agentCaller, string idempotencyKey, string? customerNumber, long amountPaisa, string? correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agentCaller);
        var id = SagaId("cash-in", agentCaller.UserId, idempotencyKey);
        if (await ExistingAsync(id, agentCaller.UserId, cancellationToken) is { } existing)
        {
            return existing;
        }

        var active = await ActiveAgentAsync(agentCaller.UserId, cancellationToken);
        if (active.IsFailure)
        {
            return active.Error;
        }

        var agent = active.Value;
        if (!PakistaniMobileNumber.TryNormalize(customerNumber, out var walletNumber))
        {
            return AgentErrors.InvalidNumber;
        }

        if (walletNumber == agentCaller.WalletNumber)
        {
            return AgentErrors.OwnNumber;
        }

        if (!AgentRules.IsValidCash(amountPaisa))
        {
            return AgentErrors.InvalidCashAmount;
        }

        var quote = CommissionPlan.CashIn(amountPaisa);
        var data = NewData(agent, AgentTransactionKind.CashIn, idempotencyKey, quote);
        data.CustomerWalletNumber = walletNumber;
        await engine.StartAsync(transactions, id, data, correlationId, cancellationToken);
        return ToView((await reader.FindAsync<AgentTransactionData>(id, cancellationToken))!, withTimeline: true);
    }

    // ---- Float moves (M9.1) ----

    /// <summary>Moves money between the agent's own wallet and its float ("ToFloat" or "ToWallet").</summary>
    public async Task<Result<AgentTransactionView>> MoveFloatAsync(Caller agentCaller, string idempotencyKey, string? direction, long amountPaisa, string? correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agentCaller);
        var id = SagaId("float", agentCaller.UserId, idempotencyKey);
        if (await ExistingAsync(id, agentCaller.UserId, cancellationToken) is { } existing)
        {
            return existing;
        }

        if (await store.ByOwnerAsync(agentCaller.UserId, cancellationToken) is not { } agent)
        {
            return AgentErrors.NotAgent;
        }

        var kind = direction switch
        {
            MoveAgentFloat.ToFloat => AgentTransactionKind.FloatIn,
            MoveAgentFloat.ToWallet => AgentTransactionKind.FloatOut,
            _ => null,
        };
        if (kind is null)
        {
            return AgentErrors.InvalidDirection;
        }

        // A suspended agent can still take its float back; nothing else.
        if (agent.Status != AgentStatus.Active && !(agent.Status == AgentStatus.Suspended && kind == AgentTransactionKind.FloatOut))
        {
            return AgentErrors.NotActive;
        }

        if (!AgentRules.IsValidFloatMove(amountPaisa))
        {
            return AgentErrors.InvalidFloatAmount;
        }

        var data = NewData(agent, kind, idempotencyKey, new AgentQuote(amountPaisa, 0, 0, 0, amountPaisa));
        await engine.StartAsync(transactions, id, data, correlationId, cancellationToken);
        return ToView((await reader.FindAsync<AgentTransactionData>(id, cancellationToken))!, withTimeline: true);
    }

    // ---- Cash-out (M9.3) ----

    /// <summary>
    /// The customer asks for cash at an agent: the fee is fixed now and a six-digit code is returned, once.
    /// Any earlier open request of the customer is cancelled.
    /// </summary>
    public async Task<Result<CashOutRequestView>> RequestCashOutAsync(Caller customer, string? agentCode, long amountPaisa, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(customer);
        if (customer.WalletNumber is null)
        {
            return AgentErrors.InvalidNumber;
        }

        if (agentCode is null || await store.ByCodeAsync(agentCode.Trim().ToUpperInvariant(), cancellationToken) is not { Status: AgentStatus.Active } agent)
        {
            return AgentErrors.UnknownAgent;
        }

        if (agent.OwnerUserId == customer.UserId)
        {
            return AgentErrors.OwnNumber;
        }

        if (!AgentRules.IsValidCash(amountPaisa))
        {
            return AgentErrors.InvalidCashAmount;
        }

        var now = timeProvider.GetUtcNow();
        foreach (var open in await store.OpenRequestsAsync(customer.UserId, now, cancellationToken))
        {
            open.CancelledAt = now;
        }

        var quote = CommissionPlan.CashOut(amountPaisa);
        var code = CashOutCodes.NewCode();
        var request = new CashOutRequest
        {
            Id = Guid.CreateVersion7(),
            CustomerUserId = customer.UserId,
            CustomerWalletNumber = customer.WalletNumber,
            CustomerDeviceId = customer.DeviceId,
            AgentId = agent.Id,
            AmountPaisa = quote.AmountPaisa,
            FeePaisa = quote.FeePaisa,
            TaxPaisa = quote.TaxPaisa,
            CommissionPaisa = quote.CommissionPaisa,
            CreatedAt = now,
            ExpiresAt = now.Add(CashOutCodes.Lifetime),
        };
        request.CodeHash = CashOutCodes.Hash(request.Id, code, options.CodeKey);
        store.Add(request);
        await store.SaveChangesAsync(cancellationToken);
        return await RequestViewAsync(request, agent, code, cancellationToken);
    }

    /// <summary>The customer's newest cash-out request (the code is not shown again).</summary>
    public async Task<Result<CashOutRequestView>> CurrentCashOutAsync(Guid customerUserId, CancellationToken cancellationToken)
    {
        if (await store.LatestRequestAsync(customerUserId, cancellationToken) is not { } request
            || await store.ByIdAsync(request.AgentId, cancellationToken) is not { } agent)
        {
            return AgentErrors.RequestNotFound;
        }

        return await RequestViewAsync(request, agent, null, cancellationToken);
    }

    public async Task<Result<CashOutRequestView>> CancelCashOutAsync(Guid customerUserId, Guid requestId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var open = (await store.OpenRequestsAsync(customerUserId, now, cancellationToken)).FirstOrDefault(request => request.Id == requestId);
        if (open is null)
        {
            return await store.RequestAsync(requestId, cancellationToken) is { } request && request.CustomerUserId == customerUserId
                ? AgentErrors.CannotCancel
                : AgentErrors.RequestNotFound;
        }

        open.CancelledAt = now;
        await store.SaveChangesAsync(cancellationToken);
        return await RequestViewAsync(open, (await store.ByIdAsync(open.AgentId, cancellationToken))!, null, cancellationToken);
    }

    /// <summary>
    /// The agent takes a customer's cash-out request with the code the customer shows: after five wrong
    /// codes the request is locked. The request is claimed and the cash-out Saga started together, so two
    /// tries cannot both pay out.
    /// </summary>
    public async Task<Result<AgentTransactionView>> TakeCashOutAsync(Caller agentCaller, string idempotencyKey, string? customerNumber, string? code, string? correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agentCaller);
        var id = SagaId("cash-out", agentCaller.UserId, idempotencyKey);
        if (await ExistingAsync(id, agentCaller.UserId, cancellationToken) is { } existing)
        {
            return existing;
        }

        var active = await ActiveAgentAsync(agentCaller.UserId, cancellationToken);
        if (active.IsFailure)
        {
            return active.Error;
        }

        var agent = active.Value;
        if (!PakistaniMobileNumber.TryNormalize(customerNumber, out var walletNumber))
        {
            return AgentErrors.InvalidNumber;
        }

        var now = timeProvider.GetUtcNow();
        if (await store.OpenRequestAtAsync(walletNumber, agent.Id, now, cancellationToken) is not { } request)
        {
            return AgentErrors.NoRequest;
        }

        if (!CashOutCodes.Matches(request.Id, code?.Trim(), request.CodeHash, options.CodeKey))
        {
            var attempts = await store.CountFailedAttemptAsync(request.Id, cancellationToken);
            return attempts >= CashOutCodes.MaxAttempts ? AgentErrors.CodeLocked : AgentErrors.WrongCode(CashOutCodes.MaxAttempts - attempts);
        }

        var data = NewData(agent, AgentTransactionKind.CashOut, idempotencyKey, new AgentQuote(request.AmountPaisa, request.FeePaisa, request.TaxPaisa, request.CommissionPaisa, request.AmountPaisa + request.FeePaisa + request.TaxPaisa));
        data.CustomerUserId = request.CustomerUserId;
        data.CustomerWalletNumber = request.CustomerWalletNumber;
        data.CustomerName = PakistaniMobileNumber.Mask(request.CustomerWalletNumber);
        data.CashOutRequestId = request.Id;
        data.DeviceId = request.CustomerDeviceId;

        var started = await engine.InTransactionAsync(
            async () =>
            {
                if (!await store.TryClaimAsync(request.Id, id, timeProvider.GetUtcNow(), cancellationToken))
                {
                    return false;
                }

                await engine.StartAsync(transactions, id, data, correlationId, cancellationToken);
                return true;
            },
            cancellationToken);
        if (!started)
        {
            return AgentErrors.CodeTaken;
        }

        return ToView((await reader.FindAsync<AgentTransactionData>(id, cancellationToken))!, withTimeline: true);
    }

    // ---- Reading ----

    /// <summary>A transaction, for its agent or its customer.</summary>
    public async Task<Result<AgentTransactionView>> TransactionAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        var saga = await reader.FindAsync<AgentTransactionData>(id, cancellationToken);
        return saga is not null && saga.SagaType == AgentTransactionSaga.SagaName && (saga.Data.AgentOwnerUserId == userId || saga.Data.CustomerUserId == userId)
            ? ToView(saga, withTimeline: true)
            : AgentErrors.TransactionNotFound;
    }

    /// <summary>The agent's transactions, newest first.</summary>
    public async Task<Result<IReadOnlyList<AgentTransactionView>>> AgentTransactionsAsync(Guid ownerUserId, int? limit, CancellationToken cancellationToken)
    {
        if (await store.ByOwnerAsync(ownerUserId, cancellationToken) is not { } agent)
        {
            return AgentErrors.NotAgent;
        }

        var sagas = await lists.ByFieldAsync<AgentTransactionData>(AgentTransactionSaga.SagaName, "agentId", agent.Id.ToString(), Math.Clamp(limit ?? 20, 1, 100), cancellationToken);
        return Result.Success<IReadOnlyList<AgentTransactionView>>([.. sagas.Select(saga => ToView(saga, withTimeline: false))]);
    }

    /// <summary>The customer's cash-ins and cash-outs at agents, newest first.</summary>
    public async Task<IReadOnlyList<AgentTransactionView>> CustomerTransactionsAsync(Guid customerUserId, int? limit, CancellationToken cancellationToken) =>
        [.. (await lists.ByFieldAsync<AgentTransactionData>(AgentTransactionSaga.SagaName, "customerUserId", customerUserId.ToString(), Math.Clamp(limit ?? 20, 1, 100), cancellationToken))
            .Select(saga => ToView(saga, withTimeline: false))];

    public async Task<Result<IReadOnlyList<PayoutView>>> PayoutsAsync(Guid ownerUserId, int? limit, CancellationToken cancellationToken)
    {
        if (await store.ByOwnerAsync(ownerUserId, cancellationToken) is not { } agent)
        {
            return AgentErrors.NotAgent;
        }

        var payouts = await store.PayoutsAsync(agent.Id, Math.Clamp(limit ?? 30, 1, 100), cancellationToken);
        return Result.Success<IReadOnlyList<PayoutView>>([.. payouts.Select(payout => new PayoutView(
            payout.Id, payout.BusinessDate, payout.TransactionCount, payout.GrossPaisa, payout.TaxPaisa, payout.NetPaisa, payout.Status, payout.FailureReason, payout.PaidAt))]);
    }

    /// <summary>What a cash-in or cash-out of <paramref name="amountPaisa"/> costs and earns.</summary>
    public static Result<AgentQuote> Quote(string? kind, long amountPaisa)
    {
        if (!AgentRules.IsValidCash(amountPaisa))
        {
            return AgentErrors.InvalidCashAmount;
        }

        return kind switch
        {
            AgentTransactionKind.CashIn => CommissionPlan.CashIn(amountPaisa),
            AgentTransactionKind.CashOut => CommissionPlan.CashOut(amountPaisa),
            _ => Error.Validation("agent.invalid_kind", "The kind is \"CashIn\" or \"CashOut\"."),
        };
    }

    // ---- Helpers ----

    private async Task<Result<Agent>> ActiveAgentAsync(Guid ownerUserId, CancellationToken cancellationToken) =>
        await store.ByOwnerAsync(ownerUserId, cancellationToken) switch
        {
            null => AgentErrors.NotAgent,
            { Status: AgentStatus.Active } agent => agent,
            _ => AgentErrors.NotActive,
        };

    /// <summary>A retry of a transaction already started returns it.</summary>
    private async Task<AgentTransactionView?> ExistingAsync(Guid id, Guid agentOwnerUserId, CancellationToken cancellationToken) =>
        await reader.FindAsync<AgentTransactionData>(id, cancellationToken) is { } saga && saga.Data.AgentOwnerUserId == agentOwnerUserId
            ? ToView(saga, withTimeline: true)
            : null;

    private AgentTransactionData NewData(Agent agent, string kind, string idempotencyKey, AgentQuote quote) => new()
    {
        Kind = kind,
        AgentId = agent.Id,
        AgentOwnerUserId = agent.OwnerUserId,
        AgentCode = agent.Code,
        AgentName = agent.ShopName,
        LowFloatPaisa = agent.LowFloatPaisa,
        IdempotencyKey = idempotencyKey,
        AmountPaisa = quote.AmountPaisa,
        FeePaisa = quote.FeePaisa,
        TaxPaisa = quote.TaxPaisa,
        CommissionPaisa = quote.CommissionPaisa,
        RequestedAt = timeProvider.GetUtcNow(),
    };

    private async Task<AgentView> ViewAsync(Agent agent, CancellationToken cancellationToken)
    {
        var unpaid = await store.UnpaidAsync(agent.Id, cancellationToken);
        return new AgentView(agent.Id, agent.Code, agent.ShopName, agent.City, agent.Area, agent.Status, agent.StatusReason, agent.LowFloatPaisa, unpaid.Count, unpaid.CommissionPaisa, agent.CreatedAt);
    }

    private async Task<CashOutRequestView> RequestViewAsync(CashOutRequest request, Agent agent, string? code, CancellationToken cancellationToken)
    {
        var saga = request.TransactionId is { } id ? await reader.FindAsync<AgentTransactionData>(id, cancellationToken) : null;
        var now = timeProvider.GetUtcNow();
        var status = saga?.State switch
        {
            "Completed" => "Completed",
            "Posting" or "NeedsReview" => "Processing",
            _ when request.CancelledAt is not null => "Cancelled",
            _ when request.FailedAttempts >= CashOutCodes.MaxAttempts => "Locked",
            _ when request.ExpiresAt <= now => "Expired",
            "Failed" => "Failed",
            _ => "Open",
        };
        return new CashOutRequestView(
            request.Id, code, status, agent.Code, agent.ShopName, request.AmountPaisa, request.FeePaisa, request.TaxPaisa,
            request.AmountPaisa + request.FeePaisa + request.TaxPaisa, request.ExpiresAt, request.TransactionId, saga?.State == "Failed" ? saga.Data.FailureReason : null);
    }

    private static AgentListing Listing(Agent agent) => new(agent.Code, agent.ShopName, agent.City, agent.Area);

    private static int Tier(string? tier) => tier switch
    {
        "L2" => 2,
        "L1" => 1,
        "L0" => 0,
        _ => -1,
    };

    /// <summary>"WPKA" and six digits derived from the agent's ID.</summary>
    internal static string CodeFor(Guid agentId)
    {
        var number = Math.Abs(BitConverter.ToInt64(agentId.ToByteArray(), 8) % 1_000_000);
        return $"WPKA{number:000000}";
    }

    internal static AgentTransactionView ToView(SagaSnapshot<AgentTransactionData> saga, bool withTimeline)
    {
        var data = saga.Data;
        return new AgentTransactionView(
            saga.Id, data.Kind, data.Status, saga.State, data.AgentCode, data.AgentName, data.CustomerWalletNumber is null ? null : PakistaniMobileNumber.Mask(data.CustomerWalletNumber),
            data.CustomerName, data.AmountPaisa, data.FeePaisa, data.TaxPaisa, data.CommissionPaisa, data.AgentBalancePaisa, data.FailureCode, data.FailureReason,
            data.RequestedAt, data.CompletedAt, withTimeline ? [.. saga.Steps.Select(step => new TimelineStep(step.Sequence, step.State, step.Detail, step.At))] : null);
    }

    /// <summary>The same inputs always give the same ID (a retry finds its Saga).</summary>
    public static Guid SagaId(string purpose, params object[] parts)
    {
        Span<byte> hash = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"walletpk:agents:{purpose}:{string.Join(':', parts)}"), hash);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }
}
