using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace MacroscopArchiveTool.Core;

/// <summary>
/// Read-only scanner of the Macroscop archive layout:
/// &lt;drive&gt;\MacroscopArchive\media\&lt;cameraGuid&gt;\...\&lt;fragment&gt;\&lt;blob&gt; (+ .fidx / .idx index files),
/// &lt;drive&gt;\MacroscopArchive\db (PostgreSQL tablespace), plus descriptors and config backups.
/// Never opens video data, never writes anything.
/// </summary>
public sealed class ArchiveScanner
{
    public const string ArchiveFolderName = "MacroscopArchive";

    /// <summary>Files at least this large are video blobs; their timestamps define the recorded time range.</summary>
    public long BlobThresholdBytes { get; init; } = 1L * 1024 * 1024;

    /// <summary>Where closed fragment folders are remembered between scans; null = always read everything.</summary>
    public string? CacheDir { get; init; }

    /// <summary>The newest fragment folders of each parent are always re-read, even if cached (a quiet camera may append to them).</summary>
    private const int AlwaysRereadNewest = 3;

    private sealed class RootStats { public int DirsRead, DirsCached; public long Files; }

    private static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    private static readonly EnumerationOptions Flat = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    /// <summary>Finds X:\MacroscopArchive on every ready local/network drive.</summary>
    public static List<string> FindArchiveRoots()
    {
        var roots = new List<string>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                if (d.DriveType is not (DriveType.Fixed or DriveType.Network or DriveType.Removable)) continue;
                var p = Path.Combine(d.RootDirectory.FullName, ArchiveFolderName);
                if (Directory.Exists(p)) roots.Add(p);
            }
            catch (Exception) { /* drive vanished or access denied: skip */ }
        }
        return roots;
    }

    public ScanReport Scan(IReadOnlyList<string> archiveRoots, CameraNames names, ToolConfig config,
                           IProgress<string>? progress = null, CancellationToken ct = default,
                           RetentionSettings? macroscopRetention = null)
    {
        var sw = Stopwatch.StartNew();
        var report = new ScanReport
        {
            ToolVersion = ToolInfo.Version,
            MachineName = Environment.MachineName,
            ServerName = names.ServerName ?? Environment.MachineName,
            ScannedAtUtc = DateTime.UtcNow,
            DefaultRetentionDays = config.DefaultRetentionDays,
        };
        if (names.SourceFile == null)
            report.Warnings.Add("Имена камер не найдены (нет ServerConfiguration.log) — в отчёте будут GUID.");

        var partials = new ConcurrentBag<(DriveStat drive, Dictionary<string, CameraStat> cams, List<string> warnings)>();
        var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, config.ScanThreads), CancellationToken = ct };

        Parallel.ForEach(archiveRoots, po, root =>
        {
            progress?.Report($"Сканирую {root} ...");
            var t = Stopwatch.StartNew();
            var stats = new RootStats();
            partials.Add(ScanRoot(root, report.ScannedAtUtc, stats, ct));
            progress?.Report($"Готово: {root} — {t.Elapsed.TotalSeconds:0} с; папок прочитано {stats.DirsRead:N0}" +
                             (stats.DirsCached > 0 ? $", из кэша {stats.DirsCached:N0}" : "") + $", файлов {stats.Files:N0}");
        });

        var cameras = new Dictionary<string, CameraStat>(StringComparer.OrdinalIgnoreCase);
        foreach (var (drive, cams, warnings) in partials.OrderBy(p => p.drive.Name, StringComparer.OrdinalIgnoreCase))
        {
            report.Drives.Add(drive);
            report.Warnings.AddRange(warnings);
            foreach (var c in cams.Values)
            {
                if (!cameras.TryGetValue(c.Id, out var total))
                    cameras[c.Id] = total = new CameraStat { Id = c.Id };
                total.Bytes += c.Bytes;
                total.FragmentCount += c.FragmentCount;
                total.OldestUtc = Min(total.OldestUtc, c.OldestUtc);
                total.NewestUtc = Max(total.NewestUtc, c.NewestUtc);
                foreach (var kv in c.BytesByDrive)
                    total.BytesByDrive[kv.Key] = total.BytesByDrive.GetValueOrDefault(kv.Key) + kv.Value;
            }
        }

        report.ConfigRead = macroscopRetention is { ById.Count: > 0 };
        report.BindingsRead = names.ServerId != null && names.ServerOf.Count > 0;
        report.SilentDaysContinuous = config.SilentDaysContinuous;
        report.SilentDaysMotion = config.SilentDaysMotion;
        // Cameras bound to this server that have no recordings at all are "lost" too: add them with zero volume.
        if (report.BindingsRead)
            foreach (var id in names.ServerOf.Keys.Where(names.IsBoundHere))
                if (!cameras.ContainsKey(id)) cameras[id] = new CameraStat { Id = id };
        foreach (var c in cameras.Values)
        {
            c.Name = names.Get(c.Id) ?? macroscopRetention?.ById.GetValueOrDefault(c.Id)?.Name
                     ?? (names.Historical.TryGetValue(c.Id, out var old) ? old + " (удалена)" : null)
                     ?? CameraNames.ToDashedGuid(c.Id) ?? c.Id;
            if (report.ConfigRead) c.InConfig = macroscopRetention!.ById.ContainsKey(c.Id);
            if (report.BindingsRead) c.BoundHere = names.IsBoundHere(c.Id);
            ApplyRetention(c, config, macroscopRetention);
        }
        if (macroscopRetention != null) ApplyDriveConfig(report.Drives, macroscopRetention.Drives);
        report.RetentionSource = DescribeRetentionSource(macroscopRetention, config);
        if (macroscopRetention != null) report.Warnings.AddRange(macroscopRetention.Warnings);
        report.Cameras = cameras.Values.ToList();

        ReportAnalyzer.Analyze(report, config.HeavyFactor);
        report.ScanSeconds = Math.Round(sw.Elapsed.TotalSeconds, 1);
        return report;
    }

    internal static void ApplyDriveConfig(List<DriveStat> drives, List<ArchiveDriveConfig> config)
    {
        static string N(string s) => s.Trim().TrimEnd('\\', '/').ToUpperInvariant();
        foreach (var d in drives)
        {
            var c = config.FirstOrDefault(x => N(x.Name) == N(d.Name))
                    ?? config.FirstOrDefault(x => x.Path != null && d.ArchiveRoot.StartsWith(x.Path.Trim(), StringComparison.OrdinalIgnoreCase));
            if (c == null) continue;
            d.Role = c.Role;
            d.MinFreeBytes = c.MinFreeBytes;
            d.MaxArchiveBytes = c.MaxArchiveBytes;
        }
    }

    /// <summary>mat.json entry wins, then Macroscop's configured max depth (or min if max is unset), then the default.</summary>
    internal static void ApplyRetention(CameraStat c, ToolConfig config, RetentionSettings? macroscop)
    {
        var explicitDays = config.ExplicitRetention(c.Id, c.Name);
        var mc = macroscop?.ById.GetValueOrDefault(c.Id);
        c.RetentionMinDays = mc?.MinDays;
        c.Settings = mc?.Settings;
        if (explicitDays.HasValue) { c.RetentionDays = explicitDays; c.RetentionSource = "mat.json"; }
        else if (mc != null && (mc.MaxDays ?? mc.MinDays) is double d && d > 0)
        {
            c.RetentionDays = (int)Math.Round(d);
            c.RetentionSource = "macroscop";
        }
        else { c.RetentionDays = config.DefaultRetentionDays; c.RetentionSource = "default"; }
    }

    private static string DescribeRetentionSource(RetentionSettings? m, ToolConfig config)
    {
        if (m == null || m.ById.Count == 0)
            return $"mat.json (по умолчанию {config.DefaultRetentionDays} дн.)";
        var when = m.BackupTimeLocal.HasValue ? m.BackupTimeLocal.Value.ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("ru-RU")) : "?";
        return $"конфигурация Macroscop (резервная копия от {when}, камер: {m.ById.Count}); исключения — mat.json";
    }

    private (DriveStat, Dictionary<string, CameraStat>, List<string>) ScanRoot(string archiveRoot, DateTime nowUtc, RootStats stats, CancellationToken ct)
    {
        var warnings = new List<string>();
        var drive = new DriveStat { ArchiveRoot = archiveRoot, Name = DriveName(archiveRoot) };
        FillVolumeInfo(drive, archiveRoot, warnings);

        var cams = new Dictionary<string, CameraStat>(StringComparer.OrdinalIgnoreCase);
        var media = Path.Combine(archiveRoot, "media");
        var dirInfo = new DirectoryInfo(archiveRoot);

        try
        {
            // Top level of MacroscopArchive: media, db, anything else.
            foreach (var entry in dirInfo.EnumerateFileSystemInfos("*", Flat))
            {
                ct.ThrowIfCancellationRequested();
                if (entry is FileInfo f) { drive.OtherArchiveBytes += f.Length; continue; }
                var d = (DirectoryInfo)entry;
                if (d.Name.Equals("media", StringComparison.OrdinalIgnoreCase)) continue;
                long size = SumFiles(d, ct);
                if (d.Name.Equals("db", StringComparison.OrdinalIgnoreCase)) drive.DbBytes += size;
                else drive.OtherArchiveBytes += size;
            }

            if (Directory.Exists(media))
            {
                var mediaDir = new DirectoryInfo(media);
                var cache = ScanCache.Open(CacheDir, archiveRoot, mediaDir.CreationTimeUtc);
                var stableBefore = nowUtc - ScanCache.StableAge;
                foreach (var f in mediaDir.EnumerateFiles("*", Flat))
                    drive.OtherArchiveBytes += f.Length;   // channel descriptors (.dsc)

                foreach (var camDir in mediaDir.EnumerateDirectories("*", Flat))
                {
                    ct.ThrowIfCancellationRequested();
                    var cam = ScanCamera(camDir, drive.Name, drive.DayCameraBytes, cache, stableBefore, stats, ct);
                    if (cam.Bytes == 0 && cam.FragmentCount == 0) continue;
                    cams[cam.Id] = cam;
                    drive.MediaBytes += cam.Bytes;
                    drive.FragmentCount += cam.FragmentCount;
                    drive.OldestUtc = Min(drive.OldestUtc, cam.OldestUtc);
                    drive.NewestUtc = Max(drive.NewestUtc, cam.NewestUtc);
                }
                cache.Save();
            }
            else warnings.Add($"{drive.Name}: нет папки media");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { warnings.Add($"{drive.Name}: ошибка сканирования — {ex.Message}"); }

        drive.CameraCount = cams.Count;
        drive.IsActive = drive.NewestUtc.HasValue && drive.NewestUtc.Value > nowUtc.AddHours(-24);
        return (drive, cams, warnings);
    }

    private CameraStat ScanCamera(DirectoryInfo camDir, string driveName, Dictionary<string, Dictionary<string, long>> days,
                                  ScanCache cache, DateTime stableBeforeUtc, RootStats stats, CancellationToken ct)
    {
        var cam = new CameraStat { Id = CameraNames.Normalize(camDir.Name) };
        int fragments = 0;

        void AddDay(string day, long bytes)
        {
            if (!days.TryGetValue(day, out var perCam)) days[day] = perCam = new(StringComparer.OrdinalIgnoreCase);
            perCam[cam.Id] = perCam.GetValueOrDefault(cam.Id) + bytes;
        }

        void Apply(ScanCache.Entry e)
        {
            cam.Bytes += e.Bytes;
            if (!e.HasBlobs) return;
            fragments++;
            cam.OldestUtc = Min(cam.OldestUtc, new DateTime(e.OldestTicks, DateTimeKind.Utc));
            cam.NewestUtc = Max(cam.NewestUtc, new DateTime(e.NewestTicks, DateTimeKind.Utc));
            foreach (var (day, bytes) in e.Days) AddDay(day, bytes);
        }

        // Explicit stack instead of recursion: archives are deep enough and this keeps the cache logic in one place.
        var stack = new Stack<(DirectoryInfo Dir, string Rel)>();
        stack.Push((camDir, camDir.Name));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, rel) = stack.Pop();
            stats.DirsRead++;
            var own = new ScanCache.Entry { DirWriteTicks = dir.LastWriteTimeUtc.Ticks };
            Dictionary<string, long>? ownDays = null;
            var subdirs = new List<DirectoryInfo>();
            try
            {
                foreach (var entry in dir.EnumerateFileSystemInfos("*", Flat))
                {
                    if (entry is DirectoryInfo sd) { subdirs.Add(sd); continue; }
                    var f = (FileInfo)entry;
                    stats.Files++;
                    own.Bytes += f.Length;
                    if (f.Length < BlobThresholdBytes) continue;
                    var t = f.LastWriteTimeUtc;
                    var day = t.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    ownDays ??= new(StringComparer.Ordinal);
                    ownDays[day] = ownDays.GetValueOrDefault(day) + f.Length;
                    if (own.OldestTicks == 0 || t.Ticks < own.OldestTicks) own.OldestTicks = t.Ticks;
                    if (t.Ticks > own.NewestTicks) own.NewestTicks = t.Ticks;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException) { continue; }

            if (ownDays != null) own.Days = ownDays.Select(kv => (kv.Key, kv.Value)).ToArray();
            Apply(own);
            if (subdirs.Count == 0 && !ReferenceEquals(dir, camDir)) cache.Put(rel, own);   // a leaf: remember it

            // Cached subfolders, except the few newest ones of this parent, which are always re-read.
            var hits = new List<(DirectoryInfo Dir, string Rel, ScanCache.Entry Entry)>();
            foreach (var sd in subdirs)
            {
                var subRel = rel + "\\" + sd.Name;
                var hit = cache.Get(subRel, sd.LastWriteTimeUtc, stableBeforeUtc);
                if (hit != null) hits.Add((sd, subRel, hit));
                else stack.Push((sd, subRel));
            }
            foreach (var (sd, subRel, e) in hits.OrderByDescending(h => h.Entry.NewestTicks).Take(AlwaysRereadNewest))
                stack.Push((sd, subRel));
            foreach (var (_, subRel, e) in hits.OrderByDescending(h => h.Entry.NewestTicks).Skip(AlwaysRereadNewest))
            {
                stats.DirsCached++;
                Apply(e);
                cache.Put(subRel, e);
            }
        }
        cam.FragmentCount = fragments;
        if (cam.Bytes > 0) cam.BytesByDrive[driveName] = cam.Bytes;
        return cam;
    }

    private static long SumFiles(DirectoryInfo d, CancellationToken ct)
    {
        long s = 0;
        foreach (var f in d.EnumerateFiles("*", Recursive)) { s += f.Length; }
        ct.ThrowIfCancellationRequested();
        return s;
    }

    private static void FillVolumeInfo(DriveStat drive, string archiveRoot, List<string> warnings)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(archiveRoot));
            if (string.IsNullOrEmpty(root)) return;
            var di = new DriveInfo(root);
            if (!di.IsReady) { warnings.Add($"{drive.Name}: том не готов"); return; }
            drive.TotalBytes = di.TotalSize;
            drive.FreeBytes = di.AvailableFreeSpace;
            drive.VolumeLabel = OperatingSystem.IsWindows() ? di.VolumeLabel : "";
        }
        catch (Exception ex) { warnings.Add($"{drive.Name}: нет сведений о томе — {ex.Message}"); }
    }

    /// <summary>
    /// "D:" when the archive folder sits directly in a drive root (the normal Macroscop layout);
    /// otherwise the full archive path, so two archives on one volume never merge into one "drive".
    /// </summary>
    internal static string DriveName(string archiveRoot)
    {
        var full = Path.GetFullPath(archiveRoot).TrimEnd('\\', '/');
        var root = Path.GetPathRoot(full) ?? "";
        var parent = Path.GetDirectoryName(full) ?? "";
        var trimmed = root.TrimEnd('\\', '/');
        bool atDriveRoot = string.Equals(parent.TrimEnd('\\', '/'), trimmed, StringComparison.OrdinalIgnoreCase);
        return atDriveRoot && trimmed.Length == 2 && trimmed[1] == ':' ? trimmed.ToUpperInvariant() : full;
    }

    internal static DateTime? Min(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : (a < b ? a : b);
    internal static DateTime? Max(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : (a > b ? a : b);
}
