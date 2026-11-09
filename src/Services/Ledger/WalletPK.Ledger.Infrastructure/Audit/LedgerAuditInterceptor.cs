using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.Domain.Holds;
using WalletPK.Ledger.Domain.Journals;
using WalletPK.Web.Identity;

namespace WalletPK.Ledger.Infrastructure.Audit;

/// <summary>
/// Adds an <see cref="AuditRecord"/> for every money-relevant change, just before EF Core saves, so the
/// audit row commits (or rolls back) together with the change itself:
/// <list type="bullet">
/// <item>journal.posted (with every entry and balance-after)</item>
/// <item>hold.placed, hold.captured, hold.released, hold.expired</item>
/// <item>account.opened, account.status_changed</item>
/// </list>
/// It reads EF Core's change tracker, so no use case can forget to audit.
/// </summary>
public sealed class LedgerAuditInterceptor(
    TimeProvider timeProvider,
    IHttpContextAccessor httpContextAccessor,
    Func<string?> correlationIdAccessor) : SaveChangesInterceptor
{
    public const string SystemActor = "system";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        AddAuditRecords(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        AddAuditRecords(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void AddAuditRecords(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        context.ChangeTracker.DetectChanges();

        var records = new List<AuditRecord>();
        var now = timeProvider.GetUtcNow();
        var actor = new HttpContextCurrentUser(httpContextAccessor).Id is { } userId ? $"user:{userId}" : SystemActor;
        var correlationId = correlationIdAccessor();

        AuditRecord Record(string eventType, string subjectType, string subjectId, object payload) =>
            AuditRecord.Create(now, eventType, subjectType, subjectId, actor, correlationId, JsonSerializer.Serialize(payload, _json));

        foreach (var entry in context.ChangeTracker.Entries<JournalTransaction>().Where(entry => entry.State == EntityState.Added))
        {
            var journal = entry.Entity;
            records.Add(Record("journal.posted", "journal", journal.Id.ToString(), new
            {
                journal.Type,
                journal.Description,
                journal.Reference,
                journal.Amount,
                journal.Currency,
                journal.PostedAt,
                Entries = journal.Entries.OrderBy(line => line.LineNumber).Select(line => new
                {
                    Line = line.LineNumber,
                    line.AccountId,
                    line.Direction,
                    line.Amount,
                    line.BalanceAfter,
                }),
            }));
        }

        foreach (var entry in context.ChangeTracker.Entries<Hold>())
        {
            var hold = entry.Entity;
            var eventType = entry.State switch
            {
                EntityState.Added => "hold.placed",
                EntityState.Modified when StatusChanged(entry) => $"hold.{hold.Status.ToString().ToLowerInvariant()}",
                _ => null,
            };

            if (eventType is not null)
            {
                records.Add(Record(eventType, "hold", hold.Id.ToString(), new
                {
                    hold.AccountId,
                    hold.Amount,
                    hold.CapturedAmount,
                    hold.Status,
                    hold.Reference,
                    hold.ExpiresAt,
                    hold.CaptureTransactionId,
                }));
            }
        }

        foreach (var entry in context.ChangeTracker.Entries<Account>())
        {
            var account = entry.Entity;
            if (entry.State == EntityState.Added)
            {
                records.Add(Record("account.opened", "account", account.Id.ToString(), new
                {
                    account.Type,
                    account.Name,
                    account.OwnerId,
                    account.Alias,
                    account.SystemCode,
                    account.ShardIndex,
                    account.Currency,
                }));
            }
            else if (entry.State == EntityState.Modified && StatusChanged(entry))
            {
                records.Add(Record("account.status_changed", "account", account.Id.ToString(), new
                {
                    From = entry.Property(nameof(Account.Status)).OriginalValue,
                    To = account.Status,
                }));
            }
        }

        context.Set<AuditRecord>().AddRange(records);
    }

    private static bool StatusChanged(EntityEntry entry)
    {
        var status = entry.Property("Status");
        return status.IsModified && !Equals(status.OriginalValue, status.CurrentValue);
    }
}
