namespace Cue4ParseMcp.Core;

/// <summary>Output paths and atomic writes for single-file asset exports.</summary>
public static class ExportFiles
{
    public static string AssetPath(GameSession session, string input, string outputDir, string extension)
    {
        var (package, objectName) = session.ParsePath(input);
        var relative = objectName is null ? package : package + "." + objectName;
        return WithinDirectory(outputDir, relative + "." + extension.TrimStart('.'));
    }

    public static string WithinDirectory(string outputDir, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(segment =>
                segment is "" or "." or ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new ArgumentException("Export path contains an invalid or unsafe path component.", nameof(relative));
        var root = Path.GetFullPath(outputDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var destination = Path.GetFullPath(Path.Combine(root, relative));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!destination.StartsWith(root, comparison))
            throw new ArgumentException("Export path escapes the output directory.", nameof(relative));
        return destination;
    }

    public static async Task WriteAsync(string destination, Func<Stream, CancellationToken, Task> write, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        destination = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await write(stream, ct);
                await stream.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            // This invocation owns this exact temporary path; never remove another export's output.
            File.Delete(temporary);
        }
    }
}
