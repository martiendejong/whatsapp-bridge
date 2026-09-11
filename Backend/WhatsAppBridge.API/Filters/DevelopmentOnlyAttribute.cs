using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace WhatsAppBridge.API.Filters;

/// <summary>
/// Makes an action reachable only when the host is running in Development. Anywhere else it
/// answers 404, as though the route did not exist.
///
/// This exists because of the <c>test-*</c> family in WhatsAppController. Those actions are
/// marked [AllowAnonymous] and were written as scratch tooling — they send messages, read any
/// stored chat, and wipe pairing state on a live session. Shipped as they were, anyone who could
/// reach the host could send WhatsApp as Martien without presenting a credential of any kind, and
/// the comment "Dev/test only — remove for production" was the only thing standing in the way.
/// A comment is not an access control.
///
/// An IAuthorizationFilter, not an IActionFilter, and the difference is observable. Action
/// filters run AFTER model binding — and after [ApiController]'s automatic 400 — so a malformed
/// POST to a gated route answered with a field-by-field validation problem, cheerfully
/// confirming the route's existence and its parameter names before the "404" ever ran.
/// Authorization filters run before binding touches the request.
///
/// 404 rather than 403 on purpose: a 403 confirms the endpoint is there and worth coming back
/// for. (Endpoint routing still answers 405 for a wrong HTTP method on a gated route — closing
/// that would mean a routing-level rewrite for little gain; a 405 leaks that some route exists,
/// not what it does or how to call it.) In Development nothing changes, so the existing manual
/// workflows keep working.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class DevelopmentOnlyAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var env = context.HttpContext.RequestServices
            .GetService<IWebHostEnvironment>();

        // No environment resolvable means this is not a normally-hosted app. Fail closed.
        if (env?.IsDevelopment() != true)
            context.Result = new NotFoundResult();
    }
}
