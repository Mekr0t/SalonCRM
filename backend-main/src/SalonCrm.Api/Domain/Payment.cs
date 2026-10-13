namespace SalonCrm.Api.Domain;

public class Payment
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid AppointmentId { get; set; }
    public decimal Amount { get; set; }
    public PaymentMethod Method { get; set; }
    public DateTime PaidAt { get; set; }
    public Guid RecordedByUserId { get; set; }

    public Appointment Appointment { get; set; } = null!;
    public User RecordedByUser { get; set; } = null!;
}
