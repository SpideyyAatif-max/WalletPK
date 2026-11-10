using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WalletPK.Common.Identifiers;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.Domain.Journals;

namespace WalletPK.Ledger.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="JournalTransaction"/> to "journal_transactions" (one row per business event).</summary>
internal sealed class JournalTransactionConfiguration : IEntityTypeConfiguration<JournalTransaction>
{
    public void Configure(EntityTypeBuilder<JournalTransaction> builder)
    {
        builder.ToTable("journal_transactions", table =>
            table.HasCheckConstraint("ck_journal_transactions_amount_positive", "amount > 0"));

        builder.HasKey(journal => journal.Id);
        builder.Property(journal => journal.Id)
            .HasConversion(id => id.Value, value => new TransactionId(value))
            .ValueGeneratedNever();

        builder.Property(journal => journal.Type).HasConversion<string>().HasMaxLength(30);
        builder.Property(journal => journal.Description).HasMaxLength(JournalTransaction.DescriptionMaxLength).IsRequired();
        builder.Property(journal => journal.Reference).HasMaxLength(JournalTransaction.ReferenceMaxLength);
        builder.Property(journal => journal.Amount);
        builder.Property(journal => journal.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(journal => journal.PostedAt);

        builder.HasIndex(journal => journal.Reference).HasDatabaseName("ix_journal_transactions_reference");
        builder.HasIndex(journal => journal.PostedAt).HasDatabaseName("ix_journal_transactions_posted_at");

        builder.HasMany(journal => journal.Entries)
            .WithOne()
            .HasForeignKey(entry => entry.TransactionId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(journal => journal.Entries).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(journal => journal.DomainEvents);
    }
}

/// <summary>Maps <see cref="JournalEntry"/> to "journal_entries" (append-only; see LedgerDatabaseGuards).</summary>
internal sealed class JournalEntryConfiguration : IEntityTypeConfiguration<JournalEntry>
{
    public void Configure(EntityTypeBuilder<JournalEntry> builder)
    {
        builder.ToTable("journal_entries", table =>
        {
            table.HasCheckConstraint("ck_journal_entries_amount_positive", "amount > 0");
            table.HasCheckConstraint("ck_journal_entries_direction", "direction IN ('Debit', 'Credit')");
            table.HasCheckConstraint("ck_journal_entries_account_sequence_positive", "account_sequence > 0");
        });

        builder.HasKey(entry => entry.Id);
        builder.Property(entry => entry.Id).UseIdentityAlwaysColumn();

        builder.Property(entry => entry.TransactionId)
            .HasConversion(id => id.Value, value => new TransactionId(value));
        builder.Property(entry => entry.AccountId)
            .HasConversion(id => id.Value, value => new AccountId(value));
        builder.Property(entry => entry.LineNumber);
        builder.Property(entry => entry.AccountSequence);
        builder.Property(entry => entry.Direction).HasConversion<string>().HasMaxLength(6);
        builder.Property(entry => entry.Amount);
        builder.Property(entry => entry.BalanceAfter);
        builder.Property(entry => entry.PostedAt);

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(entry => entry.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

        // A journal's lines are numbered 1..n exactly once.
        builder.HasIndex(entry => new { entry.TransactionId, entry.LineNumber })
            .IsUnique()
            .HasDatabaseName("ux_journal_entries_transaction_line");

        // Each account's entries are numbered 1, 2, 3 ... exactly once (M4.2).
        builder.HasIndex(entry => new { entry.AccountId, entry.AccountSequence })
            .IsUnique()
            .HasDatabaseName("ux_journal_entries_account_sequence");

        // Account statements: an account's entries, newest first.
        builder.HasIndex(entry => new { entry.AccountId, entry.Id })
            .IsDescending(false, true)
            .HasDatabaseName("ix_journal_entries_account_history");
    }
}
