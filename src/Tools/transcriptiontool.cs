namespace Hermes.Agent.Tools;

using Hermes.Agent.Core;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

// ══════════════════════════════════════════════
// Transcription / Speech-to-Text Tool
// ══════════════════════════════════════════════
//
// Upstream ref: tools/transcription_tools.py
// 3 backends: local faster-whisper, Groq (free), OpenAI Whisper
// Supports 8 audio formats.

/// <summary>
/// Transcribe audio files to text. Supports local Whisper or API backends.
/// Formats: mp3, wav, m4a, ogg, flac, webm, mp4, mpeg.
/// </summary>
public sealed class TranscriptionTool : ITool
{
    private static readonly HashSet<string> SupportedFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".m4a", ".ogg", ".flac", ".webm", ".mp4", ".mpeg"
    };

    public string Name => "transcribe";
    public string Description => "Transcribe an audio file to text. Supports mp3, wav, m4a, ogg, flac, webm.";
    public Type ParametersType => typeof(TranscriptionParameters);

    public async Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        var p = (TranscriptionParameters)parameters;

        if (string.IsNullOrWhiteSpace(p.FilePath))
            return ToolResult.Fail("File path is required.");

        if (!File.Exists(p.FilePath))
            return ToolResult.Fail($"File not found: {p.FilePath}");

        var ext = Path.GetExtension(p.FilePath).ToLowerInvariant();
        if (!SupportedFormats.Contains(ext))
            return ToolResult.Fail($"Unsupported format: {ext}. Supported: {string.Join(", ", SupportedFormats)}");

        var backend = p.Backend?.ToLowerInvariant() ?? "auto";

        // Try backends in order: API (if key available) → local whisper
        return backend switch
        {
            "openai" => await TranscribeViaApiAsync(p.FilePath, "https://api.openai.com/v1",
                Environment.GetEnvironmentVariable("OPENAI_API_KEY"), ct),
            "groq" => await TranscribeViaApiAsync(p.FilePath, "https://api.groq.com/openai/v1",
                Environment.GetEnvironmentVariable("GROQ_API_KEY"), ct),
            "local" => await TranscribeLocalAsync(p.FilePath, p.Language, ct),
            _ => await TranscribeAutoAsync(p.FilePath, p.Language, ct) // auto
        };
    }

    private async Task<ToolResult> TranscribeAutoAsync(string filePath, string? language, CancellationToken ct)
    {
        // Try Groq first (free tier), then OpenAI, then local
        var groqKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
        if (!string.IsNullOrWhiteSpace(groqKey))
            return await TranscribeViaApiAsync(filePath, "https://api.groq.com/openai/v1", groqKey, ct);

        var openaiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(openaiKey))
            return await TranscribeViaApiAsync(filePath, "https://api.openai.com/v1", openaiKey, ct);

        return await TranscribeLocalAsync(filePath, language, ct);
    }

    private async Task<ToolResult> TranscribeViaApiAsync(
        string filePath, string baseUrl, string? apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return ToolResult.Fail("API key not configured for this transcription backend.");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            using var form = new MultipartFormDataContent();

            var fileBytes = await File.ReadAllBytesAsync(filePath, ct);
            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
            form.Add(fileContent, "file", Path.GetFileName(filePath));
            form.Add(new StringContent("whisper-1"), "model");

            var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/audio/transcriptions")
            {
                Content = form
            };
            request.Headers.Add("Authorization", $"Bearer {apiKey}");

            var response = await http.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return ToolResult.Fail($"Transcription API error: {response.StatusCode} — {json}");

            using var doc = JsonDocument.Parse(json);
            var text = doc.RootElement.GetProperty("text").GetString() ?? "(empty transcription)";

            return ToolResult.Ok(text);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Transcription failed: {ex.Message}");
        }
    }

    private async Task<ToolResult> TranscribeLocalAsync(string filePath, string? language, CancellationToken ct)
    {
        try
        {
            var langArg = language is not null ? $"--language {language}" : "";
            var psi = new ProcessStartInfo
            {
                FileName = "whisper",
                Arguments = $"\"{filePath}\" --model base {langArg} --output_format txt",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };

            try { process.Start(); }
            catch (Exception ex)
            {
                return ToolResult.Fail(
                    "Local Whisper not found. Install with: pip install openai-whisper\n" +
                    "Or set GROQ_API_KEY for free cloud transcription.\n" +
                    $"Error: {ex.Message}");
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

            var stdout = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);

            return process.ExitCode == 0
                ? ToolResult.Ok(string.IsNullOrWhiteSpace(stdout) ? "(empty transcription)" : stdout)
                : ToolResult.Fail($"Whisper failed with exit code {process.ExitCode}");
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Fail("Transcription timed out after 5 minutes.");
        }
    }
}

public sealed class TranscriptionParameters
{
    public required string FilePath { get; init; }
    public string? Backend { get; init; }
    public string? Language { get; init; }
}
