namespace Hermes.Agent.Mcp;

using System.Globalization;
using System.Net;
using System.Net.Sockets;

/// <summary>
/// Validates remote MCP HTTP/SSE and WebSocket endpoints before connections are created.
/// Fails closed on ambiguous schemes; allows HTTP/WS only on loopback; blocks link-local cloud metadata host.
/// </summary>
public static class McpRemoteEndpointValidator
{
    /// <summary>Returns <c>true</c> when the URI is allowed for an outbound MCP transport.</summary>
    public static bool TryValidateRemoteUri(Uri uri, out string? error)
    {
        error = null;
        if (!uri.IsAbsoluteUri)
        {
            error = "MCP URL must be absolute.";
            return false;
        }

        if (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
            uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase))
        {
            if (IsBlockedMetadataHost(uri))
            {
                error = "MCP URL host is not allowed (metadata / link-local block).";
                return false;
            }

            return true;
        }

        if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
            uri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsLoopbackHost(uri))
            {
                error = "HTTP and WS MCP URLs are allowed only for loopback hosts (127.0.0.1, localhost, ::1). Use HTTPS or WSS elsewhere.";
                return false;
            }

            if (IsBlockedMetadataHost(uri))
            {
                error = "MCP URL host is not allowed.";
                return false;
            }

            return true;
        }

        error = string.Format(CultureInfo.InvariantCulture, "Unsupported MCP URL scheme: {0}", uri.Scheme);
        return false;
    }

    private static bool IsLoopbackHost(Uri uri)
    {
        string host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        if (IPAddress.TryParse(host, out var ip))
            return IPAddress.IsLoopback(ip);

        return false;
    }

    private static bool IsBlockedMetadataHost(Uri uri)
    {
        string host = uri.IdnHost;
        if (host.Equals("169.254.169.254", StringComparison.OrdinalIgnoreCase))
            return true;

        if (IPAddress.TryParse(host, out var ip))
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var bytes = ip.GetAddressBytes();
                // IPv4 link-local 169.254.0.0/16 (cloud instance metadata, etc.)
                if (bytes[0] == 169 && bytes[1] == 254)
                    return true;
            }
        }

        return false;
    }
}
