using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WalletPK.Common.Identifiers;
using WalletPK.Identity.Domain.Devices;
using WalletPK.Identity.Domain.Users;

namespace WalletPK.Identity.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="Device"/> to the "devices" table.</summary>
internal sealed class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    public void Configure(EntityTypeBuilder<Device> builder)
    {
        builder.ToTable("devices");

        builder.HasKey(device => device.Id);
        builder.Property(device => device.Id)
            .HasConversion(id => id.Value, value => new DeviceId(value))
            .ValueGeneratedNever();

        builder.Property(device => device.UserId)
            .HasConversion(id => id.Value, value => new UserId(value));

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(device => device.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(device => device.InstallationId).HasMaxLength(Device.InstallationIdMaxLength).IsRequired();

        // One row per (user, app installation): logging in again on the same phone reuses it.
        builder.HasIndex(device => new { device.UserId, device.InstallationId })
            .IsUnique()
            .HasDatabaseName("ux_devices_user_installation");

        builder.Property(device => device.Name).HasMaxLength(Device.NameMaxLength).IsRequired();
        builder.Property(device => device.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(device => device.TrustedAt);
        builder.Property(device => device.LastSeenAt);
        builder.Property(device => device.CoolingOffUntil);
        builder.Property(device => device.RevokedAt);

        builder.Property(device => device.Version).IsRowVersion();

        builder.Ignore(device => device.IsTrusted);
        builder.Ignore(device => device.DomainEvents);
    }
}
