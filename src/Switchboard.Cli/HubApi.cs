using System.Net.Http.Json;
using System.Text.Json;

namespace Switchboard.Cli;

/// <summary>sb's client for the hub API, using the owner's admin token from .env.</summary>
public sealed class HubApi : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public HubApi()
    {
        BaseUrl = (Environment.GetEnvironmentVariable("SWITCHBOARD_HUB_URL") ?? "http://127.0.0.1:7700").TrimEnd('/') + "/";
        var token = Environment.GetEnvironmentVariable("SWITCHBOARD_ADMIN_TOKEN");
        if (string.IsNullOrEmpty(token))
        {
            throw new CliException("SWITCHBOARD_ADMIN_TOKEN is not set. Run scripts/setup-env.sh, then use ./sb (it loads .env).");
        }

        // Starting a session waits for Claude Code to boot, answer its dialogs and connect its
        // channel, which can take most of a minute.
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(120) };
        _http.DefaultRequestHeaders.Authorization = new("Bearer", token);
    }

    public string BaseUrl { get; }

    public Task<JsonElement> GetAsync(string path) => SendAsync(HttpMethod.Get, path, null);

    public Task<JsonElement> PostAsync(string path, object body) => SendAsync(HttpMethod.Post, path, body);

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request);
        }
        catch (HttpRequestException e)
        {
            throw new CliException($"Can't reach the hub at {BaseUrl} ({e.Message}). Start it with: scripts/run-hub.sh");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new CliException(ErrorText(text) ?? $"hub answered HTTP {(int)response.StatusCode}");
            }

            return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
        }
    }

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

    public void Dispose() => _http.Dispose();
}

public sealed class CliException(string message) : Exception(message);
