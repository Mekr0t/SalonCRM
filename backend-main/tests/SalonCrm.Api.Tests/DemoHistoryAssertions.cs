using SalonCrm.Api.Domain;
using SalonCrm.Api.Features.Scheduling;

namespace SalonCrm.Api.Tests;

internal static class DemoHistoryAssertions
{
    internal static void Verify(DateTimeOffset now, List<Customer> customers, List<Appointment> appointments,
        List<Payment> payments, HashSet<(Guid, Guid)> links)
    {
        var salon = TestSalon.Options();
        var byCustomer = customers.ToDictionary(c => c.Id);
        Assert.Equal(300, customers.Count);
        Assert.InRange(appointments.Count, 1500, 12000);
        Assert.Equal(300, appointments.Select(a => a.CustomerId).Distinct().Count());
        Assert.Equal(appointments.Count(a => a.Status == AppointmentStatus.Completed), payments.Count);
        Assert.Equal(payments.Count, payments.Select(p => p.AppointmentId).Distinct().Count());
        Assert.True(appointments.Min(a => a.StartsAt) < now.UtcDateTime.AddMonths(-23));

        foreach (var a in appointments)
        {
            var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(a.StartsAt, salon.Zone));
            Assert.Contains(a.StartsAt, SlotCalculator.GridStartsUtc(date, salon, a.Service.DurationMinutes));
            Assert.Equal(a.StartsAt.AddMinutes(a.Service.DurationMinutes), a.EndsAt);
            Assert.Contains((a.EmployeeId, a.ServiceId), links);
            Assert.Equal(DateTimeKind.Utc, a.StartsAt.Kind);
            Assert.InRange(a.CreatedAt, a.StartsAt.AddDays(-21), a.StartsAt.AddDays(-1));
            Assert.True(a.CreatedAt <= now.UtcDateTime);
            Assert.True(byCustomer[a.CustomerId].CreatedAt <= a.CreatedAt);
            Assert.Equal(byCustomer[a.CustomerId].CreatedAt, byCustomer[a.CustomerId].GdprConsentAt);
            if (a.Status == AppointmentStatus.Scheduled)
            {
                Assert.InRange(a.StartsAt, now.UtcDateTime.AddMinutes(60), now.UtcDateTime.AddDays(21));
                Assert.Null(a.CompletedAt);
                Assert.Null(a.CancelledAt);
            }
            else
            {
                Assert.True(a.EndsAt <= now.UtcDateTime);
            }
            if (a.Status == AppointmentStatus.Cancelled)
            {
                Assert.NotNull(a.CancelledAt);
                Assert.True(a.CancelledAt > a.CreatedAt && a.CancelledAt < a.StartsAt);
            }
            else
            {
                Assert.Null(a.CancelledAt);
            }
            if (a.Status == AppointmentStatus.Completed)
            {
                var payment = Assert.Single(payments, p => p.AppointmentId == a.Id);
                Assert.Equal(a.Price, payment.Amount);
                Assert.Equal(a.CompletedAt, payment.PaidAt);
                Assert.Equal(a.EmployeeId, payment.RecordedByUserId);
            }
            else
            {
                Assert.DoesNotContain(payments, p => p.AppointmentId == a.Id);
            }
        }

        foreach (var group in appointments.GroupBy(a => a.EmployeeId))
        {
            var ordered = group.OrderBy(a => a.StartsAt).ToArray();
            for (var i = 1; i < ordered.Length; i++)
            {
                Assert.True(ordered[i - 1].EndsAt <= ordered[i].StartsAt, "Demo appointments overlap.");
            }
            for (var week = 0; week < 3; week++)
            {
                Assert.Contains(group, a => a.Status == AppointmentStatus.Scheduled
                    && a.StartsAt > now.UtcDateTime.AddDays(7 * week)
                    && a.StartsAt <= now.UtcDateTime.AddDays(7 * (week + 1)));
            }
        }
        var historical = appointments.Where(a => a.Status != AppointmentStatus.Scheduled).ToList();
        Assert.InRange((double)historical.Count(a => a.Status == AppointmentStatus.Completed) / historical.Count, 0.85, 0.94);
        Assert.InRange((double)payments.Count(p => p.Method == PaymentMethod.Card) / payments.Count, 0.55, 0.65);

        var loyal = customers.Where(c => string.CompareOrdinal(c.Email, "demo-105@salon.example") <= 0).Select(c => c.Id).ToHashSet();
        Assert.True(appointments.Where(a => loyal.Contains(a.CustomerId) && a.Status == AppointmentStatus.Scheduled)
            .Select(a => a.CustomerId).Distinct().Count() > loyal.Count / 2);
        var leaving = customers.Where(c => string.CompareOrdinal(c.Email, "demo-181@salon.example") >= 0
            && string.CompareOrdinal(c.Email, "demo-240@salon.example") <= 0).Select(c => c.Id).ToHashSet();
        Assert.DoesNotContain(appointments, a => leaving.Contains(a.CustomerId) && a.Status == AppointmentStatus.Scheduled);

    }
}
