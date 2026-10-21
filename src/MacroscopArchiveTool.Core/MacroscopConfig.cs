using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MacroscopArchiveTool.Core.Nrbf;

namespace MacroscopArchiveTool.Core;

/// <summary>Archive depth configured for one camera in Macroscop (Configurator → camera → Archive).</summary>
public sealed record CameraRetention(string CameraId, string? Name, double? MinDays, double? MaxDays);

public sealed class RetentionSettings
{
    public Dictionary<string, CameraRetention> ById { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? SourceFile { get; set; }
    public DateTime? BackupTimeLocal { get; set; }
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Reads per-camera archive depth from Macroscop's own configuration backups
/// (&lt;drive&gt;\MacroscopArchive\ConfigsBackups\&lt;timestamp&gt;\&lt;serverId&gt;\ServerConfigs\...\*.CmnConf).
/// The files are .NET BinaryFormatter (MS-NRBF) data; they are parsed passively with <see cref="NrbfReader"/>.
/// Read-only; the live configuration is never touched.
/// </summary>
public static class MacroscopConfig
{
    private static readonly Regex MinDepth = new(@"min\w*deepness", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MaxDepth = new(@"max\w*deepness", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IdMember = new(@"(^|[_<])(id|channelid|channel_id)(>k__BackingField)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NameMember = new(@"(^|[_<])name(>k__BackingField)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] CandidateFiles =
    {
        Path.Combine("ServerConfigs", "ServerConfigs", "SingleServerConfig.CmnConf"),
        Path.Combine("ServerConfigs", "ConfigStorage", "Config.CmnConf"),
    };

    /// <summary>Newest backup folder (by its timestamp name) across all archive roots.</summary>
    public static DirectoryInfo? FindNewestBackup(IEnumerable<string> archiveRoots) => BackupsNewestFirst(archiveRoots).FirstOrDefault();

    /// <summary>All backup folders across archive roots, newest first (by the timestamp in the folder name).</summary>
    public static IEnumerable<DirectoryInfo> BackupsNewestFirst(IEnumerable<string> archiveRoots)
    {
        var all = new List<(DirectoryInfo Dir, DateTime Time)>();
        foreach (var root in archiveRoots)
        {
            var cb = Path.Combine(root, "ConfigsBackups");
            if (!Directory.Exists(cb)) continue;
            try
            {
                foreach (var d in new DirectoryInfo(cb).EnumerateDirectories())
                    all.Add((d, ParseBackupTime(d.Name) ?? d.LastWriteTime));
            }
            catch (Exception) { /* unreadable drive: skip */ }
        }
        return all.OrderByDescending(x => x.Time).Select(x => x.Dir);
    }

    /// <summary>Folder names look like "2026-10-21 12.40.56.354".</summary>
    public static DateTime? ParseBackupTime(string name)
        => DateTime.TryParseExact(name, new[] { "yyyy-MM-dd HH.mm.ss.fff", "yyyy-MM-dd HH.mm.ss" },
               CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    /// <summary>Config file inside a backup, preferring the folder of the given server.</summary>
    public static FileInfo? FindConfigFile(DirectoryInfo backup, string? serverId)
    {
        var serverDirs = backup.EnumerateDirectories().ToList();
        if (serverId != null)
        {
            var norm = CameraNames.Normalize(serverId);
            var own = serverDirs.Where(d => CameraNames.Normalize(d.Name) == norm).ToList();
            if (own.Count > 0) serverDirs = own;
        }
        foreach (var rel in CandidateFiles)
            foreach (var sd in serverDirs)
            {
                var f = new FileInfo(Path.Combine(sd.FullName, rel));
                if (f.Exists) return f;
            }
        return null;
    }

    /// <summary>How many of the newest backups to try before giving up.</summary>
    public const int MaxBackupsToTry = 5;

    /// <summary>
    /// Loads retention for all cameras from the newest readable backup (a backup being written right now
    /// may be incomplete, so up to <see cref="MaxBackupsToTry"/> older ones are tried). Never throws.
    /// </summary>
    public static RetentionSettings Load(IEnumerable<string> archiveRoots, string? serverId)
    {
        var result = new RetentionSettings();
        var problems = new List<string>();
        try
        {
            var backups = BackupsNewestFirst(archiveRoots).Take(MaxBackupsToTry).ToList();
            if (backups.Count == 0) { result.Warnings.Add("Резервные копии конфигурации Macroscop (ConfigsBackups) не найдены. Используется mat.json."); return result; }

            foreach (var backup in backups)
            {
                var file = FindConfigFile(backup, serverId);
                if (file == null) { problems.Add($"{backup.Name}: нет файла конфигурации сервера"); continue; }
                try
                {
                    var (graph, _) = NrbfReader.ReadAll(ReadShared(file.FullName));
                    var cams = Extract(graph);
                    if (cams.Count == 0) { problems.Add($"{backup.Name}: сроки камер не распознаны"); continue; }
                    foreach (var r in cams) result.ById[r.CameraId] = r;
                    result.SourceFile = file.FullName;
                    result.BackupTimeLocal = ParseBackupTime(backup.Name) ?? file.LastWriteTime;
                    if (problems.Count > 0)
                        result.Warnings.Add("Более новые копии конфигурации пропущены: " + string.Join("; ", problems));
                    return result;
                }
                catch (Exception ex) { problems.Add($"{backup.Name}: {ex.Message}"); }
            }
            result.Warnings.Add("Сроки хранения из конфигурации Macroscop не прочитаны (" + string.Join("; ", problems) + "). Используется mat.json.");
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"Не удалось прочитать сроки хранения из конфигурации Macroscop: {ex.Message}. Используется mat.json.");
        }
        return result;
    }

    /// <summary>
    /// Finds objects that carry both a "min…deepness" and a "max…deepness" member (archive thinning layers excluded)
    /// and attributes them to the nearest object with a GUID id (the camera), searching up to 4 levels of owners.
    /// </summary>
    public static List<CameraRetention> Extract(NrbfGraph g)
    {
        var parents = BuildParents(g);
        var found = new Dictionary<string, CameraRetention>(StringComparer.OrdinalIgnoreCase);

        foreach (var o in g.AllObjects())
        {
            if (o.ClassName.Contains("Thinning", StringComparison.OrdinalIgnoreCase)) continue;
            var min = o.Members.FirstOrDefault(m => MinDepth.IsMatch(m.Key));
            var max = o.Members.FirstOrDefault(m => MaxDepth.IsMatch(m.Key));
            if (min.Key == null || max.Key == null) continue;

            var owner = FindOwnerWithId(g, parents, o, out var id);
            if (owner == null || id == null) continue;
            var name = owner.Members.Where(m => NameMember.IsMatch(m.Key)).Select(m => g.Resolve(m.Value) as string).FirstOrDefault(s => s != null);
            found[CameraNames.Normalize(id.Value.ToString("N"))] =
                new CameraRetention(CameraNames.Normalize(id.Value.ToString("N")), name, ToDouble(g.Resolve(min.Value)), ToDouble(g.Resolve(max.Value)));
        }
        return found.Values.ToList();
    }

    private static NrbfObject? FindOwnerWithId(NrbfGraph g, Dictionary<int, List<NrbfObject>> parents, NrbfObject start, out Guid? id)
    {
        var level = new List<NrbfObject> { start };
        var seen = new HashSet<int>();
        for (int depth = 0; depth <= 4 && level.Count > 0; depth++)
        {
            foreach (var o in level)
            {
                var idv = o.Members.Where(m => IdMember.IsMatch(m.Key)).Select(m => AsGuid(g, m.Value)).FirstOrDefault(x => x.HasValue && x.Value != Guid.Empty);
                if (idv.HasValue) { id = idv; return o; }
            }
            var next = new List<NrbfObject>();
            foreach (var o in level)
                if (seen.Add(o.Id) && parents.TryGetValue(o.Id, out var ps)) next.AddRange(ps);
            level = next;
        }
        id = null;
        return null;
    }

    /// <summary>child object id → objects that reference it (directly or through an array).</summary>
    private static Dictionary<int, List<NrbfObject>> BuildParents(NrbfGraph g)
    {
        var map = new Dictionary<int, List<NrbfObject>>();
        void Link(NrbfObject parent, object? v, int depth)
        {
            switch (v)
            {
                case NrbfObject c: Add(c.Id, parent); break;
                case NrbfRef r:
                    var t = g.ById.GetValueOrDefault(r.Id);
                    if (t is NrbfObject co) Add(co.Id, parent);
                    else if (t is NrbfArray ra && depth < 2) foreach (var it in ra.Items) Link(parent, it, depth + 1);
                    break;
                case NrbfArray a when depth < 2:
                    foreach (var it in a.Items) Link(parent, it, depth + 1);
                    break;
            }
        }
        void Add(int child, NrbfObject parent)
        {
            if (!map.TryGetValue(child, out var l)) map[child] = l = new List<NrbfObject>();
            if (!l.Contains(parent)) l.Add(parent);
        }
        foreach (var o in g.AllObjects())
            foreach (var m in o.Members) Link(o, m.Value, 0);
        return map;
    }

    internal static Guid? AsGuid(NrbfGraph g, object? v)
    {
        v = g.Resolve(v);
        return v switch
        {
            GuidBox b => b.Value,
            Guid x => x,
            string s when Guid.TryParse(s, out var p) => p,
            _ => null,
        };
    }

    internal static double? ToDouble(object? v) => v switch
    {
        null => null,
        TimeSpan ts => ts.TotalDays,
        IConvertible c when v is not string and not bool => Convert.ToDouble(c, CultureInfo.InvariantCulture),
        _ => null,
    };

    internal static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Human-readable structure of a config file for diagnostics. Prints class and member names, numbers and GUIDs;
    /// string values are hidden except camera-name members, so camera logins/passwords never appear.
    /// </summary>
    public static string DumpStructure(string path)
    {
        var sb = new StringBuilder();
        var bytes = ReadShared(path);
        sb.AppendLine($"Файл: {path}");
        sb.AppendLine($"Размер: {bytes.Length:N0} байт");
        sb.AppendLine("Начало: " + string.Join(" ", bytes.Take(32).Select(b => b.ToString("x2"))));
        var (g, offsets) = NrbfReader.ReadAll(bytes);
        sb.AppendLine($"Потоков NRBF: {offsets.Count} (смещения {string.Join(", ", offsets)}), объектов: {g.ById.Count}");

        sb.AppendLine().AppendLine("== Классы (число экземпляров) ==");
        foreach (var grp in g.AllObjects().GroupBy(o => o.ClassName).OrderByDescending(x => x.Count()).Take(60))
            sb.AppendLine($"{grp.Count(),6}  {Short(grp.Key)}");

        var depthObjs = g.AllObjects().Where(o => o.Members.Any(m => MinDepth.IsMatch(m.Key)) && o.Members.Any(m => MaxDepth.IsMatch(m.Key))).ToList();
        sb.AppendLine().AppendLine($"== Объекты со сроком хранения: {depthObjs.Count} ==");
        var parents = BuildParents(g);
        foreach (var o in depthObjs.Take(4))
        {
            sb.AppendLine($"-- {Short(o.ClassName)} #{o.Id}");
            foreach (var m in o.Members) sb.AppendLine($"     {m.Key} = {Describe(g, m.Key, m.Value)}");
            var chain = new List<string>();
            var cur = o;
            for (int i = 0; i < 4 && parents.TryGetValue(cur.Id, out var ps) && ps.Count > 0; i++)
            {
                var p = ps[0];
                var link = p.Members.FirstOrDefault(m => m.Value is NrbfObject c && c.Id == cur.Id || m.Value is NrbfRef r && r.Id == cur.Id).Key ?? "(через массив)";
                chain.Add($"{Short(p.ClassName)}.{link}");
                cur = p;
            }
            sb.AppendLine("     владельцы: " + (chain.Count > 0 ? string.Join(" ← ", chain) : "—"));
            if (parents.TryGetValue(o.Id, out var direct) && direct.Count > 0)
            {
                sb.AppendLine($"     члены владельца {Short(direct[0].ClassName)}:");
                foreach (var m in direct[0].Members.Take(80)) sb.AppendLine($"       {m.Key} = {Describe(g, m.Key, m.Value)}");
            }
        }

        var extracted = Extract(g);
        sb.AppendLine().AppendLine($"== Распознано камер: {extracted.Count} ==");
        foreach (var r in extracted.OrderBy(r => r.Name)) sb.AppendLine($"  {r.CameraId}  {r.Name ?? "?"}  мин={r.MinDays} макс={r.MaxDays}");
        return sb.ToString();
    }

    private static string Describe(NrbfGraph g, string member, object? v)
    {
        var r = g.Resolve(v);
        return r switch
        {
            null => "null",
            GuidBox b => b.Value.ToString(),
            string s when NameMember.IsMatch(member) && s.Length <= 120 => $"\"{s}\"",
            string s => $"<строка, {s.Length} симв.>",
            NrbfObject o => $"{{{Short(o.ClassName)} #{o.Id}}}",
            NrbfArray a => $"[массив, {a.Items.Count} эл.]",
            byte[] bytes => $"<байты, {bytes.Length}>",
            bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or TimeSpan or DateTime or char
                => Convert.ToString(r, CultureInfo.InvariantCulture) ?? "",
            _ => r.GetType().Name,
        };
    }

    /// <summary>Strips assembly-qualified generic arguments to keep type names readable.</summary>
    private static string Short(string typeName)
    {
        var s = Regex.Replace(typeName, @", [^\[\],]+, Version=[^\]]+", "");
        return s.Length > 160 ? s[..160] + "…" : s;
    }
}
