using System.Text.RegularExpressions;
using Switchboard.Core;
using Switchboard.Core.Data;
using Switchboard.Hub.Auth;
using Switchboard.Hub.Channels;
using Switchboard.Hub.Events;

namespace Switchboard.Hub;

/// <param name="RequestId">Claude Code's 5-letter id for the prompt.</param>
/// <param name="InputPreview">The tool's arguments as JSON text; for Bash, the command.</param>
public sealed record PermissionRequestReport(string RequestId, string ToolName, string Description, string InputPreview);

/// <param name="Behavior">"allow", "deny", or "dismiss" (hide it without answering).</param>
public sealed record PermissionDecisionRequest(string Behavior);

/// <summary>
/// Permission relay: a session's tool prompt (Bash, Write, ...) goes to the owner's phone,
/// and the owner's Allow/Deny goes back into the session.
///
/// Only the owner decides. The manager could easily approve its own workers' commands, but
/// then one prompt-injected message could turn into shell access. Approval stays with a person.
/// </summary>
public sealed partial class PermissionService(
    PermissionStore store,
    ChannelHub channels,
    HubEvents events,
    ILogger<PermissionService> log)
{
    // Claude Code caps input_preview at 3,500 characters; this leaves headroom and keeps a
    // misbehaving client from storing megabytes.
    private const int MaxFieldLength = 8_000;

    [GeneratedRegex("^[a-km-z]{5}$")]
    private static partial Regex RequestIdPattern();

    /// <summary>The session's channel server reports a prompt that Claude Code relayed to it.</summary>
    public async Task<PermissionRequest> ReportAsync(Caller caller, PermissionRequestReport report, CancellationToken ct)
    {
        var sessionId = caller.SessionId ?? throw HubException.Forbidden("Only sessions report permission prompts.");
        ValidateRequestId(report.RequestId);

        var request = await store.AddAsync(
            sessionId,
            report.RequestId,
            Clip(report.ToolName),
            Clip(report.Description),
            Clip(report.InputPreview),
            ct);
        log.LogInformation("{Session} asks to use {Tool} ({RequestId})", sessionId, request.ToolName, request.RequestId);
        events.Publish("permission", request);
        return request;
    }

    public async Task<IReadOnlyList<PermissionRequest>> PendingAsync(Caller caller, CancellationToken ct)
    {
        RequireOwner(caller);
        return await store.PendingAsync(ct);
    }

    public async Task<PermissionRequest> DecideAsync(
        Caller caller, string sessionId, string requestId, PermissionDecisionRequest req, CancellationToken ct)
    {
        RequireOwner(caller);
        SessionId.Validate(sessionId);
        ValidateRequestId(requestId);
        var decision = req.Behavior switch
        {
            "allow" => "allow",
            "deny" => "deny",
            "dismiss" => "dismissed",
            _ => throw HubException.BadRequest("Behavior must be allow, deny or dismiss."),
        };

        // Claim the decision first. If two devices answer at once, only one UPDATE matches.
        var request = await store.TryDecideAsync(sessionId, requestId, decision, ct)
            ?? throw HubException.Conflict("This prompt was already answered (or doesn't exist).");

        if (decision is "allow" or "deny"
            && !await channels.SendPermissionAsync(sessionId, requestId, decision, ct))
        {
            // Couldn't deliver the answer: put the request back so it can be answered later.
            await store.UndoDecisionAsync(request.Id, ct);
            throw HubException.Conflict($"'{sessionId}' has no channel connection right now. Answer in its terminal, or try again.");
        }

        log.LogInformation("Owner answered {Session}/{RequestId}: {Decision}", sessionId, requestId, decision);
        events.Publish("permission-decided", new { sessionId, requestId, decision });
        return request with { Decision = decision, DecidedAt = DateTimeOffset.UtcNow };
    }

    /// <summary>Called when a session stops or restarts: its old prompts can't be answered.</summary>
    public async Task ExpireForSessionAsync(string sessionId, CancellationToken ct)
    {
        await store.ExpireForSessionAsync(sessionId, ct);
        events.Publish("permission-decided", new { sessionId, requestId = (string?)null, decision = "expired" });
    }

    private static void RequireOwner(Caller caller)
    {
        if (!caller.IsOwner)
        {
            throw HubException.Forbidden("Only the owner can see and answer permission prompts. Not even the manager.");
        }
    }

    private static void ValidateRequestId(string requestId)
    {
        if (!RequestIdPattern().IsMatch(requestId))
        {
            throw HubException.BadRequest("Invalid request id: expected five lowercase letters (no 'l').");
        }
    }

    private static string Clip(string? text) =>
        text is null ? "" : text.Length <= MaxFieldLength ? text : text[..MaxFieldLength] + " …";
}
