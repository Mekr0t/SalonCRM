using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalonCrm.Api.Domain;

namespace SalonCrm.Api.Data.Configurations;

public class UserServiceConfiguration : IEntityTypeConfiguration<UserService>
{
    public void Configure(EntityTypeBuilder<UserService> b)
    {
        b.ToTable("user_services");
        b.HasKey(x => new { x.UserId, x.ServiceId });
        b.HasOne(x => x.User).WithMany(u => u.UserServices).HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Service).WithMany(s => s.UserServices).HasForeignKey(x => x.ServiceId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
