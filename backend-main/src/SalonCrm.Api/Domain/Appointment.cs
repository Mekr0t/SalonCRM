namespace SalonCrm.Api.Domain;

public class Appointment
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid CustomerId { get; set; }
    public Guid EmployeeId { get; set; }
    public Guid ServiceId { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public AppointmentStatus Status { get; set; } = AppointmentStatus.Scheduled;
    public decimal Price { get; set; }
    public AppointmentSource Source { get; set; }
    public string? CustomerNote { get; set; }
    public string? StaffNote { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? CancelledAt { get; set; }

    public Customer Customer { get; set; } = null!;
    public User Employee { get; set; } = null!;
    public Service Service { get; set; } = null!;
    public Payment? Payment { get; set; }
}
