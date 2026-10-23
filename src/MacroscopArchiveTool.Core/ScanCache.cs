using System.Text;

namespace MacroscopArchiveTool.Core;

/// <summary>
/// Remembers what was found in closed fragment folders (media\&lt;camera&gt;\…\&lt;fragment&gt;) of one archive root,
/// so a repeated scan does not have to open hundreds of thousands of folders on slow (iSCSI) disks again.
/// A folder is taken from the cache only if its modification time (which NTFS changes when a file is added,
/// removed or renamed inside it) is unchanged and nothing in it was written during the last <see cref="StableAge"/>.
/// The cache is only an accelerator: any read/write problem means a normal full scan.
/// </summary>
public sealed class ScanCache
{
    private const string Magic = "MATSC2";
    /// <summary>Once in a while everything is re-read anyway, so a missed change cannot live in the cache for long.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    /// <summary>Folders with anything newer than this are always re-read (a fragment may still be recorded into).</summary>
    public static readonly TimeSpan StableAge = TimeSpan.FromDays(2);

    public sealed class Entry
    {
        public long DirWriteTicks;
        public long Bytes;
        public long OldestTicks;    // of blobs; 0 = no blobs
        public long NewestTicks;
        public (string Day, long Bytes)[] Days = Array.Empty<(string, long)>();
        public bool HasBlobs => NewestTicks != 0;
    }

    private readonly string? _file;
    private readonly long _identity;
    private readonly long _builtTicks;
    private readonly Dictionary<string, Entry> _old;
    private readonly Dictionary<string, Entry> _new = new(StringComparer.OrdinalIgnoreCase);

    private ScanCache(string? file, long identity, Dictionary<string, Entry> old, long builtTicks = 0)
    {
        _file = file; _identity = identity; _old = old;
        _builtTicks = old.Count > 0 ? builtTicks : DateTime.UtcNow.Ticks;
    }

    public int Count => _old.Count;

    public static ScanCache Open(string? cacheDir, string archiveRoot, DateTime mediaCreatedUtc)
    {
        var empty = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        if (cacheDir == null) return new ScanCache(null, 0, empty);
        var file = Path.Combine(cacheDir, FileName(archiveRoot));
        long identity = mediaCreatedUtc.Ticks;
        try
        {
            if (!File.Exists(file)) return new ScanCache(file, identity, empty);
            using var r = new BinaryReader(File.OpenRead(file), Encoding.UTF8);
            if (r.ReadString() != Magic || r.ReadInt64() != identity) return new ScanCache(file, identity, empty);
            long built = r.ReadInt64();
            if (DateTime.UtcNow.Ticks - built > MaxAge.Ticks || built > DateTime.UtcNow.Ticks) return new ScanCache(file, identity, empty);
            int n = r.ReadInt32();
            var old = new Dictionary<string, Entry>(n, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < n; i++)
            {
                var key = r.ReadString();
                var e = new Entry { DirWriteTicks = r.ReadInt64(), Bytes = r.ReadInt64(), OldestTicks = r.ReadInt64(), NewestTicks = r.ReadInt64() };
                int d = r.ReadInt32();
                e.Days = new (string, long)[d];
                for (int j = 0; j < d; j++) e.Days[j] = (r.ReadString(), r.ReadInt64());
                old[key] = e;
            }
            return new ScanCache(file, identity, old, built);
        }
        catch (Exception) { return new ScanCache(file, identity, empty); }   // damaged or other format → full scan
    }

    /// <summary>A usable cached result for this folder, or null when it has to be read.</summary>
    public Entry? Get(string relPath, DateTime dirWriteUtc, DateTime stableBeforeUtc)
    {
        if (!_old.TryGetValue(relPath, out var e)) return null;
        if (e.DirWriteTicks != dirWriteUtc.Ticks || dirWriteUtc >= stableBeforeUtc) return null;
        if (e.NewestTicks >= stableBeforeUtc.Ticks) return null;
        return e;
    }

    public void Put(string relPath, Entry e) => _new[relPath] = e;

    /// <summary>Writes only the folders seen in this scan, so deleted fragments drop out by themselves.</summary>
    public void Save()
    {
        if (_file == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            using (var w = new BinaryWriter(File.Create(tmp), Encoding.UTF8))
            {
                w.Write(Magic); w.Write(_identity); w.Write(_builtTicks); w.Write(_new.Count);
                foreach (var (key, e) in _new)
                {
                    w.Write(key); w.Write(e.DirWriteTicks); w.Write(e.Bytes); w.Write(e.OldestTicks); w.Write(e.NewestTicks);
                    w.Write(e.Days.Length);
                    foreach (var (day, bytes) in e.Days) { w.Write(day); w.Write(bytes); }
                }
            }
            File.Move(tmp, _file, true);
        }
        catch (Exception) { /* no cache next time — just a slower scan */ }
    }

    public static string DefaultDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MacroscopArchiveTool", "scancache");

    internal static string FileName(string archiveRoot)
    {
        var full = Path.GetFullPath(archiveRoot).TrimEnd('\\', '/');
        var sb = new StringBuilder();
        foreach (var ch in full) sb.Append(char.IsLetterOrDigit(ch) ? char.ToUpperInvariant(ch) : '_');
        return sb + ".bin";
    }
}
