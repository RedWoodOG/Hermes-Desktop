using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace HermesDesktop.Services;

/// <summary>
/// Simple screenshot capture service for session replay.
/// Captures a provided UIElement as a timestamped PNG.
/// </summary>
internal sealed class SessionRecorder
{
    private string? _recordingDir;
    private string? _sessionId;

    /// <summary>Whether recording is currently active.</summary>
    public bool IsRecording { get; private set; }

    /// <summary>
    /// Start recording for a session. Creates the recordings directory.
    /// </summary>
    public void StartRecording(string sessionId)
    {
        _sessionId = sessionId;
        _recordingDir = Path.Combine(HermesEnvironment.HermesHomePath, "recordings", sessionId);
        Directory.CreateDirectory(_recordingDir);
        IsRecording = true;
    }

    /// <summary>
    /// Stop recording.
    /// </summary>
    public void StopRecording()
    {
        IsRecording = false;
    }

    /// <summary>
    /// Capture a screenshot of the given UIElement and save as a timestamped PNG.
    /// Returns the file path, or null if not recording or capture fails.
    /// </summary>
    public async Task<string?> CaptureAsync(UIElement element, string label)
    {
        if (!IsRecording || _recordingDir is null) return null;

        try
        {
            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(element);

            var pixelBuffer = await rtb.GetPixelsAsync();
            var pixels = pixelBuffer.ToArray();

            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
            var safeLabel = SanitizeFileName(label);
            var fileName = $"{timestamp}_{safeLabel}.png";
            var filePath = Path.Combine(_recordingDir, fileName);

            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                (uint)rtb.PixelWidth,
                (uint)rtb.PixelHeight,
                96, 96,
                pixels);
            await encoder.FlushAsync();

            // Write to file
            stream.Seek(0);
            using var fileStream = File.Create(filePath);
            using var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            var buffer = new byte[stream.Size];
            reader.ReadBytes(buffer);
            await fileStream.WriteAsync(buffer);

            return filePath;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SessionRecorder capture failed for {label}: {ex}");
            return null;
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new char[name.Length];
        for (int i = 0; i < name.Length; i++)
            sanitized[i] = Array.IndexOf(invalid, name[i]) >= 0 ? '_' : name[i];
        return new string(sanitized);
    }
}
