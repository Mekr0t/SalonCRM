using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SalonCrm.Api.Domain;

namespace SalonCrm.Api.Data.Seed;

public class BaseSeeder(
    AppDbContext db,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<BaseSeeder> logger)
{
    private sealed record ServiceDef(ServiceCategory Category, string Name, int Minutes, decimal Price);

    private sealed record WorkerDef(string FullName, string Email, ServiceCategory[] Categories);

    private static readonly ServiceDef[] ServiceDefs =
    [
        new(ServiceCategory.Skin, "Hĺbkové čistenie pleti", 60, 45),
        new(ServiceCategory.Skin, "Hydratačné ošetrenie pleti", 60, 50),
        new(ServiceCategory.Skin, "Anti-age ošetrenie", 75, 60),
        new(ServiceCategory.Makeup, "Denné líčenie", 45, 30),
        new(ServiceCategory.Makeup, "Svadobné líčenie", 90, 70),
        new(ServiceCategory.Nails, "Gélové nechty – nová modelácia", 120, 40),
        new(ServiceCategory.Nails, "Doplnenie gélových nechtov", 90, 30),
        new(ServiceCategory.Nails, "Manikúra s gél lakom", 60, 25),
        new(ServiceCategory.BrowsLashes, "Laminácia obočia", 45, 30),
        new(ServiceCategory.BrowsLashes, "Lash lifting", 60, 35),
        new(ServiceCategory.Waxing, "Depilácia nôh", 45, 30),
        new(ServiceCategory.Waxing, "Depilácia podpazušia", 15, 10),
        new(ServiceCategory.Massage, "Relaxačná masáž", 60, 40),
        new(ServiceCategory.Massage, "Masáž chrbta a šije", 30, 25),
    ];

    private static readonly WorkerDef[] WorkerDefs =
    [
        new("Jana Kováčová", "jana.kovacova@salon.local", [ServiceCategory.Skin, ServiceCategory.Makeup]),
        new("Lucia Horváthová", "lucia.horvathova@salon.local", [ServiceCategory.Nails]),
        new("Petra Novotná", "petra.novotna@salon.local", [ServiceCategory.BrowsLashes, ServiceCategory.Waxing]),
        new("Mária Vargová", "maria.vargova@salon.local", [ServiceCategory.Massage, ServiceCategory.Skin]),
    ];

    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (await db.Users.AnyAsync(ct))
        {
            return;
        }

        var adminPassword = configuration["Seed:AdminPassword"];
        var userPassword = configuration["Seed:UserPassword"];
        if (string.IsNullOrWhiteSpace(adminPassword) || string.IsNullOrWhiteSpace(userPassword))
        {
            logger.LogWarning(
                "Base seed skipped: Seed:AdminPassword and Seed:UserPassword must both be set.");
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var hasher = new PasswordHasher<User>();

        User NewUser(string fullName, string email, UserRole role, string password)
        {
            var user = new User
            {
                Email = email.ToLowerInvariant(),
                FullName = fullName,
                PasswordHash = "",
                Role = role,
                IsActive = true,
                CreatedAt = now,
            };
            user.PasswordHash = hasher.HashPassword(user, password);
            return user;
        }

        db.Users.Add(NewUser("Administrátor salónu", "admin@salon.local", UserRole.Admin, adminPassword));

        var services = ServiceDefs.Select(s => new Service
        {
            Name = s.Name,
            Category = s.Category,
            DurationMinutes = s.Minutes,
            Price = s.Price,
            IsActive = true,
        }).ToList();
        db.Services.AddRange(services);

        foreach (var w in WorkerDefs)
        {
            var worker = NewUser(w.FullName, w.Email, UserRole.User, userPassword);
            db.Users.Add(worker);
            foreach (var service in services.Where(s => w.Categories.Contains(s.Category)))
            {
                db.UserServices.Add(new UserService { UserId = worker.Id, ServiceId = service.Id });
            }
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Base seed applied: 1 admin, {Workers} workers, {Services} services.",
            WorkerDefs.Length, services.Count);
    }
}
