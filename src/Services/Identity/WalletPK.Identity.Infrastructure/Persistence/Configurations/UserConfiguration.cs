using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WalletPK.Common.Identifiers;
using WalletPK.Identity.Domain.Users;

namespace WalletPK.Identity.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="User"/> to the "users" table (snake_case columns via the naming convention).</summary>
internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");

        builder.HasKey(user => user.Id);
        builder.Property(user => user.Id)
            .HasConversion(id => id.Value, value => new UserId(value))
            .ValueGeneratedNever();

        builder.Property(user => user.PhoneNumber)
            .HasConversion(phone => phone.Value, value => PhoneNumber.Create(value).Value)
            .HasMaxLength(13)
            .IsRequired();

        // One account per phone number, enforced by the database even under concurrent sign-ups.
        builder.HasIndex(user => user.PhoneNumber)
            .IsUnique()
            .HasDatabaseName("ux_users_phone_number");

        builder.Property(user => user.MpinHash).HasMaxLength(256).IsRequired();
        builder.Property(user => user.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(user => user.KycTier).HasConversion<string>().HasMaxLength(4);
        builder.Property(user => user.CreatedAt);
        builder.Property(user => user.UpdatedAt);

        // PostgreSQL's built-in xmin column acts as the optimistic concurrency token.
        builder.Property(user => user.Version).IsRowVersion();

        builder.Ignore(user => user.DomainEvents);
    }
}
