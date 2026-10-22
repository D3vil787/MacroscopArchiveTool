namespace MacroscopArchiveTool.Core;

/// <summary>Derived metrics: depth, write rate, bitrate, share, heavy cameras, retention overruns.</summary>
public static class ReportAnalyzer
{
    /// <summary>Records older than retention + this slack are reported as an overrun.</summary>
    public const double RetentionSlackDays = 1.0;

    public static void Analyze(ScanReport report, double heavyFactor)
    {
        var now = report.ScannedAtUtc;
        report.HeavyFactor = heavyFactor;
        foreach (var c in report.Cameras)
        {
            c.DepthDays = c.OldestUtc.HasValue ? Math.Max(0, (now - c.OldestUtc.Value).TotalDays) : 0;
            // Rate over the recorded span; at least one hour to avoid dividing by ~0 for brand-new cameras.
            var span = Math.Max(c.DepthDays, 1.0 / 24);
            c.BytesPerDay = c.Bytes / span;
            c.AvgMbitPerSec = c.BytesPerDay * 8 / 86400 / 1_000_000;
            c.ExceedsRetention = c.RetentionDays is > 0 && c.DepthDays > c.RetentionDays.Value + RetentionSlackDays;
        }

        var totalRate = report.Cameras.Sum(c => c.BytesPerDay);
        foreach (var c in report.Cameras)
            c.SharePercent = totalRate > 0 ? c.BytesPerDay / totalRate * 100 : 0;

        // "Heavy" = writes heavyFactor x more than the median camera with at least a day of history.
        var rates = report.Cameras.Where(c => c.DepthDays >= 1).Select(c => c.BytesPerDay).OrderBy(r => r).ToList();
        var median = Median(rates);
        foreach (var c in report.Cameras)
            c.IsHeavy = median > 0 && c.DepthDays >= 1 && c.BytesPerDay >= heavyFactor * median;

        report.Cameras = report.Cameras.OrderByDescending(c => c.BytesPerDay).ToList();
        foreach (var c in report.Cameras) Classify(report, c, now);

        // Disks that no longer receive video (reserve, unused, or simply idle): when does the last recording expire?
        var retention = report.Cameras.ToDictionary(c => c.Id, c => c.RetentionDays ?? report.DefaultRetentionDays, StringComparer.OrdinalIgnoreCase);
        foreach (var d in report.Drives)
        {
            d.DrainForecastUtc = null;
            if (d.IsActive || d.DayCameraBytes.Count == 0) continue;
            DateTime? last = null;
            foreach (var (day, cams) in d.DayCameraBytes)
            {
                if (!DateTime.TryParseExact(day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var t)) continue;
                foreach (var cam in cams.Keys)
                {
                    var expires = t.AddDays(retention.GetValueOrDefault(cam, report.DefaultRetentionDays > 0 ? report.DefaultRetentionDays : 90) + 1).ToUniversalTime();
                    if (last == null || expires > last) last = expires;
                }
            }
            d.DrainForecastUtc = last;
        }
    }

    /// <summary>Lost-camera checks: removed from the configuration, switched off, not recording, nothing recorded at all.</summary>
    private static void Classify(ScanReport r, CameraStat c, DateTime now)
    {
        c.SilentDays = c.NewestUtc is DateTime n ? Math.Max(0, (now - n).TotalDays) : null;
        c.ArchiveUntilUtc = c.NewestUtc is DateTime nn && c.RetentionDays is > 0 ? nn.AddDays(c.RetentionDays.Value) : null;
        var limit = c.Settings?.ArchiveMode == 2 ? r.SilentDaysContinuous : r.SilentDaysMotion;
        c.LostKind =
            c.InConfig == false ? "deleted" :
            c.Settings?.Disabled == true ? "disabled" :
            c.Settings?.ArchiveEnabled == false ? "recording-off" :
            c.Bytes == 0 && c.BoundHere == true ? "no-archive" :
            c.SilentDays > limit ? (c.BoundHere == false && c.InConfig == true ? "moved" : "silent") :
            null;
    }

    /// <summary>Kinds that mean "a camera occupies a licence or disk space for nothing" — shown and alarmed.</summary>
    public static bool IsProblem(string? kind) => kind is "silent" or "no-archive" or "deleted" or "disabled";

    public static double Median(IReadOnlyList<double> sorted)
    {
        if (sorted.Count == 0) return 0;
        int m = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[m] : (sorted[m - 1] + sorted[m]) / 2;
    }

    /// <summary>Total bytes written per day by all cameras of the report.</summary>
    public static double DailyRate(ScanReport r) => r.Cameras.Sum(c => c.BytesPerDay);

    /// <summary>Free space on drives that received video during the last 24 h.</summary>
    public static long ActiveFreeBytes(ScanReport r) => r.Drives.Where(d => d.IsActive).Sum(d => d.FreeBytes);

    /// <summary>
    /// Rough number of days until active drives are full at the current write rate.
    /// Ignores Macroscop's per-disk "min free space" and "max archive size" limits.
    /// </summary>
    public static double? HeadroomDays(ScanReport r)
    {
        var rate = DailyRate(r);
        return rate > 0 ? ActiveFreeBytes(r) / rate : null;
    }
}
