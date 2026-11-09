using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WalletPK.Common.Identifiers;
using WalletPK.Ledger.Domain.Accounts;

namespace WalletPK.Ledger.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Account"/> to "accounts". The check constraints are the database's own copy of the
/// most important rules, so they hold even if application code has a bug or someone edits rows by hand.
/// </summary>
internal sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    /// <summary>Fixed creation time for the seeded system accounts (seed data must never change between migrations).</summary>
    internal static readonly DateTimeOffset SeedTime = new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);

    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.ToTable("accounts", table =>
        {
            // A wallet can never go below zero (available = balance minus holds).
            table.HasCheckConstraint("ck_accounts_available_non_negative", "allow_negative OR balance - held >= 0");
            table.HasCheckConstraint("ck_accounts_held_non_negative", "held >= 0");

            // Wallets have an owner and no system code; system accounts the opposite.
            table.HasCheckConstraint(
                "ck_accounts_owner_or_system_code",
                "(type = 'System' AND owner_id IS NULL AND system_code IS NOT NULL) OR (type <> 'System' AND owner_id IS NOT NULL AND system_code IS NULL)");
        });

        builder.HasKey(account => account.Id);
        builder.Property(account => account.Id)
            .HasConversion(id => id.Value, value => new AccountId(value))
            .ValueGeneratedNever();

        builder.Property(account => account.Type).HasConversion<string>().HasMaxLength(20);
        builder.Property(account => account.Name).HasMaxLength(100).IsRequired();

        builder.Property(account => account.OwnerId)
            .HasConversion(id => id!.Value.Value, value => new UserId(value));

        builder.Property(account => account.SystemCode).HasMaxLength(40);
        builder.Property(account => account.ShardIndex);
        builder.Property(account => account.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(account => account.NormalBalance).HasConversion<string>().HasMaxLength(6);
        builder.Property(account => account.AllowNegative);
        builder.Property(account => account.Balance);
        builder.Property(account => account.Held);
        builder.Property(account => account.Status).HasConversion<string>().HasMaxLength(10);
        builder.Property(account => account.CreatedAt);
        builder.Property(account => account.UpdatedAt);
        builder.Property(account => account.Version).IsRowVersion();
        builder.Property(account => account.LockToken);

        // One wallet of each type per user; each system account shard exists once.
        builder.HasIndex(account => new { account.OwnerId, account.Type })
            .IsUnique()
            .HasFilter("owner_id IS NOT NULL")
            .HasDatabaseName("ux_accounts_owner_type");
        builder.HasIndex(account => new { account.SystemCode, account.ShardIndex })
            .IsUnique()
            .HasFilter("system_code IS NOT NULL")
            .HasDatabaseName("ux_accounts_system_code_shard");

        builder.Ignore(account => account.Available);
        builder.Ignore(account => account.IsWallet);
        builder.Ignore(account => account.DomainEvents);

        // The chart of accounts: every system account shard is created by the migration itself.
        builder.HasData(ChartOfAccounts.SystemAccounts
            .SelectMany(definition => Enumerable.Range(0, definition.Shards)
                .Select(shard => Account.OpenSystemShard(definition, shard, SeedTime))));
    }
}
