using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace MacroscopArchiveTool.Core.Cleanup;

public enum CleanupCategory
{
    /// <summary>Old folders in MacroscopArchive\ConfigsBackups (Macroscop writes one to every disk on each config change).</summary>
    ConfigBackups,
    /// <summary>Tablespace folders, installation and data folder of a PostgreSQL version Macroscop no longer uses.</summary>
    OldPostgres,
    /// <summary>Old session folders of Macroscop logs.</summary>
    MacroscopLogs,
    /// <summary>Leftovers of this tool: update downloads, old reports, files replaced by updates.</summary>
    ToolTemp,
    /// <summary>MacroscopArchive\db\backups — Macroscop's own event database backups (shown only).</summary>
    DbBackups,
    /// <summary>archive_event_* databases of archive disks that no longer exist (shown only).</summary>
    OrphanEventDb,
}

public enum CleanupAction
{
    /// <summary>Move the paths to the quarantine folder of their volume.</summary>
    Quarantine,
    /// <summary>Run the PostgreSQL uninstaller (or remove the stopped service), then quarantine what is left.</summary>
    UninstallPostgres,
    /// <summary>Information only; nothing is changed.</summary>
    ShowOnly,
}

public sealed class CleanupItem
{
    public string Id { get; set; } = "";
    /// <summary>What the item is about (a backup folder of one disk, a PostgreSQL version, …); with the category it makes the id.
    /// A plan made from one scan therefore matches the same group in a later scan even if a file was added meanwhile.</summary>
    public string Key { get; set; } = "";
    [JsonConverter(typeof(JsonStringEnumConverter))] public CleanupCategory Category { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))] public CleanupAction Action { get; set; }
    /// <summary>Short title, e.g. "A: — 67 копий".</summary>
    public string Title { get; set; } = "";
    /// <summary>Why it is safe (or why it is only shown).</summary>
    public string Note { get; set; } = "";
    public List<string> Paths { get; set; } = new();
    public long Bytes { get; set; }
    public int Files { get; set; }
    public DateTime? OldestUtc { get; set; }
    public DateTime? NewestUtc { get; set; }
    /// <summary>Selected by default.</summary>
    public bool Recommended { get; set; }
    /// <summary>For <see cref="CleanupAction.UninstallPostgres"/>: service name and uninstaller.</summary>
    public string? ServiceName { get; set; }
    public string? Uninstaller { get; set; }

    public static string MakeId(CleanupCategory c, IEnumerable<string> keys)
    {
        var text = c + "|" + string.Join("|", keys.Select(k => k.ToLowerInvariant()).OrderBy(k => k, StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
    }
}

public sealed class CleanupOptions
{
    /// <summary>Config backups younger than this are always kept.</summary>
    public int BackupDays { get; set; } = 30;
    /// <summary>At least this many newest config backups stay on every disk.</summary>
    public int BackupsToKeep { get; set; } = 10;
    public int LogDays { get; set; } = 90;
    public int ReportDays { get; set; } = 180;
    /// <summary>Quarantined files are deleted for good after this many days (0 = never automatically).</summary>
    public int QuarantineDays { get; set; } = 14;
    /// <summary>PostgreSQL superuser password for listing event databases (Macroscop installs with the vendor default).</summary>
    public string PostgresPassword { get; set; } = "masterkey";
}

public sealed class QuarantineEntry
{
    public string Original { get; set; } = "";
    public string Stored { get; set; } = "";
    public long Bytes { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))] public CleanupCategory Category { get; set; }
}

/// <summary>What one cleanup run moved to the quarantine folder of one volume.</summary>
public sealed class QuarantineSet
{
    public string SetId { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string Folder { get; set; } = "";
    public List<QuarantineEntry> Entries { get; set; } = new();
    /// <summary>Actions that cannot be undone from quarantine (e.g. an uninstalled PostgreSQL version).</summary>
    public List<string> Notes { get; set; } = new();
    [JsonIgnore] public long Bytes => Entries.Sum(e => e.Bytes);
}

public sealed class CleanupScan
{
    public string ToolVersion { get; set; } = ToolInfo.Version;
    public string MachineName { get; set; } = Environment.MachineName;
    public DateTime ScannedAtUtc { get; set; }
    public CleanupOptions Options { get; set; } = new();
    public List<CleanupItem> Items { get; set; } = new();
    public List<QuarantineSet> Quarantine { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<string> Log { get; set; } = new();
}

/// <summary>Which items to clean, sent to a remote server.</summary>
public sealed class CleanupPlan
{
    public CleanupOptions Options { get; set; } = new();
    public List<string> ItemIds { get; set; } = new();
    /// <summary>For restore/purge: one quarantine run, or (purge) everything older than <see cref="OlderThanDays"/>.</summary>
    public string? SetId { get; set; }
    public int? OlderThanDays { get; set; }
}

public static class CleanupText
{
    public static string Category(CleanupCategory c) => c switch
    {
        CleanupCategory.ConfigBackups => "Старые резервные копии конфигурации",
        CleanupCategory.OldPostgres => "Неиспользуемая версия PostgreSQL",
        CleanupCategory.MacroscopLogs => "Старые журналы Macroscop",
        CleanupCategory.ToolTemp => "Временные файлы MacroscopArchiveTool",
        CleanupCategory.DbBackups => "Резервные копии базы событий (только просмотр)",
        CleanupCategory.OrphanEventDb => "Базы событий отключённых дисков (только просмотр)",
        _ => c.ToString(),
    };
}
