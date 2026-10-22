using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MacroscopArchiveTool.Core.Cleanup;

/// <summary>
/// Finds what Macroscop (and this tool) leave behind and moves selected items to a quarantine folder on the same
/// volume — an instant rename that can be undone. Quarantined files are deleted for good later (after
/// <see cref="CleanupOptions.QuarantineDays"/> or on request). Video, active databases and current settings are never touched:
/// every category has its own conservative rule, and items that cannot be judged safely are only shown.
/// </summary>
public sealed class CleanupEngine
{
    public const string QuarantineFolder = "_MacroscopArchiveTool_quarantine";
    private const string ManifestName = "manifest.json";
    private static readonly Regex BackupName = new(@"^(\d{4}-\d{2}-\d{2} \d{2}\.\d{2}\.\d{2}\.\d{3})$", RegexOptions.Compiled);
    private static readonly Regex PgTablespace = new(@"^PG_(\d+)_\d+$", RegexOptions.Compiled);
    private static readonly EnumerationOptions Deep = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
    private static readonly JsonSerializerOptions Json = new(ToolConfig.JsonOptions) { WriteIndented = true };

    private readonly ICleanupEnvironment _env;
    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    public CleanupEngine(ICleanupEnvironment env) { _env = env; }

    // ================================================================== scan

    public CleanupScan Scan(CleanupOptions o, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var scan = new CleanupScan { ScannedAtUtc = UtcNow(), Options = o };
        void Say(string m) { progress?.Report(m); scan.Log.Add(m); }
        var roots = _env.VolumeRoots();
        var archives = roots.Select(r => Path.Combine(r, ArchiveScanner.ArchiveFolderName)).Where(Directory.Exists).ToList();

        Say("Резервные копии конфигурации…");
        Try(scan, () => ScanBackups(scan, archives, o, ct));
        ct.ThrowIfCancellationRequested();
        Say("PostgreSQL…");
        Try(scan, () => ScanPostgres(scan, archives, o));
        ct.ThrowIfCancellationRequested();
        Say("Журналы Macroscop…");
        Try(scan, () => ScanLogs(scan, o));
        Say("Временные файлы утилиты…");
        Try(scan, () => ScanToolTemp(scan, o));
        scan.Quarantine = ListQuarantine();
        AssignIds(scan);
        Say($"Найдено: {scan.Items.Count(i => i.Action != CleanupAction.ShowOnly)} пунктов, {HtmlReport.Bytes(scan.Items.Where(i => i.Action != CleanupAction.ShowOnly).Sum(i => i.Bytes))}");
        return scan;
    }

    private static void Try(CleanupScan scan, Action a)
    {
        try { a(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { scan.Warnings.Add(ex.Message); }
    }

    private void ScanBackups(CleanupScan scan, List<string> archives, CleanupOptions o, CancellationToken ct)
    {
        var cutoff = UtcNow().AddDays(-o.BackupDays);
        foreach (var archive in archives)
        {
            var dir = Path.Combine(archive, "ConfigsBackups");
            if (!Directory.Exists(dir)) continue;
            var backups = new DirectoryInfo(dir).EnumerateDirectories()
                .Select(d => (Dir: d, When: ParseBackupTime(d.Name)))
                .Where(x => x.When != null)
                .OrderByDescending(x => x.When)
                .ToList();
            var old = backups.Skip(Math.Max(1, o.BackupsToKeep)).Where(x => x.When!.Value.ToUniversalTime() < cutoff).ToList();
            if (old.Count == 0) continue;
            long bytes = 0; int files = 0;
            foreach (var b in old)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var f in b.Dir.EnumerateFiles("*", Deep)) { bytes += f.Length; files++; }
            }
            var vol = VolumeName(archive);
            scan.Items.Add(new CleanupItem
            {
                Category = CleanupCategory.ConfigBackups, Action = CleanupAction.Quarantine, Recommended = true, Key = dir,
                Title = $"{vol} — {old.Count} копий из {backups.Count}",
                Note = $"Остаются {backups.Count - old.Count} самых новых копий (не меньше {o.BackupsToKeep} и все моложе {o.BackupDays} дн.).",
                Paths = old.Select(b => b.Dir.FullName).ToList(), Bytes = bytes, Files = files,
                OldestUtc = old.Min(b => b.When)!.Value.ToUniversalTime(), NewestUtc = old.Max(b => b.When)!.Value.ToUniversalTime(),
            });
        }
    }

    public static DateTime? ParseBackupTime(string name)
        => BackupName.IsMatch(name) && DateTime.TryParseExact(name, "yyyy-MM-dd HH.mm.ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t) ? t : null;

    private void ScanPostgres(CleanupScan scan, List<string> archives, CleanupOptions o)
    {
        var services = _env.PostgresServices();
        var active = services.Where(s => s.Running).OrderByDescending(s => s.Version).FirstOrDefault();

        // Event database backups made by Macroscop: shown only.
        foreach (var archive in archives)
        {
            var b = Path.Combine(archive, "db", "backups");
            if (!Directory.Exists(b)) continue;
            var (bytes, files, oldest, newest) = Measure(b);
            if (files == 0) continue;
            scan.Items.Add(new CleanupItem
            {
                Category = CleanupCategory.DbBackups, Action = CleanupAction.ShowOnly,
                Title = $"{b} — {files} файлов", Note = "Резервные копии базы событий делает сам Macroscop; программа их не трогает.",
                Paths = { b }, Bytes = bytes, Files = files, OldestUtc = oldest, NewestUtc = newest,
            });
        }

        if (active == null)
        {
            scan.Warnings.Add("Не найдена работающая служба PostgreSQL — остатки старых версий не проверялись.");
            return;
        }
        bool InUse(int version) => services.Any(s => s.Version == version && s.Running);

        // 1) Tablespace folders of older versions next to the active one on every archive disk.
        var byVersion = new SortedDictionary<int, List<string>>();
        foreach (var archive in archives)
        {
            var db = Path.Combine(archive, "db");
            if (!Directory.Exists(db)) continue;
            var dirs = new DirectoryInfo(db).EnumerateDirectories().Select(d => (d, m: PgTablespace.Match(d.Name))).Where(x => x.m.Success).ToList();
            bool hasActive = dirs.Any(x => int.Parse(x.m.Groups[1].Value) == active.Version);
            foreach (var (d, m) in dirs)
            {
                int v = int.Parse(m.Groups[1].Value);
                if (v >= active.Version || InUse(v) || !hasActive) continue;
                if (!byVersion.TryGetValue(v, out var list)) byVersion[v] = list = new();
                list.Add(d.FullName);
            }
        }
        foreach (var (v, paths) in byVersion)
        {
            long bytes = 0; int files = 0; DateTime? newest = null;
            foreach (var p in paths) { var (b, f, _, n) = Measure(p); bytes += b; files += f; newest = ArchiveScanner.Max(newest, n); }
            scan.Items.Add(new CleanupItem
            {
                Category = CleanupCategory.OldPostgres, Action = CleanupAction.Quarantine, Recommended = true, Key = "tablespaces-" + v,
                Title = $"PostgreSQL {v}: табличные пространства на {paths.Count} дисках",
                Note = $"Работает PostgreSQL {active.Version}; служба версии {v} не запущена, её папки PG_{v}_… рядом с действующими PG_{active.Version}_… не используются.",
                Paths = paths, Bytes = bytes, Files = files, NewestUtc = newest,
            });
        }

        // 2) Installation and data folder of an older version whose service is stopped (or gone).
        var candidates = services.Where(s => s.Version < active.Version && !s.Running).ToList();
        if (Directory.Exists(_env.PostgresRoot))
            foreach (var d in Directory.EnumerateDirectories(_env.PostgresRoot))
                if (int.TryParse(Path.GetFileName(d).Split('.')[0], out var v) && v < active.Version && !InUse(v)
                    && !candidates.Any(c => string.Equals(c.InstallDir?.TrimEnd('\\', '/'), d.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)))
                    candidates.Add(new PostgresService("", v, false, d, null));
        foreach (var s in candidates)
        {
            var paths = new List<string>();
            if (s.DataDir != null && Directory.Exists(s.DataDir) && !Overlaps(s.DataDir, active)) paths.Add(s.DataDir);
            string? uninstaller = s.InstallDir == null ? null : Path.Combine(s.InstallDir, "uninstall-postgresql.exe");
            if (uninstaller != null && !File.Exists(uninstaller)) uninstaller = null;
            if (s.InstallDir != null && Directory.Exists(s.InstallDir) && !Overlaps(s.InstallDir, active)) paths.Add(s.InstallDir);
            if (paths.Count == 0 && s.Name.Length == 0) continue;
            long bytes = 0; int files = 0; DateTime? newest = null;
            foreach (var p in paths) { var (b, f, _, n) = Measure(p); bytes += b; files += f; newest = ArchiveScanner.Max(newest, n); }
            scan.Items.Add(new CleanupItem
            {
                Category = CleanupCategory.OldPostgres, Action = CleanupAction.UninstallPostgres, Recommended = true, Key = "install-" + s.Version,
                Title = $"PostgreSQL {s.Version}: программа{(s.DataDir != null ? " и данные" : "")}" + (s.Name.Length > 0 ? $" (служба {s.Name} остановлена)" : " (без службы)"),
                Note = (uninstaller != null ? "Будет запущен штатный деинсталлятор PostgreSQL, " : s.Name.Length > 0 ? "Служба будет удалена, " : "") +
                       "папки программы и данных переносятся в карантин. Удаление программы нельзя отменить из карантина, данные — можно вернуть.",
                Paths = paths, Bytes = bytes, Files = files, NewestUtc = newest,
                ServiceName = s.Name.Length > 0 ? s.Name : null, Uninstaller = uninstaller,
            });
        }

        // 3) Event databases of archive disks that are not connected any more (shown only).
        var dbs = _env.EventDatabases(active, o.PostgresPassword, out var err);
        if (dbs == null) { scan.Warnings.Add($"Список баз событий недоступен: {err}"); return; }
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var archive in archives)
            if (ReadLogicDriveId(archive) is { } id) known.Add(Hex(id));
        foreach (var (name, bytes) in dbs)
        {
            var hex = Hex(name.Substring(Math.Min(name.Length, "archive_event".Length)));
            if (hex.Length < 32 || known.Contains(hex)) continue;
            scan.Items.Add(new CleanupItem
            {
                Category = CleanupCategory.OrphanEventDb, Action = CleanupAction.ShowOnly,
                Title = $"{name} — {HtmlReport.Bytes(bytes)}",
                Note = "Ни на одном подключённом диске нет архива с этим идентификатором. Если диск отключён навсегда, базу можно удалить средствами PostgreSQL.",
                Paths = { name }, Bytes = bytes,
            });
        }
    }

    private void ScanLogs(CleanupScan scan, CleanupOptions o)
    {
        if (!Directory.Exists(_env.MacroscopDataDir)) return;
        var cutoff = UtcNow().AddDays(-o.LogDays);
        foreach (var logs in Directory.EnumerateDirectories(_env.MacroscopDataDir, "Logs_*"))
        {
            var entries = new DirectoryInfo(logs).EnumerateFileSystemInfos()
                .Select(e => (Entry: e, Newest: e is DirectoryInfo d ? Measure(d.FullName).Newest ?? d.LastWriteTimeUtc : e.LastWriteTimeUtc))
                .OrderByDescending(x => x.Newest).ToList();
            // Keep the current session and the one with the newest ServerConfiguration.log (camera names are read from it).
            var keep = new HashSet<string>(entries.Take(1).Select(x => x.Entry.FullName), StringComparer.OrdinalIgnoreCase);
            var cfg = new DirectoryInfo(logs).EnumerateFiles("ServerConfiguration.log", Deep).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (cfg != null) foreach (var x in entries) if (cfg.FullName.StartsWith(x.Entry.FullName, StringComparison.OrdinalIgnoreCase)) keep.Add(x.Entry.FullName);
            var old = entries.Where(x => !keep.Contains(x.Entry.FullName) && x.Newest < cutoff).ToList();
            if (old.Count == 0) continue;
            long bytes = 0; int files = 0;
            foreach (var x in old) { var (b, f, _, _) = x.Entry is DirectoryInfo ? Measure(x.Entry.FullName) : (((FileInfo)x.Entry).Length, 1, null, null); bytes += b; files += f; }
            scan.Items.Add(new CleanupItem
            {
                Category = CleanupCategory.MacroscopLogs, Action = CleanupAction.Quarantine, Recommended = true, Key = logs,
                Title = $"{Path.GetFileName(logs)} — {old.Count} старых сеансов",
                Note = $"Журналы старше {o.LogDays} дн.; текущий сеанс и последний ServerConfiguration.log остаются.",
                Paths = old.Select(x => x.Entry.FullName).ToList(), Bytes = bytes, Files = files,
                OldestUtc = old.Min(x => x.Newest), NewestUtc = old.Max(x => x.Newest),
            });
        }
    }

    private void ScanToolTemp(CleanupScan scan, CleanupOptions o)
    {
        var paths = new List<string>();
        if (Directory.Exists(_env.TempDir))
            paths.AddRange(Directory.EnumerateDirectories(_env.TempDir, "MacroscopArchiveTool_update_*"));
        if (Directory.Exists(_env.AppDir))
            paths.AddRange(Directory.EnumerateFiles(_env.AppDir, "*.old", SearchOption.TopDirectoryOnly));
        var reports = Path.Combine(_env.AppDir, "reports");
        if (Directory.Exists(reports))
        {
            var cutoff = UtcNow().AddDays(-o.ReportDays);
            paths.AddRange(new DirectoryInfo(reports).EnumerateFiles().Where(f => f.LastWriteTimeUtc < cutoff).Select(f => f.FullName));
        }
        if (paths.Count == 0) return;
        long bytes = 0; int files = 0;
        foreach (var p in paths) { var (b, f, _, _) = Directory.Exists(p) ? Measure(p) : (new FileInfo(p).Length, 1, null, null); bytes += b; files += f; }
        scan.Items.Add(new CleanupItem
        {
            Category = CleanupCategory.ToolTemp, Action = CleanupAction.Quarantine, Recommended = true, Key = "tool-temp",
            Title = $"{paths.Count} файлов и папок",
            Note = $"Загрузки обновлений, файлы прежних версий программы, отчёты старше {o.ReportDays} дн.",
            Paths = paths, Bytes = bytes, Files = files,
        });
    }

    // ================================================================== apply

    /// <summary>Re-scans with the same options and cleans the requested items that are still found (so nothing stale is touched).</summary>
    public CleanupScan Apply(CleanupPlan plan, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var fresh = Scan(plan.Options, null, ct);
        void Say(string m) { progress?.Report(m); fresh.Log.Add(m); }
        var wanted = new HashSet<string>(plan.ItemIds);
        var items = fresh.Items.Where(i => wanted.Contains(i.Id) && i.Action != CleanupAction.ShowOnly).ToList();
        var missing = wanted.Count - items.Count;
        if (missing > 0) Say($"{missing} пунктов уже не найдено (изменились с момента проверки) — пропущены.");

        var setId = UtcNow().ToLocalTime().ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var sets = new Dictionary<string, QuarantineSet>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            Say($"{CleanupText.Category(item.Category)}: {item.Title}");
            try
            {
                if (item.Action == CleanupAction.UninstallPostgres)
                {
                    if (item.Uninstaller != null) _env.RunUninstaller(item.Uninstaller, Say);
                    else if (item.ServiceName != null) _env.DeleteService(item.ServiceName, Say);
                    Set(sets, VolumeOf(item.Paths.FirstOrDefault() ?? _env.PostgresRoot), setId).Notes.Add($"{item.Title}: " + (item.Uninstaller != null ? "удалена деинсталлятором" : item.ServiceName != null ? $"служба {item.ServiceName} удалена" : "папки перенесены"));
                }
                foreach (var path in item.Paths.Where(p => Directory.Exists(p) || File.Exists(p)))
                {
                    var vol = VolumeOf(path);
                    MoveToQuarantine(Set(sets, vol, setId), vol, path, item.Category);
                }
            }
            catch (Exception ex)
            {
                fresh.Warnings.Add($"{item.Title}: {ex.Message}");
                Say("  ОШИБКА: " + ex.Message);
            }
            foreach (var s in sets.Values) SaveManifest(s);
        }
        var moved = sets.Values.Sum(s => s.Bytes);
        Say($"В карантин перенесено {HtmlReport.Bytes(moved)}. Вернуть или удалить окончательно — в разделе «Карантин».");
        fresh.Items.RemoveAll(i => items.Contains(i));
        fresh.Quarantine = ListQuarantine();
        return fresh;
    }

    private QuarantineSet Set(Dictionary<string, QuarantineSet> sets, string volumeRoot, string setId)
    {
        if (!sets.TryGetValue(volumeRoot, out var s))
        {
            var folder = Path.Combine(volumeRoot, QuarantineFolder, setId);
            Directory.CreateDirectory(folder);
            sets[volumeRoot] = s = new QuarantineSet { SetId = setId, CreatedUtc = UtcNow(), Folder = folder };
        }
        return s;
    }

    private static void MoveToQuarantine(QuarantineSet set, string volumeRoot, string path, CleanupCategory category)
    {
        var rel = Path.GetRelativePath(volumeRoot, path);
        var stored = Path.Combine(set.Folder, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(stored)!);
        long bytes;
        if (Directory.Exists(path)) { bytes = Measure(path).Bytes; Directory.Move(path, stored); }
        else { bytes = new FileInfo(path).Length; File.Move(path, stored); }
        set.Entries.Add(new QuarantineEntry { Original = path, Stored = stored, Bytes = bytes, Category = category });
    }

    // ================================================================== quarantine

    public List<QuarantineSet> ListQuarantine()
    {
        var list = new List<QuarantineSet>();
        foreach (var root in _env.VolumeRoots())
        {
            var q = Path.Combine(root, QuarantineFolder);
            if (!Directory.Exists(q)) continue;
            foreach (var d in Directory.EnumerateDirectories(q))
            {
                var m = Path.Combine(d, ManifestName);
                try
                {
                    var set = File.Exists(m) ? JsonSerializer.Deserialize<QuarantineSet>(File.ReadAllText(m), Json) : null;
                    if (set == null) continue;
                    set.Folder = d;
                    list.Add(set);
                }
                catch (Exception) { /* damaged manifest: left for manual inspection */ }
            }
        }
        return list.OrderByDescending(s => s.CreatedUtc).ToList();
    }

    /// <summary>Moves everything of a cleanup run back where it was (paths that exist again are skipped).</summary>
    public List<string> Restore(string setId, IProgress<string>? progress = null)
    {
        var problems = new List<string>();
        foreach (var set in ListQuarantine().Where(s => s.SetId == setId))
        {
            foreach (var e in set.Entries.ToList())
            {
                try
                {
                    if (Directory.Exists(e.Original) || File.Exists(e.Original)) { problems.Add($"{e.Original}: уже существует, не восстановлено"); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(e.Original)!);
                    if (Directory.Exists(e.Stored)) Directory.Move(e.Stored, e.Original);
                    else if (File.Exists(e.Stored)) File.Move(e.Stored, e.Original);
                    set.Entries.Remove(e);
                    progress?.Report("Восстановлено: " + e.Original);
                }
                catch (Exception ex) { problems.Add($"{e.Original}: {ex.Message}"); }
            }
            problems.AddRange(set.Notes.Select(n => $"Не восстанавливается из карантина: {n}"));
            if (set.Entries.Count == 0) TryDelete(set.Folder); else SaveManifest(set);
        }
        return problems;
    }

    /// <summary>Deletes a cleanup run for good (or every run older than <paramref name="olderThanDays"/>).</summary>
    public long Purge(string? setId, int? olderThanDays = null, IProgress<string>? progress = null)
    {
        long freed = 0;
        var cutoff = olderThanDays is int d ? UtcNow().AddDays(-d) : DateTime.MaxValue;
        foreach (var set in ListQuarantine().Where(s => setId != null ? s.SetId == setId : s.CreatedUtc < cutoff))
        {
            freed += set.Bytes;
            TryDelete(set.Folder);
            progress?.Report($"Удалено окончательно: {set.SetId} на {Path.GetPathRoot(set.Folder)} ({HtmlReport.Bytes(set.Bytes)})");
        }
        return freed;
    }

    private static void SaveManifest(QuarantineSet s)
    {
        Directory.CreateDirectory(s.Folder);
        File.WriteAllText(Path.Combine(s.Folder, ManifestName), JsonSerializer.Serialize(s, Json));
    }

    private static void TryDelete(string folder)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(folder, true);
            var parent = Path.GetDirectoryName(folder);
            if (parent != null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
        }
        catch (Exception) { }
    }

    // ================================================================== helpers

    private static (long Bytes, int Files, DateTime? Oldest, DateTime? Newest) Measure(string dir)
    {
        long b = 0; int n = 0; DateTime? oldest = null, newest = null;
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", Deep))
        {
            b += f.Length; n++;
            var t = f.LastWriteTimeUtc;
            oldest = ArchiveScanner.Min(oldest, t); newest = ArchiveScanner.Max(newest, t);
        }
        return (b, n, oldest, newest);
    }

    /// <summary>Root of the volume that holds <paramref name="path"/> (quarantine must stay on it so moving is a rename).</summary>
    private string VolumeOf(string path)
    {
        var full = Path.GetFullPath(path);
        return _env.VolumeRoots().Where(r => full.StartsWith(r, StringComparison.OrdinalIgnoreCase)).OrderByDescending(r => r.Length).FirstOrDefault()
               ?? Path.GetPathRoot(full) ?? full;
    }

    private static string VolumeName(string archive) => ArchiveScanner.DriveName(archive);

    /// <summary>True when <paramref name="path"/> is, contains or lies inside a folder of the active PostgreSQL.</summary>
    private static bool Overlaps(string path, PostgresService active)
    {
        static string N(string p) => Path.GetFullPath(p).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var a = N(path);
        foreach (var other in new[] { active.DataDir, active.InstallDir })
        {
            if (other == null) continue;
            var b = N(other);
            if (a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string Hex(string s) => new string(s.Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();

    private static string? ReadLogicDriveId(string archive)
    {
        try
        {
            var f = Path.Combine(archive, "ArchiveLogicDrive.xml");
            if (!File.Exists(f)) return null;
            var text = File.ReadAllText(f);
            var m = Regex.Match(text, @"<ArchiveLogicDriveId>\s*([0-9a-fA-F-]{32,36})\s*</ArchiveLogicDriveId>");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Assigns stable ids (category + paths), so a plan made from one scan matches the same items in a later scan.</summary>
    public static void AssignIds(CleanupScan scan)
    {
        foreach (var i in scan.Items) i.Id = CleanupItem.MakeId(i.Category, new[] { i.Action.ToString(), i.Key.Length > 0 ? i.Key : string.Join("|", i.Paths) });
    }
}
