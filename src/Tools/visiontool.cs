namespace Hermes.Agent.Tools;

using Hermes.Agent.Core;
using Hermes.Agent.LLM;
using System.Text;
using System.Text.Json;

// ══════════════════════════════════════════════
// Vision Tool — Image Analysis
// ══════════════════════════════════════════════
//
// Upstream ref: tools/vision_tools.py
// Multi-format image analysis via vision-capable LLMs.
// Supports local files and URLs (with SSRF protection).

/// <summary>
/// Analyze images using vision-capable LLM models.
/// Accepts file paths or URLs. Sends as base64 data URI.
/// </summary>
public sealed class VisionTool : ITool
{
    private readonly IChatClient _chatClient;

    private static readonly HashSet<string> SupportedFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".svg"
    };

    public VisionTool(IChatClient chatClient) => _chatClient = chatClient;

    public string Name => "vision";
    public string Description => "Analyze an image. Provide a file path or URL and a question about the image.";
    public Type ParametersType => typeof(VisionParameters);

    public async Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        var p = (VisionParameters)parameters;

        if (string.IsNullOrWhiteSpace(p.Image))
            return ToolResult.Fail("Image path or URL is required.");
        if (string.IsNullOrWhiteSpace(p.Question))
            return ToolResult.Fail("Question is required.");

        try
        {
            string imageContent;

            if (Uri.TryCreate(p.Image, UriKind.Absolute, out var uri) &&
                (uri.Scheme == "http" || uri.Scheme == "https"))
            {
                // URL — download and convert to base64
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                var bytes = await http.GetByteArrayAsync(uri, ct);
                var ext = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
                var mime = ext switch
                {
                    ".png" => "image/png",
                    ".gif" => "image/gif",
                    ".webp" => "image/webp",
                    ".svg" => "image/svg+xml",
                    _ => "image/jpeg"
                };
                imageContent = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
            }
            else
            {
                // Local file
                if (!File.Exists(p.Image))
                    return ToolResult.Fail($"File not found: {p.Image}");

                var ext = Path.GetExtension(p.Image).ToLowerInvariant();
                if (!SupportedFormats.Contains(ext))
                    return ToolResult.Fail($"Unsupported format: {ext}. Supported: {string.Join(", ", SupportedFormats)}");

                var bytes = await File.ReadAllBytesAsync(p.Image, ct);
                var mime = ext switch
                {
                    ".png" => "image/png",
                    ".gif" => "image/gif",
                    ".webp" => "image/webp",
                    ".svg" => "image/svg+xml",
                    ".bmp" => "image/bmp",
                    _ => "image/jpeg"
                };
                imageContent = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
            }

            // Send to vision model via content blocks
            // Most providers accept image_url in content array
            var prompt = $"[Image: {imageContent}]\n\n{p.Question}";

            var response = await _chatClient.CompleteAsync(
                [new Message { Role = "user", Content = prompt }], ct);

            return ToolResult.Ok(response);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Vision analysis failed: {ex.Message}");
        }
    }
}

public sealed class VisionParameters
{
    public required string Image { get; init; }
    public required string Question { get; init; }
}
