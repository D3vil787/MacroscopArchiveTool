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
    }

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
