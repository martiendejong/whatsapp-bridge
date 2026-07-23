using System.Reflection;
using Microsoft.AspNetCore.Mvc;

namespace WhatsAppBridge.API.Controllers;

/// <summary>
/// Public, unauthenticated endpoint that serves AI-INTEGRATION.md — the canonical URL for an
/// AI agent to discover how to use this bridge's API without needing repo access.
/// </summary>
[ApiController]
[Route("api")]
public class AiDocsController : ControllerBase
{
    private const string ResourceName = "AI-INTEGRATION.md";

    [HttpGet("ai-docs")]
    public IActionResult GetAiDocs()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream == null)
            return StatusCode(500, new { error = "AI integration guide is not available." });

        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        return Content(content, "text/markdown; charset=utf-8");
    }
}
