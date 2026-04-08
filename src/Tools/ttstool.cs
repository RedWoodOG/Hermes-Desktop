namespace Hermes.Agent.Tools;

using Hermes.Agent.Core;
using System.Diagnostics;
using System.Text;

// ══════════════════════════════════════════════
// Text-to-Speech Tool
// ══════════════════════════════════════════════
//
// Upstream ref: tools/tts_tool.py
// 5 backends: Edge TTS (free), ElevenLabs, OpenAI TTS, MiniMax, NeuTTS
// Start with Edge TTS (no API key needed).

/// <summary>
/// Convert text to speech audio. Uses Edge TTS by default (free, no API key).
/// Saves audio file and returns the path.
/// </summary>
public sealed class TextToSpeechTool : ITool
{
    private readonly string _outputDir;

    public TextToSpeechTool(string outputDir)
    {
        _outputDir = outputDir;
        Directory.CreateDirectory(outputDir);
    }

    public string Name => "tts";
    public string Description => "Convert text to speech audio. Returns path to the generated audio file. Uses Edge TTS (free).";
    public Type ParametersType => typeof(TtsParameters);

    public async Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        var p = (TtsParameters)parameters;

        if (string.IsNullOrWhiteSpace(p.Text))
            return ToolResult.Fail("Text is required.");

        var voice = p.Voice ?? "en-US-AriaNeural";
        var filename = $"tts_{DateTime.UtcNow:yyyyMMdd_HHmmss}.mp3";
        var outputPath = Path.Combine(_outputDir, filename);

        try
        {
            // Edge TTS via Python edge-tts package (widely available, free)
            var psi = new ProcessStartInfo
            {
                FileName = "edge-tts",
                Arguments = $"--voice \"{voice}\" --text \"{Escape(p.Text)}\" --write-media \"{outputPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };

            try { process.Start(); }
            catch
            {
                // Fallback: try via python -m edge_tts
                psi.FileName = "python";
                psi.Arguments = $"-m edge_tts --voice \"{voice}\" --text \"{Escape(p.Text)}\" --write-media \"{outputPath}\"";
                using var fallback = new Process { StartInfo = psi };

                try { fallback.Start(); }
                catch (Exception ex)
                {
                    return ToolResult.Fail(
                        "Edge TTS not found. Install with: pip install edge-tts\n" +
                        $"Error: {ex.Message}");
                }

                using var tCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                tCts.CancelAfter(TimeSpan.FromSeconds(60));
                await fallback.WaitForExitAsync(tCts.Token);

                if (fallback.ExitCode != 0)
                {
                    var err = await fallback.StandardError.ReadToEndAsync(tCts.Token);
                    return ToolResult.Fail($"TTS failed: {err}");
                }

                return File.Exists(outputPath)
                    ? ToolResult.Ok($"Audio saved to: {outputPath}")
                    : ToolResult.Fail("TTS completed but output file was not created.");
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(timeoutCts.Token);

            if (process.ExitCode != 0)
            {
                var err = await process.StandardError.ReadToEndAsync(timeoutCts.Token);
                return ToolResult.Fail($"TTS failed: {err}");
            }

            return File.Exists(outputPath)
                ? ToolResult.Ok($"Audio saved to: {outputPath}")
                : ToolResult.Fail("TTS completed but output file was not created.");
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Fail("TTS timed out after 60 seconds.");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"TTS failed: {ex.Message}");
        }
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

public sealed class TtsParameters
{
    public required string Text { get; init; }
    public string? Voice { get; init; }
}
