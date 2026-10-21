using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace MacroscopArchiveTool.Core.Migration;

public sealed class CopyOptions
{
    /// <summary>0 = unlimited.</summary>
    public long SpeedLimitBytesPerSec { get; set; }
    /// <summary>Re-read every copied file from the destination and compare SHA-256 with the source.</summary>
    public bool Verify { get; set; } = true;
    /// <summary>Extra read attempts for a source file before it is recorded as lost.</summary>
    public int Retries { get; set; } = 2;
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>Remove destination files and folders that no longer exist in the source (retention deletes them).</summary>
    public bool Mirror { get; set; } = true;
    public int BufferSize { get; set; } = 4 * 1024 * 1024;
    /// <summary>Relative folders not touched by this pass (e.g. "db" while PostgreSQL still has it open).</summary>
    public List<string> ExcludePrefixes { get; set; } = new();
    /// <summary>Relative folders copied again even if they look unchanged (e.g. "db" in the final pass).</summary>
    public List<string> ForcePrefixes { get; set; } = new();

    internal bool Under(string rel, List<string> prefixes)
        => prefixes.Any(p => rel.Equals(p, StringComparison.OrdinalIgnoreCase)
                             || rel.StartsWith(p.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                             || rel.StartsWith(p.TrimEnd('\\', '/') + "/", StringComparison.OrdinalIgnoreCase));
}

public sealed class CopyProgress
{
    public string Phase { get; init; } = "";
    public long BytesTotal { get; init; }
    public long BytesDone { get; init; }
    public int FilesTotal { get; init; }
    public int FilesDone { get; init; }
    public int FilesSkipped { get; init; }
    public int Losses { get; init; }
    public string CurrentFile { get; init; } = "";
    public double BytesPerSec { get; init; }
    public TimeSpan? Eta { get; init; }
    public double Percent => BytesTotal > 0 ? Math.Min(100, BytesDone * 100.0 / BytesTotal) : (FilesTotal > 0 ? FilesDone * 100.0 / FilesTotal : 0);
}

/// <summary>A source file that could not be read; video for that camera and time range is lost.</summary>
public sealed class CopyLoss
{
    public string RelativePath { get; set; } = "";
    public long Size { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public string? CameraId { get; set; }
    public string Error { get; set; } = "";
}

public sealed class CopyResult
{
    public int FilesTotal { get; set; }
    public int FilesCopied { get; set; }
    public int FilesSkipped { get; set; }
    public int FilesVanished { get; set; }
    public int FilesDeleted { get; set; }
    public long BytesCopied { get; set; }
    public TimeSpan Elapsed { get; set; }
    public bool Cancelled { get; set; }
    public List<CopyLoss> Losses { get; set; } = new();
}

/// <summary>
/// Copies a MacroscopArchive folder to a new volume so that Macroscop sees the same logical drive there.
/// Each source file is read once: SHA-256 is computed while copying and, if requested, compared with a re-read of the
/// destination. Timestamps are preserved. Unreadable files are retried, then skipped and reported as losses.
/// A manifest next to the destination makes the copy resumable and lets a final pass copy only what changed.
/// The source is never modified.
/// </summary>
public sealed class ArchiveCopier
{
    public const string ManifestFolder = "_MacroscopArchiveTool";
    public const string PartialSuffix = ".mat-partial";

    /// <summary>Test hook to inject read failures.</summary>
    internal Func<string, Stream>? OpenSourceOverride { get; set; }

    private sealed record Entry(string Rel, long Size, long MtimeTicks, string Sha256);

    private sealed record SourceFile(string Rel, string Full, long Size, DateTime MtimeUtc, DateTime CtimeUtc, FileAttributes Attr);

    /// <summary>Manifest path for a destination archive root like R:\MacroscopArchive.</summary>
    public static string ManifestPath(string destRoot)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(destRoot).TrimEnd('\\', '/')) ?? destRoot;
        return Path.Combine(parent, ManifestFolder, "copy-manifest.jsonl");
    }

    public CopyResult Run(string sourceRoot, string destRoot, CopyOptions o, IProgress<CopyProgress>? progress = null,
                          CancellationToken ct = default, ManualResetEventSlim? pauseGate = null)
    {
        var sw = Stopwatch.StartNew();
        var result = new CopyResult();
        sourceRoot = Path.GetFullPath(sourceRoot);
        destRoot = Path.GetFullPath(destRoot);
        if (!Directory.Exists(sourceRoot)) throw new DirectoryNotFoundException($"Нет исходной папки {sourceRoot}");
        if (IsSameOrNested(sourceRoot, destRoot)) throw new ArgumentException("Папка назначения не может находиться внутри исходной и наоборот.");
        Directory.CreateDirectory(destRoot);

        progress?.Report(new CopyProgress { Phase = "Чтение списка файлов…" });
        var (files, dirs) = ListSource(sourceRoot, result, ct, o);
        result.FilesTotal = files.Count;
        long totalBytes = files.Sum(f => f.Size);

        var manifestPath = ManifestPath(destRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        var manifest = LoadManifest(manifestPath);

        foreach (var d in dirs) Directory.CreateDirectory(Path.Combine(destRoot, d));

        long done = 0;
        var rate = new RateMeter();
        var lastReport = Stopwatch.StartNew();
        var throttle = new Throttle(o.SpeedLimitBytesPerSec);
        int filesDone = 0;

        void Report(string current, bool force = false)
        {
            if (progress == null || (!force && lastReport.ElapsedMilliseconds < 250)) return;
            lastReport.Restart();
            var bps = rate.BytesPerSec;
            progress.Report(new CopyProgress
            {
                Phase = "Копирование",
                BytesTotal = totalBytes, BytesDone = done, FilesTotal = files.Count, FilesDone = filesDone,
                FilesSkipped = result.FilesSkipped, Losses = result.Losses.Count, CurrentFile = current, BytesPerSec = bps,
                Eta = bps > 0 ? TimeSpan.FromSeconds((totalBytes - done) / bps) : null,
            });
        }

        using var manifestWriter = new StreamWriter(new FileStream(manifestPath, FileMode.Append, FileAccess.Write, FileShare.Read));
        try
        {
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                pauseGate?.Wait(ct);
                var dest = Path.Combine(destRoot, f.Rel);

                if (!o.Under(f.Rel, o.ForcePrefixes) && IsUpToDate(f, dest, manifest))
                {
                    result.FilesSkipped++;
                    done += f.Size; filesDone++;
                    Report(f.Rel);
                    continue;
                }

                var copied = CopyOne(f, dest, o, ct, pauseGate, throttle, n => { done += n; rate.Add(n); Report(f.Rel); }, out var sha, out var error, out var vanished);
                if (copied)
                {
                    result.FilesCopied++;
                    result.BytesCopied += f.Size;
                    var e = new Entry(f.Rel, f.Size, f.MtimeUtc.Ticks, sha!);
                    manifest[f.Rel] = e;
                    manifestWriter.WriteLine(JsonSerializer.Serialize(e));
                    manifestWriter.Flush();
                }
                else if (vanished)
                {
                    result.FilesVanished++;   // deleted by Macroscop retention meanwhile: not a loss
                    TryDelete(dest);
                }
                else
                {
                    result.Losses.Add(new CopyLoss
                    {
                        RelativePath = f.Rel, Size = f.Size, LastWriteUtc = f.MtimeUtc,
                        CameraId = CameraIdFromPath(f.Rel), Error = error ?? "ошибка чтения",
                    });
                    done += f.Size; // count as processed so progress reaches 100%
                }
                filesDone++;
                Report(f.Rel);
            }

            if (o.Mirror) result.FilesDeleted = MirrorDeletes(destRoot, files, dirs, o, ct);
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
        }

        result.Elapsed = sw.Elapsed;
        progress?.Report(new CopyProgress
        {
            Phase = result.Cancelled ? "Остановлено" : "Готово",
            BytesTotal = totalBytes, BytesDone = done, FilesTotal = files.Count, FilesDone = filesDone,
            FilesSkipped = result.FilesSkipped, Losses = result.Losses.Count,
        });
        return result;
    }

    private bool CopyOne(SourceFile f, string dest, CopyOptions o, CancellationToken ct, ManualResetEventSlim? pauseGate,
                         Throttle throttle, Action<long> onBytes, out string? sha, out string? error, out bool vanished)
    {
        sha = null; error = null; vanished = false;
        var partial = dest + PartialSuffix;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var buffer = new byte[o.BufferSize];

        for (int attempt = 0; attempt <= o.Retries; attempt++)
        {
            long countedThisAttempt = 0;
            try
            {
                using (var src = OpenSource(f.Full))
                using (var dst = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.SequentialScan))
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    int n;
                    while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        pauseGate?.Wait(ct);
                        hash.AppendData(buffer, 0, n);
                        dst.Write(buffer, 0, n);
                        countedThisAttempt += n;
                        onBytes(n);
                        throttle.Consume(n, ct);
                    }
                    dst.Flush(flushToDisk: true);
                    sha = Convert.ToHexString(hash.GetHashAndReset());
                }

                if (o.Verify)
                {
                    var check = HashFile(partial, buffer, ct);
                    if (!string.Equals(check, sha, StringComparison.Ordinal))
                        throw new IOException("контрольная сумма копии не совпала с исходной");
                }

                File.Move(partial, dest, overwrite: true);
                File.SetCreationTimeUtc(dest, f.CtimeUtc);
                File.SetLastWriteTimeUtc(dest, f.MtimeUtc);
                var attr = f.Attr & ~(FileAttributes.Directory | FileAttributes.Compressed | FileAttributes.Encrypted | FileAttributes.SparseFile | FileAttributes.ReparsePoint);
                if (attr != 0) File.SetAttributes(dest, attr);
                return true;
            }
            catch (OperationCanceledException) { TryDelete(partial); throw; }
            catch (FileNotFoundException) { TryDelete(partial); vanished = true; return false; }
            catch (DirectoryNotFoundException) { TryDelete(partial); vanished = true; return false; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(partial);
                onBytes(-countedThisAttempt);
                error = ex.Message;
                if (!File.Exists(f.Full)) { vanished = true; return false; }
                if (attempt < o.Retries) Thread.Sleep(o.RetryDelay);
            }
        }
        return false;
    }

    private Stream OpenSource(string path)
        => OpenSourceOverride?.Invoke(path)
           ?? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);

    private static string HashFile(string path, byte[] buffer, CancellationToken ct)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int n;
        while ((n = s.Read(buffer, 0, buffer.Length)) > 0) { ct.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, n); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static bool IsUpToDate(SourceFile f, string dest, Dictionary<string, Entry> manifest)
    {
        if (!manifest.TryGetValue(f.Rel, out var e)) return false;
        if (e.Size != f.Size || e.MtimeTicks != f.MtimeUtc.Ticks) return false;
        var di = new FileInfo(dest);
        return di.Exists && di.Length == f.Size && di.LastWriteTimeUtc.Ticks == f.MtimeUtc.Ticks;
    }

    private static (List<SourceFile> Files, List<string> Dirs) ListSource(string root, CopyResult result, CancellationToken ct, CopyOptions o)
    {
        var files = new List<SourceFile>();
        var dirs = new List<string>();
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(root));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var d = stack.Pop();
            try
            {
                foreach (var e in d.EnumerateFileSystemInfos())
                {
                    var rel = Path.GetRelativePath(root, e.FullName);
                    if (o.Under(rel, o.ExcludePrefixes)) continue;
                    if (e is DirectoryInfo sub)
                    {
                        if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        dirs.Add(rel);
                        stack.Push(sub);
                    }
                    else if (e is FileInfo fi && !fi.Name.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        files.Add(new SourceFile(rel, fi.FullName, fi.Length, fi.LastWriteTimeUtc, fi.CreationTimeUtc, fi.Attributes));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Losses.Add(new CopyLoss
                {
                    RelativePath = Path.GetRelativePath(root, d.FullName) + Path.DirectorySeparatorChar,
                    CameraId = CameraIdFromPath(Path.GetRelativePath(root, d.FullName)),
                    Error = "папка не читается: " + ex.Message,
                });
            }
        }
        // Newest first: those recordings stay on the new disk the longest (retention removes the oldest first),
        // so if a failing source dies mid-copy, the most useful part is already saved.
        files.Sort((a, b) => b.MtimeUtc.CompareTo(a.MtimeUtc));
        return (files, dirs);
    }

    private static int MirrorDeletes(string destRoot, List<SourceFile> files, List<string> dirs, CopyOptions o, CancellationToken ct)
    {
        var keepFiles = new HashSet<string>(files.Select(f => f.Rel), StringComparer.OrdinalIgnoreCase);
        var keepDirs = new HashSet<string>(dirs, StringComparer.OrdinalIgnoreCase);
        int deleted = 0;
        foreach (var f in Directory.EnumerateFiles(destRoot, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(destRoot, f);
            if (keepFiles.Contains(rel) || o.Under(rel, o.ExcludePrefixes)) continue;
            // Not in the source snapshot: deleted from the source (retention) or our own leftover partial file.
            TryDelete(f);
            deleted++;
        }
        foreach (var d in Directory.EnumerateDirectories(destRoot, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length))
        {
            var rel = Path.GetRelativePath(destRoot, d);
            if (keepDirs.Contains(rel) || o.Under(rel, o.ExcludePrefixes)) continue;
            try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch (IOException) { }
        }
        return deleted;
    }

    private static Dictionary<string, Entry> LoadManifest(string path)
    {
        var m = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return m;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { var e = JsonSerializer.Deserialize<Entry>(line); if (e != null) m[e.Rel] = e; }
            catch (JsonException) { /* torn last line after a crash */ }
        }
        return m;
    }

    /// <summary>"media\&lt;32 hex&gt;\..." → camera id.</summary>
    public static string? CameraIdFromPath(string rel)
    {
        var parts = rel.Split('\\', '/');
        return parts.Length >= 2 && parts[0].Equals("media", StringComparison.OrdinalIgnoreCase)
               && parts[1].Length == 32 && parts[1].All(Uri.IsHexDigit) ? parts[1].ToLowerInvariant() : null;
    }

    private static bool IsSameOrNested(string a, string b)
    {
        string N(string p) => p.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return N(a).StartsWith(N(b), StringComparison.OrdinalIgnoreCase) || N(b).StartsWith(N(a), StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private sealed class RateMeter
    {
        private readonly Queue<(long T, long Bytes)> _q = new();
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private long _sum;
        public void Add(long n)
        {
            var now = _sw.ElapsedMilliseconds;
            _q.Enqueue((now, n)); _sum += n;
            while (_q.Count > 0 && now - _q.Peek().T > 5000) _sum -= _q.Dequeue().Bytes;
        }
        public double BytesPerSec
        {
            get
            {
                if (_q.Count < 2) return 0;
                var span = (_sw.ElapsedMilliseconds - _q.Peek().T) / 1000.0;
                return span > 0.2 ? _sum / span : 0;
            }
        }
    }

    private sealed class Throttle
    {
        private readonly long _limit;
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private long _bytes;
        public Throttle(long limit) => _limit = limit;
        public void Consume(long n, CancellationToken ct)
        {
            if (_limit <= 0) return;
            _bytes += n;
            var expectedMs = _bytes * 1000.0 / _limit;
            var ahead = expectedMs - _sw.ElapsedMilliseconds;
            if (ahead > 5) ct.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(Math.Min(ahead, 2000)));
            if (_sw.ElapsedMilliseconds > 10_000) { _sw.Restart(); _bytes = 0; }   // keep the window short
        }
    }
}
