namespace Hermes.Agent.Dreamer;

using System.Text;

/// <summary>Centralized Dreamer project slug normalization and validation.</summary>
public static class DreamerProjectSlug
{
    public static bool TryNormalize(string? slug, out string normalized)
    {
        normalized = Normalize(slug);
        return normalized.Length > 0;
    }

    public static string Normalize(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return "";

        var trimmed = slug.Trim();
        if (Path.IsPathRooted(trimmed))
            return "";

        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(trimmed.Length);
        var lastWasSeparator = false;

        foreach (var c in trimmed)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
                lastWasSeparator = false;
                continue;
            }

            if (c is '-' or '_' or '.' or '/' or '\\' || char.IsWhiteSpace(c))
            {
                if (!lastWasSeparator)
                    builder.Append('-');

                lastWasSeparator = true;
                continue;
            }

            if (Array.IndexOf(invalidChars, c) >= 0)
                continue;
        }

        return builder.ToString().Trim('-');
    }
}
