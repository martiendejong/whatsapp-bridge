using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using WhatsAppBridge.API.Filters;
using Xunit;

namespace WhatsAppBridge.Tests;

/// <summary>
/// The gate in front of the fourteen anonymous test-* endpoints. What it protects against is
/// concrete: those actions send messages, read stored chats and wipe pairing state, and before
/// the gate the only thing between them and the internet was a source comment.
/// </summary>
public class DevelopmentOnlyAttributeTests
{
    private sealed class StubEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "test";
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static AuthorizationFilterContext Run(string? environmentName)
    {
        var services = new ServiceCollection();
        if (environmentName != null)
            services.AddSingleton<IWebHostEnvironment>(new StubEnvironment { EnvironmentName = environmentName });

        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var filterContext = new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>());

        new DevelopmentOnlyAttribute().OnAuthorization(filterContext);
        return filterContext;
    }

    [Fact]
    public void In_development_the_route_behaves_as_if_the_filter_were_absent()
    {
        Assert.Null(Run("Development").Result);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Anywhere_else_the_route_does_not_exist(string environment)
    {
        Assert.IsType<NotFoundResult>(Run(environment).Result);
    }

    /// <summary>No resolvable environment means "not a normally hosted app" — fail closed.</summary>
    [Fact]
    public void Without_an_environment_the_gate_fails_closed()
    {
        Assert.IsType<NotFoundResult>(Run(null).Result);
    }
}
