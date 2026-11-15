using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;

namespace Switchboard.Manager;

/// <summary>
/// Calls the hub's HTTP API with this session's token, and turns the answers into short text
/// for the model. Hub errors become McpException, which the SDK reports to Claude as a
/// failed tool call with that message, so Claude can see what went wrong and try again.
/// </summary>
public sealed class HubClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<string> StartAsync(string id, string projectDir, string? permissionMode, CancellationToken ct)
    {
        var result = await SendAsync(HttpMethod.Post, "api/sessions", new { id, projectDir, permissionMode }, ct);
        var dialogs = result.GetProperty("dialogs").EnumerateArray().Select(d => d.GetString()).ToList();
        var answered = dialogs.Count == 0 ? "" : $" (answered: {string.Join(", ", dialogs)})";
        return $"Started session '{id}' in {projectDir}{answered}. Its channel is connected; it is ready for messages.";
    }

    public async Task<string> SendAsync(string id, string text, CancellationToken ct)
    {
        var result = await SendAsync(HttpMethod.Post, $"api/sessions/{Uri.EscapeDataString(id)}/messages", new { text }, ct);
        var messageId = result.GetProperty("messageId").GetInt64();
        return result.GetProperty("delivered").GetBoolean()
            ? $"Message #{messageId} delivered to '{id}'. Its reply will arrive as a channel message from=\"{id}\"; " +
              "you don't need to poll. End your turn if there is nothing else to do."
            : $"Message #{messageId} is queued: '{id}' has no channel connection right now. It will be delivered when it reconnects.";
    }

    public async Task<string> ReadScreenAsync(string id, int scrollback, CancellationToken ct)
    {
        var result = await SendAsync(HttpMethod.Get, $"api/sessions/{Uri.EscapeDataString(id)}/screen?scrollback={scrollback}", null, ct);
        return result.GetProperty("screen").GetString() ?? "";
    }

    public async Task<string> ListAsync(CancellationToken ct)
    {
        var result = await SendAsync(HttpMethod.Get, "api/sessions", null, ct);
        var text = new StringBuilder();
        foreach (var s in result.EnumerateArray())
        {
            var idle = s.GetProperty("idleSeconds") is { ValueKind: JsonValueKind.Number } i ? $", idle {i.GetInt32()}s" : "";
            var channel = s.GetProperty("channel").GetBoolean() ? "connected" : "offline";
            text.AppendLine(
                $"- {s.GetProperty("id").GetString()} ({s.GetProperty("role").GetString()}): " +
                $"{s.GetProperty("status").GetString()}, window {s.GetProperty("window").GetString()}, channel {channel}{idle}, " +
                $"dir {s.GetProperty("projectDir").GetString()}");
        }

        return text.Length == 0 ? "No sessions yet." : text.ToString();
    }

    public async Task<string> StopAsync(string id, bool force, CancellationToken ct)
    {
        await SendAsync(HttpMethod.Post, $"api/sessions/{Uri.EscapeDataString(id)}/stop", new { force }, ct);
        return $"Stopped session '{id}'.";
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException e)
        {
            throw new McpException($"Can't reach the Switchboard hub at {http.BaseAddress}: {e.Message}. Is it running?");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                var error = TryGetError(text) ?? $"HTTP {(int)response.StatusCode}";
                throw new McpException($"Hub refused: {error}");
            }

            return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
        }
    }

    private static string? TryGetError(string text)
    {
        try
        {
            return JsonDocument.Parse(text).RootElement.GetProperty("error").GetString();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}
