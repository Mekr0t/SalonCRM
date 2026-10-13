namespace SalonCrm.Api.Domain;

public class Customer
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Email { get; set; }
    public required string EmailNormalized { get; set; }
    public required string FullName { get; set; }
    public string? Phone { get; set; }
    public string? Notes { get; set; }
    public DateTime GdprConsentAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public List<Appointment> Appointments { get; set; } = [];
    public List<RetentionAction> RetentionActions { get; set; } = [];
}
