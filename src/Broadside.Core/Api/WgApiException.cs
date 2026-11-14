namespace Broadside.Core.Api;

public enum WgErrorKind
{
    /// <summary>Temporary: rate limit, source unavailable, network, 5xx. Retried.</summary>
    Transient,

    /// <summary>The application ID is missing, invalid or blocked. Retrying will not help.</summary>
    Configuration,

    /// <summary>The request itself was rejected (bad field, too many IDs…).</summary>
    Request,
}

public sealed class WgApiException : Exception
{
    public WgApiException(WgErrorKind kind, string message, int? code = null, string? field = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        Code = code;
        Field = field;
    }

    public WgErrorKind Kind { get; }
    public int? Code { get; }
    public string? Field { get; }

    /// <summary>Classifies a Wargaming API error object. Code 407 covers both rate limits and bad IDs, so the message decides.</summary>
    public static WgApiException FromApiError(int code, string? message, string? field)
    {
        var msg = message ?? "UNKNOWN_ERROR";
        var kind = msg switch
        {
            "REQUEST_LIMIT_EXCEEDED" or "SOURCE_NOT_AVAILABLE" or "SERVICE_UNAVAILABLE" => WgErrorKind.Transient,
            "INVALID_APPLICATION_ID" or "APPLICATION_IS_BLOCKED" or "DEMO_APPLICATION_IS_BLOCKED"
                or "INVALID_IP_ADDRESS" or "APPLICATION_ID_NOT_SPECIFIED" => WgErrorKind.Configuration,
            _ when code >= 500 => WgErrorKind.Transient,
            _ => WgErrorKind.Request,
        };
        var text = kind == WgErrorKind.Configuration
            ? $"Wargaming API rejected the application ID ({msg}). Check it in Settings."
            : $"Wargaming API error {code} {msg}" + (field is null ? "" : $" (field: {field})");
        return new WgApiException(kind, text, code, field);
    }
}
