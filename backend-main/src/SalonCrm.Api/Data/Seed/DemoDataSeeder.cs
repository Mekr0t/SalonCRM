using Bogus;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SalonCrm.Api.Domain;
using SalonCrm.Api.Features.Scheduling;

namespace SalonCrm.Api.Data.Seed;

public sealed class DemoDataSeeder(
    AppDbContext db,
    IConfiguration configuration,
    TimeProvider clock,
    IOptions<SalonOptions> salon,
    ILogger<DemoDataSeeder> logger)
{
    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (!configuration.GetValue<bool>("Seed:DemoData") || await db.Customers.AnyAsync(ct))
        {
            return;
        }

        var services = await db.Services.AsNoTracking().Where(s => s.IsActive).ToListAsync(ct);
        services.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
        var workers = await db.Users.AsNoTracking().Where(u => u.IsActive && u.Role == UserRole.User)
            .OrderBy(u => u.Email).ToListAsync(ct);
        var links = await db.UserServices.AsNoTracking().ToListAsync(ct);
        var eligible = services.ToDictionary(s => s.Id,
            s => workers.Where(w => links.Any(l => l.ServiceId == s.Id && l.UserId == w.Id)).ToList());
        services = services.Where(s => eligible[s.Id].Count > 0).ToList();
        if (services.Count == 0)
        {
            logger.LogWarning("Demo history skipped: no active services with eligible workers. Apply the base seed first.");
            return;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var generator = new HistoryGenerator(now, salon.Value, services, eligible);
        var (customers, appointments, payments) = generator.Generate();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.Customers.AddRange(customers);
        db.Appointments.AddRange(appointments);
        db.Payments.AddRange(payments);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        logger.LogInformation("Demo history applied: {Customers} customers, {Appointments} appointments, {Payments} payments.",
            customers.Count, appointments.Count, payments.Count);
    }

    // One local random stream: never change Bogus' global seed or depend on DB-generated UUID ordering.
    internal sealed class HistoryGenerator(
        DateTime now, SalonOptions salon, List<Service> services, Dictionary<Guid, List<User>> eligible)
    {
        private readonly Faker _faker = new("sk") { Random = new Randomizer(42) };
        private readonly DateOnly _today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, salon.Zone));
        private readonly Dictionary<(Guid EmployeeId, DateOnly Date), List<BusyInterval>> _busy = [];
        private readonly List<Customer> _customers = [];
        private readonly List<Appointment> _appointments = [];
        private readonly List<Payment> _payments = [];

        public (List<Customer>, List<Appointment>, List<Payment>) Generate()
        {
            for (var i = 0; i < 300; i++)
            {
                var customer = new Customer
                {
                    FullName = _faker.Name.FullName(),
                    Email = $"demo-{i + 1:D3}@salon.example",
                    EmailNormalized = $"demo-{i + 1:D3}@salon.example",
                    Phone = _faker.Phone.PhoneNumber(),
                };
                var favorite = Pick(services);
                var worker = Pick(eligible[favorite.Id]);
                var first = _today.AddMonths(-24).AddDays(_faker.Random.Int(0, 270));
                var beforeCount = _appointments.Count;

                if (i >= 240) // One-off/new customers: one or two visits, some recent.
                {
                    first = _faker.Random.Bool() ? _today.AddDays(-_faker.Random.Int(2, 60)) : first;
                    var visits = _faker.Random.Int(1, 2);
                    for (var n = 0; n < visits; n++)
                    {
                        AddVisit(customer, favorite, worker, first, false, false);
                        first = first.AddDays(_faker.Random.Int(14, 90));
                    }
                }
                else
                {
                    var leaving = i >= 180;
                    var end = leaving ? _today.AddMonths(-_faker.Random.Int(3, 15)) : _today.AddDays(-1);
                    if (leaving && first > end.AddDays(-180))
                    {
                        first = end.AddDays(-180);
                    }
                    var date = first;
                    while (date <= end)
                    {
                        AddVisit(customer, favorite, worker, date, false, leaving, end);
                        var interval = i < 105 || leaving ? Interval(favorite.Category) : _faker.Random.Int(60, 90);
                        var progress = leaving
                            ? Math.Clamp((double)(date.DayNumber - first.DayNumber) / Math.Max(1, end.DayNumber - first.DayNumber), 0, 1)
                            : 0;
                        var jitter = i < 105 || leaving ? _faker.Random.Double(0.85, 1.15) : _faker.Random.Double(0.65, 1.35);
                        date = date.AddDays(Math.Max(7, (int)Math.Round(interval * jitter * (1 + progress * 1.5))));
                    }
                }

                // Ended histories have no future bookings. Most loyal customers do; others sometimes do.
                var futureChance = i < 105 ? 0.85f : i < 180 ? 0.2f : i >= 240 ? 0.1f : 0;
                if (_faker.Random.Bool(futureChance))
                {
                    AddVisit(customer, favorite, worker, _today.AddDays(_faker.Random.Int(1, 21)), true, false);
                }

                var firstBooking = _appointments.Skip(beforeCount).MinBy(a => a.CreatedAt);
                customer.CreatedAt = firstBooking?.CreatedAt.AddDays(-_faker.Random.Int(0, 3)) ?? now.AddDays(-1);
                customer.GdprConsentAt = customer.CreatedAt;
                _customers.Add(customer);
            }

            return (_customers, _appointments, _payments);
        }

        private T Pick<T>(List<T> values) => values[_faker.Random.Int(0, values.Count - 1)];

        private static int Interval(ServiceCategory category) => category switch
        {
            ServiceCategory.Nails => 21,
            ServiceCategory.Skin => 35,
            ServiceCategory.Massage => 42,
            _ => 35,
        };

        private void AddVisit(Customer customer, Service favorite, User favoriteWorker, DateOnly date,
            bool future, bool leaving, DateOnly? lastDate = null)
        {
            var service = _faker.Random.Bool(0.7f) ? favorite : Pick(services);
            var candidates = eligible[service.Id];
            var alternatives = candidates.Where(w => w.Id != favoriteWorker.Id).ToList();
            var employee = candidates.Any(w => w.Id == favoriteWorker.Id)
                && (alternatives.Count == 0 || _faker.Random.Bool(0.8f))
                ? favoriteWorker
                : Pick(alternatives.Count > 0 ? alternatives : candidates);
            var statusRoll = _faker.Random.Double();
            var status = future ? AppointmentStatus.Scheduled
                : statusRoll < (leaving ? 0.80 : 0.90) ? AppointmentStatus.Completed
                : statusRoll < (leaving ? 0.94 : 0.96) ? AppointmentStatus.Cancelled : AppointmentStatus.NoShow;
            var slot = FindSlot(date, service, employee, future, lastDate);
            if (slot is null)
            {
                return;
            }

            var startsAt = slot.Value;
            var createdAt = startsAt.AddDays(-_faker.Random.Int(1, 21));
            if (createdAt > now)
            {
                createdAt = now;
            }

            var endsAt = startsAt.AddMinutes(service.DurationMinutes);
            var cancelledAt = status == AppointmentStatus.Cancelled
                ? createdAt.AddTicks((long)((startsAt - createdAt).Ticks * _faker.Random.Double(0.05, 0.95)))
                : (DateTime?)null;
            var appointment = new Appointment
            {
                CustomerId = customer.Id,
                EmployeeId = employee.Id,
                ServiceId = service.Id,
                StartsAt = startsAt,
                EndsAt = endsAt,
                Status = status,
                Source = _faker.Random.Bool(0.7f) ? AppointmentSource.Online : AppointmentSource.Staff,
                Price = service.Price,
                CreatedAt = createdAt,
                CancelledAt = cancelledAt,
                CompletedAt = status == AppointmentStatus.Completed ? endsAt : null,
                UpdatedAt = cancelledAt ?? (future ? createdAt : endsAt),
            };
            _appointments.Add(appointment);
            var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(startsAt, salon.Zone));
            var key = (employee.Id, localDate);
            if (!_busy.TryGetValue(key, out var intervals))
            {
                intervals = [];
                _busy[key] = intervals;
            }
            intervals.Add(new BusyInterval(startsAt, endsAt));

            if (status == AppointmentStatus.Completed)
            {
                _payments.Add(new Payment
                {
                    AppointmentId = appointment.Id,
                    Amount = appointment.Price,
                    Method = _faker.Random.Bool(0.6f) ? PaymentMethod.Card : PaymentMethod.Cash,
                    PaidAt = endsAt,
                    RecordedByUserId = employee.Id,
                });
            }
        }

        private DateTime? FindSlot(DateOnly wantedDate, Service service, User employee, bool future, DateOnly? lastDate)
        {
            // Try later grid starts first, then nearby dates; never move across a history/future boundary.
            var preferredMinute = _faker.Random.Int(0, 31) * salon.SlotStepMinutes;
            foreach (var offset in new[] { 0, 1, -1, 2, -2 })
            {
                var date = wantedDate.AddDays(offset);
                if (date < _today.AddMonths(-24) || date > (lastDate ?? (future ? _today.AddDays(21) : _today.AddDays(-1))))
                {
                    continue;
                }
                var starts = SlotCalculator.GridStartsUtc(date, salon, service.DurationMinutes);
                var preferred = Math.Min(preferredMinute / salon.SlotStepMinutes, Math.Max(0, starts.Count - 1));
                foreach (var start in starts.Skip(preferred))
                {
                    if (future && (start < now.AddMinutes(salon.MinLeadMinutes) || start > now.AddDays(21)))
                    {
                        continue;
                    }
                    var end = start.AddMinutes(service.DurationMinutes);
                    if (_busy.TryGetValue((employee.Id, date), out var busy)
                        && busy.Any(b => start < b.EndUtc && b.StartUtc < end))
                    {
                        continue;
                    }
                    return start;
                }
            }
            return null;
        }
    }
}
