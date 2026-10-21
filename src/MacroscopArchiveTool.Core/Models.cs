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
}
