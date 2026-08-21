using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AudioSourceMixer.WindowsAudio;

internal sealed record SessionPresentation(string DisplayName, string ProcessFileName);

internal static partial class SessionPresentationResolver
{
    private static readonly IReadOnlyDictionary<string, string> KnownApplications = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["msedge"] = "Microsoft Edge",
        ["chrome"] = "Google Chrome",
        ["potplayermini64"] = "PotPlayer",
        ["potplayermini"] = "PotPlayer",
        ["spotify"] = "Spotify"
    };

    public static SessionPresentation Resolve(string? sessionDisplayName, string? executablePath,
        uint processId, string? processName)
    {
        if (processId == 0) return new SessionPresentation("系统声音", "系统");
        var fileName = SafeFileName(executablePath) ?? NormalizeProcessFileName(processName, processId);
        if (IsReadableDisplayName(sessionDisplayName))
            return new SessionPresentation(sessionDisplayName!.Trim(), fileName);

        var versionName = ReadVersionName(executablePath);
        if (!string.IsNullOrWhiteSpace(versionName)) return new SessionPresentation(versionName, fileName);

        var key = Path.GetFileNameWithoutExtension(fileName);
        if (KnownApplications.TryGetValue(key, out var known)) return new SessionPresentation(known, fileName);
        if (!string.IsNullOrWhiteSpace(key)) return new SessionPresentation(key, fileName);
        return new SessionPresentation($"进程 {processId}", fileName);
    }

    internal static bool IsReadableDisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value.Trim();
        if (trimmed.StartsWith('@') || trimmed.Contains("ms-resource", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains('\\') || trimmed.Contains('/')) return false;
        return !GuidPattern().IsMatch(trimmed);
    }

    private static string? ReadVersionName(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath)) return null;
        try
        {
            var version = FileVersionInfo.GetVersionInfo(executablePath);
            return FirstReadable(version.FileDescription, version.ProductName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static string? FirstReadable(params string?[] values)
        => values.FirstOrDefault(IsReadableDisplayName)?.Trim();

    private static string? SafeFileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFileName(path); }
        catch (ArgumentException) { return null; }
    }

    private static string NormalizeProcessFileName(string? processName, uint processId)
    {
        if (string.IsNullOrWhiteSpace(processName)) return $"进程 {processId}";
        return processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName : processName + ".exe";
    }

    [GeneratedRegex("^\\{?[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\\}?$", RegexOptions.IgnoreCase)]
    private static partial Regex GuidPattern();
}
