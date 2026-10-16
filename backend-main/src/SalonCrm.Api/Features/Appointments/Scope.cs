using System.Security.Claims;
using SalonCrm.Api.Auth;
using SalonCrm.Api.Domain;

namespace SalonCrm.Api.Features.Appointments;

// SPEC §4.4: the single place that decides which rows the current user may see.
// Admin sees everything; a User sees only own appointments and customers he has served.
// Every query by id goes through these filters, so a foreign record is simply "not found".
public static class Scope
{
    public static bool IsAdmin(this ClaimsPrincipal user) => user.IsInRole(Roles.Admin);

    public static IQueryable<Appointment> VisibleTo(this IQueryable<Appointment> appointments, ClaimsPrincipal user)
    {
        if (user.IsAdmin())
        {
            return appointments;
        }

        var me = user.GetUserId();
        return appointments.Where(a => a.EmployeeId == me);
    }

    public static IQueryable<Customer> VisibleTo(this IQueryable<Customer> customers, ClaimsPrincipal user)
    {
        if (user.IsAdmin())
        {
            return customers;
        }

        var me = user.GetUserId();
        return customers.Where(c => c.Appointments.Any(a => a.EmployeeId == me));
    }
}
