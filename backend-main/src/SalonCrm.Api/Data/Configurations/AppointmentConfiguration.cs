using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalonCrm.Api.Domain;

namespace SalonCrm.Api.Data.Configurations;

public class AppointmentConfiguration : IEntityTypeConfiguration<Appointment>
{
    public void Configure(EntityTypeBuilder<Appointment> b)
    {
        b.ToTable("appointments", t =>
        {
            t.HasCheckConstraint("ck_appointments_ends_after_starts", "ends_at > starts_at");
            t.HasCheckConstraint("ck_appointments_price", "price >= 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.HasIndex(x => new { x.EmployeeId, x.StartsAt });
        b.HasIndex(x => new { x.CustomerId, x.StartsAt });
        b.HasIndex(x => x.Status);
        b.HasOne(x => x.Customer).WithMany(c => c.Appointments).HasForeignKey(x => x.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Employee).WithMany(u => u.Appointments).HasForeignKey(x => x.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Service).WithMany().HasForeignKey(x => x.ServiceId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
