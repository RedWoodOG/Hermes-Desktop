using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using Hermes.Agent.Updates;
using Microsoft.Extensions.Logging;
using Windows.Storage;

namespace HermesDesktop.Services;

/// <summary>
/// Checks GitHub Releases for a newer portable zip and optionally downloads it with SHA-256 verification.
/// </summary>
internal sealed class UpdateService : IDisposable
{
    internal const string LocalSettingsCheckOnStartup = "updates.check_on_startup";

    private readonly HttpClient _http;
    private readonly Assembly _appAssembly;
    private readonly ILogger<UpdateService> _logger;
    private bool _disposed;

    public UpdateService(ILogger<UpdateService> logger)
    {
        _logger = logger;
        _appAssembly = typeof(global::HermesDesktop.App).Assembly;
        string ver = _appAssembly.GetName().Version?.ToString() ?? "0.0";
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HermesDesktop", ver));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>Last completed check (for Settings UI). Not thread-safe across concurrent checks.</summary>
    internal PortableUpdateCheckResult? LastCheck { get; private set; }

    internal static bool GetCheckOnStartupEnabled()
    {
        try
        {
            if (ApplicationData.Current.LocalSettings.Values.TryGetValue(LocalSettingsCheckOnStartup, out object? raw) &&
                raw is bool b)
                return b;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"UpdateService.GetCheckOnStartupEnabled: {ex.Message}");
        }

        return true;
    }

    internal static void SetCheckOnStartupEnabled(bool value)
    {
        try
        {
            ApplicationData.Current.LocalSettings.Values[LocalSettingsCheckOnStartup] = value;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"UpdateService.SetCheckOnStartupEnabled: {ex.Message}");
        }
    }

    /// <summary>Kill-switch: set <c>HERMES_DESKTOP_SKIP_UPDATE_CHECK=1</c> to disable network checks.</summary>
    internal static bool IsUpdateCheckDisabledByEnvironment() =>
        string.Equals(Environment.GetEnvironmentVariable("HERMES_DESKTOP_SKIP_UPDATE_CHECK"), "1", StringComparison.Ordinal);

    /// <summary>
    /// Detect whether the running executable lives under <c>%LOCALAPPDATA%\Microsoft\WinGet\Packages\</c>,
    /// i.e. it was installed via <c>winget install VyreVaultStudios.HermesDesktop</c>.
    /// In that case the in-app downloader would collide with Winget's own package directory
    /// — callers should route users to <c>winget upgrade</c> instead.
    /// </summary>
    internal static bool IsRunningFromWingetInstall()
    {
        try
        {
            var exe = typeof(global::HermesDesktop.App).Assembly.Location;
            if (string.IsNullOrEmpty(exe))
                return false;

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(localAppData))
                return false;

            var wingetRoot = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
            return exe.StartsWith(wingetRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"UpdateService.IsRunningFromWingetInstall: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Shell out to <c>winget upgrade VyreVaultStudios.HermesDesktop</c>. Returns the process
    /// exit code; non-zero indicates Winget could not perform the upgrade (network, signature,
    /// or unknown package). Use only when <see cref="IsRunningFromWingetInstall"/> is true.
    /// </summary>
    /// <remarks>
    /// Qodo Reliability finding: stdout/stderr MUST be drained concurrently with the
    /// <see cref="Process.WaitForExitAsync(CancellationToken)"/> wait. If we redirect both
    /// streams but never read them, winget can block on a full pipe buffer and the await
    /// hangs indefinitely — which would freeze the UI thread that scheduled this Task.
    /// </remarks>
    internal static async Task<int> RequestWingetUpgradeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var psi = new ProcessStartInfo("winget.exe", "upgrade --id VyreVaultStudios.HermesDesktop --accept-source-agreements --accept-package-agreements")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return -1;

            // Drain both streams to prevent pipe-buffer deadlock. Reads must run
            // concurrently with WaitForExitAsync, otherwise a full pipe blocks
            // winget on a write and WaitForExit never returns (Qodo finding).
            //
            // Note: ReadToEndAsync(CancellationToken) on .NET 10 already returns
            // Task<string>. We deliberately do not call .AsTask() because in this
            // WinUI project System.Runtime.WindowsRuntime brings in a WinRT
            // AsTask extension that shadows the Task<T>.AsTask polyfill and
            // produces CS1929. Plain Task.WhenAll handles the parallel wait.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var exitTask = process.WaitForExitAsync(cancellationToken);

            await Task.WhenAll(stdoutTask, stderrTask, exitTask).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(stdout))
                Debug.WriteLine($"UpdateService.RequestWingetUpgradeAsync stdout: {stdout}");
            if (!string.IsNullOrWhiteSpace(stderr))
                Debug.WriteLine($"UpdateService.RequestWingetUpgradeAsync stderr: {stderr}");

            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            // Preserve cancellation semantics — callers (CancellationToken) need to
            // distinguish "user/system cancelled the upgrade" from "winget failed for
            // unknown reasons" (-2). Rethrowing makes cancellation observable rather
            // than silently turning into a generic non-zero exit code (CodeRabbit,
            // 2026-05-14).
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"UpdateService.RequestWingetUpgradeAsync: {ex.Message}");
            return -2;
        }
    }

    internal async Task<PortableUpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        if (IsUpdateCheckDisabledByEnvironment())
        {
            LastCheck = PortableUpdateCheckResult.Skipped("HERMES_DESKTOP_SKIP_UPDATE_CHECK=1");
            return LastCheck;
        }

        // When installed via Winget, the in-app downloader would write to a directory Winget owns.
        // Skip the network round-trip; the user gets updates via `winget upgrade` instead.
        if (IsRunningFromWingetInstall())
        {
            LastCheck = PortableUpdateCheckResult.Skipped("Managed by Winget — run 'winget upgrade VyreVaultStudios.HermesDesktop'.");
            return LastCheck;
        }

        if (!GitHubPortableReleaseParser.TryParseDesktopAssemblyVersion(_appAssembly, out var currentVersion) ||
            currentVersion is null)
        {
            LastCheck = PortableUpdateCheckResult.Failed("Could not read app version from assembly.");
            return LastCheck;
        }

        Uri api = GitHubPortableReleaseParser.LatestReleaseApiUri(
            GitHubPortableReleaseParser.DefaultOwner,
            GitHubPortableReleaseParser.DefaultRepo);

        try
        {
            using var response = await _http.GetAsync(api, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string msg = $"GitHub returned {(int)response.StatusCode} {response.ReasonPhrase}.";
                LastCheck = PortableUpdateCheckResult.Failed(msg);
                return LastCheck;
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var offer = GitHubPortableReleaseParser.TryParseLatestReleaseJson(json);
            if (offer is null)
            {
                LastCheck = PortableUpdateCheckResult.Failed("Could not parse latest release (missing portable zip?).");
                return LastCheck;
            }

            if (!GitHubPortableReleaseParser.IsNewerThan(offer.Version, currentVersion))
            {
                LastCheck = PortableUpdateCheckResult.UpToDate();
                return LastCheck;
            }

            LastCheck = PortableUpdateCheckResult.Available(offer);
            return LastCheck;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed");
            LastCheck = PortableUpdateCheckResult.Failed(ex.Message);
            return LastCheck;
        }
    }

    /// <summary>
    /// Downloads the portable zip and verifies it against the release <c>.sha256</c> sidecar.
    /// Fails closed if the checksum file is missing or hashes do not match.
    /// </summary>
    internal async Task<PortableVerifiedDownloadResult> DownloadVerifiedPortableAsync(
        PortableReleaseOffer offer,
        CancellationToken cancellationToken = default)
    {
        if (offer.Sha256BrowserDownloadUri is null)
            return new PortableVerifiedDownloadResult(PortableVerifiedDownloadStatus.Failed, "Release has no .sha256 sidecar.", null);

        string downloadsDir = Path.Combine(HermesEnvironment.HermesHomePath, "hermes-cs", "downloads");
        Directory.CreateDirectory(downloadsDir);

        string safeTag = string.Join("_", offer.TagName.Split(Path.GetInvalidFileNameChars()));
        string finalPath = Path.Combine(downloadsDir, $"HermesDesktop-{safeTag}-portable.zip");
        string tempPath = Path.Combine(downloadsDir, $".{safeTag}.{Guid.NewGuid():N}.part");

        try
        {
            string shaText = await _http.GetStringAsync(offer.Sha256BrowserDownloadUri, cancellationToken)
                .ConfigureAwait(false);
            if (!GitHubPortableReleaseParser.TryParseSha256SumFile(shaText, out ReadOnlyMemory<byte> expectedDigest))
            {
                return new PortableVerifiedDownloadResult(
                    PortableVerifiedDownloadStatus.Failed,
                    "Could not parse .sha256 file from release.",
                    null);
            }

            await using (var network = await _http.GetStreamAsync(offer.ZipBrowserDownloadUri, cancellationToken)
                             .ConfigureAwait(false))
            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[65536];
                while (true)
                {
                    int read = await network.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                        .ConfigureAwait(false);
                    if (read <= 0)
                        break;
                    incremental.AppendData(buffer.AsSpan(0, read));
                    await fs.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                byte[] computed = incremental.GetHashAndReset();
                if (!GitHubPortableReleaseParser.FixedTimeEquals(computed, expectedDigest.Span))
                {
                    return new PortableVerifiedDownloadResult(
                        PortableVerifiedDownloadStatus.Failed,
                        "Downloaded zip SHA-256 did not match the release .sha256 file.",
                        null);
                }
            }

            if (File.Exists(finalPath))
                File.Delete(finalPath);
            File.Move(tempPath, finalPath);
            return new PortableVerifiedDownloadResult(PortableVerifiedDownloadStatus.Succeeded, null, finalPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Verified download failed");
            return new PortableVerifiedDownloadResult(PortableVerifiedDownloadStatus.Failed, ex.Message, null);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "Temp update file cleanup");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}
