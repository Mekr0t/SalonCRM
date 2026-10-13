namespace SalonCrm.Api.Domain;

public class Service
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public ServiceCategory Category { get; set; }
    public string? Description { get; set; }
    public int DurationMinutes { get; set; }
    public decimal Price { get; set; }
    public bool IsActive { get; set; } = true;

    public List<UserService> UserServices { get; set; } = [];
}
