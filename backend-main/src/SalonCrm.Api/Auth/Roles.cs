namespace SalonCrm.Api.Auth;

public static class Roles
{
    public const string Admin = "Admin";
    public const string Staff = "Admin,User"; // any signed-in employee
}
