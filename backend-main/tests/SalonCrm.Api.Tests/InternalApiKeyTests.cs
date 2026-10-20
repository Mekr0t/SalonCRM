using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SalonCrm.Api.Features.Internal;

namespace SalonCrm.Api.Tests;

public sealed class InternalApiKeyTests
{
    [Theory]
    [InlineData(null, null, false)]
    [InlineData("", "", false)]
    [InlineData("   ", "   ", false)]
    [InlineData("configured-key", null, false)]
    [InlineData("configured-key", "wrong", false)]
    [InlineData("configured-key", "configured-key", true)]
    [InlineData("configured-key", "configured-key,wrong", false)]
    [InlineData("configured-key", "configured-key ", false)]
    public void Filter_requires_exact_nonempty_key_and_fails_closed(string? configured, string? supplied, bool allowed)
    {
        using var services = new ServiceCollection().AddSingleton<IConfiguration>(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Internal:ApiKey"] = configured,
            }).Build()).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Headers.Cookie = "salon_sid=cookie-does-not-replace-key";
        if (supplied is not null)
        {
            http.Request.Headers["X-Internal-Api-Key"] = supplied;
        }
        var context = new AuthorizationFilterContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), []);

        new InternalApiKeyAttribute().OnAuthorization(context);

        if (allowed)
        {
            Assert.Null(context.Result);
        }
        else
        {
            var result = Assert.IsType<ObjectResult>(context.Result);
            Assert.Equal(StatusCodes.Status401Unauthorized, result.StatusCode);
            Assert.Equal(401, Assert.IsType<ProblemDetails>(result.Value).Status);
            Assert.Contains("application/problem+json", result.ContentTypes);
        }
    }

    [Fact]
    public void Duplicate_header_values_do_not_authorize()
    {
        using var services = new ServiceCollection().AddSingleton<IConfiguration>(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Internal:ApiKey"] = "key",
            }).Build()).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Headers["X-Internal-Api-Key"] = new[] { "key", "key" };
        var context = new AuthorizationFilterContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), []);
        new InternalApiKeyAttribute().OnAuthorization(context);
        Assert.Equal(401, Assert.IsType<ObjectResult>(context.Result).StatusCode);
    }
}
