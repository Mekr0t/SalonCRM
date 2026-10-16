using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalonCrm.Api.Auth;
using SalonCrm.Api.Data;
using SalonCrm.Api.Domain;

namespace SalonCrm.Api.Features.Services;

public sealed record ServiceDto(
    Guid Id,
    string Name,
    ServiceCategory Category,
    string? Description,
    int DurationMinutes,
    decimal Price,
    bool IsActive);

public sealed record ServiceRequest(
    [Required, MaxLength(200)] string Name,
    [Required, EnumDataType(typeof(ServiceCategory))] ServiceCategory? Category, // nullable so an omitted category is a 400
    [MaxLength(2000)] string? Description,
    [Required, Range(15, 480)] int? DurationMinutes,
    [Required, Range(0.0, 10000.0)] decimal? Price);

[ApiController]
[Authorize(Roles = Roles.Admin)]
[Route("api/admin/services")]
public class AdminServicesController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<List<ServiceDto>> List(CancellationToken ct) =>
        await Project(db.Services.OrderBy(s => s.Category).ThenBy(s => s.Name)).ToListAsync(ct);

    [HttpPost]
    public async Task<IActionResult> Create(ServiceRequest request, CancellationToken ct)
    {
        if (!IsValid(request))
        {
            return ValidationProblem(ModelState);
        }

        var service = new Service { Name = request.Name.Trim(), IsActive = true };
        Apply(service, request);
        db.Services.Add(service);
        await db.SaveChangesAsync(ct);
        return Created($"/api/admin/services/{service.Id}", ToDto(service));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, ServiceRequest request, CancellationToken ct)
    {
        if (!IsValid(request))
        {
            return ValidationProblem(ModelState);
        }

        var service = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (service is null)
        {
            return NotFound();
        }

        Apply(service, request); // existing appointments keep the price they were booked with
        await db.SaveChangesAsync(ct);
        return Ok(ToDto(service));
    }

    [HttpPost("{id:guid}/deactivate")]
    public Task<IActionResult> Deactivate(Guid id, CancellationToken ct) => SetActiveAsync(id, false, ct);

    [HttpPost("{id:guid}/activate")]
    public Task<IActionResult> Activate(Guid id, CancellationToken ct) => SetActiveAsync(id, true, ct);

    private async Task<IActionResult> SetActiveAsync(Guid id, bool active, CancellationToken ct)
    {
        var service = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (service is null)
        {
            return NotFound();
        }

        service.IsActive = active;
        await db.SaveChangesAsync(ct);
        return Ok(ToDto(service));
    }

    // Rules attributes cannot express: duration multiple of 15, price with at most 2 decimals.
    private bool IsValid(ServiceRequest request)
    {
        if (request.DurationMinutes is { } minutes && minutes % 15 != 0)
        {
            ModelState.AddModelError(nameof(request.DurationMinutes), "Duration must be a multiple of 15 minutes.");
        }

        if (request.Price is { } price && decimal.Round(price, 2) != price)
        {
            ModelState.AddModelError(nameof(request.Price), "Price must have at most 2 decimal places.");
        }

        return ModelState.IsValid;
    }

    private static void Apply(Service service, ServiceRequest request)
    {
        service.Name = request.Name.Trim();
        service.Category = request.Category!.Value;
        service.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        service.DurationMinutes = request.DurationMinutes!.Value;
        service.Price = request.Price!.Value;
    }

    private static ServiceDto ToDto(Service s) =>
        new(s.Id, s.Name, s.Category, s.Description, s.DurationMinutes, s.Price, s.IsActive);

    private static IQueryable<ServiceDto> Project(IQueryable<Service> services) => services.AsNoTracking()
        .Select(s => new ServiceDto(s.Id, s.Name, s.Category, s.Description, s.DurationMinutes, s.Price, s.IsActive));
}
