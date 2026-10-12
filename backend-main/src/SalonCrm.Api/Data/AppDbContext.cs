using Microsoft.EntityFrameworkCore;

namespace SalonCrm.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options);
