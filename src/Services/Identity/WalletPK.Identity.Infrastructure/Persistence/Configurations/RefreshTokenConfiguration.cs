using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WalletPK.Common.Identifiers;
using WalletPK.Identity.Domain.Devices;
using WalletPK.Identity.Domain.Sessions;
using WalletPK.Identity.Domain.Users;

namespace WalletPK.Identity.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="RefreshToken"/> to the "refresh_tokens" table. Only token hashes are stored.</summary>
internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens");

        builder.HasKey(token => token.Id);
        builder.Property(token => token.Id).ValueGeneratedNever();

        builder.Property(token => token.UserId)
            .HasConversion(id => id.Value, value => new UserId(value));
        builder.Property(token => token.DeviceId)
            .HasConversion(id => id.Value, value => new DeviceId(value));

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(token => token.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(token => token.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Looked up on every refresh, so it is indexed (and unique: 256-bit tokens never collide).
        builder.Property(token => token.TokenHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(token => token.TokenHash)
            .IsUnique()
            .HasDatabaseName("ux_refresh_tokens_token_hash");

        builder.Property(token => token.FamilyId);
        builder.HasIndex(token => token.FamilyId).HasDatabaseName("ix_refresh_tokens_family_id");

        builder.Property(token => token.CreatedAt);
        builder.Property(token => token.ExpiresAt);
        builder.Property(token => token.ConsumedAt);
        builder.Property(token => token.RevokedAt);

        builder.Property(token => token.Version).IsRowVersion();

        builder.Ignore(token => token.IsConsumed);
        builder.Ignore(token => token.IsRevoked);
    }
}
