using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WalletPK.Persistence.Inbox;
using WalletPK.Reconciliation.Application;
using WalletPK.Reconciliation.Domain;

namespace WalletPK.Reconciliation.Infrastructure.Persistence;

/// <summary>
/// The Reconciliation database (walletpk_reconciliation): external payments, settlement files and their
/// lines, runs, breaks, the audit chain and the inbox.
/// </summary>
public sealed class ReconciliationDbContext(DbContextOptions<ReconciliationDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.AddInboxMessages();
        modelBuilder.ApplyConfiguration(new ExternalPaymentConfiguration());
        modelBuilder.ApplyConfiguration(new SettlementFileConfiguration());
        modelBuilder.ApplyConfiguration(new SettlementLineConfiguration());
        modelBuilder.ApplyConfiguration(new ReconRunConfiguration());
        modelBuilder.ApplyConfiguration(new ReconBreakConfiguration());
        modelBuilder.ApplyConfiguration(new AuditEntryConfiguration());
    }
}

internal sealed class ExternalPaymentConfiguration : IEntityTypeConfiguration<ExternalPayment>
{
    public void Configure(EntityTypeBuilder<ExternalPayment> builder)
    {
        builder.ToTable("external_payments");
        builder.HasKey(payment => payment.Id);
        builder.Property(payment => payment.Id).ValueGeneratedNever();
        builder.Property(payment => payment.Kind).HasMaxLength(20).IsRequired();
        builder.Property(payment => payment.Party).HasMaxLength(40).IsRequired();
        builder.Property(payment => payment.ProviderReference).HasMaxLength(64);
        builder.Property(payment => payment.Status).HasMaxLength(10).IsRequired();
        builder.Property(payment => payment.ServiceStatus).HasMaxLength(40).IsRequired();
        builder.HasIndex(payment => new { payment.Party, payment.CompletedOn }).HasFilter("completed_on IS NOT NULL");
        builder.HasIndex(payment => payment.ProviderReference).HasFilter("provider_reference IS NOT NULL");
    }
}

internal sealed class SettlementFileConfiguration : IEntityTypeConfiguration<SettlementFileRecord>
{
    public void Configure(EntityTypeBuilder<SettlementFileRecord> builder)
    {
        builder.ToTable("settlement_files");
        builder.HasKey(file => file.Id);
        builder.Property(file => file.Id).ValueGeneratedNever();
        builder.Property(file => file.Party).HasMaxLength(40).IsRequired();
        builder.Property(file => file.Sha256).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Property(file => file.Content).IsRequired();
        builder.HasIndex(file => new { file.Party, file.BusinessDate, file.FetchedAt });
    }
}

internal sealed class SettlementLineConfiguration : IEntityTypeConfiguration<SettlementLineRecord>
{
    public void Configure(EntityTypeBuilder<SettlementLineRecord> builder)
    {
        builder.ToTable("settlement_lines");
        builder.HasKey(line => line.Id);
        builder.Property(line => line.Id).UseIdentityAlwaysColumn();
        builder.Property(line => line.Party).HasMaxLength(40).IsRequired();
        builder.Property(line => line.ProviderReference).HasMaxLength(64).IsRequired();
        builder.Property(line => line.ClientReference).HasMaxLength(64).IsRequired();
        builder.HasOne<SettlementFileRecord>().WithMany().HasForeignKey(line => line.FileId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(line => new { line.Party, line.BusinessDate, line.ClientId });
    }
}

internal sealed class ReconRunConfiguration : IEntityTypeConfiguration<ReconRun>
{
    public void Configure(EntityTypeBuilder<ReconRun> builder)
    {
        builder.ToTable("recon_runs");
        builder.HasKey(run => run.Id);
        builder.Property(run => run.Id).ValueGeneratedNever();
        builder.Property(run => run.Party).HasMaxLength(40).IsRequired();
        builder.Property(run => run.Status).HasMaxLength(10).IsRequired();
        builder.Property(run => run.Problem).HasMaxLength(500);
        builder.Property(run => run.TriggeredBy).HasMaxLength(100).IsRequired();
        builder.Ignore(run => run.Breaks);
        builder.HasOne<SettlementFileRecord>().WithMany().HasForeignKey(run => run.FileId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(run => new { run.BusinessDate, run.Party, run.StartedAt });
    }
}

internal sealed class ReconBreakConfiguration : IEntityTypeConfiguration<ReconBreak>
{
    public void Configure(EntityTypeBuilder<ReconBreak> builder)
    {
        builder.ToTable("recon_breaks");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).ValueGeneratedNever();
        builder.Property(item => item.Party).HasMaxLength(40).IsRequired();
        builder.Property(item => item.Kind).HasMaxLength(30).IsRequired();
        builder.Property(item => item.Key).HasMaxLength(64).IsRequired();
        builder.Property(item => item.ProviderReference).HasMaxLength(64);
        builder.Property(item => item.ClientReference).HasMaxLength(64);
        builder.Property(item => item.OurStatus).HasMaxLength(10);
        builder.Property(item => item.Description).HasMaxLength(500).IsRequired();
        builder.Property(item => item.Status).HasMaxLength(10).IsRequired();
        builder.Property(item => item.Resolution).HasMaxLength(20);
        builder.Property(item => item.Note).HasMaxLength(1_000);
        builder.Property(item => item.UpdatedBy).HasMaxLength(100);
        builder.Ignore(item => item.AtStakePaisa);

        // M12.2: one break per party, day, kind and item, however often the day is run again.
        builder.HasIndex(item => new { item.Party, item.BusinessDate, item.Kind, item.Key }).IsUnique();
        builder.HasIndex(item => new { item.Status, item.BusinessDate });
    }
}

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.ToTable("audit_log");
        builder.HasKey(entry => entry.Sequence);
        builder.Property(entry => entry.Sequence).UseIdentityAlwaysColumn();
        builder.Property(entry => entry.Actor).HasMaxLength(100).IsRequired();
        builder.Property(entry => entry.Action).HasMaxLength(40).IsRequired();
        builder.Property(entry => entry.Subject).HasMaxLength(100).IsRequired();
        builder.Property(entry => entry.Details).IsRequired();
        builder.Property(entry => entry.PreviousHash).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Property(entry => entry.Hash).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.HasIndex(entry => entry.Hash).IsUnique();
        builder.HasIndex(entry => new { entry.Subject, entry.Sequence });
    }
}

/// <summary>The store behind the use cases.</summary>
internal sealed class ReconStore(ReconciliationDbContext db) : IReconStore
{
    /// <summary>Runs, Finance's actions and audit writes happen one at a time ("WPKRECON").</summary>
    private const long ReconLock = 0x5750_4B52_4543_4F4E;

    private readonly List<(string Actor, string Action, string Subject, string Details, DateTimeOffset At)> _pendingAudit = [];

    public async Task<ExternalPayment?> PaymentAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Set<ExternalPayment>().FindAsync([id], cancellationToken);

    public async Task<IReadOnlyList<ExternalPayment>> PaymentsAsync(IReadOnlyCollection<Guid> ids, IReadOnlyCollection<string> providerReferences, CancellationToken cancellationToken) =>
        ids.Count == 0 && providerReferences.Count == 0
            ? []
            : await db.Set<ExternalPayment>().AsNoTracking()
                .Where(payment => ids.Contains(payment.Id) || (payment.ProviderReference != null && providerReferences.Contains(payment.ProviderReference)))
                .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ExternalPayment>> CompletedOnAsync(string party, DateOnly date, CancellationToken cancellationToken) =>
        await db.Set<ExternalPayment>().AsNoTracking()
            .Where(payment => payment.Party == party && payment.CompletedOn == date)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlySet<Guid>> ListedOnAsync(string party, IReadOnlyCollection<DateOnly> dates, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        var listed = await db.Set<SettlementLineRecord>().AsNoTracking()
            .Where(line => line.Party == party && dates.Contains(line.BusinessDate) && line.ClientId != null && ids.Contains(line.ClientId.Value))
            .Select(line => line.ClientId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
        return listed.ToHashSet();
    }

    public async Task SaveFileAsync(SettlementFileRecord file, IReadOnlyList<SettlementLine>? lines, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        db.Add(file);
        if (lines is null)
        {
            return;
        }

        await db.Set<SettlementLineRecord>()
            .Where(line => line.Party == file.Party && line.BusinessDate == file.BusinessDate)
            .ExecuteDeleteAsync(cancellationToken);
        db.AddRange(lines.Select(line => new SettlementLineRecord
        {
            FileId = file.Id,
            Party = file.Party,
            BusinessDate = file.BusinessDate,
            ProviderReference = line.ProviderReference,
            ClientReference = line.ClientReference,
            ClientId = Guid.TryParse(line.ClientReference, out var id) ? id : null,
            AmountPaisa = line.AmountPaisa,
            CompletedAt = line.CompletedAt,
        }));
    }

    public Task<SettlementFileRecord?> FileAsync(Guid id, CancellationToken cancellationToken) =>
        db.Set<SettlementFileRecord>().AsNoTracking().SingleOrDefaultAsync(file => file.Id == id, cancellationToken);

    public async Task<IReadOnlyList<ReconBreak>> BreaksForAsync(string party, DateOnly date, CancellationToken cancellationToken) =>
        await db.Set<ReconBreak>()
            .Where(item => item.Party == party && item.BusinessDate == date)
            .OrderBy(item => item.Kind).ThenBy(item => item.Key)
            .ToListAsync(cancellationToken);

    public Task<ReconBreak?> BreakAsync(Guid id, CancellationToken cancellationToken) =>
        db.Set<ReconBreak>().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);

    public async Task<IReadOnlyList<ReconBreak>> BreaksAsync(BreakQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var breaks = db.Set<ReconBreak>().AsNoTracking();
        if (string.Equals(query.Status, "open", StringComparison.Ordinal))
        {
            breaks = breaks.Where(item => item.Status == BreakStatuses.Open || item.Status == BreakStatuses.Disputed);
        }
        else if (query.Status is { Length: > 0 } status)
        {
            breaks = breaks.Where(item => item.Status == status);
        }

        if (query.Kind is { Length: > 0 } kind)
        {
            breaks = breaks.Where(item => item.Kind == kind);
        }

        if (query.Party is { Length: > 0 } party)
        {
            breaks = breaks.Where(item => item.Party == party);
        }

        if (query.From is { } from)
        {
            breaks = breaks.Where(item => item.BusinessDate >= from);
        }

        if (query.To is { } to)
        {
            breaks = breaks.Where(item => item.BusinessDate <= to);
        }

        return await breaks.OrderByDescending(item => item.BusinessDate).ThenBy(item => item.Party).ThenBy(item => item.Kind).ThenBy(item => item.Key)
            .Take(query.Limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, int>> OpenBreakCountsAsync(CancellationToken cancellationToken)
    {
        var counts = await db.Set<ReconBreak>().AsNoTracking()
            .Where(item => item.Status == BreakStatuses.Open || item.Status == BreakStatuses.Disputed)
            .GroupBy(item => item.Kind)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        return Enum.GetNames<BreakKind>().ToDictionary(kind => kind, kind => counts.FirstOrDefault(count => count.Key == kind)?.Count ?? 0);
    }

    public Task<ReconRun?> RunAsync(Guid id, CancellationToken cancellationToken) =>
        db.Set<ReconRun>().AsNoTracking().SingleOrDefaultAsync(run => run.Id == id, cancellationToken);

    public async Task<IReadOnlyList<ReconRun>> RunsAsync(DateOnly? date, string? party, int limit, CancellationToken cancellationToken)
    {
        var runs = db.Set<ReconRun>().AsNoTracking();
        if (date is { } day)
        {
            runs = runs.Where(run => run.BusinessDate == day);
        }

        if (party is { Length: > 0 })
        {
            runs = runs.Where(run => run.Party == party);
        }

        return await runs.OrderByDescending(run => run.StartedAt).Take(limit).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ReconRun>> LatestRunsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        await db.Set<ReconRun>()
            .FromSql($"""
                SELECT DISTINCT ON (business_date, party) * FROM recon_runs
                WHERE business_date >= {from} AND business_date <= {to}
                ORDER BY business_date, party, started_at DESC
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    public void Add(object entity) => db.Add(entity);

    public void Audit(string actor, string action, string subject, string details, DateTimeOffset at) =>
        _pendingAudit.Add((actor, action, subject, details, AuditChain.Truncate(at)));

    public async Task<IReadOnlyList<AuditEntry>> AuditTrailAsync(long? after, int limit, string? subject, CancellationToken cancellationToken) =>
        await db.Set<AuditEntry>().AsNoTracking()
            .Where(entry => (after == null || entry.Sequence > after) && (subject == null || entry.Subject == subject))
            .OrderBy(entry => entry.Sequence)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<AuditEntry>> AuditRecentAsync(long? before, int limit, CancellationToken cancellationToken) =>
        await db.Set<AuditEntry>().AsNoTracking()
            .Where(entry => before == null || entry.Sequence < before)
            .OrderByDescending(entry => entry.Sequence)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public async Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        _pendingAudit.Clear();
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({ReconLock})", cancellationToken);
            var result = await work();
            await db.SaveChangesAsync(cancellationToken);
            await AppendAuditAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
        finally
        {
            _pendingAudit.Clear();
        }
    }

    /// <summary>Writes the transaction's audit entries onto the chain (the lock is held, so the head cannot move).</summary>
    private async Task AppendAuditAsync(CancellationToken cancellationToken)
    {
        if (_pendingAudit.Count == 0)
        {
            return;
        }

        var previous = await db.Set<AuditEntry>().AsNoTracking().OrderByDescending(entry => entry.Sequence).Select(entry => entry.Hash).FirstOrDefaultAsync(cancellationToken)
            ?? AuditChain.Genesis;
        foreach (var (actor, action, subject, details, when) in _pendingAudit)
        {
            var hash = AuditChain.Hash(previous, actor, action, subject, details, when);
            db.Add(new AuditEntry { Actor = actor, Action = action, Subject = subject, Details = details, At = when, PreviousHash = previous, Hash = hash });

            // One at a time, so the sequence numbers follow the chain.
            await db.SaveChangesAsync(cancellationToken);
            previous = hash;
        }
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
