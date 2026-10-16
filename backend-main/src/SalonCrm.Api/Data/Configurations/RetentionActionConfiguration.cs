using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalonCrm.Api.Domain;

namespace SalonCrm.Api.Data.Configurations;

public class RetentionActionConfiguration : IEntityTypeConfiguration<RetentionAction>
{
    public void Configure(EntityTypeBuilder<RetentionAction> b)
    {
        b.ToTable("retention_actions", t =>
            t.HasCheckConstraint("ck_retention_actions_channel", EnumChecks.In<RetentionChannel>("channel")));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.ChurnProbability).HasPrecision(4, 3);
        b.HasOne(x => x.Customer).WithMany(c => c.RetentionActions).HasForeignKey(x => x.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.CreatedByUser).WithMany().HasForeignKey(x => x.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
