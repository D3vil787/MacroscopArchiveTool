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
        @"channelId:\s*(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\((?<name>.*?)\),\s*serverId:\s*(?<server>[0-9a-fA-F-]{36})?",
        RegexOptions.Compiled);

    private static readonly Regex ServerLine = new(
        @"^\s*id:\s*(?<id>[0-9a-fA-F-]{36}),\s*port:.*?name:\s*(?<name>.+?)(?:\s+isReplicationServer.*)?\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public Dictionary<string, string> ById { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Server each channel is bound to (normalized ids), from the same log lines.</summary>
    public Dictionary<string, string> ServerOf { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Names from older logs too (ServerConfiguration.log of every session), for cameras deleted since.</summary>
    public Dictionary<string, string> Historical { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the channel is bound to this server according to the newest log.</summary>
    public bool IsBoundHere(string id) => ServerId != null && ServerOf.TryGetValue(Normalize(id), out var s) && s == Normalize(ServerId);
    public string? ServerName { get; private set; }
    public string? ServerId { get; private set; }
    public string? SourceFile { get; private set; }
    /// <summary>When Macroscop wrote the newest channel-binding list to the log (server local time); null = unknown.</summary>
    public DateTime? BindingsTimeLocal { get; private set; }

    private static readonly Regex LogStamp = new(@"^\[(?<t>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)", RegexOptions.Compiled | RegexOptions.Multiline);

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

        // Older sessions remember cameras that were deleted later; newest name wins.
        foreach (var f in new DirectoryInfo(logsDir)
                     .EnumerateFiles("ServerConfiguration.log", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                     .OrderBy(f => f.LastWriteTimeUtc))
        {
            try { foreach (Match m in ChannelLine.Matches(ReadShared(f.FullName))) result.Historical[Normalize(m.Groups["id"].Value)] = m.Groups["name"].Value.Trim(); }
            catch (Exception) { /* locked or unreadable: skip */ }
        }
        return result;
    }

    public void Parse(string text)
    {
        foreach (Match m in ChannelLine.Matches(text))
        {
            var id = Normalize(m.Groups["id"].Value);
            ById[id] = m.Groups["name"].Value.Trim();
            if (m.Groups["server"].Success) ServerOf[id] = Normalize(m.Groups["server"].Value);
        }

        var bindings = text.LastIndexOf("CHANNEL_BINDINGS:", StringComparison.Ordinal);
        if (bindings >= 0)
        {
            Match? stamp = null;
            foreach (Match m in LogStamp.Matches(text)) { if (m.Index > bindings) break; stamp = m; }
            if (stamp != null && DateTime.TryParseExact(stamp.Groups["t"].Value, "yyyy-MM-dd HH:mm:ss",
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t))
                BindingsTimeLocal = t;
        }

        var s = ServerLine.Match(text);
        if (s.Success)
        {
            ServerName = s.Groups["name"].Value.Trim();
            ServerId = s.Groups["id"].Value.Trim();
        }
    }

    /// <summary>Reads a file that Macroscop may still hold open for writing.</summary>
    internal static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return sr.ReadToEnd();
    }
}
