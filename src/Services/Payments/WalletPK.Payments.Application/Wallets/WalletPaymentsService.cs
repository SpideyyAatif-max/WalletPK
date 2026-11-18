using WalletPK.Common.Primitives;
using WalletPK.Common.Results;
using WalletPK.Payments.Application.BankTransfers;
using WalletPK.Payments.Domain;
using WalletPK.Sagas;

namespace WalletPK.Payments.Application.Wallets;

/// <summary>Who is calling: from the access token.</summary>
/// <param name="UserId">The customer.</param>
/// <param name="WalletNumber">Their wallet number (the phone number in the token, E.164).</param>
/// <param name="DeviceId">Their device.</param>
public sealed record Caller(Guid UserId, string? WalletNumber, string? DeviceId);

public sealed record TimelineStep(int Sequence, string State, string Detail, DateTimeOffset At);

public sealed record P2PTransferView(
    Guid Id,
    string Status,
    string Step,
    string ToWalletNumber,
    long AmountPaisa,
    long FeePaisa,
    long TaxPaisa,
    string? Note,
    string? FailureCode,
    string? FailureReason,
    DateTimeOffset RequestedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<TimelineStep>? Timeline);

/// <summary>A money request as one of its two people sees it; Direction is "incoming" (you are asked to pay) or "outgoing" (you asked).</summary>
public sealed record MoneyRequestView(
    Guid Id,
    string Status,
    string Direction,
    string RequesterWalletNumber,
    string PayerWalletNumber,
    long AmountPaisa,
    string? Note,
    DateTimeOffset ExpiresAt,
    Guid? SplitId,
    string? SplitTitle,
    string? LastFailureCode,
    string? LastFailureReason,
    string? DeclineReason,
    long FeePaisa,
    long TaxPaisa,
    DateTimeOffset RequestedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<TimelineStep>? Timeline);

public sealed record SplitShareView(Guid RequestId, string WalletNumber, long AmountPaisa, string Status, DateTimeOffset? PaidAt);

public sealed record SplitView(
    Guid Id,
    string Title,
    long TotalPaisa,
    long OwnSharePaisa,
    long CollectedPaisa,
    long OutstandingPaisa,
    int PaidCount,
    IReadOnlyList<SplitShareView> Shares,
    DateTimeOffset CreatedAt);

/// <param name="WalletNumber">A participant's wallet (mobile) number.</param>
/// <param name="AmountPaisa">Their share, or null to split equally.</param>
public sealed record SplitParticipant(string? WalletNumber, long? AmountPaisa);

public sealed record SentP2PTransfer(P2PTransferView Transfer, bool Created);

public sealed record CreatedMoneyRequest(MoneyRequestView Request, bool Created);

public sealed record CreatedSplit(SplitView Split, bool Created);

public static class WalletPaymentErrors
{
    public static readonly Error NoWalletNumber = Error.Unauthorized("payments.no_wallet_number", "The access token has no phone number.");
    public static readonly Error InvalidWalletNumber = Error.Validation("payments.invalid_wallet_number", "A wallet number is a Pakistani mobile number, e.g. 03001234567.");
    public static readonly Error SelfTransfer = Error.Validation("payments.self", "You cannot send money to, or request money from, yourself.");
    public static readonly Error AmountOutOfRange = Error.Validation(
        "payments.amount_out_of_range",
        $"The amount is between {WalletMoney.Rupees(WalletTransferRules.MinAmountPaisa)} and {WalletMoney.Rupees(WalletTransferRules.MaxAmountPaisa)}.");
    public static readonly Error NoteTooLong = Error.Validation("payments.note_too_long", $"The note is at most {WalletTransferRules.MaxNoteLength} characters.");
    public static readonly Error ExpiryOutOfRange = Error.Validation("payments.expiry_out_of_range", $"A request lasts 1 to {WalletTransferRules.MaxRequestHours} hours.");
    public static readonly Error KeyReused = Error.Conflict("payments.idempotency_key_reused", "This Idempotency-Key was already used for something else.");
    public static readonly Error TransferNotFound = Error.NotFound("payments.transfer_not_found", "No such transfer.");
    public static readonly Error RequestNotFound = Error.NotFound("payments.request_not_found", "No such money request.");
    public static readonly Error SplitNotFound = Error.NotFound("payments.split_not_found", "No such split bill.");
    public static readonly Error NotPayer = Error.Forbidden("payments.not_payer", "Only the person asked to pay can do this.");
    public static readonly Error NotRequester = Error.Forbidden("payments.not_requester", "Only the person who asked can cancel the request.");

    public static Error NotPending(string status) => Error.Conflict("payments.request_not_pending", $"The request is {status.ToLowerInvariant()}, not waiting for payment.");

    public static readonly Error SplitTitle = Error.Validation("payments.split_title", $"A split bill needs a title of at most {WalletTransferRules.MaxSplitTitleLength} characters.");
    public static readonly Error SplitParticipants = Error.Validation("payments.split_participants", $"A split bill has 1 to {WalletTransferRules.MaxSplitParticipants} other people, each once.");
    public static readonly Error SplitAmounts = Error.Validation(
        "payments.split_amounts",
        "Give every participant an amount, or none (equal shares); the shares cannot add up to more than the total, and each is at least Rs. 1.");
}

/// <summary>
/// Wallet payments through Payments: sending money (M6.1), asking for it, and splitting bills (M6.2).
/// Every operation starts or moves on a Saga; the money itself moves in the Ledger, one journal per
/// payment, so it is instant and all-or-nothing.
/// </summary>
public sealed class WalletPaymentsService(
    ISagaEngine engine,
    ISagaReader reader,
    ISagaQueries queries,
    P2PTransferSaga p2pTransfers,
    MoneyRequestSaga moneyRequests,
    TimeProvider timeProvider)
{
    // ---- M6.1 Send money ----

    public async Task<Result<SentP2PTransfer>> SendAsync(Caller caller, string idempotencyKey, string? toWalletNumber, long amountPaisa, string? note, string? correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var checkedInput = Check(caller, toWalletNumber, amountPaisa, note);
        if (checkedInput.IsFailure)
        {
            return checkedInput.Error;
        }

        var (me, to, cleanNote) = checkedInput.Value;
        var id = SagaIds.From("p2p-transfer", caller.UserId, idempotencyKey);
        var data = new P2PTransferData
        {
            SenderUserId = caller.UserId,
            SenderWalletNumber = me,
            IdempotencyKey = idempotencyKey,
            ToWalletNumber = to,
            AmountPaisa = amountPaisa,
            Note = cleanNote,
            DeviceId = caller.DeviceId,
            RequestedAt = timeProvider.GetUtcNow(),
        };

        var created = await engine.StartAsync(p2pTransfers, id, data, correlationId, cancellationToken);
        var saga = await reader.FindAsync<P2PTransferData>(id, cancellationToken) ?? throw new InvalidOperationException($"Transfer {id} vanished.");
        if (!created && (saga.Data.SenderUserId != caller.UserId || saga.Data.ToWalletNumber != to || saga.Data.AmountPaisa != amountPaisa))
        {
            return WalletPaymentErrors.KeyReused;
        }

        return new SentP2PTransfer(ToView(saga, withTimeline: true), created);
    }

    public async Task<Result<P2PTransferView>> GetTransferAsync(Caller caller, Guid id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var saga = await reader.FindAsync<P2PTransferData>(id, cancellationToken);
        return saga is null || saga.SagaType != P2PTransferSaga.SagaName || saga.Data.SenderUserId != caller.UserId
            ? WalletPaymentErrors.TransferNotFound
            : ToView(saga, withTimeline: true);
    }

    public async Task<IReadOnlyList<P2PTransferView>> ListTransfersAsync(Caller caller, int? limit, DateTimeOffset? before, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var sagas = await queries.ListAsync<P2PTransferData>(P2PTransferSaga.SagaName, SagaListField.SenderUserId, caller.UserId.ToString(), Limit(limit), before, cancellationToken);
        return [.. sagas.Select(saga => ToView(saga, withTimeline: false))];
    }

    // ---- M6.2 Request money ----

    public async Task<Result<CreatedMoneyRequest>> RequestAsync(
        Caller caller,
        string idempotencyKey,
        string? fromWalletNumber,
        long amountPaisa,
        string? note,
        int? expiresInHours,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var checkedInput = Check(caller, fromWalletNumber, amountPaisa, note);
        if (checkedInput.IsFailure)
        {
            return checkedInput.Error;
        }

        if (expiresInHours is < 1 or > WalletTransferRules.MaxRequestHours)
        {
            return WalletPaymentErrors.ExpiryOutOfRange;
        }

        var (me, payer, cleanNote) = checkedInput.Value;
        var now = timeProvider.GetUtcNow();
        var id = SagaIds.From("money-request", caller.UserId, idempotencyKey);
        var data = new MoneyRequestData
        {
            RequesterUserId = caller.UserId,
            RequesterWalletNumber = me,
            PayerWalletNumber = payer,
            AmountPaisa = amountPaisa,
            Note = cleanNote,
            RequestedAt = now,
            ExpiresAt = now.AddHours(expiresInHours ?? WalletTransferRules.DefaultRequestHours),
        };

        var created = await engine.StartAsync(moneyRequests, id, data, correlationId, cancellationToken);
        var saga = await reader.FindAsync<MoneyRequestData>(id, cancellationToken) ?? throw new InvalidOperationException($"Request {id} vanished.");
        if (!created && (saga.Data.RequesterUserId != caller.UserId || saga.Data.PayerWalletNumber != payer || saga.Data.AmountPaisa != amountPaisa))
        {
            return WalletPaymentErrors.KeyReused;
        }

        return new CreatedMoneyRequest(ToView(saga, me, withTimeline: true), created);
    }

    /// <summary>A request the caller asked for or is asked to pay.</summary>
    public async Task<Result<MoneyRequestView>> GetRequestAsync(Caller caller, Guid id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var saga = await FindRequestAsync(caller, id, cancellationToken);
        return saga is null ? WalletPaymentErrors.RequestNotFound : ToView(saga, caller.WalletNumber, withTimeline: true);
    }

    /// <param name="caller">Who.</param>
    /// <param name="incoming">True: requests the caller is asked to pay; false: requests the caller made.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="before">Paging cursor (requestedAt of the last one).</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    public async Task<Result<IReadOnlyList<MoneyRequestView>>> ListRequestsAsync(Caller caller, bool incoming, int? limit, DateTimeOffset? before, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (incoming && caller.WalletNumber is null)
        {
            return WalletPaymentErrors.NoWalletNumber;
        }

        var sagas = incoming
            ? await queries.ListAsync<MoneyRequestData>(MoneyRequestSaga.SagaName, SagaListField.PayerWalletNumber, caller.WalletNumber!, Limit(limit), before, cancellationToken)
            : await queries.ListAsync<MoneyRequestData>(MoneyRequestSaga.SagaName, SagaListField.RequesterUserId, caller.UserId.ToString(), Limit(limit), before, cancellationToken);
        return Result.Success<IReadOnlyList<MoneyRequestView>>([.. sagas.Select(saga => ToView(saga, caller.WalletNumber, withTimeline: false))]);
    }

    /// <summary>The payer pays: the money moves from their wallet to the requester's (answer arrives in a moment).</summary>
    public Task<Result<MoneyRequestView>> PayAsync(Caller caller, Guid id, CancellationToken cancellationToken) =>
        ActAsync(caller, id, asPayer: true, saga => new PayMoneyRequest(caller.UserId, caller.DeviceId), cancellationToken);

    public Task<Result<MoneyRequestView>> DeclineAsync(Caller caller, Guid id, string? reason, CancellationToken cancellationToken) =>
        ActAsync(caller, id, asPayer: true, _ => new DeclineMoneyRequest(string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()[..Math.Min(reason.Trim().Length, WalletTransferRules.MaxNoteLength)]), cancellationToken);

    public Task<Result<MoneyRequestView>> CancelAsync(Caller caller, Guid id, CancellationToken cancellationToken) =>
        ActAsync(caller, id, asPayer: false, _ => new CancelMoneyRequest(), cancellationToken);

    private async Task<Result<MoneyRequestView>> ActAsync(Caller caller, Guid id, bool asPayer, Func<SagaSnapshot<MoneyRequestData>, object> message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var saga = await FindRequestAsync(caller, id, cancellationToken);
        if (saga is null)
        {
            return WalletPaymentErrors.RequestNotFound;
        }

        if (asPayer && saga.Data.PayerWalletNumber != caller.WalletNumber)
        {
            return WalletPaymentErrors.NotPayer;
        }

        if (!asPayer && saga.Data.RequesterUserId != caller.UserId)
        {
            return WalletPaymentErrors.NotRequester;
        }

        if (saga.State != MoneyRequestStates.Pending)
        {
            return WalletPaymentErrors.NotPending(saga.State);
        }

        // The Saga checks the state again under its row lock (two taps on "Pay" pay once).
        await engine.HandleAsync(id, message(saga), causationId: null, cancellationToken);
        var after = await reader.FindAsync<MoneyRequestData>(id, cancellationToken);
        return ToView(after!, caller.WalletNumber, withTimeline: true);
    }

    private async Task<SagaSnapshot<MoneyRequestData>?> FindRequestAsync(Caller caller, Guid id, CancellationToken cancellationToken)
    {
        var saga = await reader.FindAsync<MoneyRequestData>(id, cancellationToken);
        var mine = saga is not null && saga.SagaType == MoneyRequestSaga.SagaName
            && (saga.Data.RequesterUserId == caller.UserId || (caller.WalletNumber is not null && saga.Data.PayerWalletNumber == caller.WalletNumber));
        return mine ? saga : null;
    }

    // ---- M6.2 Split a bill ----

    /// <summary>
    /// Splits <paramref name="totalPaisa"/> between the caller and <paramref name="participants"/>: one money
    /// request per participant, all created together (one transaction). Equal shares when no amounts are
    /// given (the caller's share, when <paramref name="includeMe"/>, is not requested from anyone).
    /// </summary>
    public async Task<Result<CreatedSplit>> SplitAsync(
        Caller caller,
        string idempotencyKey,
        string? title,
        long totalPaisa,
        IReadOnlyList<SplitParticipant>? participants,
        bool includeMe,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.WalletNumber is null || !PakistaniMobileNumber.TryNormalize(caller.WalletNumber, out var me))
        {
            return WalletPaymentErrors.NoWalletNumber;
        }

        var cleanTitle = title?.Trim();
        if (string.IsNullOrEmpty(cleanTitle) || cleanTitle.Length > WalletTransferRules.MaxSplitTitleLength)
        {
            return WalletPaymentErrors.SplitTitle;
        }

        if (totalPaisa is < WalletTransferRules.MinAmountPaisa or > WalletTransferRules.MaxAmountPaisa)
        {
            return WalletPaymentErrors.AmountOutOfRange;
        }

        if (participants is not { Count: >= 1 and <= WalletTransferRules.MaxSplitParticipants })
        {
            return WalletPaymentErrors.SplitParticipants;
        }

        var numbers = new List<string>();
        foreach (var participant in participants)
        {
            if (!PakistaniMobileNumber.TryNormalize(participant.WalletNumber, out var number))
            {
                return WalletPaymentErrors.InvalidWalletNumber;
            }

            if (number == me)
            {
                return WalletPaymentErrors.SelfTransfer;
            }

            numbers.Add(number);
        }

        if (numbers.Distinct().Count() != numbers.Count)
        {
            return WalletPaymentErrors.SplitParticipants;
        }

        var shares = Shares(totalPaisa, participants, includeMe);
        if (shares is null)
        {
            return WalletPaymentErrors.SplitAmounts;
        }

        var ownShare = totalPaisa - shares.Sum();
        var splitId = SagaIds.From("split", caller.UserId, idempotencyKey);
        var now = timeProvider.GetUtcNow();

        var created = await engine.InTransactionAsync(
            async () =>
            {
                var any = false;
                for (var i = 0; i < numbers.Count; i++)
                {
                    var data = new MoneyRequestData
                    {
                        RequesterUserId = caller.UserId,
                        RequesterWalletNumber = me,
                        PayerWalletNumber = numbers[i],
                        AmountPaisa = shares[i],
                        Note = cleanTitle,
                        RequestedAt = now,
                        ExpiresAt = now.AddHours(WalletTransferRules.DefaultRequestHours),
                        SplitId = splitId,
                        SplitTitle = cleanTitle,
                        SplitTotalPaisa = totalPaisa,
                        SplitOwnSharePaisa = ownShare,
                    };
                    any |= await engine.StartAsync(moneyRequests, SagaIds.From("split-share", splitId, numbers[i]), data, correlationId, cancellationToken);
                }

                return any;
            },
            cancellationToken);

        var split = await GetSplitAsync(caller, splitId, cancellationToken);
        if (split.IsFailure)
        {
            return split.Error;
        }

        if (!created && (split.Value.TotalPaisa != totalPaisa || split.Value.Title != cleanTitle || split.Value.Shares.Count != numbers.Count))
        {
            return WalletPaymentErrors.KeyReused;
        }

        return new CreatedSplit(split.Value, created);
    }

    public async Task<Result<SplitView>> GetSplitAsync(Caller caller, Guid splitId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var requests = await queries.ListAsync<MoneyRequestData>(MoneyRequestSaga.SagaName, SagaListField.SplitId, splitId.ToString(), WalletTransferRules.MaxSplitParticipants, null, cancellationToken);
        if (requests.Count == 0 || requests[0].Data.RequesterUserId != caller.UserId)
        {
            return WalletPaymentErrors.SplitNotFound;
        }

        var first = requests[0].Data;
        var shares = requests
            .OrderBy(request => request.Data.PayerWalletNumber, StringComparer.Ordinal)
            .Select(request => new SplitShareView(
                request.Id,
                request.Data.PayerWalletNumber,
                request.Data.AmountPaisa,
                request.State,
                request.State == MoneyRequestStates.Paid ? request.CompletedAt : null))
            .ToList();
        var collected = shares.Where(share => share.Status == MoneyRequestStates.Paid).Sum(share => share.AmountPaisa);
        var requested = shares.Sum(share => share.AmountPaisa);
        var outstanding = shares.Where(share => share.Status is MoneyRequestStates.Pending or MoneyRequestStates.Paying).Sum(share => share.AmountPaisa);

        return new SplitView(
            splitId,
            first.SplitTitle ?? "",
            first.SplitTotalPaisa ?? requested,
            first.SplitOwnSharePaisa ?? 0,
            collected,
            outstanding,
            shares.Count(share => share.Status == MoneyRequestStates.Paid),
            shares,
            requests.Min(request => request.CreatedAt));
    }

    public async Task<IReadOnlyList<SplitView>> ListSplitsAsync(Caller caller, int? limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var ids = await queries.SplitIdsAsync(caller.UserId, Limit(limit), cancellationToken);
        var splits = new List<SplitView>();
        foreach (var id in ids)
        {
            if (await GetSplitAsync(caller, id, cancellationToken) is { IsSuccess: true } split)
            {
                splits.Add(split.Value);
            }
        }

        return splits;
    }

    /// <summary>Each participant's share; null when the amounts given do not work.</summary>
    internal static long[]? Shares(long totalPaisa, IReadOnlyList<SplitParticipant> participants, bool includeMe)
    {
        var given = participants.Count(participant => participant.AmountPaisa is not null);
        long[] shares;
        if (given == 0)
        {
            // Equal shares; the paisa that do not divide evenly go to the first participants.
            var people = participants.Count + (includeMe ? 1 : 0);
            var each = totalPaisa / people;
            var left = totalPaisa - (each * people);
            shares = [.. participants.Select((_, index) => each + (index < left ? 1 : 0))];
        }
        else if (given == participants.Count)
        {
            shares = [.. participants.Select(participant => participant.AmountPaisa!.Value)];
            if (shares.Sum() > totalPaisa || (!includeMe && shares.Sum() != totalPaisa))
            {
                return null;
            }
        }
        else
        {
            return null;
        }

        return shares.All(share => share >= WalletTransferRules.MinAmountPaisa) ? shares : null;
    }

    // ---- Helpers ----

    private static Result<(string Me, string Other, string? Note)> Check(Caller caller, string? otherWalletNumber, long amountPaisa, string? note)
    {
        if (caller.WalletNumber is null || !PakistaniMobileNumber.TryNormalize(caller.WalletNumber, out var me))
        {
            return WalletPaymentErrors.NoWalletNumber;
        }

        if (!PakistaniMobileNumber.TryNormalize(otherWalletNumber, out var other))
        {
            return WalletPaymentErrors.InvalidWalletNumber;
        }

        if (other == me)
        {
            return WalletPaymentErrors.SelfTransfer;
        }

        if (amountPaisa is < WalletTransferRules.MinAmountPaisa or > WalletTransferRules.MaxAmountPaisa)
        {
            return WalletPaymentErrors.AmountOutOfRange;
        }

        var cleanNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        return cleanNote is { Length: > WalletTransferRules.MaxNoteLength }
            ? WalletPaymentErrors.NoteTooLong
            : (me, other, cleanNote);
    }

    private static int Limit(int? limit) => Math.Clamp(limit ?? 20, 1, 100);

    private static IReadOnlyList<TimelineStep> Timeline(IReadOnlyList<SagaStep> steps) =>
        [.. steps.Select(step => new TimelineStep(step.Sequence, step.State, step.Detail, step.At))];

    internal static P2PTransferView ToView(SagaSnapshot<P2PTransferData> saga, bool withTimeline)
    {
        var data = saga.Data;
        return new P2PTransferView(
            saga.Id, data.Status, saga.State, data.ToWalletNumber, data.AmountPaisa, data.FeePaisa, data.TaxPaisa, data.Note,
            data.FailureCode, data.FailureReason, data.RequestedAt, saga.UpdatedAt, saga.CompletedAt,
            withTimeline ? Timeline(saga.Steps) : null);
    }

    internal static MoneyRequestView ToView(SagaSnapshot<MoneyRequestData> saga, string? viewerWalletNumber, bool withTimeline)
    {
        var data = saga.Data;
        var incoming = viewerWalletNumber is not null && data.PayerWalletNumber == viewerWalletNumber && data.RequesterWalletNumber != viewerWalletNumber;
        return new MoneyRequestView(
            saga.Id, saga.State, incoming ? "incoming" : "outgoing", data.RequesterWalletNumber, data.PayerWalletNumber, data.AmountPaisa, data.Note,
            data.ExpiresAt, data.SplitId, data.SplitTitle, data.LastFailureCode, data.LastFailureReason, data.DeclineReason, data.FeePaisa, data.TaxPaisa,
            data.RequestedAt, saga.UpdatedAt, saga.CompletedAt, withTimeline ? Timeline(saga.Steps) : null);
    }
}
