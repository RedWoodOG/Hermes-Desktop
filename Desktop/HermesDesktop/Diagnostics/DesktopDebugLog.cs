using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace HermesDesktop.Diagnostics;

/// <summary>NDJSON append for debug session e58a67 — remove after verification.</summary>
internal static class DesktopDebugLog
{
    private const string LogPath = @"C:\Hermes_Desktop_FE\debug-e58a67.log";

    public static void Write(string hypothesisId, string location, string message, object? data = null)
    {
        #region agent log
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["sessionId"] = "e58a67",
                ["hypothesisId"] = hypothesisId,
                ["location"] = location,
                ["message"] = message,
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            if (data is not null)
                payload["data"] = data;
            File.AppendAllText(LogPath, JsonSerializer.Serialize(payload) + Environment.NewLine);
        }
        catch
        {
            /* intentionally empty — debug ingest must not break app */
        }
        #endregion
    }
}
