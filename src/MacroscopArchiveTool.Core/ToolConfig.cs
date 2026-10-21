using System.Text.Json;
using System.Text.Json.Serialization;

namespace MacroscopArchiveTool.Core;

/// <summary>Settings read from mat.json next to the executable (all optional).</summary>
public sealed class ToolConfig
{
    /// <summary>Retention used for cameras not listed in CameraRetentionDays.</summary>
    public int DefaultRetentionDays { get; set; } = 90;

    /// <summary>Per-camera retention, keyed by camera name or GUID (case-insensitive).</summary>
    public Dictionary<string, int> CameraRetentionDays { get; set; } = new();

    /// <summary>A camera is "heavy" when it writes this many times more than the median camera.</summary>
    public double HeavyFactor { get; set; } = 2.5;

    /// <summary>Explicit MacroscopArchive folders. Empty = find X:\MacroscopArchive on every drive.</summary>
    public List<string> ArchiveRoots { get; set; } = new();

    /// <summary>Macroscop log folder used to resolve camera names. Null = default location.</summary>
    public string? LogsDir { get; set; }

    /// <summary>How many drives are scanned in parallel.</summary>
    public int ScanThreads { get; set; } = 4;

    public const string DefaultLogsDir = @"C:\ProgramData\Macroscop\Logs_Macroscop";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static ToolConfig Load(string path)
    {
        var cfg = JsonSerializer.Deserialize<ToolConfig>(File.ReadAllText(path), JsonOptions) ?? new ToolConfig();
        cfg.CameraRetentionDays = new Dictionary<string, int>(cfg.CameraRetentionDays, StringComparer.OrdinalIgnoreCase);
        return cfg;
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));

    /// <summary>Retention for a camera: explicit by GUID, then by name, then the default.</summary>
    public int RetentionFor(string cameraId, string cameraName)
    {
        var map = new Dictionary<string, int>(CameraRetentionDays, StringComparer.OrdinalIgnoreCase);
        if (map.TryGetValue(cameraId, out var d)) return d;
        var dashed = CameraNames.ToDashedGuid(cameraId);
        if (dashed != null && map.TryGetValue(dashed, out d)) return d;
        if (!string.IsNullOrWhiteSpace(cameraName) && map.TryGetValue(cameraName.Trim(), out d)) return d;
        return DefaultRetentionDays;
    }
}
