using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WalletPK.Common.Identifiers;
using WalletPK.Identity.Domain.Kyc;
using WalletPK.Identity.Domain.Users;

namespace WalletPK.Identity.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="KycProfile"/> to "kyc_profiles": one row per verified user, CNIC encrypted.</summary>
internal sealed class KycProfileConfiguration : IEntityTypeConfiguration<KycProfile>
{
    public void Configure(EntityTypeBuilder<KycProfile> builder)
    {
        builder.ToTable("kyc_profiles");

        builder.HasKey(profile => profile.Id);
        builder.Property(profile => profile.Id)
            .HasColumnName("user_id")
            .HasConversion(id => id.Value, value => new UserId(value))
            .ValueGeneratedNever();

        builder.HasOne<User>()
            .WithOne()
            .HasForeignKey<KycProfile>(profile => profile.Id)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(profile => profile.CnicCiphertext).HasMaxLength(200).IsRequired();

        // One CNIC, one wallet: enforced on the blind index because the ciphertext differs every time.
        builder.Property(profile => profile.CnicLookupHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(profile => profile.CnicLookupHash)
            .IsUnique()
            .HasDatabaseName("ux_kyc_profiles_cnic_lookup_hash");

        builder.Property(profile => profile.CnicMasked).HasMaxLength(15).IsRequired();
        builder.Property(profile => profile.FullName).HasMaxLength(KycProfile.FullNameMaxLength).IsRequired();
        builder.Property(profile => profile.DateOfBirth);
        builder.Property(profile => profile.VerificationReference).HasMaxLength(64).IsRequired();
        builder.Property(profile => profile.VerifiedAt);

        builder.Property(profile => profile.Version).IsRowVersion();

        builder.Ignore(profile => profile.UserId);
    }
}
