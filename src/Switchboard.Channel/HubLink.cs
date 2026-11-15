using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Switchboard.Channel;

/// <summary>
/// The channel server's two links to the hub:
///  - a WebSocket the hub pushes messages down (kept open, reconnects on its own);
///  - plain HTTP POSTs for replies, so the reply tool gets a direct yes/no answer.
/// </summary>
public sealed class HubLink(Uri hubUrl, string token, string sessionId, HttpClient http)
{
    // The SDK locks the options it is given, and locked options must name a type resolver.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    /// <summary>Connects, pushes every message into Claude Code, and reconnects when dropped.</summary>
    public async Task RunAsync(McpServer server, CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndReceiveAsync(server, () => failures = 0, ct);
                Log("hub closed the connection");
            }
            catch (WebSocketException e) when (e.Message.Contains("401", StringComparison.Ordinal))
            {
                // The token was revoked: this session was stopped or restarted with a new token.
                Log("hub rejected our token (session stopped or restarted); retrying in 60s");
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
                continue;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Network trouble, a hub restart, or a bad frame: drop the socket and reconnect.
                // Anything unacked is resent by the hub, so nothing is lost.
                Log($"connection problem: {e.Message}");
            }

            await Task.Delay(Backoff[Math.Min(failures++, Backoff.Length - 1)], ct);
        }
    }

    private async Task ConnectAndReceiveAsync(McpServer server, Action onConnected, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", $"Bearer {token}");
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        await ws.ConnectAsync(WebSocketUri(), ct);
        onConnected();
        Log("connected to hub");

        var buffer = new byte[16 * 1024];
        var frame = new MemoryStream();
        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }

            frame.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
            {
                continue;
            }

            var hubFrame = JsonSerializer.Deserialize<HubFrame>(frame.ToArray(), Json);
            frame.SetLength(0);
            switch (hubFrame)
            {
                case { Type: "message" }:
                    await PushAsync(server, hubFrame, ct);
                    // Ack only after the push is written to Claude Code. If we die before this
                    // line, the hub sends the message again when we reconnect.
                    var ack = JsonSerializer.SerializeToUtf8Bytes(new { type = "ack", id = hubFrame.Id }, Json);
                    await ws.SendAsync(ack, WebSocketMessageType.Text, endOfMessage: true, ct);
                    break;

                case { Type: "permission", RequestId: { } requestId, Behavior: "allow" or "deny" }:
                    // The owner's answer to a tool prompt. Claude Code applies it to that one
                    // tool call, closes the terminal dialog, and ignores later answers to it.
                    await server.SendNotificationAsync(
                        "notifications/claude/channel/permission",
                        new { request_id = requestId, behavior = hubFrame.Behavior },
                        Json,
                        ct);
                    Log($"sent verdict {hubFrame.Behavior} for {requestId}");
                    break;
            }
        }
    }

    /// <summary>
    /// Claude Code is showing a tool prompt and relayed it to us: pass it to the hub, which shows
    /// it on the owner's phone. If this fails, the prompt still waits in the terminal.
    /// </summary>
    public async Task ReportPermissionRequestAsync(JsonNode? parameters)
    {
        try
        {
            var report = new
            {
                requestId = (string?)parameters?["request_id"],
                toolName = (string?)parameters?["tool_name"],
                description = (string?)parameters?["description"],
                inputPreview = (string?)parameters?["input_preview"],
            };
            using var response = await http.PostAsJsonAsync(new Uri(hubUrl, "api/channel/permission-request"), report, Json);
            Log(response.IsSuccessStatusCode
                ? $"relayed permission prompt {report.requestId} ({report.toolName})"
                : $"hub refused permission prompt {report.requestId}: HTTP {(int)response.StatusCode}");
        }
        catch (Exception e)
        {
            Log($"couldn't relay permission prompt: {e.Message}");
        }
    }

    // A reminder right next to every message. The server instructions say the same, but in
    // tests Sonnet and Haiku answered short messages like "hello" in the terminal anyway, where
    // nobody sees them. Repeating it with each message fixed that. Only the session sees this
    // line; Postgres and the UI keep the original text.
    private static string WithReplyHint(HubFrame message) =>
        $"{message.Text}\n\n(Answer with the reply tool, to=\"{message.From ?? "owner"}\". The sender can't see your terminal.)";

    private static Task PushAsync(McpServer server, HubFrame message, CancellationToken ct) =>
        // Claude Code shows this as <channel source="switchboard-channel" from="…" message_id="…">.
        // Meta keys must be letters, digits and underscores, or Claude Code drops them silently.
        server.SendNotificationAsync(
            "notifications/claude/channel",
            new
            {
                content = WithReplyHint(message),
                meta = new Dictionary<string, string>
                {
                    ["from"] = message.From ?? "owner",
                    ["message_id"] = message.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
            },
            Json,
            ct);

    public async Task<string> ReplyAsync(string to, string text, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(new Uri(hubUrl, "api/channel/reply"), new { to, text }, Json, ct);
        }
        catch (HttpRequestException e)
        {
            throw new McpException($"Can't reach the Switchboard hub: {e.Message}. The reply was not sent.");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var result = JsonDocument.Parse(body).RootElement;
                var delivered = result.GetProperty("delivered").GetBoolean();
                return to == "owner" || delivered
                    ? $"Sent to {to}."
                    : $"Saved for {to}; it will get it as soon as its channel is connected.";
            }

            throw new McpException($"Reply not sent: {ErrorText(body) ?? $"HTTP {(int)response.StatusCode}"}");
        }
    }

    private Uri WebSocketUri() => new UriBuilder(new Uri(hubUrl, "api/channel"))
    {
        Scheme = hubUrl.Scheme == "https" ? "wss" : "ws",
    }.Uri;

    private static string? ErrorText(string body)
    {
        try
        {
            return JsonDocument.Parse(body).RootElement.GetProperty("error").GetString();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private void Log(string text) => Console.Error.WriteLine($"switchboard-channel[{sessionId}]: {text}");

    // Frames from the hub: {type:"message", id, from, text} or {type:"permission", requestId, behavior}.
    private sealed record HubFrame(string Type, long Id, string? From, string? Text, string? RequestId, string? Behavior);
}
