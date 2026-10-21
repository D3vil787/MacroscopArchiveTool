using System.Text.Json;
using System.Text.Json.Serialization;

namespace MacroscopArchiveTool.Core.Migration;

public enum RetireStage
{
    /// <summary>Parameters chosen, nothing changed yet.</summary>
    Planned,
    /// <summary>Target volume (VHDX or empty volume) exists and is mounted at a temporary letter.</summary>
    TargetReady,
    /// <summary>Live copy finished while Macroscop kept running.</summary>
    Copied,
    /// <summary>Letters swapped: Macroscop now reads the archive from the target. The old disk is unmounted.</summary>
    Switched,
    /// <summary>The retired disk has been emptied by retention and removed.</summary>
    Finished,
}

public enum TargetKind { Vhdx, Volume }

/// <summary>Persistent state of retiring one archive disk; survives restarts of the tool and of Windows.</summary>
public sealed class RetireState
{
    public string Id { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RetireStage Stage { get; set; }

    public char SourceLetter { get; set; }
    public string? ArchiveLogicDriveId { get; set; }
    public long SourceArchiveBytes { get; set; }
    public string? SourceVolumeId { get; set; }
    /// <summary>Windows UniqueId of the old disk and where it is (e.g. iSCSI LUN), for taking it offline.</summary>
    public string? SourceDiskId { get; set; }
    public string? SourceDiskLocation { get; set; }
    public bool SourceDiskOffline { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TargetKind TargetKind { get; set; }
    /// <summary>Volume that holds the VHDX file (Vhdx) or the empty target volume itself (Volume).</summary>
    public char TargetVolumeLetter { get; set; }
    public string? VhdxPath { get; set; }
    public long VhdxMaxBytes { get; set; }
    public char TempLetter { get; set; }
    public string? TargetVolumeId { get; set; }
    public string TargetLabel { get; set; } = "";

    public CopyOptions Options { get; set; } = new();
    public bool AllowActiveSource { get; set; }

    public DateTime? SourceNewestUtc { get; set; }
    public DateTime? DrainForecastUtc { get; set; }

    public List<string> StoppedServices { get; set; } = new();
    public string? AttachTaskName { get; set; }

    public long BytesCopied { get; set; }
    public List<CopyLoss> Losses { get; set; } = new();
    public List<string> Log { get; set; } = new();

    public string Title => $"Вывод диска {SourceLetter}: → " +
        (TargetKind == TargetKind.Vhdx ? $"виртуальный диск {VhdxPath}" : $"том {TargetVolumeLetter}:");

    private static readonly JsonSerializerOptions Json = new(ToolConfig.JsonOptions);

    public void Save(string dir)
    {
        UpdatedUtc = DateTime.UtcNow;
        if (Log.Count > 3000) Log.RemoveRange(0, Log.Count - 3000);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Id + ".json");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }

    public static RetireState Load(string path)
        => JsonSerializer.Deserialize<RetireState>(File.ReadAllText(path), Json) ?? throw new InvalidDataException(path);

    public static List<RetireState> LoadAll(string dir)
    {
        var list = new List<RetireState>();
        if (!Directory.Exists(dir)) return list;
        foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
        {
            try { list.Add(Load(f)); } catch (Exception) { /* foreign or damaged file */ }
        }
        return list.OrderByDescending(s => s.CreatedUtc).ToList();
    }
}
