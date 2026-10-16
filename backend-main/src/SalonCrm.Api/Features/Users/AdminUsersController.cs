using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SalonCrm.Api.Auth;
using SalonCrm.Api.Data;
using SalonCrm.Api.Domain;

namespace SalonCrm.Api.Features.Users;

public sealed record ServiceRefDto(Guid Id, string Name);

public sealed record UserDto(
    Guid Id,
    string Email,
    string FullName,
    UserRole Role,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? LastLoginAt,
    List<ServiceRefDto> Services);

public sealed record CreateUserRequest(
    [Required, EmailAddress, MaxLength(254)] string Email,
    [Required, MaxLength(200)] string FullName,
    [Required, EnumDataType(typeof(UserRole))] UserRole? Role, // nullable so an omitted role is a 400, not the enum default
    [Required, MinLength(8), MaxLength(256)] string Password,
    [Required] Guid[] ServiceIds);

public sealed record UpdateUserRequest(
    [Required, MaxLength(200)] string FullName,
    [Required, EnumDataType(typeof(UserRole))] UserRole? Role,
    [Required] Guid[] ServiceIds);

public sealed record SetPasswordRequest([Required, MinLength(8), MaxLength(256)] string Password);

public sealed record DeactivateUserResponse(int FutureAppointments);

[ApiController]
[Authorize(Roles = Roles.Admin)]
[Route("api/admin/users")]
public class AdminUsersController(
    AppDbContext db,
    SessionService sessions,
    IPasswordHasher<User> hasher,
    TimeProvider timeProvider) : ControllerBase
{
    [HttpGet]
    public async Task<List<UserDto>> List(CancellationToken ct) =>
        await Project(db.Users.OrderBy(u => u.FullName)).ToListAsync(ct);

    [HttpPost]
    public async Task<IActionResult> Create(CreateUserRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var serviceIds = request.ServiceIds.Distinct().ToList();

        if (await db.Users.AnyAsync(u => u.Email == email, ct))
        {
            return DuplicateEmail();
        }

        if (await InvalidServiceIdsAsync(serviceIds, ct))
        {
            return UnknownServices();
        }

        var user = new User
        {
            Email = email,
            FullName = request.FullName.Trim(),
            PasswordHash = "",
            Role = request.Role!.Value,
            IsActive = true,
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
        };
        user.PasswordHash = hasher.HashPassword(user, request.Password);
        db.Users.Add(user);
        db.UserServices.AddRange(serviceIds.Select(s => new UserService { UserId = user.Id, ServiceId = s }));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return DuplicateEmail(); // lost a race with a concurrent create
        }

        var dto = await Project(db.Users.Where(u => u.Id == user.Id)).SingleAsync(ct);
        return Created($"/api/admin/users/{user.Id}", dto);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateUserRequest request, CancellationToken ct)
    {
        var role = request.Role!.Value; // [Required] guarantees a value
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return NotFound();
        }

        if (user.Role == UserRole.Admin && role != UserRole.Admin)
        {
            if (id == User.GetUserId())
            {
                return Conflict409("Nemôžete si odobrať rolu Admin.");
            }

            if (user.IsActive && await IsLastActiveAdminAsync(id, ct))
            {
                return Conflict409("Posledného aktívneho administrátora nie je možné degradovať.");
            }
        }

        var serviceIds = request.ServiceIds.Distinct().ToList();
        if (await InvalidServiceIdsAsync(serviceIds, ct))
        {
            return UnknownServices();
        }

        user.FullName = request.FullName.Trim();
        user.Role = role;

        var existing = await db.UserServices.Where(us => us.UserId == id).ToListAsync(ct);
        db.UserServices.RemoveRange(existing.Where(us => !serviceIds.Contains(us.ServiceId)));
        var have = existing.Select(us => us.ServiceId).ToHashSet();
        db.UserServices.AddRange(serviceIds.Where(s => !have.Contains(s))
            .Select(s => new UserService { UserId = id, ServiceId = s }));

        await db.SaveChangesAsync(ct);
        return Ok(await Project(db.Users.Where(u => u.Id == id)).SingleAsync(ct));
    }

    [HttpPost("{id:guid}/deactivate")]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return NotFound();
        }

        if (id == User.GetUserId())
        {
            return Conflict409("Nemôžete deaktivovať sám seba.");
        }

        if (user.IsActive && user.Role == UserRole.Admin && await IsLastActiveAdminAsync(id, ct))
        {
            return Conflict409("Posledného aktívneho administrátora nie je možné deaktivovať.");
        }

        user.IsActive = false;
        await db.SaveChangesAsync(ct);
        await sessions.RevokeAllForUserAsync(id, null, ct);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var future = await db.Appointments.CountAsync(
            a => a.EmployeeId == id && a.Status == AppointmentStatus.Scheduled && a.StartsAt > now, ct);
        return Ok(new DeactivateUserResponse(future));
    }

    [HttpPost("{id:guid}/activate")]
    public async Task<IActionResult> Activate(Guid id, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return NotFound();
        }

        user.IsActive = true;
        await db.SaveChangesAsync(ct);
        return Ok(await Project(db.Users.Where(u => u.Id == id)).SingleAsync(ct));
    }

    [HttpPost("{id:guid}/password")]
    public async Task<IActionResult> SetPassword(Guid id, SetPasswordRequest request, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return NotFound();
        }

        user.PasswordHash = hasher.HashPassword(user, request.Password);
        await db.SaveChangesAsync(ct);

        // Admin resetting their own password keeps the current session.
        await sessions.RevokeAllForUserAsync(id, id == User.GetUserId() ? User.GetSessionId() : null, ct);
        return NoContent();
    }

    private static IQueryable<UserDto> Project(IQueryable<User> users) => users.AsNoTracking().Select(u => new UserDto(
        u.Id, u.Email, u.FullName, u.Role, u.IsActive, u.CreatedAt, u.LastLoginAt,
        u.UserServices.OrderBy(us => us.Service.Name)
            .Select(us => new ServiceRefDto(us.ServiceId, us.Service.Name)).ToList()));

    private async Task<bool> IsLastActiveAdminAsync(Guid targetId, CancellationToken ct) =>
        !await db.Users.AnyAsync(u => u.Id != targetId && u.Role == UserRole.Admin && u.IsActive, ct);

    private async Task<bool> InvalidServiceIdsAsync(List<Guid> ids, CancellationToken ct) =>
        ids.Count > 0 && await db.Services.CountAsync(s => ids.Contains(s.Id), ct) != ids.Count;

    private ObjectResult UnknownServices()
    {
        ModelState.AddModelError("serviceIds", "One or more service ids do not exist.");
        return (ObjectResult)ValidationProblem(ModelState);
    }

    private ObjectResult DuplicateEmail() => Conflict409("Používateľ s týmto e-mailom už existuje.");

    private ObjectResult Conflict409(string detail) =>
        Problem(statusCode: StatusCodes.Status409Conflict, title: "Conflict", detail: detail);
}
