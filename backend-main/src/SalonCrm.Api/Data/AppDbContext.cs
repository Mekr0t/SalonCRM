using Microsoft.EntityFrameworkCore;
using SalonCrm.Api.Domain;

namespace SalonCrm.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<Service> Services => Set<Service>();
    public DbSet<UserService> UserServices => Set<UserService>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<RetentionAction> RetentionActions => Set<RetentionAction>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<UserRole>().HaveConversion<string>();
        configurationBuilder.Properties<ServiceCategory>().HaveConversion<string>();
        configurationBuilder.Properties<AppointmentStatus>().HaveConversion<string>();
        configurationBuilder.Properties<AppointmentSource>().HaveConversion<string>();
        configurationBuilder.Properties<PaymentMethod>().HaveConversion<string>();
        configurationBuilder.Properties<RetentionChannel>().HaveConversion<string>();
        configurationBuilder.Properties<decimal>().HavePrecision(10, 2);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
}
