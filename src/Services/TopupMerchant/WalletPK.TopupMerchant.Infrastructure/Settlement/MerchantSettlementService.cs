using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WalletPK.Common.Time;
using WalletPK.Contracts.Ledger;
using WalletPK.Messaging.Serialization;
using WalletPK.Persistence.Outbox;
using WalletPK.TopupMerchant.Application.Merchants;
using WalletPK.TopupMerchant.Application.TopUps;
using WalletPK.TopupMerchant.Infrastructure.Persistence;

namespace WalletPK.TopupMerchant.Infrastructure.Settlement;

/// <summary>Settlement settings, from "Merchants:Settlement".</summary>
public sealed class MerchantSettlementOptions
{
    public const string SectionName = "Merchants:Settlement";

    public bool Enabled { get; set; } = true;

    /// <summary>When the nightly run starts, Pakistan time ("00:30": just after midnight, for the day before).</summary>
    public TimeOnly RunAt { get; set; } = new(0, 30);

    /// <summary>How often the job wakes up (to start the run, and to resend unanswered settlements).</summary>
    public int PollSeconds { get; set; } = 60;

    /// <summary>A settlement the Ledger has not answered for this long is sent again (the Ledger posts it once).</summary>
    public int ResendMinutes { get; set; } = 10;

    public bool IsValid() => PollSeconds >= 1 && ResendMinutes >= 1;
}

public sealed record SettlementRunResult(DateOnly BusinessDate, int Settlements, int Payments, long NetPaisa, int Resent);

/// <summary>
/// Nightly merchant settlement (M8.5). After midnight Pakistan time, for each merchant, the completed QR
/// payments of the day before that no settlement includes yet are recorded in one settlement (with the
/// payments, so none is settled twice) and the Ledger is asked to move the net to the merchant's bank
/// (SettleMerchant: merchant wallet → BANK_FLOAT). A settlement's ID comes from the merchant and the
/// date, so running twice, or on two instances, settles once; a payment that missed its night (it
/// completed late) goes into the next one; a refused settlement frees its payments for the next run.
/// </summary>
public sealed partial class MerchantSettlementService(
    IServiceScopeFactory scopes,
    MerchantSettlementOptions options,
    TimeProvider timeProvider,
    ILogger<MerchantSettlementService> logger) : BackgroundService
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
                    LogRun(logger, result.BusinessDate, result.Settlements, result.Payments, result.NetPaisa);
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
    /// Settles <paramref name="businessDate"/>: payments completed before its end (Pakistan time), or before
    /// now when that is earlier (operations can settle today so far).
    /// </summary>
    public async Task<SettlementRunResult> RunAsync(DateOnly businessDate, CancellationToken cancellationToken)
    {
        var endOfDay = new DateTimeOffset(businessDate.AddDays(1).ToDateTime(TimeOnly.MinValue), PakistanTime.UtcOffset);
        var now = timeProvider.GetUtcNow();
        var cutoff = endOfDay < now ? endOfDay : now;

        List<Guid> merchants;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TopupMerchantDbContext>();
            var sagaType = MerchantPaymentSaga.SagaName;
            var completed = "Completed";
            merchants = await db.Database.SqlQuery<Guid>($"""
                SELECT DISTINCT (s.data->>'merchantId')::uuid AS "Value" FROM saga_instances AS s
                 WHERE s.saga_type = {sagaType} AND s.state = {completed} AND s.completed_at < {cutoff}
                   AND NOT EXISTS (SELECT 1 FROM settled_payments AS p WHERE p.payment_id = s.id)
                """).ToListAsync(cancellationToken);
        }

        int settlements = 0, payments = 0;
        long net = 0;
        foreach (var merchantId in merchants)
        {
            if (await SettleAsync(merchantId, businessDate, cutoff, cancellationToken) is { } settlement)
            {
                settlements++;
                payments += settlement.PaymentCount;
                net += settlement.NetPaisa;
            }
        }

        var resent = await ResendAsync(cancellationToken);
        return new SettlementRunResult(businessDate, settlements, payments, net, resent);
    }

    /// <summary>The settlement ID for a merchant and a business date.</summary>
    public static Guid SettlementId(Guid merchantId, DateOnly businessDate) =>
        TopUpService.SagaId("settlement", merchantId, businessDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));

    private async Task<MerchantSettlement?> SettleAsync(Guid merchantId, DateOnly businessDate, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TopupMerchantDbContext>();
        var serializer = scope.ServiceProvider.GetRequiredService<JsonEventSerializer>();

        var id = SettlementId(merchantId, businessDate);
        if (await db.Set<Merchant>().AsNoTracking().SingleOrDefaultAsync(merchant => merchant.Id == merchantId, cancellationToken) is not { Status: "Active" } merchant
            || await db.Set<MerchantSettlement>().AnyAsync(settlement => settlement.Id == id, cancellationToken))
        {
            return null;
        }

        var unsettled = await MerchantPaymentQueries.UnsettledAsync(db, merchantId, cutoff, int.MaxValue, cancellationToken);
        if (unsettled.Count == 0)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var settlement = new MerchantSettlement
        {
            Id = id,
            MerchantId = merchantId,
            BusinessDate = businessDate,
            PaymentCount = unsettled.Count,
            GrossPaisa = unsettled.Sum(payment => payment.AmountPaisa),
            FeePaisa = unsettled.Sum(payment => payment.FeePaisa),
            TaxPaisa = unsettled.Sum(payment => payment.TaxPaisa),
            NetPaisa = unsettled.Sum(payment => payment.NetPaisa),
            CreatedAt = now,
            SentAt = now,
        };
        db.Add(settlement);
        db.AddRange(unsettled.Select(payment => new SettledPayment { PaymentId = payment.PaymentId, SettlementId = id }));
        db.AddToOutbox(serializer, Command(settlement, merchant), now, correlationId: id.ToString());

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return settlement;
        }
        catch (DbUpdateException exception)
        {
            // Another run (or instance) took this settlement or one of its payments first.
            LogConflict(logger, merchant.Code, exception.InnerException?.Message ?? exception.Message);
            return null;
        }
    }

    /// <summary>Sends again the settlements the Ledger has not answered for a while; returns how many.</summary>
    public async Task<int> ResendAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TopupMerchantDbContext>();
        var serializer = scope.ServiceProvider.GetRequiredService<JsonEventSerializer>();
        var now = timeProvider.GetUtcNow();
        var stale = now.AddMinutes(-options.ResendMinutes);

        var pending = await db.Set<MerchantSettlement>()
            .Where(settlement => settlement.Status == "Pending" && settlement.SentAt < stale)
            .Join(db.Set<Merchant>(), settlement => settlement.MerchantId, merchant => merchant.Id, (settlement, merchant) => new { settlement, merchant })
            .Take(100)
            .ToListAsync(cancellationToken);
        foreach (var item in pending)
        {
            db.AddToOutbox(serializer, Command(item.settlement, item.merchant), now, correlationId: item.settlement.Id.ToString());
            item.settlement.SentAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        return pending.Count;
    }

    private static SettleMerchant Command(MerchantSettlement settlement, Merchant merchant) =>
        new(settlement.Id, merchant.Id, merchant.OwnerUserId, settlement.NetPaisa,
            $"Settlement {settlement.BusinessDate:yyyy-MM-dd} ({settlement.PaymentCount} payment{(settlement.PaymentCount == 1 ? "" : "s")})");

    [LoggerMessage(Level = LogLevel.Information, Message = "Settled {BusinessDate}: {Settlements} merchants, {Payments} payments, {NetPaisa} paisa")]
    private static partial void LogRun(ILogger logger, DateOnly businessDate, int settlements, int payments, long netPaisa);

    [LoggerMessage(Level = LogLevel.Information, Message = "Settlement for {Merchant} was taken by another run: {Detail}")]
    private static partial void LogConflict(ILogger logger, string merchant, string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "Settlement round failed; trying again at the next poll")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);
}
