using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalonCrm.Api.Auth;
using SalonCrm.Api.Data;
using SalonCrm.Api.Domain;
using SalonCrm.Api.Features.Common;
using SalonCrm.Api.Features.Appointments;
using SalonCrm.Api.Features.Scheduling;

namespace SalonCrm.Api.Features.Customers;

public sealed record CustomerListItemDto(
    Guid Id,
    string FullName,
    string Email,
    string? Phone,
    int VisitCount,
    DateTime? LastVisitAt,
    DateTime? NextAppointmentAt);

public sealed record CustomerPageDto(List<CustomerListItemDto> Items, int Page, int PageSize, int TotalCount);

public sealed record CustomerAppointmentServiceDto(Guid Id, string Name);

public sealed record CustomerPaymentDto(decimal Amount, PaymentMethod Method, DateTime PaidAt);

public sealed record CustomerAppointmentDto(
    Guid Id,
    DateTime StartsAt,
    DateTime EndsAt,
    AppointmentStatus Status,
    decimal Price,
    CustomerAppointmentServiceDto Service,
    PersonRefDto Employee,
    CustomerPaymentDto? Payment);

public sealed record CustomerDetailDto(
    Guid Id,
    string FullName,
    string Email,
    string? Phone,
    string? Notes,
    DateTime GdprConsentAt,
    DateTime CreatedAt,
    int VisitCount,
    decimal TotalSpent,
    List<CustomerAppointmentDto> Appointments);

public sealed record CustomerLookupItemDto(Guid Id, string FullName, string Email);

public sealed record ListCustomersQuery
{
    [MaxLength(100)] public string? Search { get; init; }
    [Range(1, 1_000_000)] public int Page { get; init; } = 1;
    [Range(1, 100)] public int PageSize { get; init; } = 20;
}

public sealed record UpdateCustomerRequest(
    [MaxLength(200)] string? FullName,
    [MaxLength(50)] string? Phone,
    [MaxLength(4000)] string? Notes);

public sealed record CustomerLookupRequest([Required, MaxLength(500)] Guid[] Ids);

[ApiController]
[Authorize(Roles = Roles.Staff)]
[Route("api/customers")]
public class CustomersController(AppDbContext db, TimeProvider timeProvider) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] ListCustomersQuery query, CancellationToken ct)
    {
        var customers = db.Customers.AsNoTracking().VisibleTo(User);
        var search = query.Search?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            var pattern = "%" + EscapeLike(search) + "%";
            customers = customers.Where(c =>
                EF.Functions.ILike(c.FullName, pattern, "\\")
                || EF.Functions.ILike(c.Email, pattern, "\\")
                || (c.Phone != null && EF.Functions.ILike(c.Phone, pattern, "\\")));
        }

        var totalCount = await customers.CountAsync(ct);

        // Counts come only from appointments the current user may see (all for Admin, own for User).
        var visible = db.Appointments.VisibleTo(User);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var items = await customers
            .OrderBy(c => c.FullName).ThenBy(c => c.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .Select(c => new CustomerListItemDto(
                c.Id,
                c.FullName,
                c.Email,
                c.Phone,
                visible.Count(a => a.CustomerId == c.Id && a.Status == AppointmentStatus.Completed),
                visible.Where(a => a.CustomerId == c.Id && a.Status == AppointmentStatus.Completed)
                    .Max(a => (DateTime?)a.StartsAt),
                visible.Where(a => a.CustomerId == c.Id && a.Status == AppointmentStatus.Scheduled && a.StartsAt > now)
                    .Min(a => (DateTime?)a.StartsAt)))
            .ToListAsync(ct);

        return Ok(new CustomerPageDto(items, query.Page, query.PageSize, totalCount));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var dto = await LoadDetailAsync(id, ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateCustomerRequest request, CancellationToken ct)
    {
        var customer = await db.Customers.VisibleTo(User).FirstOrDefaultAsync(c => c.Id == id, ct);
        if (customer is null)
        {
            return NotFound();
        }

        if (request.FullName is not null)
        {
            var name = request.FullName.Trim();
            if (name.Length == 0)
            {
                return this.BadRequestProblem(Messages.CustomerNameEmpty);
            }

            customer.FullName = name;
        }

        if (request.Phone is not null)
        {
            customer.Phone = BookingService.NullIfBlank(request.Phone);
        }

        if (request.Notes is not null)
        {
            customer.Notes = BookingService.NullIfBlank(request.Notes);
        }

        await db.SaveChangesAsync(ct);
        return Ok(await LoadDetailAsync(id, ct));
    }

    [HttpPost("lookup")]
    public async Task<List<CustomerLookupItemDto>> Lookup(CustomerLookupRequest request, CancellationToken ct)
    {
        var ids = request.Ids.Distinct().ToList();
        return await db.Customers.AsNoTracking().VisibleTo(User)
            .Where(c => ids.Contains(c.Id))
            .OrderBy(c => c.FullName).ThenBy(c => c.Id)
            .Select(c => new CustomerLookupItemDto(c.Id, c.FullName, c.Email))
            .ToListAsync(ct);
    }

    private async Task<CustomerDetailDto?> LoadDetailAsync(Guid id, CancellationToken ct)
    {
        var customer = await db.Customers.AsNoTracking().VisibleTo(User)
            .Where(c => c.Id == id)
            .Select(c => new { c.Id, c.FullName, c.Email, c.Phone, c.Notes, c.GdprConsentAt, c.CreatedAt })
            .FirstOrDefaultAsync(ct);
        if (customer is null)
        {
            return null;
        }

        var appointments = await db.Appointments.AsNoTracking().VisibleTo(User)
            .Where(a => a.CustomerId == id)
            .OrderByDescending(a => a.StartsAt).ThenBy(a => a.Id)
            .Select(a => new CustomerAppointmentDto(
                a.Id, a.StartsAt, a.EndsAt, a.Status, a.Price,
                new CustomerAppointmentServiceDto(a.Service.Id, a.Service.Name),
                new PersonRefDto(a.Employee.Id, a.Employee.FullName),
                a.Payment == null ? null : new CustomerPaymentDto(a.Payment.Amount, a.Payment.Method, a.Payment.PaidAt)))
            .ToListAsync(ct);

        return new CustomerDetailDto(
            customer.Id, customer.FullName, customer.Email, customer.Phone, customer.Notes,
            customer.GdprConsentAt, customer.CreatedAt,
            appointments.Count(a => a.Status == AppointmentStatus.Completed),
            appointments.Sum(a => a.Payment?.Amount ?? 0m),
            appointments);
    }

    // Escape character for ILIKE is backslash (the default); '%' and '_' in user input are literals.
    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
