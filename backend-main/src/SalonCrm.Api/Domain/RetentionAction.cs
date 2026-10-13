namespace SalonCrm.Api.Domain;

public class RetentionAction
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid CustomerId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public RetentionChannel Channel { get; set; }
    public required string Note { get; set; }
    public decimal? ChurnProbability { get; set; }
    public DateTime CreatedAt { get; set; }

    public Customer Customer { get; set; } = null!;
    public User CreatedByUser { get; set; } = null!;
}
