namespace Switchboard.Hub;

/// <summary>An error with an HTTP status, turned into {"error": "..."} by the error middleware.</summary>
public sealed class HubException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;

    public static HubException NotFound(string message) => new(StatusCodes.Status404NotFound, message);
    public static HubException Forbidden(string message) => new(StatusCodes.Status403Forbidden, message);
    public static HubException Conflict(string message) => new(StatusCodes.Status409Conflict, message);
    public static HubException BadRequest(string message) => new(StatusCodes.Status400BadRequest, message);
}
