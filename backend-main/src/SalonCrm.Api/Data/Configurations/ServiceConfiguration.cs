using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalonCrm.Api.Domain;

namespace SalonCrm.Api.Data.Configurations;

public class ServiceConfiguration : IEntityTypeConfiguration<Service>
{
    public void Configure(EntityTypeBuilder<Service> b)
    {
        b.ToTable("services", t =>
        {
            t.HasCheckConstraint("ck_services_duration_minutes", "duration_minutes > 0 AND duration_minutes % 15 = 0");
            t.HasCheckConstraint("ck_services_price", "price >= 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
    }
}
