using System.Text.RegularExpressions;

namespace Cue4ParseMcp.Core;

/// <summary>
/// Removes secrets and machine details from text that leaves the process inside a report: known AES keys,
/// endpoint URLs (only scheme://host survives), configured local paths, any other absolute local path,
/// long hex runs, bearer/JWT tokens and key=value credentials. Only the first line is kept (no stack
/// traces) and the result is length-capped. Game paths ("/Game/...") and public GUIDs are preserved.
/// </summary>
internal sealed partial class ReportSanitizer
{
    private const int MaxLength = 300;
    private const int MinSecretLength = 8;
    private readonly List<string> _secrets = [];
    private readonly List<(string Path, string Label)> _paths = [];
    private bool _sorted;

    public void AddSecret(string? value)
    {
        var secret = value?.Trim();
        if (secret is not { Length: >= MinSecretLength }) return;
        _secrets.Add(secret);
        // Keys are accepted with or without a 0x prefix; redact both spellings.
        _secrets.Add(secret.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? secret[2..] : "0x" + secret);
        _sorted = false;
    }

    public void AddPath(string? path, string label)
    {
        var trimmed = path?.Trim().TrimEnd('\\', '/');
        // A drive root or "/" alone would erase unrelated text.
        if (trimmed is not { Length: > 3 }) return;
        _paths.Add((trimmed.Replace('/', '\\'), label));
        _paths.Add((trimmed.Replace('\\', '/'), label));
        _sorted = false;
    }

    public string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (!_sorted)
        {
            // Longest first so a nested path or a key prefix can't leave a partial match behind.
            _secrets.Sort((a, b) => b.Length.CompareTo(a.Length));
            _paths.Sort((a, b) => b.Path.Length.CompareTo(a.Path.Length));
            _sorted = true;
        }

        var s = text;
        var newline = s.IndexOfAny(['\r', '\n']);
        if (newline >= 0) s = s[..newline];

        // URLs first: an endpoint keeps its scheme://host while userinfo, path, query and fragment disappear.
        s = UrlPattern().Replace(s, m => EndpointLabel(m.Value) ?? "[url]");
        foreach (var secret in _secrets)
            s = s.Replace(secret, "[redacted]", StringComparison.OrdinalIgnoreCase);
        foreach (var (path, label) in _paths)
            s = s.Replace(path, label, StringComparison.OrdinalIgnoreCase);
        s = DrivePathPattern().Replace(s, "<local-path>");
        s = UncPathPattern().Replace(s, "<local-path>");
        s = UnixPathPattern().Replace(s, "<local-path>");
        s = HomePathPattern().Replace(s, "<local-path>");
        s = CredentialPattern().Replace(s, "$1=[redacted]");
        s = BearerPattern().Replace(s, "Bearer [redacted]");
        s = JwtPattern().Replace(s, "[redacted-token]");
        s = LongHexPattern().Replace(s, "[redacted-hex]");

        return s.Length <= MaxLength ? s : s[..MaxLength] + "...";
    }

    /// <summary>scheme://host of an absolute URL, dropping userinfo, port, path, query and fragment.</summary>
    public static string? EndpointLabel(string? url)
    {
        if (url is not { Length: > 0 } || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.IdnHost.Length == 0)
            return null;
        return $"{uri.Scheme}://{uri.IdnHost}";
    }

    /// <summary>Exception type plus cleaned message; never the stack trace or inner exceptions.</summary>
    public string Describe(Exception ex) => $"{ex.GetType().Name}: {Clean(ex.Message)}";

    [GeneratedRegex(@"\b[a-zA-Z][a-zA-Z0-9+.\-]*://[^\s'""<>()\[\]{}]+", RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"\b[a-zA-Z]:[\\/][^\s'""<>|]*", RegexOptions.CultureInvariant)]
    private static partial Regex DrivePathPattern();

    [GeneratedRegex(@"\\\\[^\s'""<>|\\]+\\[^\s'""<>|]*", RegexOptions.CultureInvariant)]
    private static partial Regex UncPathPattern();

    // Game paths ("/Game", "/Script", plugin mounts) are kept; well-known machine roots are not.
    [GeneratedRegex(@"(?<![\w.~/\-])/(?:home|Users|tmp|var|mnt|opt|root|private|Volumes|media|srv|run|proc|usr|etc|workspace|builds?)/[^\s'""<>]*", RegexOptions.CultureInvariant)]
    private static partial Regex UnixPathPattern();

    [GeneratedRegex(@"(?<![\w])~[\\/][^\s'""<>]*", RegexOptions.CultureInvariant)]
    private static partial Regex HomePathPattern();

    [GeneratedRegex(@"(?i)\b(token|access_token|key|apikey|api_key|secret|password|passwd|sig|signature|auth|code)=[^\s&'""]+", RegexOptions.CultureInvariant)]
    private static partial Regex CredentialPattern();

    [GeneratedRegex(@"(?i)\bbearer\s+\S+", RegexOptions.CultureInvariant)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"\beyJ[\w\-]{5,}\.[\w\-]{5,}\.[\w\-]*", RegexOptions.CultureInvariant)]
    private static partial Regex JwtPattern();

    // AES-256 keys are 64 hex digits; public 32-digit GUIDs stay readable.
    [GeneratedRegex(@"\b(?:0x)?[0-9A-Fa-f]{40,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex LongHexPattern();
}
