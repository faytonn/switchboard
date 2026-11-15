using Switchboard.Core.Data;

namespace Switchboard.Hub.Auth;

/// <summary>
/// Who made a request. The owner (admin token, later the web login) has <see cref="SessionId"/>
/// null, which matches how messages store the owner. A session is identified by its token,
/// never by anything it claims in the request, so from_session can be trusted.
/// </summary>
public sealed record Caller(string? SessionId, string Role)
{
    public const string OwnerRole = "owner";

    public static readonly Caller Owner = new(null, OwnerRole);

    public bool IsOwner => Role == OwnerRole;
    public bool IsManager => Role == SessionRoles.Manager;
    public bool CanControlSessions => IsOwner || IsManager;

    public string Describe() => SessionId ?? "owner";

    public static Caller From(HttpContext ctx) =>
        ctx.Items[typeof(Caller)] as Caller ?? throw new InvalidOperationException("Endpoint is missing AgentAuthFilter.");
}
