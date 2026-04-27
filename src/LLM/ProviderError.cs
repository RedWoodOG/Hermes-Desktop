namespace Hermes.Agent.LLM;

using System.Net;

public enum ProviderErrorCode
{
    Unknown,
    ProviderTimeout,
    ProviderAuth,
    RateLimit,
    Transport,
    StreamParseError
}

public sealed class LlmProviderException : Exception
{
    public LlmProviderException(
        ProviderErrorCode code,
        string message,
        Exception? innerException = null,
        HttpStatusCode? statusCode = null)
        : base(message, innerException)
    {
        Code = code;
        StatusCode = statusCode;
    }

    public ProviderErrorCode Code { get; }
    public HttpStatusCode? StatusCode { get; }

    public static LlmProviderException FromHttp(HttpRequestException ex)
    {
        var code = ex.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ProviderErrorCode.ProviderAuth,
            HttpStatusCode.TooManyRequests => ProviderErrorCode.RateLimit,
            _ => ProviderErrorCode.Transport
        };

        var message = ex.StatusCode is null
            ? $"Provider request failed: {ex.Message}"
            : $"Provider request failed with HTTP {(int)ex.StatusCode.Value} ({ex.StatusCode.Value}).";

        return new LlmProviderException(code, message, ex, ex.StatusCode);
    }

    public static LlmProviderException Timeout(Exception ex) =>
        new(ProviderErrorCode.ProviderTimeout, "Provider stream timed out.", ex);

    public static LlmProviderException Transport(Exception ex) =>
        new(ProviderErrorCode.Transport, $"Provider transport failed: {ex.Message}", ex);

    public static LlmProviderException StreamParse(Exception ex, string? detail = null) =>
        new(ProviderErrorCode.StreamParseError, detail ?? $"Provider stream parse failed: {ex.Message}", ex);
}
