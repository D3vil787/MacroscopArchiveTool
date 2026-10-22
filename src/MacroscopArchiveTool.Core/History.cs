using System.Text.Json;

namespace MacroscopArchiveTool.Core;

/// <summary>Key figures of one scan, for the "how it changes" section of the report.</summary>
public sealed record HistoryPoint(DateTime ScannedAtUtc, long VideoBytes, double DailyBytes, double MaxDepthDays, long ActiveFreeBytes, double? HeadroomDays, int Cameras, int OverRetention);

/// <summary>Earlier scans of the same server, read from the JSON reports kept in the reports folder.</summary>
public static class ReportHistory
{
    public static HistoryPoint Point(ScanReport r) => new(
        r.ScannedAtUtc, r.Drives.Sum(d => d.MediaBytes), ReportAnalyzer.DailyRate(r),
        r.Cameras.Count > 0 ? r.Cameras.Max(c => c.DepthDays) : 0, ReportAnalyzer.ActiveFreeBytes(r), ReportAnalyzer.HeadroomDays(r),
        r.Cameras.Count, r.Cameras.Count(c => c.ExceedsRetention));

    /// <summary>All scans of <paramref name="machineName"/> found in <paramref name="dir"/>, oldest first (one per hour at most).</summary>
    public static List<HistoryPoint> Load(string dir, string machineName, int max = 200)
    {
        var points = new List<HistoryPoint>();
        if (!Directory.Exists(dir)) return points;
        foreach (var f in Directory.EnumerateFiles(dir, "macroscop_*.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(max * 2))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                if (!doc.RootElement.TryGetProperty("machineName", out var m) || !string.Equals(m.GetString(), machineName, StringComparison.OrdinalIgnoreCase)) continue;
                var r = ReportStore.LoadJson(f);
                ReportAnalyzer.Analyze(r, r.HeavyFactor > 0 ? r.HeavyFactor : 2.5);
                points.Add(Point(r));
            }
            catch (Exception) { /* not a scan report */ }
        }
        return points.GroupBy(p => new DateTime(p.ScannedAtUtc.Year, p.ScannedAtUtc.Month, p.ScannedAtUtc.Day, p.ScannedAtUtc.Hour, 0, 0))
                     .Select(g => g.Last()).OrderBy(p => p.ScannedAtUtc).TakeLast(max).ToList();
    }
}

/// <summary>
/// metrics.json for monitoring (Zabbix agent UserParameter reads it): key figures and per-disk values of the last scan.
/// Written to %ProgramData%\MacroscopArchiveTool\metrics.json after every scan.
/// </summary>
public static class MetricsWriter
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MacroscopArchiveTool", "metrics.json");

    public static object Build(ScanReport r)
    {
        static long Unix(DateTime? t) => t is DateTime d ? new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)).ToUnixTimeSeconds() : 0;
        var p = ReportHistory.Point(r);
        return new
        {
            server = r.ServerName,
            machine = r.MachineName,
            tool_version = r.ToolVersion,
            scanned_unix = Unix(r.ScannedAtUtc),
            scan_seconds = r.ScanSeconds,
            video_bytes = p.VideoBytes,
            daily_bytes = (long)p.DailyBytes,
            max_depth_days = Math.Round(p.MaxDepthDays, 1),
            active_free_bytes = p.ActiveFreeBytes,
            headroom_days = p.HeadroomDays is double h ? Math.Round(h, 1) : -1,
            cameras = p.Cameras,
            cameras_over_retention = p.OverRetention,
            cameras_heavy = r.Cameras.Count(c => c.IsHeavy),
            warnings = r.Warnings.Count,
            drives = r.Drives.Select(d => new
            {
                name = d.Name,
                role = d.Role ?? "",
                total_bytes = d.TotalBytes,
                free_bytes = d.FreeBytes,
                used_pct = d.TotalBytes > 0 ? Math.Round((double)d.UsedBytes / d.TotalBytes * 100, 1) : 0,
                video_bytes = d.MediaBytes,
                active = d.IsActive ? 1 : 0,
                last_write_unix = Unix(d.NewestUtc),
                oldest_unix = Unix(d.OldestUtc),
                drain_unix = Unix(d.DrainForecastUtc),
                // An archive disk that should be written to but was not during the last 24 hours.
                expected_active = d.Role is "Только архив" or "Архив + БД" ? 1 : 0,
            }).ToList(),
        };
    }

    public static void Write(ScanReport r, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Build(r), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }
}
