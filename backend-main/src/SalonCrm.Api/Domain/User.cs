namespace SalonCrm.Api.Domain;

public class User
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Email { get; set; }
    public required string PasswordHash { get; set; }
    public required string FullName { get; set; }
    public UserRole Role { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }

    public List<Session> Sessions { get; set; } = [];
    public List<UserService> UserServices { get; set; } = [];
    public List<Appointment> Appointments { get; set; } = [];
}
