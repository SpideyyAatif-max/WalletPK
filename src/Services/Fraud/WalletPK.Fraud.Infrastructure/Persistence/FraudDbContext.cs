using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WalletPK.Fraud.Application;
using WalletPK.Fraud.Domain;
using WalletPK.Messaging.Events;
using WalletPK.Messaging.Serialization;
using WalletPK.Persistence.Inbox;
using WalletPK.Persistence.Outbox;

namespace WalletPK.Fraud.Infrastructure.Persistence;

/// <summary>
/// The fraud database (walletpk_fraud): risk rules, alerts, cases and their notes, freezes, and the
/// hash-chained audit log. Freeze commands and alerts leave through the outbox; identity and ledger events
/// come in through the inbox. The feature windows are in Redis, not here.
/// </summary>
public sealed class FraudDbContext(DbContextOptions<FraudDbContext> options) : DbContext(options)
{
    public const string CaseNumbers = "case_numbers";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddOutboxMessages();
        modelBuilder.AddInboxMessages();
        modelBuilder.HasSequence<long>(CaseNumbers).StartsAt(1);
        modelBuilder.ApplyConfiguration(new RuleConfiguration());
        modelBuilder.ApplyConfiguration(new AlertConfiguration());
        modelBuilder.ApplyConfiguration(new CaseConfiguration());
        modelBuilder.ApplyConfiguration(new CaseNoteConfiguration());
        modelBuilder.ApplyConfiguration(new AccountFlagConfiguration());
        modelBuilder.ApplyConfiguration(new AuditEntryConfiguration());
    }
}

internal sealed class RuleConfiguration : IEntityTypeConfiguration<RuleRecord>
{
    public void Configure(EntityTypeBuilder<RuleRecord> builder)
    {
        builder.ToTable("rules");
        builder.HasKey(rule => rule.Id);
        builder.Property(rule => rule.Id).HasMaxLength(40);
        builder.Property(rule => rule.Name).HasMaxLength(100).IsRequired();
        builder.Property(rule => rule.Description).HasMaxLength(500).IsRequired();
        builder.Property(rule => rule.Definition).HasColumnType("jsonb").IsRequired();
        builder.Property(rule => rule.UpdatedBy).HasMaxLength(100).IsRequired();
        builder.Property(rule => rule.Version).IsConcurrencyToken();
    }
}

internal sealed class AlertConfiguration : IEntityTypeConfiguration<FraudAlert>
{
    public void Configure(EntityTypeBuilder<FraudAlert> builder)
    {
        builder.ToTable("alerts");
        builder.HasKey(alert => alert.Id);
        builder.Property(alert => alert.Id).ValueGeneratedNever();
        builder.Property(alert => alert.Kind).HasMaxLength(16).IsRequired();
        builder.Property(alert => alert.Severity).HasMaxLength(8).IsRequired();
        builder.Property(alert => alert.Action).HasMaxLength(8).IsRequired();
        builder.Property(alert => alert.Summary).HasMaxLength(500).IsRequired();
        builder.Property(alert => alert.TransactionType).HasMaxLength(20);
        builder.Property(alert => alert.Counterparty).HasMaxLength(64);
        builder.HasIndex(alert => new { alert.UserId, alert.CreatedAt });
        builder.HasIndex(alert => alert.CaseId);
        builder.HasIndex(alert => alert.CreatedAt);
    }
}

internal sealed class CaseConfiguration : IEntityTypeConfiguration<FraudCase>
{
    public void Configure(EntityTypeBuilder<FraudCase> builder)
    {
        builder.ToTable("cases");
        builder.HasKey(fraudCase => fraudCase.Id);
        builder.Property(fraudCase => fraudCase.Id).ValueGeneratedNever();
        builder.Property(fraudCase => fraudCase.Kind).HasMaxLength(16).IsRequired();
        builder.Property(fraudCase => fraudCase.Title).HasMaxLength(200).IsRequired();
        builder.Property(fraudCase => fraudCase.Status).HasMaxLength(16).IsRequired();
        builder.Property(fraudCase => fraudCase.Resolution).HasMaxLength(20);
        builder.Property(fraudCase => fraudCase.AssignedTo).HasMaxLength(100);
        builder.HasIndex(fraudCase => fraudCase.Number).IsUnique();
        builder.HasIndex(fraudCase => new { fraudCase.Status, fraudCase.CreatedAt });
        builder.HasIndex(fraudCase => new { fraudCase.UserId, fraudCase.Kind, fraudCase.Status });
    }
}

internal sealed class CaseNoteConfiguration : IEntityTypeConfiguration<CaseNote>
{
    public void Configure(EntityTypeBuilder<CaseNote> builder)
    {
        builder.ToTable("case_notes");
        builder.HasKey(note => note.Id);
        builder.Property(note => note.Id).ValueGeneratedNever();
        builder.Property(note => note.Author).HasMaxLength(100).IsRequired();
        builder.Property(note => note.Text).HasMaxLength(2_000).IsRequired();
        builder.HasOne<FraudCase>().WithMany().HasForeignKey(note => note.CaseId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(note => new { note.CaseId, note.CreatedAt });
    }
}

internal sealed class AccountFlagConfiguration : IEntityTypeConfiguration<AccountFlag>
{
    public void Configure(EntityTypeBuilder<AccountFlag> builder)
    {
        builder.ToTable("account_flags");
        builder.HasKey(flag => flag.UserId);
        builder.Property(flag => flag.UserId).ValueGeneratedNever();
        builder.Property(flag => flag.Reason).HasMaxLength(200).IsRequired();
        builder.Property(flag => flag.ChangedBy).HasMaxLength(100).IsRequired();
        builder.HasIndex(flag => flag.RequestId);
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
    }
}

internal sealed class FraudStore(FraudDbContext db, JsonEventSerializer serializer, TimeProvider timeProvider) : IFraudStore
{
    /// <summary>One audit writer at a time (a transaction-scoped advisory lock), so the chain never forks.</summary>
    private const long AuditLock = 0x5750_4B41_5544_4954;

    /// <summary>The first half of the two-key advisory locks per user ("WPKU"); Postgres keeps two-key locks apart from one-key ones.</summary>
    private const int UserLockSpace = 0x5750_4B55;

    public async Task<IReadOnlyList<RuleRecord>> RulesAsync(CancellationToken cancellationToken) =>
        await db.Set<RuleRecord>().AsNoTracking().OrderBy(rule => rule.Id).ToListAsync(cancellationToken);

    public Task<RuleRecord?> RuleAsync(string id, CancellationToken cancellationToken) =>
        db.Set<RuleRecord>().SingleOrDefaultAsync(rule => rule.Id == id, cancellationToken);

    public void Add(RuleRecord rule) => db.Add(rule);

    public void Add(FraudAlert alert) => db.Add(alert);

    public void Add(FraudCase fraudCase) => db.Add(fraudCase);

    public void Add(CaseNote note) => db.Add(note);

    public void Add(AccountFlag flag) => db.Add(flag);

    public Task<FraudCase?> CaseAsync(Guid id, CancellationToken cancellationToken) =>
        db.Set<FraudCase>().SingleOrDefaultAsync(fraudCase => fraudCase.Id == id, cancellationToken);

    public Task<FraudCase?> OpenCaseAsync(Guid userId, string kind, CancellationToken cancellationToken) =>
        db.Set<FraudCase>()
            .Where(fraudCase => fraudCase.UserId == userId && fraudCase.Kind == kind && fraudCase.Status != "Closed")
            .OrderByDescending(fraudCase => fraudCase.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<FraudCase>> CasesAsync(string? status, int limit, CancellationToken cancellationToken) =>
        await db.Set<FraudCase>().AsNoTracking()
            .Where(fraudCase => status == null || fraudCase.Status == status)
            .OrderByDescending(fraudCase => fraudCase.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<FraudAlert>> AlertsAsync(Guid? userId, Guid? caseId, string? severity, int limit, CancellationToken cancellationToken) =>
        await db.Set<FraudAlert>().AsNoTracking()
            .Where(alert => (userId == null || alert.UserId == userId) && (caseId == null || alert.CaseId == caseId) && (severity == null || alert.Severity == severity))
            .OrderByDescending(alert => alert.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<CaseNote>> NotesAsync(Guid caseId, CancellationToken cancellationToken) =>
        await db.Set<CaseNote>().AsNoTracking().Where(note => note.CaseId == caseId).OrderBy(note => note.CreatedAt).ToListAsync(cancellationToken);

    public Task<AccountFlag?> FlagAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Set<AccountFlag>().SingleOrDefaultAsync(flag => flag.UserId == userId, cancellationToken);

    public async Task<long> NextCaseNumberAsync(CancellationToken cancellationToken) =>
        (await db.Database.SqlQueryRaw<long>($"SELECT nextval('{FraudDbContext.CaseNumbers}') AS \"Value\"").ToListAsync(cancellationToken)).Single();

    public async Task AuditAsync(string actor, string action, string subject, string details, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({AuditLock})", cancellationToken);
        var previous = await db.Set<AuditEntry>().AsNoTracking().OrderByDescending(entry => entry.Sequence).Select(entry => entry.Hash).FirstOrDefaultAsync(cancellationToken)
            ?? AuditChain.Genesis;
        var when = AuditChain.Truncate(at);
        db.Add(new AuditEntry
        {
            Actor = actor,
            Action = action,
            Subject = subject,
            Details = details,
            At = when,
            PreviousHash = previous,
            Hash = AuditChain.Hash(previous, actor, action, subject, details, when),
        });

        // Saved now, inside the caller's transaction, so a second entry in the same transaction chains onto it.
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task LockUsersAsync(IEnumerable<Guid> userIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        foreach (var key in userIds.Select(UserLockKey).Distinct().Order())
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({UserLockSpace}, {key})", cancellationToken);
        }
    }

    private static int UserLockKey(Guid userId)
    {
        Span<byte> bytes = stackalloc byte[16];
        userId.TryWriteBytes(bytes);
        return BitConverter.ToInt32(bytes[..4]) ^ BitConverter.ToInt32(bytes[4..8]) ^ BitConverter.ToInt32(bytes[8..12]) ^ BitConverter.ToInt32(bytes[12..]);
    }

    public async Task<IReadOnlyList<AuditEntry>> AuditTrailAsync(long? after, int limit, CancellationToken cancellationToken) =>
        await db.Set<AuditEntry>().AsNoTracking()
            .Where(entry => after == null || entry.Sequence > after)
            .OrderBy(entry => entry.Sequence)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public void Publish(IntegrationEvent message, string? correlationId) =>
        db.AddToOutbox(serializer, message, timeProvider.GetUtcNow(), correlationId);

    public async Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (db.Database.CurrentTransaction is not null)
        {
            return await work();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var result = await work();
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
