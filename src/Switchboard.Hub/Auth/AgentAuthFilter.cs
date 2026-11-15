using Switchboard.Core.Data;

namespace Switchboard.Hub.Auth;

/// <summary>
/// Requires "Authorization: Bearer &lt;token&gt;" on every /api request and works out the
/// <see cref="Caller"/>: the admin token is the owner, a session token is that session.
/// </summary>
public sealed class AgentAuthFilter(HubOptions options, SessionStore sessions) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var header = http.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return Unauthorized();
        }

        var token = header["Bearer ".Length..].Trim();
        Caller? caller = null;
        if (Tokens.FixedTimeEquals(token, options.AdminToken))
        {
            caller = Caller.Owner;
        }
        else if (await sessions.FindByTokenHashAsync(Tokens.Hash(token), http.RequestAborted) is { } session)
        {
            // Looking up by hash needs no constant-time compare: the database finds the row by
            // index, and the attacker would have to guess a full 256-bit token to match one.
            caller = new Caller(session.Id, session.Role);
        }

        if (caller is null)
        {
            return Unauthorized();
        }

        http.Items[typeof(Caller)] = caller;
        return await next(context);
    }

    private static IResult Unauthorized() =>
        Results.Json(new { error = "Missing or invalid token." }, statusCode: StatusCodes.Status401Unauthorized);
}
