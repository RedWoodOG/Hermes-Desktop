namespace Hermes.Agent.Updates;

using System;

/// <summary>Outcome of comparing the running app to the latest GitHub portable release.</summary>
public enum PortableUpdateCheckStatus
{
    /// <summary>Could not reach GitHub or parse the response.</summary>
    Failed,

    /// <summary>Latest release tag is not newer than the running build.</summary>
    UpToDate,

    /// <summary>A newer release exists on GitHub.</summary>
    UpdateAvailable,

    /// <summary>Update checks skipped (e.g. unofficial channel / env kill-switch).</summary>
    Skipped,
}

/// <summary>Parsed latest-release payload for the portable zip channel.</summary>
public sealed record PortableReleaseOffer(
    string TagName,
    Version Version,
    Uri ReleasePageUri,
    Uri ZipBrowserDownloadUri,
    Uri? Sha256BrowserDownloadUri);

/// <summary>Result of an update check suitable for UI and download gating.</summary>
public sealed record PortableUpdateCheckResult(
    PortableUpdateCheckStatus Status,
    string? ErrorMessage,
    PortableReleaseOffer? Offer)
{
    public static PortableUpdateCheckResult Failed(string message) =>
        new(PortableUpdateCheckStatus.Failed, message, null);

    public static PortableUpdateCheckResult UpToDate() =>
        new(PortableUpdateCheckStatus.UpToDate, null, null);

    public static PortableUpdateCheckResult Skipped(string reason) =>
        new(PortableUpdateCheckStatus.Skipped, reason, null);

    public static PortableUpdateCheckResult Available(PortableReleaseOffer offer) =>
        new(PortableUpdateCheckStatus.UpdateAvailable, null, offer);

    public bool CanDownloadVerified => Offer?.Sha256BrowserDownloadUri is not null;
}

/// <summary>Result of downloading the portable zip and verifying SHA-256.</summary>
public enum PortableVerifiedDownloadStatus
{
    Succeeded,
    Failed,
}

public sealed record PortableVerifiedDownloadResult(
    PortableVerifiedDownloadStatus Status,
    string? ErrorMessage,
    string? SavedZipPath);
