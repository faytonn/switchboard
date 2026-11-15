using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Npgsql;
using Switchboard.Core.Data;
using Switchboard.Core.Tmux;
using Switchboard.Hub;
using Switchboard.Hub.Auth;
using Switchboard.Hub.Channels;
using Switchboard.Hub.Events;

var options = HubOptions.FromEnvironment();
var connectionString = Environment.GetEnvironmentVariable("SWITCHBOARD_DB")
    ?? throw new InvalidOperationException("SWITCHBOARD_DB is not set. Start the hub with scripts/run-hub.sh.");

var builder = WebApplication.CreateBuilder(args);
// Loopback only. Your phone reaches it through `tailscale serve` on Windows, which forwards
// to localhost; nothing on your Wi-Fi can connect to this port directly.
builder.WebHost.UseUrls(options.Url);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<MessageStore>();
builder.Services.AddSingleton(TmuxClient.FromEnvironment());
builder.Services.AddSingleton<HubEvents>();
builder.Services.AddSingleton<ChannelHub>();
builder.Services.AddSingleton<SessionService>();
builder.Services.AddSingleton<AgentAuthFilter>();
builder.Services.AddOwnerLogin(options);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(OwnerLogin.RateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

// Bring the database schema up to date before taking requests.
var applied = await Migrator.MigrateAsync(app.Services.GetRequiredService<NpgsqlDataSource>());
if (applied.Count > 0)
{
    app.Logger.LogInformation("Applied migrations: {Versions}", string.Join(", ", applied));
}

foreach (var dll in new[] { options.ManagerDll, options.ChannelDll })
{
    if (!File.Exists(dll))
    {
        app.Logger.LogWarning("MCP server not built yet ({Path}). Sessions can't start until it is.", dll);
    }
}

if (options.OwnerPasswordHash is null)
{
    app.Logger.LogWarning("No web UI password set. Run ./sb passwd, then restart the hub.");
}

// Behind `tailscale serve`, requests arrive from 127.0.0.1 with X-Forwarded-For (the phone's
// address) and X-Forwarded-Proto (https). This makes the hub use those, but only when the
// request comes from loopback, so nobody else can fake them.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
});

// Security headers for every response.
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    // Only our own scripts and styles run, nothing inline. Even if a session's message
    // contained HTML, the browser would refuse to run scripts from it.
    h.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; " +
                              "connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    h.XContentTypeOptions = "nosniff";
    h["Referrer-Policy"] = "no-referrer";
    if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/auth"))
    {
        h.CacheControl = "no-store";
    }

    await next(ctx);
});

// Turn known errors into {"error": "..."} with a fitting status, so API clients (the web UI,
// sb and the manager's tools) get a readable message instead of an HTML error page.
app.Use(async (ctx, next) =>
{
    try
    {
        await next(ctx);
    }
    catch (Exception e) when (e is HubException or TmuxException or ArgumentException)
    {
        ctx.Response.StatusCode = e is HubException h ? h.Status : StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new { error = e.Message });
    }
});

// The web UI: static files from wwwroot. They hold no data; everything comes from /api,
// which needs the login cookie. "no-cache" makes the browser check for a newer version
// each time instead of running stale JavaScript after an update.
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = f => f.Context.Response.Headers.CacheControl = "no-cache",
});

// Pings every 30s keep idle channel connections alive and notice dead ones.
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseAuthentication();
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { ok = true }));
app.MapOwnerLogin(options);

var api = app.MapGroup("/api").AddEndpointFilter<AgentAuthFilter>();

api.MapGet("/whoami", (HttpContext ctx) => Caller.From(ctx));

api.MapGet("/sessions", (HttpContext ctx, SessionService svc) =>
    svc.ListAsync(Caller.From(ctx), ctx.RequestAborted));

api.MapPost("/sessions", async (StartSessionRequest req, HttpContext ctx, SessionService svc) =>
{
    var result = await svc.StartAsync(Caller.From(ctx), req, ctx.RequestAborted);
    return Results.Created($"/api/sessions/{result.Session.Id}", result);
});

api.MapPost("/sessions/{id}/messages", (string id, SendMessageRequest req, HttpContext ctx, SessionService svc) =>
    svc.SendAsync(Caller.From(ctx), id, req, ctx.RequestAborted));

api.MapGet("/sessions/{id}/messages", (string id, int? limit, HttpContext ctx, SessionService svc) =>
    svc.HistoryAsync(Caller.From(ctx), id, limit ?? 20, ctx.RequestAborted));

api.MapGet("/messages/search", (string q, string? session, int? limit, HttpContext ctx, SessionService svc) =>
    svc.SearchAsync(Caller.From(ctx), q, session, limit ?? 10, ctx.RequestAborted));

api.MapGet("/sessions/{id}/screen", (string id, int? scrollback, HttpContext ctx, SessionService svc) =>
    svc.ReadScreenAsync(Caller.From(ctx), id, scrollback ?? 0, ctx.RequestAborted));

api.MapPost("/sessions/{id}/stop", async (string id, StopSessionRequest? req, HttpContext ctx, SessionService svc) =>
{
    await svc.StopAsync(Caller.From(ctx), id, req ?? new StopSessionRequest(), ctx.RequestAborted);
    return Results.NoContent();
});

// Live updates for the web UI (owner only): message, delivered, sessions, ping.
api.MapGet("/events", (HttpContext ctx, HubEvents events) =>
    Caller.From(ctx).IsOwner
        ? TypedResults.ServerSentEvents(events.StreamAsync(ctx.RequestAborted))
        : Results.Json(new { error = "Only the owner receives live events." }, statusCode: StatusCodes.Status403Forbidden));

// Channel servers connect here (WebSocket) to receive messages for their session.
// The session is whoever owns the token; one session can't listen in on another's messages.
api.MapGet("/channel", async (HttpContext ctx, ChannelHub hub) =>
{
    var caller = Caller.From(ctx);
    if (caller.SessionId is null)
    {
        return Results.Json(new { error = "Only sessions connect a channel." }, statusCode: StatusCodes.Status403Forbidden);
    }

    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        return Results.Json(new { error = "Expected a WebSocket upgrade." }, statusCode: StatusCodes.Status400BadRequest);
    }

    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    await hub.RunAsync(caller.SessionId, socket, ctx.RequestAborted);
    return Results.Empty;
});

// The channel server's reply tool posts here.
api.MapPost("/channel/reply", (ReplyRequest req, HttpContext ctx, SessionService svc) =>
    svc.ReplyAsync(Caller.From(ctx), req, ctx.RequestAborted));

app.Run();
