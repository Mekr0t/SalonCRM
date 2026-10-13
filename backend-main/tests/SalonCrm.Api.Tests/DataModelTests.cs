using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SalonCrm.Api.Data;
using SalonCrm.Api.Data.Seed;
using SalonCrm.Api.Domain;

namespace SalonCrm.Api.Tests;

[Collection("api")]
public class DataModelTests(ApiFactory factory)
{
    private static readonly DateTime Base = new(2030, 1, 1, 9, 0, 0, DateTimeKind.Utc);

    private AsyncServiceScope NewScope() => factory.Services.CreateAsyncScope();

    private static async Task<T> Scalar<T>(AppDbContext db, string sql)
    {
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return (T)(await cmd.ExecuteScalarAsync())!;
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    [Fact]
    public async Task All_tables_exist_with_snake_case_names()
    {
        await using var scope = NewScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        string[] expected =
        [
            "appointments", "customers", "payments", "retention_actions",
            "services", "sessions", "user_services", "users",
        ];

        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'";
        var tables = new List<string>();
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync()) tables.Add(r.GetString(0));
        }

        foreach (var t in expected) Assert.Contains(t, tables);
    }

    [Fact]
    public async Task Overlap_constraint_and_extension_exist()
    {
        await using var scope = NewScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Equal(1L, await Scalar<long>(db,
            "SELECT count(*) FROM pg_constraint WHERE conname = 'ex_appointments_no_overlap'"));
        Assert.Equal(1L, await Scalar<long>(db,
            "SELECT count(*) FROM pg_extension WHERE extname = 'btree_gist'"));
    }

    [Fact]
    public async Task Enum_is_stored_as_text()
    {
        await using var scope = NewScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Equal("Admin", await Scalar<string>(db,
            "SELECT role FROM users WHERE email = 'admin@salon.local'"));
    }

    [Fact]
    public async Task Enum_values_are_written_as_text_literals()
    {
        await using var scope = NewScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var f = await CreateFixtureAsync(db, ServiceCategory.Nails);

        var online = Appt(f, f.EmployeeA, Base, Base.AddHours(1));
        online.Source = AppointmentSource.Online;
        var staff = Appt(f, f.EmployeeA, Base.AddHours(2), Base.AddHours(3), AppointmentStatus.Completed);
        db.Appointments.AddRange(online, staff);
        db.Payments.Add(new Payment
        {
            AppointmentId = online.Id, Amount = 10m, Method = PaymentMethod.Card,
            PaidAt = DateTime.UtcNow, RecordedByUserId = f.EmployeeA,
        });
        db.RetentionActions.Add(new RetentionAction
        {
            CustomerId = f.Customer, CreatedByUserId = f.EmployeeA, Channel = RetentionChannel.Phone,
            Note = "call", CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        Assert.Equal("Scheduled", await Scalar<string>(db,
            $"SELECT status FROM appointments WHERE id = '{online.Id}'"));
        Assert.Equal("Online", await Scalar<string>(db,
            $"SELECT source FROM appointments WHERE id = '{online.Id}'"));
        Assert.Equal("Completed", await Scalar<string>(db,
            $"SELECT status FROM appointments WHERE id = '{staff.Id}'"));
        Assert.Equal("Staff", await Scalar<string>(db,
            $"SELECT source FROM appointments WHERE id = '{staff.Id}'"));
        Assert.Equal("Nails", await Scalar<string>(db,
            $"SELECT category FROM services WHERE id = '{f.Service}'"));
        Assert.Equal("Card", await Scalar<string>(db,
            $"SELECT method FROM payments WHERE appointment_id = '{online.Id}'"));
        Assert.Equal("Phone", await Scalar<string>(db,
            $"SELECT channel FROM retention_actions WHERE customer_id = '{f.Customer}'"));
        Assert.Equal("User", await Scalar<string>(db,
            $"SELECT role FROM users WHERE id = '{f.EmployeeA}'"));
    }

    [Fact]
    public async Task Enum_columns_have_text_data_type()
    {
        await using var scope = NewScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        string[] expected =
        [
            "appointments.status", "appointments.source", "services.category", "payments.method",
            "retention_actions.channel", "users.role",
        ];

        var types = new Dictionary<string, string>();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT table_name || '.' || column_name, data_type
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND (table_name, column_name) IN (
                    ('appointments', 'status'), ('appointments', 'source'), ('services', 'category'),
                    ('payments', 'method'), ('retention_actions', 'channel'), ('users', 'role'))
                """;
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) types[r.GetString(0)] = r.GetString(1);
        }
        finally
        {
            await conn.CloseAsync();
        }

        Assert.Equal(expected.Length, types.Count);
        foreach (var column in expected)
            Assert.Equal("text", types.GetValueOrDefault(column));
    }

    private sealed record Fixture(Guid Customer, Guid EmployeeA, Guid EmployeeB, Guid Service);

    private static async Task<Fixture> CreateFixtureAsync(AppDbContext db,
        ServiceCategory category = ServiceCategory.Skin)
    {
        var tag = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        var customer = new Customer
        {
            Email = $"c{tag}@x.test", EmailNormalized = $"c{tag}@x.test", FullName = "Test",
            GdprConsentAt = now, CreatedAt = now,
        };
        var a = new User { Email = $"a{tag}@x.test", PasswordHash = "x", FullName = "A", Role = UserRole.User, CreatedAt = now };
        var b = new User { Email = $"b{tag}@x.test", PasswordHash = "x", FullName = "B", Role = UserRole.User, CreatedAt = now };
        var service = new Service { Name = "S" + tag, Category = category, DurationMinutes = 60, Price = 10m, IsActive = false };
        db.AddRange(customer, a, b, service);
        await db.SaveChangesAsync();
        return new Fixture(customer.Id, a.Id, b.Id, service.Id);
    }

    private static Appointment Appt(Fixture f, Guid employee, DateTime start, DateTime end,
        AppointmentStatus status = AppointmentStatus.Scheduled) => new()
    {
        CustomerId = f.Customer, EmployeeId = employee, ServiceId = f.Service,
        StartsAt = start, EndsAt = end, Status = status, Price = 10m,
        Source = AppointmentSource.Staff, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task Overlapping_scheduled_appointments_for_same_employee_are_rejected()
    {
        await using var scope = NewScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var f = await CreateFixtureAsync(db);
        db.Appointments.Add(Appt(f, f.EmployeeA, Base, Base.AddHours(1)));
        await db.SaveChangesAsync();

        db.Appointments.Add(Appt(f, f.EmployeeA, Base.AddMinutes(30), Base.AddMinutes(90)));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var pg = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal("23P01", pg.SqlState);
    }

    [Fact]
    public async Task Overlap_with_cancelled_appointment_is_allowed()
    {
        await using var scope = NewScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var f = await CreateFixtureAsync(db);
        db.Appointments.Add(Appt(f, f.EmployeeA, Base, Base.AddHours(1), AppointmentStatus.Cancelled));
        await db.SaveChangesAsync();

        db.Appointments.Add(Appt(f, f.EmployeeA, Base.AddMinutes(30), Base.AddMinutes(90)));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Same_time_for_different_employee_is_allowed()
    {
        await using var scope = NewScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var f = await CreateFixtureAsync(db);
        db.Appointments.Add(Appt(f, f.EmployeeA, Base, Base.AddHours(1)));
        db.Appointments.Add(Appt(f, f.EmployeeB, Base, Base.AddHours(1)));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Back_to_back_appointments_are_allowed()
    {
        await using var scope = NewScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var f = await CreateFixtureAsync(db);
        db.Appointments.Add(Appt(f, f.EmployeeA, Base, Base.AddHours(1)));
        db.Appointments.Add(Appt(f, f.EmployeeA, Base.AddHours(1), Base.AddHours(2)));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Base_seed_creates_expected_accounts_and_services()
    {
        await using var scope = NewScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Equal(1, await db.Users.CountAsync(u => u.Email.EndsWith("@salon.local") && u.Role == UserRole.Admin));
        Assert.Equal(4, await db.Users.CountAsync(u => u.Email.EndsWith("@salon.local") && u.Role == UserRole.User));
        Assert.Equal(14, await db.Services.CountAsync(s => s.IsActive));

        async Task<int> Count(string email) =>
            await db.UserServices.CountAsync(us => us.User.Email == email);
        Assert.Equal(5, await Count("jana.kovacova@salon.local"));
        Assert.Equal(3, await Count("lucia.horvathova@salon.local"));
        Assert.Equal(4, await Count("petra.novotna@salon.local"));
        Assert.Equal(5, await Count("maria.vargova@salon.local"));

        var admin = await db.Users.SingleAsync(u => u.Email == "admin@salon.local");
        var hasher = new PasswordHasher<User>();
        Assert.Equal(PasswordVerificationResult.Success,
            hasher.VerifyHashedPassword(admin, admin.PasswordHash, ApiFactory.AdminPassword));
        var jana = await db.Users.SingleAsync(u => u.Email == "jana.kovacova@salon.local");
        Assert.Equal(PasswordVerificationResult.Success,
            hasher.VerifyHashedPassword(jana, jana.PasswordHash, ApiFactory.UserPassword));
    }
}
