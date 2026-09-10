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
/// 404 rather than 403 on purpose: a 403 confirms the endpoint is there and worth coming back
/// for. In Development nothing changes, so the existing manual workflows keep working.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class DevelopmentOnlyAttribute : Attribute, IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        var env = context.HttpContext.RequestServices
            .GetService<IWebHostEnvironment>();

        // No environment resolvable means this is not a normally-hosted app. Fail closed.
        if (env?.IsDevelopment() != true)
            context.Result = new NotFoundResult();
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}
