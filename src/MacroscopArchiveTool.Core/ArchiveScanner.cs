using System.Collections.Concurrent;
using System.Diagnostics;

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
                           IProgress<string>? progress = null, CancellationToken ct = default)
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
            partials.Add(ScanRoot(root, report.ScannedAtUtc, ct));
            progress?.Report($"Готово: {root}");
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

        foreach (var c in cameras.Values)
        {
            c.Name = names.Get(c.Id) ?? CameraNames.ToDashedGuid(c.Id) ?? c.Id;
            c.RetentionDays = config.RetentionFor(c.Id, c.Name);
        }
        report.Cameras = cameras.Values.ToList();

        ReportAnalyzer.Analyze(report, config.HeavyFactor);
        report.ScanSeconds = Math.Round(sw.Elapsed.TotalSeconds, 1);
        return report;
    }

    private (DriveStat, Dictionary<string, CameraStat>, List<string>) ScanRoot(string archiveRoot, DateTime nowUtc, CancellationToken ct)
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
                foreach (var f in mediaDir.EnumerateFiles("*", Flat))
                    drive.OtherArchiveBytes += f.Length;   // channel descriptors (.dsc)

                foreach (var camDir in mediaDir.EnumerateDirectories("*", Flat))
                {
                    ct.ThrowIfCancellationRequested();
                    var cam = ScanCamera(camDir, drive.Name, ct);
                    if (cam.Bytes == 0 && cam.FragmentCount == 0) continue;
                    cams[cam.Id] = cam;
                    drive.MediaBytes += cam.Bytes;
                    drive.FragmentCount += cam.FragmentCount;
                    drive.OldestUtc = Min(drive.OldestUtc, cam.OldestUtc);
                    drive.NewestUtc = Max(drive.NewestUtc, cam.NewestUtc);
                }
            }
            else warnings.Add($"{drive.Name}: нет папки media");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { warnings.Add($"{drive.Name}: ошибка сканирования — {ex.Message}"); }

        drive.CameraCount = cams.Count;
        drive.IsActive = drive.NewestUtc.HasValue && drive.NewestUtc.Value > nowUtc.AddHours(-24);
        return (drive, cams, warnings);
    }

    private CameraStat ScanCamera(DirectoryInfo camDir, string driveName, CancellationToken ct)
    {
        var cam = new CameraStat { Id = CameraNames.Normalize(camDir.Name) };
        var fragmentDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int n = 0;
        foreach (var f in camDir.EnumerateFiles("*", Recursive))
        {
            if ((++n & 0x3FF) == 0) ct.ThrowIfCancellationRequested();
            cam.Bytes += f.Length;
            if (f.Length >= BlobThresholdBytes)
            {
                var t = f.LastWriteTimeUtc;
                cam.OldestUtc = Min(cam.OldestUtc, t);
                cam.NewestUtc = Max(cam.NewestUtc, t);
                fragmentDirs.Add(f.DirectoryName ?? "");
            }
        }
        cam.FragmentCount = fragmentDirs.Count;
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
