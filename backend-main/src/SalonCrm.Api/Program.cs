using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using SalonCrm.Api.Auth;
using SalonCrm.Api.Data;
using SalonCrm.Api.Data.Seed;
using SalonCrm.Api.Domain;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddScoped<BaseSeeder>();
builder.Services.AddScoped<SessionService>();
builder.Services.AddDbContext<AppDbContext>(o => o
    .UseNpgsql(builder.Configuration.GetConnectionString("Main"))
    .UseSnakeCaseNamingConvention());

builder.Services.AddAuthentication(SessionDefaults.Scheme)
    .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionDefaults.Scheme, null);
builder.Services.AddAuthorization(o =>
    o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("login", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = httpContext.RequestServices.GetRequiredService<IConfiguration>()
                .GetValue("RateLimiting:LoginPermitLimit", 5),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
    o.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(
            new ProblemDetailsContext
            {
                HttpContext = context.HttpContext,
                ProblemDetails =
                {
                    // 429 has no built-in ProblemDetails default type, so set it explicitly.
                    Type = "https://tools.ietf.org/html/rfc6585#section-4",
                    Status = StatusCodes.Status429TooManyRequests,
                    Title = "Too Many Requests",
                    Detail = "Príliš veľa pokusov o prihlásenie. Skúste to znova o chvíľu.",
                },
            });
    };
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference().AllowAnonymous();
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Unknown API paths must return JSON 404, never the SPA index.html.
app.MapFallback("/api/{**path}", () => Results.Problem(
    statusCode: StatusCodes.Status404NotFound,
    detail: "The requested API endpoint does not exist.")).AllowAnonymous();
app.MapFallbackToFile("index.html").AllowAnonymous();

await DatabaseInitializer.InitializeAsync(app.Services);

app.Run();

public partial class Program;
