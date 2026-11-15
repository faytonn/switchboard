using Switchboard.Core.Data;

namespace Switchboard.Hub.Auth;

/// <summary>
/// Works out the <see cref="Caller"/> of every /api request:
///  - "Authorization: Bearer &lt;token&gt;": the admin token is the owner; a session token is that session.
///  - otherwise the web UI's login cookie, which is the owner.
/// </summary>
public sealed class AgentAuthFilter(HubOptions options, SessionStore sessions) : IEndpointFilter
{
    // The web UI sends this header on every API call. A page on another site can't add a
    // custom header to a request to us without a CORS preflight, which we never allow, so
    // requiring it on cookie-authenticated writes blocks CSRF even where SameSite falls short.
    public const string CsrfHeader = "X-Switchboard";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var caller = await FromBearerAsync(http) ?? FromCookie(http);
        if (caller is null)
        {
            return Results.Json(new { error = "Missing or invalid token." }, statusCode: StatusCodes.Status401Unauthorized);
        }

        http.Items[typeof(Caller)] = caller;
        return await next(context);
    }

    private async Task<Caller?> FromBearerAsync(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return null;
        }

        var token = header["Bearer ".Length..].Trim();
        if (Tokens.FixedTimeEquals(token, options.AdminToken))
        {
            return Caller.Owner;
        }

        // Looking up by hash needs no constant-time compare: the database finds the row by
        // index, and an attacker would have to guess a full 256-bit token to match one.
        return await sessions.FindByTokenHashAsync(Tokens.Hash(token), http.RequestAborted) is { } session
            ? new Caller(session.Id, session.Role)
            : null;
    }

    private static Caller? FromCookie(HttpContext http)
    {
        if (http.User.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var safe = HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method);
        return safe || http.Request.Headers.ContainsKey(CsrfHeader) ? Caller.Owner : null;
    }
}
