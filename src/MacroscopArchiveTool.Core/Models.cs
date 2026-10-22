namespace MacroscopArchiveTool.Core;

/// <summary>Result of scanning the Macroscop archive on one server.</summary>
public sealed class ScanReport
{
    public string ToolVersion { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string MachineName { get; set; } = "";
    public DateTime ScannedAtUtc { get; set; }
    public double ScanSeconds { get; set; }
    public int DefaultRetentionDays { get; set; }
    public double HeavyFactor { get; set; }
    /// <summary>Camera list of the Macroscop configuration was read (so "not in configuration" is meaningful).</summary>
    public bool ConfigRead { get; set; }
    /// <summary>Channel-to-server bindings were read from ServerConfiguration.log.</summary>
    public bool BindingsRead { get; set; }
    /// <summary>Days without new recordings after which a camera counts as silent (continuous / motion recording).</summary>
    public double SilentDaysContinuous { get; set; } = 1;
    public double SilentDaysMotion { get; set; } = 7;
    /// <summary>Where camera retention came from, e.g. "конфигурация Macroscop (копия от …)".</summary>
    public string RetentionSource { get; set; } = "";
    public List<DriveStat> Drives { get; set; } = new();
    public List<CameraStat> Cameras { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

/// <summary>One logical drive that carries a MacroscopArchive folder.</summary>
public sealed class DriveStat
{
    /// <summary>Display name, e.g. "D:".</summary>
    public string Name { get; set; } = "";
    /// <summary>Full path of the MacroscopArchive folder.</summary>
    public string ArchiveRoot { get; set; } = "";
    public string VolumeLabel { get; set; } = "";
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }

    /// <summary>Video and index files under media\&lt;camera&gt;.</summary>
    public long MediaBytes { get; set; }
    /// <summary>PostgreSQL tablespace files under db.</summary>
    public long DbBytes { get; set; }
    /// <summary>Everything else inside MacroscopArchive (descriptors, config backups).</summary>
    public long OtherArchiveBytes { get; set; }

    public DateTime? OldestUtc { get; set; }
    public DateTime? NewestUtc { get; set; }
    public int FragmentCount { get; set; }
    public int CameraCount { get; set; }

    /// <summary>
    /// Video blob bytes by recording day (server local date "yyyy-MM-dd") and camera id —
    /// for the "drive → days → cameras" drill-down in the report.
    /// </summary>
    public Dictionary<string, Dictionary<string, long>> DayCameraBytes { get; set; } = new();

    /// <summary>Role in Macroscop ("Только архив", "Резервный архив", …) and its limits, from Server.SrvConf.</summary>
    public string? Role { get; set; }
    public long? MinFreeBytes { get; set; }
    public long? MaxArchiveBytes { get; set; }
    /// <summary>For reserve and unused disks with video: when the last recording expires by camera retention.</summary>
    public DateTime? DrainForecastUtc { get; set; }

    /// <summary>True when video was written to this drive during the last 24 hours.</summary>
    public bool IsActive { get; set; }

    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
    public long ArchiveBytes => MediaBytes + DbBytes + OtherArchiveBytes;
    /// <summary>Space used on the volume by anything outside MacroscopArchive.</summary>
    public long NonArchiveBytes => TotalBytes > 0 ? Math.Max(0, UsedBytes - ArchiveBytes) : 0;
}

/// <summary>Archive statistics of one camera across all drives of a server.</summary>
public sealed class CameraStat
{
    /// <summary>Camera GUID as used for the media folder name (32 lowercase hex chars).</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public long Bytes { get; set; }
    public DateTime? OldestUtc { get; set; }
    public DateTime? NewestUtc { get; set; }
    public int FragmentCount { get; set; }
    public Dictionary<string, long> BytesByDrive { get; set; } = new();
    /// <summary>Effective retention used for the overrun check (Macroscop "max" depth, or mat.json).</summary>
    public int? RetentionDays { get; set; }
    /// <summary>Macroscop "min" depth, when known.</summary>
    public double? RetentionMinDays { get; set; }
    /// <summary>"macroscop", "mat.json" or "default".</summary>
    public string RetentionSource { get; set; } = "";
    /// <summary>Recording settings from the Macroscop configuration, when available.</summary>
    public CameraSettings? Settings { get; set; }

    // Derived values, filled by ReportAnalyzer.
    public double DepthDays { get; set; }
    public double BytesPerDay { get; set; }
    public double AvgMbitPerSec { get; set; }
    public double SharePercent { get; set; }
    public bool IsHeavy { get; set; }
    public bool ExceedsRetention { get; set; }

    /// <summary>In the Macroscop camera list (null = unknown).</summary>
    public bool? InConfig { get; set; }
    /// <summary>Bound to the scanned server (null = unknown).</summary>
    public bool? BoundHere { get; set; }
    /// <summary>Days since the last recording (null when there is none).</summary>
    public double? SilentDays { get; set; }
    /// <summary>When the newest recording expires by retention.</summary>
    public DateTime? ArchiveUntilUtc { get; set; }
    /// <summary>
    /// Why the camera looks lost: "silent" (stopped recording), "no-archive" (nothing recorded within retention),
    /// "deleted" (removed from the configuration, archive left), "disabled" (switched off in Macroscop),
    /// "moved" (bound to another server now), "recording-off" (archive recording disabled); null = fine.
    /// </summary>
    public string? LostKind { get; set; }
}
