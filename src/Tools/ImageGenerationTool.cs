namespace Hermes.Agent.Tools;

using Hermes.Agent.Core;
using System.Net.Http.Json;
using System.Text.Json;

// ══════════════════════════════════════════════
// Image Generation Tool
// ══════════════════════════════════════════════
//
// Upstream ref: tools/image_generation_tool.py
// Generates images via external APIs (FAL.ai, OpenAI DALL-E, etc.)
// Saves output as PNG/JPEG to local directory.

/// <summary>
/// Generate images from text prompts via external APIs.
/// Supports FAL.ai (default) and OpenAI DALL-E endpoints.
/// </summary>
public sealed class ImageGenerationTool : ITool
{
    private readonly string _outputDir;
    private readonly HttpClient _http;

    public ImageGenerationTool(string outputDir, HttpClient? http = null)
    {
        _outputDir = outputDir;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        Directory.CreateDirectory(outputDir);
    }

    public string Name => "generate_image";
    public string Description => "Generate an image from a text prompt. Returns the file path to the generated image.";
    public Type ParametersType => typeof(ImageGenParameters);

    public async Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        var p = (ImageGenParameters)parameters;

        if (string.IsNullOrWhiteSpace(p.Prompt))
            return ToolResult.Fail("Prompt is required.");

        var size = p.Size ?? "1024x1024";

        try
        {
            // Try OpenAI-compatible endpoint (works with many providers)
            var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                      ?? Environment.GetEnvironmentVariable("FAL_API_KEY");

            if (string.IsNullOrWhiteSpace(apiKey))
                return ToolResult.Fail("No image generation API key configured. Set OPENAI_API_KEY or FAL_API_KEY.");

            var baseUrl = Environment.GetEnvironmentVariable("IMAGE_GEN_BASE_URL")
                       ?? "https://api.openai.com/v1";

            var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/images/generations")
            {
                Content = JsonContent.Create(new
                {
                    prompt = p.Prompt,
                    n = p.Count > 0 ? Math.Min(p.Count, 4) : 1,
                    size,
                    response_format = "b64_json"
                })
            };
            request.Headers.Add("Authorization", $"Bearer {apiKey}");

            var response = await _http.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return ToolResult.Fail($"Image generation failed: {response.StatusCode} — {json}");

            using var doc = JsonDocument.Parse(json);
            var images = doc.RootElement.GetProperty("data").EnumerateArray();

            var paths = new List<string>();
            var index = 0;
            foreach (var img in images)
            {
                var b64 = img.GetProperty("b64_json").GetString();
                if (b64 is null) continue;

                var bytes = Convert.FromBase64String(b64);
                var filename = $"generated_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{index}.png";
                var path = Path.Combine(_outputDir, filename);
                await File.WriteAllBytesAsync(path, bytes, ct);
                paths.Add(path);
                index++;
            }

            if (paths.Count == 0)
                return ToolResult.Fail("No images were generated.");

            return ToolResult.Ok($"Generated {paths.Count} image(s):\n{string.Join("\n", paths)}");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Image generation failed: {ex.Message}");
        }
    }
}

public sealed class ImageGenParameters
{
    public required string Prompt { get; init; }
    public string? Size { get; init; }
    public int Count { get; init; } = 1;
}
