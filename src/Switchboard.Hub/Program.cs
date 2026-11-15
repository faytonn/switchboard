using Npgsql;
using Switchboard.Core.Data;
using Switchboard.Core.Tmux;
using Switchboard.Hub;
using Switchboard.Hub.Auth;
using Switchboard.Hub.Channels;

var options = HubOptions.FromEnvironment();
var connectionString = Environment.GetEnvironmentVariable("SWITCHBOARD_DB")
    ?? throw new InvalidOperationException("SWITCHBOARD_DB is not set. Start the hub with scripts/run-hub.sh.");

var builder = WebApplication.CreateBuilder(args);
// Loopback only for now. Phase 5 decides how the phone reaches the hub.
builder.WebHost.UseUrls(options.Url);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<MessageStore>();
builder.Services.AddSingleton(TmuxClient.FromEnvironment());
builder.Services.AddSingleton<ChannelHub>();
builder.Services.AddSingleton<SessionService>();
builder.Services.AddSingleton<AgentAuthFilter>();

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

// Pings every 30s keep idle channel connections alive and notice dead ones.
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

// Turn known errors into {"error": "..."} with a fitting status, so API clients (and the
// manager's tools) get a readable message instead of an HTML error page.
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

app.MapGet("/health", () => Results.Ok(new { ok = true }));

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

api.MapGet("/sessions/{id}/screen", (string id, int? scrollback, HttpContext ctx, SessionService svc) =>
    svc.ReadScreenAsync(Caller.From(ctx), id, scrollback ?? 0, ctx.RequestAborted));

api.MapPost("/sessions/{id}/stop", async (string id, StopSessionRequest? req, HttpContext ctx, SessionService svc) =>
{
    await svc.StopAsync(Caller.From(ctx), id, req ?? new StopSessionRequest(), ctx.RequestAborted);
    return Results.NoContent();
});

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
