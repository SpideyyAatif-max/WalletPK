using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WalletPK.Agents.Application;
using WalletPK.Agents.Domain;
using WalletPK.Agents.Infrastructure.Persistence;
using WalletPK.Common.Time;
using WalletPK.Contracts.Ledger;
using WalletPK.Messaging.Serialization;
using WalletPK.Persistence.Outbox;

namespace WalletPK.Agents.Infrastructure.Payouts;

/// <summary>Commission payout settings, from "Agents:Payouts".</summary>
public sealed class CommissionPayoutOptions
{
    public const string SectionName = "Agents:Payouts";

    public bool Enabled { get; set; } = true;

    /// <summary>When the nightly run starts, Pakistan time ("01:00": after midnight, for the day before).</summary>
    public TimeOnly RunAt { get; set; } = new(1, 0);

    /// <summary>How often the job wakes up (to start the run, and to resend unanswered payouts).</summary>
    public int PollSeconds { get; set; } = 60;

    /// <summary>A payout the Ledger has not answered for this long is sent again (the Ledger posts it once).</summary>
    public int ResendMinutes { get; set; } = 10;

    public bool IsValid() => PollSeconds >= 1 && ResendMinutes >= 1;
}

public sealed record PayoutRunResult(DateOnly BusinessDate, int Payouts, int Transactions, long GrossPaisa, long TaxPaisa, int Resent);

/// <summary>
/// Nightly commission payouts (M9.4). After midnight Pakistan time, for each agent, the commissions of the
/// transactions completed the day before that no payout includes yet are recorded in one payout (with the
/// transactions, so none is paid twice), and the Ledger pays them to the float less 12% withholding tax
/// (PayAgentCommission: COMMISSION_PAYABLE → float + TAX_PAYABLE). A payout's ID comes from the agent and
/// the date, so running twice, or on two instances, pays once; a transaction that missed its night goes
/// into the next one; a refused payout frees its transactions for the next run.
/// </summary>
public sealed partial class CommissionPayoutService(
    IServiceScopeFactory scopes,
    CommissionPayoutOptions options,
    TimeProvider timeProvider,
    ILogger<CommissionPayoutService> logger) : BackgroundService
{
    private DateOnly? _lastRun;

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
                var now = timeProvider.GetPakistanNow();
                var today = DateOnly.FromDateTime(now.DateTime);
                if (_lastRun != today && TimeOnly.FromDateTime(now.DateTime) >= options.RunAt)
                {
                    var result = await RunAsync(today.AddDays(-1), stoppingToken);
                    _lastRun = today;
                    LogRun(logger, result.BusinessDate, result.Payouts, result.Transactions, result.GrossPaisa);
                }

                await ResendAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                LogRunFailed(logger, exception);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Pays the commissions of <paramref name="businessDate"/>: transactions completed before its end
    /// (Pakistan time), or before now when that is earlier (operations can pay today so far).
    /// </summary>
    public async Task<PayoutRunResult> RunAsync(DateOnly businessDate, CancellationToken cancellationToken)
    {
        var endOfDay = new DateTimeOffset(businessDate.AddDays(1).ToDateTime(TimeOnly.MinValue), PakistanTime.UtcOffset);
        var now = timeProvider.GetUtcNow();
        var cutoff = endOfDay < now ? endOfDay : now;

        List<Guid> agents;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
            var sagaType = AgentTransactionSaga.SagaName;
            var completed = "Completed";
            agents = await db.Database.SqlQuery<Guid>($"""
                SELECT DISTINCT (s.data->>'agentId')::uuid AS "Value" FROM saga_instances AS s
                 WHERE s.saga_type = {sagaType} AND s.state = {completed} AND s.completed_at < {cutoff}
                   AND (s.data->>'commissionPaisa')::bigint > 0
                   AND NOT EXISTS (SELECT 1 FROM paid_commissions AS p WHERE p.transaction_id = s.id)
                """).ToListAsync(cancellationToken);
        }

        int payouts = 0, transactions = 0;
        long gross = 0, tax = 0;
        foreach (var agentId in agents)
        {
            if (await PayAsync(agentId, businessDate, cutoff, cancellationToken) is { } payout)
            {
                payouts++;
                transactions += payout.TransactionCount;
                gross += payout.GrossPaisa;
                tax += payout.TaxPaisa;
            }
        }

        var resent = await ResendAsync(cancellationToken);
        return new PayoutRunResult(businessDate, payouts, transactions, gross, tax, resent);
    }

    /// <summary>The payout ID for an agent and a business date.</summary>
    public static Guid PayoutId(Guid agentId, DateOnly businessDate) =>
        AgentService.SagaId("payout", agentId, businessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    private async Task<CommissionPayout?> PayAsync(Guid agentId, DateOnly businessDate, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
        var serializer = scope.ServiceProvider.GetRequiredService<JsonEventSerializer>();

        var id = PayoutId(agentId, businessDate);
        if (await db.Set<Agent>().AsNoTracking().SingleOrDefaultAsync(agent => agent.Id == agentId, cancellationToken) is not { } agent
            || agent.Status is not (AgentStatus.Active or AgentStatus.Suspended)
            || await db.Set<CommissionPayout>().AnyAsync(payout => payout.Id == id, cancellationToken))
        {
            return null;
        }

        var unpaid = await CommissionQueries.UnpaidAsync(db, agentId, cutoff, cancellationToken);
        var grossPaisa = unpaid.Sum(transaction => transaction.CommissionPaisa);
        var taxPaisa = CommissionPlan.Withholding(grossPaisa);
        if (unpaid.Count == 0 || grossPaisa - taxPaisa <= 0)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var payout = new CommissionPayout
        {
            Id = id,
            AgentId = agentId,
            BusinessDate = businessDate,
            TransactionCount = unpaid.Count,
            GrossPaisa = grossPaisa,
            TaxPaisa = taxPaisa,
            NetPaisa = grossPaisa - taxPaisa,
            CreatedAt = now,
            SentAt = now,
        };
        db.Add(payout);
        db.AddRange(unpaid.Select(transaction => new PaidCommission { TransactionId = transaction.TransactionId, PayoutId = id }));
        db.AddToOutbox(serializer, Command(payout, agent), now, correlationId: id.ToString());

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return payout;
        }
        catch (DbUpdateException exception)
        {
            // Another run (or instance) took this payout or one of its transactions first.
            LogConflict(logger, agent.Code, exception.InnerException?.Message ?? exception.Message);
            return null;
        }
    }

    /// <summary>Sends again the payouts the Ledger has not answered for a while; returns how many.</summary>
    public async Task<int> ResendAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
        var serializer = scope.ServiceProvider.GetRequiredService<JsonEventSerializer>();
        var now = timeProvider.GetUtcNow();
        var stale = now.AddMinutes(-options.ResendMinutes);

        var pending = await db.Set<CommissionPayout>()
            .Where(payout => payout.Status == "Pending" && payout.SentAt < stale)
            .Join(db.Set<Agent>(), payout => payout.AgentId, agent => agent.Id, (payout, agent) => new { payout, agent })
            .Take(100)
            .ToListAsync(cancellationToken);
        foreach (var item in pending)
        {
            db.AddToOutbox(serializer, Command(item.payout, item.agent), now, correlationId: item.payout.Id.ToString());
            item.payout.SentAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        return pending.Count;
    }

    private static PayAgentCommission Command(CommissionPayout payout, Agent agent) =>
        new(payout.Id, agent.Id, agent.OwnerUserId, payout.GrossPaisa, payout.TaxPaisa,
            $"Commission {payout.BusinessDate:yyyy-MM-dd} ({payout.TransactionCount} transaction{(payout.TransactionCount == 1 ? "" : "s")})");

    [LoggerMessage(Level = LogLevel.Information, Message = "Commissions for {BusinessDate}: {Payouts} agents, {Transactions} transactions, {GrossPaisa} paisa")]
    private static partial void LogRun(ILogger logger, DateOnly businessDate, int payouts, int transactions, long grossPaisa);

    [LoggerMessage(Level = LogLevel.Information, Message = "Payout for {Agent} was taken by another run: {Detail}")]
    private static partial void LogConflict(ILogger logger, string agent, string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "Commission payout round failed; trying again at the next poll")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);
}
