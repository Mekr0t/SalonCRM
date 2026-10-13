using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using SalonCrm.Api.Data;
using SalonCrm.Api.Data.Seed;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<BaseSeeder>();
builder.Services.AddDbContext<AppDbContext>(o => o
    .UseNpgsql(builder.Configuration.GetConnectionString("Main"))
    .UseSnakeCaseNamingConvention());

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();

// Unknown API paths must return JSON 404, never the SPA index.html.
app.MapFallback("/api/{**path}", () => Results.Problem(
    statusCode: StatusCodes.Status404NotFound,
    detail: "The requested API endpoint does not exist."));
app.MapFallbackToFile("index.html");

await DatabaseInitializer.InitializeAsync(app.Services);

app.Run();

public partial class Program;
