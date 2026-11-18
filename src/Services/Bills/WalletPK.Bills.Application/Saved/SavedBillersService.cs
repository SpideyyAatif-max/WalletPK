using WalletPK.Bills.Application.Billers;
using WalletPK.Bills.Application.Payments;
using WalletPK.Bills.Domain;
using WalletPK.Common.Results;

namespace WalletPK.Bills.Application.Saved;

/// <summary>A biller a customer pays every month (M7.6), with the last bill seen for it.</summary>
public sealed class SavedBiller
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string Biller { get; set; } = "";

    public string ConsumerNumber { get; set; } = "";

    public string? Nickname { get; set; }

    public string? CustomerName { get; set; }

    public string? LastBillingMonth { get; set; }

    public DateOnly? LastDueDate { get; set; }

    public long? LastPayablePaisa { get; set; }

    /// <summary>Unpaid or Paid, as of <see cref="LastCheckedAt"/>.</summary>
    public string? LastStatus { get; set; }

    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>When the reminder job looks at it next.</summary>
    public DateTimeOffset NextCheckAt { get; set; }

    /// <summary>The billing month the customer was last reminded of (one reminder per bill).</summary>
    public string? RemindedForMonth { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Remembers a bill the biller just reported.</summary>
    public void Remember(BillInfo bill, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(bill);
        CustomerName = bill.CustomerName;
        LastBillingMonth = bill.BillingMonth;
        LastDueDate = bill.DueDate;
        LastPayablePaisa = bill.PayablePaisa;
        LastStatus = bill.Status;
        LastCheckedAt = now;
    }
}

public sealed record SavedBillerView(
    Guid Id,
    string Biller,
    string BillerName,
    string Category,
    string ConsumerNumber,
    string? Nickname,
    string? CustomerName,
    string? LastBillingMonth,
    DateOnly? LastDueDate,
    long? LastPayablePaisa,
    string? LastStatus,
    DateTimeOffset? LastCheckedAt);

/// <summary>Port: saved billers (table saved_billers).</summary>
public interface ISavedBillers
{
    Task<IReadOnlyList<SavedBiller>> ForUserAsync(Guid userId, CancellationToken cancellationToken);

    Task<SavedBiller?> FindAsync(Guid userId, Guid id, CancellationToken cancellationToken);

    Task<SavedBiller?> FindAsync(Guid userId, string biller, string consumerNumber, CancellationToken cancellationToken);

    void Add(SavedBiller saved);

    void Remove(SavedBiller saved);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public static class SavedBillerErrors
{
    public static readonly Error NotFound = Error.NotFound("bills.saved_not_found", "No such saved biller.");
    public static readonly Error TooMany = Error.Conflict("bills.saved_limit", $"You can save at most {BillRules.MaxSavedBillers} billers.");
    public static readonly Error NicknameTooLong = Error.Validation("bills.nickname_too_long", $"A nickname is at most {BillRules.MaxNicknameLength} characters.");
}

/// <summary>Saved billers (M7.6): the bills a customer pays every month, checked for reminders.</summary>
public sealed class SavedBillersService(ISavedBillers saved, IBillerGateway billers, TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<SavedBillerView>> ListAsync(Guid userId, CancellationToken cancellationToken) =>
        [.. (await saved.ForUserAsync(userId, cancellationToken)).Select(ToView)];

    /// <summary>
    /// Saves a biller (saving it again updates the nickname). The bill is looked up at once: a consumer the
    /// biller does not know is refused; if the biller is down, it is saved anyway and checked later.
    /// </summary>
    public async Task<Result<SavedBillerView>> SaveAsync(Guid userId, string? billerCode, string? consumerNumber, string? nickname, CancellationToken cancellationToken)
    {
        var target = BillsService.Validate(billerCode, consumerNumber);
        if (target.IsFailure)
        {
            return target.Error;
        }

        var name = string.IsNullOrWhiteSpace(nickname) ? null : nickname.Trim();
        if (name is { Length: > BillRules.MaxNicknameLength })
        {
            return SavedBillerErrors.NicknameTooLong;
        }

        var (biller, number) = target.Value;
        var now = timeProvider.GetUtcNow();
        if (await saved.FindAsync(userId, biller.Code, number, cancellationToken) is { } existing)
        {
            existing.Nickname = name ?? existing.Nickname;
            await saved.SaveChangesAsync(cancellationToken);
            return ToView(existing);
        }

        if ((await saved.ForUserAsync(userId, cancellationToken)).Count >= BillRules.MaxSavedBillers)
        {
            return SavedBillerErrors.TooMany;
        }

        var inquiry = await billers.InquireAsync(biller, number, cancellationToken);
        if (inquiry.Outcome == BillerCallOutcome.NotFound)
        {
            return BillErrors.ConsumerNotFound;
        }

        if (inquiry.Outcome == BillerCallOutcome.Rejected)
        {
            return BillErrors.Refused(inquiry.Code, inquiry.Detail);
        }

        var entry = new SavedBiller
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Biller = biller.Code,
            ConsumerNumber = number,
            Nickname = name,
            CreatedAt = now,
            NextCheckAt = now,
        };
        if (inquiry.Bill is { } bill)
        {
            entry.Remember(bill, now);
        }

        saved.Add(entry);
        await saved.SaveChangesAsync(cancellationToken);
        return ToView(entry);
    }

    public async Task<Result> RemoveAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        if (await saved.FindAsync(userId, id, cancellationToken) is not { } entry)
        {
            return SavedBillerErrors.NotFound;
        }

        saved.Remove(entry);
        await saved.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    internal static SavedBillerView ToView(SavedBiller entry)
    {
        var biller = BillerCatalog.ByCode(entry.Biller);
        return new SavedBillerView(
            entry.Id, entry.Biller, biller?.Name ?? entry.Biller, biller?.Category ?? "", entry.ConsumerNumber, entry.Nickname, entry.CustomerName,
            entry.LastBillingMonth, entry.LastDueDate, entry.LastPayablePaisa, entry.LastStatus, entry.LastCheckedAt);
    }
}
