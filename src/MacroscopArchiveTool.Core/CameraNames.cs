using System.Text;
using System.Text.RegularExpressions;

namespace MacroscopArchiveTool.Core;

/// <summary>
/// Resolves camera GUIDs to human names using Macroscop's own ServerConfiguration.log,
/// which lists every channel bound to the server as "channelId: GUID(Name), serverId: ...".
/// </summary>
public sealed class CameraNames
{
    private static readonly Regex ChannelLine = new(
        @"channelId:\s*(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\((?<name>.*?)\),\s*serverId",
        RegexOptions.Compiled);

    private static readonly Regex ServerLine = new(
        @"^\s*id:\s*[0-9a-fA-F-]{36},\s*port:.*?name:\s*(?<name>.+?)(?:\s+isReplicationServer.*)?\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public Dictionary<string, string> ById { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? ServerName { get; private set; }
    public string? SourceFile { get; private set; }

    /// <summary>Normalizes a GUID to the media-folder form: 32 lowercase hex chars without dashes.</summary>
    public static string Normalize(string id) => id.Replace("-", "").Trim().ToLowerInvariant();

    /// <summary>Converts a 32-char folder id back to the dashed GUID form, or null if it is not a GUID.</summary>
    public static string? ToDashedGuid(string id)
        => Guid.TryParseExact(Normalize(id), "N", out var g) ? g.ToString("D") : null;

    public string? Get(string id) => ById.TryGetValue(Normalize(id), out var n) ? n : null;

    /// <summary>Loads names from the newest ServerConfiguration.log found under logsDir.</summary>
    public static CameraNames FromLogsDir(string logsDir)
    {
        var result = new CameraNames();
        if (!Directory.Exists(logsDir)) return result;

        var newest = new DirectoryInfo(logsDir)
            .EnumerateFiles("ServerConfiguration.log", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        if (newest == null) return result;

        result.SourceFile = newest.FullName;
        result.Parse(ReadShared(newest.FullName));
        return result;
    }

    public void Parse(string text)
    {
        foreach (Match m in ChannelLine.Matches(text))
            ById[Normalize(m.Groups["id"].Value)] = m.Groups["name"].Value.Trim();

        var s = ServerLine.Match(text);
        if (s.Success) ServerName = s.Groups["name"].Value.Trim();
    }

    /// <summary>Reads a file that Macroscop may still hold open for writing.</summary>
    internal static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return sr.ReadToEnd();
    }
}
