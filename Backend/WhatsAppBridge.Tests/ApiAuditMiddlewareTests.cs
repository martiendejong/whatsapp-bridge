using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppBridge.API.Data;
using WhatsAppBridge.API.Middleware;
using Xunit;

namespace WhatsAppBridge.Tests;

/// <summary>
/// Coverage for the API audit trail: every request under /api gets a row, the two filter
/// dimensions (phone, event type) are populated correctly, and credential-bearing routes
/// never have their bodies stored.
///
/// Same InMemory-per-test convention as OutboundGuardrailServiceTests.
/// </summary>
public class ApiAuditMiddlewareTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>
    /// Runs the middleware over a synthetic request and returns the audit rows it wrote.
    /// </summary>
    private static async Task<List<Models_ApiAuditLog>> RunAsync(
        AppDbContext db,
        string method,
        string path,
        string? jsonBody = null,
        int statusCode = 200,
        string? responseBody = null,
        Claim[]? claims = null,
        string? queryString = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Method = method;
        context.Request.Path = path;
        if (queryString != null) context.Request.QueryString = new QueryString(queryString);

        if (jsonBody != null)
        {
            var bytes = Encoding.UTF8.GetBytes(jsonBody);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
            context.Request.ContentType = "application/json";
        }

        if (claims != null)
            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        context.Response.Body = new MemoryStream();

        var middleware = new ApiAuditMiddleware(async ctx =>
        {
            ctx.Response.StatusCode = statusCode;
            if (responseBody != null)
                await ctx.Response.WriteAsync(responseBody);
        }, NullLogger<ApiAuditMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        return await db.ApiAuditLogs
            .Select(a => new Models_ApiAuditLog
            {
                EventType = a.EventType,
                Phone = a.Phone,
                Body = a.Body,
                Outcome = a.Outcome,
                StatusCode = a.StatusCode,
                Path = a.Path,
                Method = a.Method,
                AuthScheme = a.AuthScheme,
                ApiConnectionId = a.ApiConnectionId,
                ApiConnectionName = a.ApiConnectionName,
                ResponsePreview = a.ResponsePreview,
            })
            .ToListAsync();
    }

    /// <summary>Local projection so tests don't depend on the entity's full shape.</summary>
    private sealed class Models_ApiAuditLog
    {
        public string EventType { get; set; } = "";
        public string? Phone { get; set; }
        public string? Body { get; set; }
        public string Outcome { get; set; } = "";
        public int StatusCode { get; set; }
        public string Path { get; set; } = "";
        public string Method { get; set; } = "";
        public string AuthScheme { get; set; } = "";
        public int? ApiConnectionId { get; set; }
        public string? ApiConnectionName { get; set; }
        public string? ResponsePreview { get; set; }
    }

    // ─── Every /api request is recorded ──────────────────────────────────────────────────────

    [Fact]
    public async Task Records_a_row_for_an_api_request()
    {
        using var db = NewContext();
        var rows = await RunAsync(db, "GET", "/api/wa/getChats");

        var row = Assert.Single(rows);
        Assert.Equal("getChats", row.EventType);
        Assert.Equal("GET", row.Method);
        Assert.Equal("/api/wa/getChats", row.Path);
        Assert.Equal("ok", row.Outcome);
    }

    [Fact]
    public async Task Ignores_non_api_traffic()
    {
        using var db = NewContext();
        var rows = await RunAsync(db, "GET", "/index.html");

        Assert.Empty(rows);
    }

    /// <summary>
    /// The gap this table exists to close: WhatsAppController's session send endpoints never
    /// call the outbound guardrail, so they must still show up in the audit trail.
    /// </summary>
    [Fact]
    public async Task Records_endpoints_that_bypass_the_outbound_guardrail()
    {
        using var db = NewContext();
        var rows = await RunAsync(db, "POST",
            "/api/whatsapp/sessions/94e81b67-04e5-4bb3-b5cf-7a70f528ab1a/send",
            jsonBody: """{"to":"31633984381","message":"hallo"}""");

        var row = Assert.Single(rows);
        Assert.Equal("send", row.EventType);
        Assert.Equal("31633984381", row.Phone);
        Assert.Equal("hallo", row.Body);
    }

    // ─── Filter dimension: phone ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("31633984381", "31633984381")]
    [InlineData("31633984381@c.us", "31633984381")]
    [InlineData("254715438010:78@s.whatsapp.net", "254715438010")]
    public async Task Normalizes_recipient_to_a_bare_number(string to, string expected)
    {
        using var db = NewContext();
        var rows = await RunAsync(db, "POST", "/api/wa/sendMessage",
            jsonBody: $$"""{"to":"{{to}}","body":"x"}""");

        Assert.Equal(expected, Assert.Single(rows).Phone);
    }

    [Fact]
    public async Task Reads_the_number_from_the_query_string_for_reads()
    {
        using var db = NewContext();
        var rows = await RunAsync(db, "GET", "/api/wa/getMessages",
            queryString: "?chatId=31621484793@s.whatsapp.net&limit=50");

        Assert.Equal("31621484793", Assert.Single(rows).Phone);
    }

    [Fact]
    public async Task Leaves_phone_null_when_the_request_concerns_no_number()
    {
        using var db = NewContext();
        var rows = await RunAsync(db, "GET", "/api/version");

        Assert.Null(Assert.Single(rows).Phone);
    }

    // ─── Filter dimension: event type ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/api/wa/sendMessage", "sendMessage")]
    [InlineData("/api/wa/sendMedia", "sendMedia")]
    [InlineData("/api/whatsapp/sessions/94e81b67-04e5-4bb3-b5cf-7a70f528ab1a/send-media", "send-media")]
    [InlineData("/api/wa/audit/42", "audit")]
    public async Task Derives_a_stable_event_type_skipping_ids(string path, string expected)
    {
        using var db = NewContext();
        var rows = await RunAsync(db, "POST", path);

        Assert.Equal(expected, Assert.Single(rows).EventType);
    }

    // ─── What was sent ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Stores_the_full_outbound_message_text()
    {
        using var db = NewContext();
        var text = "Dit is precies wat er verstuurd is, woord voor woord.";
        var rows = await RunAsync(db, "POST", "/api/wa/sendMessage",
            jsonBody: $$"""{"to":"31633984381","body":"{{text}}"}""");

        Assert.Equal(text, Assert.Single(rows).Body);
    }

    [Fact]
    public async Task Never_stores_bodies_for_credential_routes()
    {
        using var db = NewContext();
        var rows = await RunAsync(db, "POST", "/api/auth/login",
            jsonBody: """{"email":"info@martiendejong.nl","password":"hunter2"}""");

        var row = Assert.Single(rows);
        Assert.Null(row.Body);
        Assert.Equal("login", row.EventType);   // the attempt is still recorded
    }

    // ─── Attribution: which API key ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Attributes_the_call_to_the_api_key_that_made_it()
    {
        using var db = NewContext();
        var rows = await RunAsync(db, "POST", "/api/wa/sendMessage",
            jsonBody: """{"to":"31633984381","body":"x"}""",
            claims: new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "7"),
                new Claim("ApiConnectionId", "3"),
                new Claim("ApiConnectionName", "jengo-agi"),
            });

        var row = Assert.Single(rows);
        Assert.Equal("ApiKey", row.AuthScheme);
        Assert.Equal(3, row.ApiConnectionId);
        Assert.Equal("jengo-agi", row.ApiConnectionName);
    }

    [Fact]
    public async Task Marks_unauthenticated_calls_as_anonymous()
    {
        using var db = NewContext();
        var rows = await RunAsync(db, "GET", "/api/wa/getChats");

        var row = Assert.Single(rows);
        Assert.Equal("Anonymous", row.AuthScheme);
        Assert.Null(row.ApiConnectionId);
    }

    // ─── Outcome ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(200, "ok")]
    [InlineData(201, "ok")]
    [InlineData(403, "blocked")]
    [InlineData(500, "error")]
    [InlineData(401, "error")]
    public async Task Maps_status_code_to_outcome(int status, string expected)
    {
        using var db = NewContext();
        var rows = await RunAsync(db, "POST", "/api/wa/sendMessage", statusCode: status);

        Assert.Equal(expected, Assert.Single(rows).Outcome);
    }

    [Fact]
    public async Task Keeps_the_block_reason_next_to_the_blocked_attempt()
    {
        using var db = NewContext();
        var reason = "Blocked: '254715438010' is not on the outbound allow-list.";
        var rows = await RunAsync(db, "POST", "/api/wa/sendMessage",
            jsonBody: """{"to":"254715438010","body":"nag"}""",
            statusCode: 403,
            responseBody: $$"""{"error":"{{reason}}"}""");

        var row = Assert.Single(rows);
        Assert.Equal("blocked", row.Outcome);
        Assert.Contains("not on the outbound allow-list", row.ResponsePreview);
    }

    /// <summary>The response must still reach the caller after being tapped for the audit row.</summary>
    [Fact]
    public async Task Passes_the_response_body_through_unchanged()
    {
        using var db = NewContext();

        var services = new ServiceCollection();
        services.AddSingleton(db);
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Request.Method = "GET";
        context.Request.Path = "/api/wa/getChats";
        var responseStream = new MemoryStream();
        context.Response.Body = responseStream;

        var middleware = new ApiAuditMiddleware(
            async ctx => await ctx.Response.WriteAsync("""{"chats":[]}"""),
            NullLogger<ApiAuditMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        responseStream.Position = 0;
        Assert.Equal("""{"chats":[]}""", new StreamReader(responseStream).ReadToEnd());
    }

    /// <summary>An audit failure must never take down the request it was recording.</summary>
    [Fact]
    public async Task Request_still_succeeds_when_the_audit_write_fails()
    {
        var services = new ServiceCollection();   // no AppDbContext registered
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Request.Method = "GET";
        context.Request.Path = "/api/wa/getChats";
        context.Response.Body = new MemoryStream();

        var called = false;
        var middleware = new ApiAuditMiddleware(ctx =>
        {
            called = true;
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        }, NullLogger<ApiAuditMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.True(called);
        Assert.Equal(200, context.Response.StatusCode);
    }
}
