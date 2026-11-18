using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WalletPK.Bills.Application.Billers;
using WalletPK.Bills.Application.Saved;
using WalletPK.Bills.Domain;
using WalletPK.Bills.Infrastructure.Persistence;
using WalletPK.Common.Time;
using WalletPK.Contracts.Bills;
using WalletPK.Messaging.Serialization;
using WalletPK.Persistence.Outbox;

namespace WalletPK.Bills.Infrastructure.Reminders;

/// <summary>Reminder settings, from "Bills:Reminders".</summary>
public sealed class BillReminderOptions
{
    public const string SectionName = "Bills:Reminders";

    public bool Enabled { get; set; } = true;

    /// <summary>How often the job looks for saved billers that are due a check.</summary>
    public int PollSeconds { get; set; } = 300;

    /// <summary>Saved billers checked per round.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>A saved biller is checked again after this long.</summary>
    public int CheckIntervalHours { get; set; } = 12;

    /// <summary>A check that could not reach the biller is tried again after this long.</summary>
    public int RetryMinutes { get; set; } = 60;

    /// <summary>Remind this many days before the due date.</summary>
    public int DaysBefore { get; set; } = 3;

    public bool IsValid() => PollSeconds >= 1 && BatchSize is >= 1 and <= 1_000 && CheckIntervalHours >= 1 && RetryMinutes >= 1 && DaysBefore is >= 0 and <= 20;
}

/// <summary>
/// Due-date reminders (M7.6). Every few minutes, takes the saved billers due a check (each at most every
/// 12 hours), asks the biller for this month's bill, remembers it, and, when it is unpaid and due within
/// three days, publishes one <see cref="BillDueReminder"/> per bill (through the outbox, with the update),
/// which the notifications service turns into a push or SMS.
/// </summary>
public sealed partial class BillReminderService(
    IServiceScopeFactory scopes,
    BillReminderOptions options,
    TimeProvider timeProvider,
    ILogger<BillReminderService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var checkedCount = await RunOnceAsync(stoppingToken);
                if (checkedCount >= options.BatchSize)
                {
                    continue;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                LogRoundFailed(logger, exception);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One round; returns how many saved billers were checked.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var lease = now.AddMinutes(options.RetryMinutes);

        // Take a batch nobody else is taking (another instance of the service), and push their next
        // check out by a lease, so a crash mid-round does not lose them for long.
        List<Guid> due;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BillsDbContext>();
            due = await db.Database.SqlQuery<Guid>($"""
                UPDATE saved_billers SET next_check_at = {lease}
                 WHERE id IN (SELECT id FROM saved_billers WHERE next_check_at <= {now} ORDER BY next_check_at LIMIT {options.BatchSize} FOR UPDATE SKIP LOCKED)
                RETURNING id AS "Value"
                """).ToListAsync(cancellationToken);
        }

        foreach (var id in due)
        {
            await CheckAsync(id, cancellationToken);
        }

        return due.Count;
    }

    private async Task CheckAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillsDbContext>();
        var billers = scope.ServiceProvider.GetRequiredService<IBillerGateway>();
        var serializer = scope.ServiceProvider.GetRequiredService<JsonEventSerializer>();

        if (await db.Set<SavedBiller>().SingleOrDefaultAsync(saved => saved.Id == id, cancellationToken) is not { } saved
            || BillerCatalog.ByCode(saved.Biller) is not { } biller)
        {
            return;
        }

        var inquiry = await billers.InquireAsync(biller, saved.ConsumerNumber, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (inquiry is not { Outcome: BillerCallOutcome.Ok, Bill: { } bill })
        {
            // The lease already moved the next check out by the retry delay.
            LogCheckFailed(logger, saved.Biller, inquiry.Outcome, inquiry.Detail);
            return;
        }

        saved.Remember(bill, now);
        saved.NextCheckAt = now.AddHours(options.CheckIntervalHours);
        if (BillReminderRule.ShouldRemind(bill.Status, bill.DueDate, bill.BillingMonth, now.ToBusinessDate(), saved.RemindedForMonth, options.DaysBefore))
        {
            saved.RemindedForMonth = bill.BillingMonth;
            db.AddToOutbox(
                serializer,
                new BillDueReminder(saved.UserId, saved.Id, saved.Biller, saved.ConsumerNumber, saved.Nickname, bill.BillingMonth, bill.DueDate, bill.PayablePaisa),
                now,
                correlationId: $"bill-reminder-{saved.Id}-{bill.BillingMonth}");
            LogReminded(logger, saved.Biller, bill.BillingMonth, bill.DueDate);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bill reminder round failed")]
    private static partial void LogRoundFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Saved {Biller} bill could not be checked: {Outcome} ({Detail}); trying again later")]
    private static partial void LogCheckFailed(ILogger logger, string biller, BillerCallOutcome outcome, string? detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reminded a customer of their {Biller} bill {Month} (due {DueDate})")]
    private static partial void LogReminded(ILogger logger, string biller, string month, DateOnly dueDate);
}
