using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SalonCrm.Api.Auth;
using SalonCrm.Api.Data;
using SalonCrm.Api.Domain;
using SalonCrm.Api.Features.Common;
using SalonCrm.Api.Features.Scheduling;

namespace SalonCrm.Api.Features.Stats;

public sealed record PeriodQuery(DateOnly? From, DateOnly? To);

public sealed record MonthlyQuery(int? Months);

public sealed record StatsOverviewDto(
    DateOnly From,
    DateOnly To,
    decimal Revenue,
    int CompletedVisits,
    decimal? NoShowRate,
    int NewCustomers,
    decimal? AverageVisitValue);

public sealed record MonthlyStatsDto(string Month, decimal Revenue, int CompletedVisits, int NewCustomers);

public sealed record ServiceStatsDto(Guid ServiceId, string Name, ServiceCategory Category, int CompletedVisits, decimal Revenue);

public sealed record EmployeeStatsDto(
    Guid EmployeeId,
    string FullName,
    int CompletedVisits,
    decimal Revenue,
    int BookedMinutes,
    int OpeningMinutes,
    decimal Utilization);

[ApiController]
[Authorize(Roles = Roles.Admin)]
[Route("api/admin/stats")]
public class AdminStatsController(AppDbContext db, IOptions<SalonOptions> salonOptions, TimeProvider timeProvider) : ControllerBase
{
    private const int DefaultMonths = 12;
    private const int MaxMonths = 24;

    private SalonOptions Salon => salonOptions.Value;

    [HttpGet("overview")]
    public async Task<IActionResult> Overview([FromQuery] PeriodQuery query, CancellationToken ct)
    {
        var (period, error) = StatsPeriod.Resolve(query.From, query.To, Salon, timeProvider);
        if (period is null)
        {
            return this.BadRequestProblem(error!);
        }

        var totals = await TotalsAsync(period.StartUtc, period.EndUtc, ct);
        var finished = totals.Completed + totals.NoShow;
        return Ok(new StatsOverviewDto(
            period.From,
            period.To,
            Money(totals.Revenue),
            totals.Completed,
            finished == 0 ? null : Math.Round((decimal)totals.NoShow / finished, 4, MidpointRounding.AwayFromZero),
            totals.NewCustomers,
            totals.Completed == 0 ? null : Money(totals.Revenue / totals.Completed)));
    }

    // Local calendar months, oldest first, including the current (partial) one; empty months are zeros.
    [HttpGet("monthly")]
    public async Task<IActionResult> Monthly([FromQuery] MonthlyQuery query, CancellationToken ct)
    {
        var months = query.Months ?? DefaultMonths;
        if (months is < 1 or > MaxMonths)
        {
            return this.BadRequestProblem($"Parameter 'months' must be between 1 and {MaxMonths}.");
        }

        var zone = Salon.Zone;
        var today = StatsPeriod.Today(timeProvider, zone);
        var firstMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-(months - 1));

        var result = new List<MonthlyStatsDto>(months);
        for (var i = 0; i < months; i++)
        {
            var monthStart = firstMonth.AddMonths(i);
            var monthEnd = monthStart.AddMonths(1).AddDays(-1);
            var (startUtc, endUtc) = StatsPeriod.UtcRange(monthStart, monthEnd, zone);
            var totals = await TotalsAsync(startUtc, endUtc, ct);
            result.Add(new MonthlyStatsDto(
                monthStart.ToString("yyyy-MM"), Money(totals.Revenue), totals.Completed, totals.NewCustomers));
        }

        return Ok(result);
    }

    [HttpGet("services")]
    public async Task<IActionResult> Services([FromQuery] PeriodQuery query, CancellationToken ct)
    {
        var (period, error) = StatsPeriod.Resolve(query.From, query.To, Salon, timeProvider);
        if (period is null)
        {
            return this.BadRequestProblem(error!);
        }

        // Services with at least one Completed visit in the period, including inactive ones.
        var services = await db.Appointments.AsNoTracking()
            .Where(a => a.Status == AppointmentStatus.Completed && a.StartsAt >= period.StartUtc && a.StartsAt < period.EndUtc)
            .GroupBy(a => new { a.ServiceId, a.Service.Name, a.Service.Category })
            .OrderByDescending(g => g.Sum(a => a.Payment != null ? a.Payment.Amount : 0m))
            .ThenBy(g => g.Key.Name)
            .Select(g => new ServiceStatsDto(
                g.Key.ServiceId,
                g.Key.Name,
                g.Key.Category,
                g.Count(),
                g.Sum(a => a.Payment != null ? a.Payment.Amount : 0m)))
            .ToListAsync(ct);

        return Ok(services.Select(s => s with { Revenue = Money(s.Revenue) }));
    }

    [HttpGet("employees")]
    public async Task<IActionResult> Employees([FromQuery] PeriodQuery query, CancellationToken ct)
    {
        var (period, error) = StatsPeriod.Resolve(query.From, query.To, Salon, timeProvider);
        if (period is null)
        {
            return this.BadRequestProblem(error!);
        }

        // Active employees with at least one service, plus anyone with an appointment in the period.
        var employees = await db.Users.AsNoTracking()
            .Where(u => (u.IsActive && u.UserServices.Any())
                || u.Appointments.Any(a => a.StartsAt >= period.StartUtc && a.StartsAt < period.EndUtc))
            .OrderBy(u => u.FullName)
            .Select(u => new { u.Id, u.FullName })
            .ToListAsync(ct);

        var visits = await db.Appointments.AsNoTracking()
            .Where(a => a.StartsAt >= period.StartUtc && a.StartsAt < period.EndUtc)
            .GroupBy(a => a.EmployeeId)
            .Select(g => new
            {
                EmployeeId = g.Key,
                Completed = g.Count(a => a.Status == AppointmentStatus.Completed),
                BookedMinutes = g.Where(a => a.Status != AppointmentStatus.Cancelled)
                    .Sum(a => (a.EndsAt - a.StartsAt).TotalMinutes),
            })
            .ToListAsync(ct);

        var revenue = await db.Payments.AsNoTracking()
            .Where(p => p.Appointment.Status == AppointmentStatus.Completed
                && p.Appointment.StartsAt >= period.StartUtc && p.Appointment.StartsAt < period.EndUtc)
            .GroupBy(p => p.Appointment.EmployeeId)
            .Select(g => new { EmployeeId = g.Key, Revenue = g.Sum(p => p.Amount) })
            .ToListAsync(ct);

        var openingMinutes = StatsPeriod.OpeningMinutes(period.From, period.To, Salon);
        var visitsById = visits.ToDictionary(v => v.EmployeeId);
        var revenueById = revenue.ToDictionary(r => r.EmployeeId, r => r.Revenue);

        return Ok(employees.Select(e =>
        {
            visitsById.TryGetValue(e.Id, out var v);
            var booked = (int)Math.Round(v?.BookedMinutes ?? 0, MidpointRounding.AwayFromZero);
            return new EmployeeStatsDto(
                e.Id,
                e.FullName,
                v?.Completed ?? 0,
                Money(revenueById.GetValueOrDefault(e.Id)),
                booked,
                openingMinutes,
                openingMinutes == 0
                    ? 0m
                    : Math.Round((decimal)booked / openingMinutes, 4, MidpointRounding.AwayFromZero));
        }));
    }

    private sealed record PeriodTotals(decimal Revenue, int Completed, int NoShow, int NewCustomers);

    // Totals of one UTC interval, shared by overview and monthly. Every number is computed in SQL.
    private async Task<PeriodTotals> TotalsAsync(DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        var counts = await db.Appointments.AsNoTracking()
            .Where(a => a.StartsAt >= startUtc && a.StartsAt < endUtc
                && (a.Status == AppointmentStatus.Completed || a.Status == AppointmentStatus.NoShow))
            .GroupBy(a => a.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var revenue = await db.Payments.AsNoTracking()
            .Where(p => p.Appointment.Status == AppointmentStatus.Completed
                && p.Appointment.StartsAt >= startUtc && p.Appointment.StartsAt < endUtc)
            .SumAsync(p => p.Amount, ct);

        // A new customer = the customer's first Completed visit (over all time) falls into the interval.
        var newCustomers = await db.Appointments.AsNoTracking()
            .Where(a => a.Status == AppointmentStatus.Completed)
            .GroupBy(a => a.CustomerId)
            .Select(g => new { CustomerId = g.Key, FirstStartsAt = g.Min(a => a.StartsAt) })
            .CountAsync(c => c.FirstStartsAt >= startUtc && c.FirstStartsAt < endUtc, ct);

        return new PeriodTotals(
            revenue,
            counts.FirstOrDefault(c => c.Status == AppointmentStatus.Completed)?.Count ?? 0,
            counts.FirstOrDefault(c => c.Status == AppointmentStatus.NoShow)?.Count ?? 0,
            newCustomers);
    }

    private static decimal Money(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
