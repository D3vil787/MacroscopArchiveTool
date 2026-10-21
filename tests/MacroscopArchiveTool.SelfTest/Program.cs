using System.Text;
using MacroscopArchiveTool.Core;

// Dependency-free test runner (NuGet is not always reachable from the build sandbox).
// Builds a fake Macroscop archive with the real on-disk layout and checks the scanner and reports.
var tests = new List<(string Name, Action Body)>
{
    ("Имена камер и сервера из ServerConfiguration.log", NamesParse),
    ("Срок хранения по имени, GUID и по умолчанию", RetentionLookup),
    ("Сканирование: объёмы, глубина, диски, отметки", ScanFakeArchive),
    ("JSON туда-обратно и сводный HTML", JsonAndHtml),
    ("NRBF: сроки камер из бинарной конфигурации", NrbfRetention),
    ("NRBF: дамп структуры без паролей", NrbfDumpHidesSecrets),
    ("Сроки из ConfigsBackups: новейшая копия, приоритет mat.json", RetentionFromBackups),
};

int failed = 0;
foreach (var (name, body) in tests)
{
    try { body(); Console.WriteLine($"[ OK ] {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"[FAIL] {name}: {ex.Message}"); }
}
Console.WriteLine(failed == 0 ? $"Все тесты пройдены ({tests.Count})." : $"Провалено: {failed} из {tests.Count}");
return failed == 0 ? 0 : 1;

// ---------------------------------------------------------------------------

static void NamesParse()
{
    var n = new CameraNames();
    n.Parse(SampleLog);
    Eq(3, n.ById.Count, "число камер");
    Eq("Склад 1", n.Get("7a1c0000000040008000000000000037"), "имя по id без дефисов");
    Eq("Парковка 2 (бывшая парковка 2 .34)", n.Get("7a1c0000-0000-4000-8000-0000000000b2"), "имя со скобками");
    Eq("Cервер 1", n.ServerName, "имя сервера");
}

static void RetentionLookup()
{
    var cfg = new ToolConfig
    {
        DefaultRetentionDays = 90,
        CameraRetentionDays = new(StringComparer.OrdinalIgnoreCase)
        {
            ["офис центр"] = 14,
            ["7a1c0000-0000-4000-8000-0000000000b2"] = 30,
        },
    };
    Eq(14, cfg.RetentionFor("7a1c00000000400080000000000000a1", "Офис центр"), "по имени без учёта регистра");
    Eq(30, cfg.RetentionFor("7a1c00000000400080000000000000b2", "Парковка 2"), "по GUID с дефисами");
    Eq(90, cfg.RetentionFor("00000000000000000000000000000001", "Прочая"), "по умолчанию");
}

static void ScanFakeArchive()
{
    using var fx = new FakeArchive();
    var r = fx.Scan();

    Eq(2, r.Drives.Count, "дисков");
    Eq(3, r.Cameras.Count, "камер");
    Eq("Cервер 1", r.ServerName, "сервер");

    var heavy = r.Cameras.Single(c => c.Name == "Склад 1");
    Eq(2, heavy.BytesByDrive.Count, "камера на двух дисках");
    Eq(3, heavy.FragmentCount, "фрагментов у тяжёлой камеры");
    Eq(12 * FakeArchive.Blob + 85 * 3, heavy.Bytes, "объём тяжёлой камеры (блоки + .fidx)");
    Near(40, heavy.DepthDays, 0.1, "глубина = возраст самого старого блока");
    True(heavy.IsHeavy, "тяжёлая камера отмечена");
    True(!heavy.ExceedsRetention, "40 дн. при сроке 90 — не нарушение");
    Eq(r.Cameras[0].Id, heavy.Id, "сортировка по объёму в сутки");

    var office = r.Cameras.Single(c => c.Name == "Офис центр");
    Eq(14, office.RetentionDays ?? 0, "срок офиса из настроек");
    True(office.ExceedsRetention, "20 дн. при сроке 14 — нарушение");
    True(!office.IsHeavy, "офис не тяжёлый");

    var unknown = r.Cameras.Single(c => c.Id == "0123456789abcdef0123456789abcdef");
    Eq("01234567-89ab-cdef-0123-456789abcdef", unknown.Name, "неизвестная камера показана как GUID");

    var d1 = r.Drives.Single(d => d.ArchiveRoot == fx.Root1);
    Eq(4096L, d1.DbBytes, "объём db");
    Eq(1000L + 300, d1.OtherArchiveBytes, "прочее в архиве (.dsc + xml)");
    True(d1.IsActive, "диск с записью за сутки активен");
    var d2 = r.Drives.Single(d => d.ArchiveRoot == fx.Root2);
    True(!d2.IsActive, "диск без записи за сутки неактивен");

    Near(100, r.Cameras.Sum(c => c.SharePercent), 0.001, "сумма долей = 100%");
}

static void JsonAndHtml()
{
    using var fx = new FakeArchive();
    var r = fx.Scan();
    var json = Path.Combine(fx.Dir, "r.json");
    ReportStore.SaveJson(r, json);
    var back = ReportStore.LoadJson(json);
    Eq(r.Cameras.Count, back.Cameras.Count, "камеры после JSON");
    Eq(r.Cameras[0].Bytes, back.Cameras[0].Bytes, "объём после JSON");
    True(File.ReadAllText(json, Encoding.UTF8).Contains("Склад 1"), "кириллица в JSON без экранирования");

    var second = ReportStore.LoadJson(json);
    second.ServerName = "Сервер 2";
    var html = HtmlReport.Render(new[] { r, second });
    True(html.Contains("Все серверы"), "сводный блок");
    True(html.Contains("Сервер «Сервер 2»"), "секция второго сервера");
    True(html.Contains("сверх срока"), "отметка нарушения срока");
    True(html.Contains("Парковка 2 (бывшая парковка 2 .34)") || html.Contains("Склад 1"), "имена камер");
    True(!html.Contains("<script src"), "без внешних скриптов");
    True(HtmlReport.Render(new[] { r }).Contains("<h2>Сводка</h2>"), "одиночный отчёт");
}

// ---------------------------------------------------------------------------

static void Eq<T>(T expected, T actual, string what)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"{what}: ожидалось «{expected}», получено «{actual}»");
}

static void Near(double expected, double actual, double tol, string what)
{
    if (Math.Abs(expected - actual) > tol) throw new Exception($"{what}: ожидалось {expected}, получено {actual}");
}

static void True(bool cond, string what) { if (!cond) throw new Exception(what); }

static void NrbfRetention()
{
    var bytes = FakeMacroscopConfig.Build(90, 14);
    var (g, offsets) = MacroscopArchiveTool.Core.Nrbf.NrbfReader.ReadAll(bytes);
    Eq(1, offsets.Count, "один поток NRBF после своего заголовка");
    True(offsets[0] == FakeMacroscopConfig.WrapperHeader.Length, "поток найден за заголовком-обёрткой");
    var r = MacroscopConfig.Extract(g).ToDictionary(x => x.CameraId);
    Eq(4, r.Count, "распознано камер (слои прореживания не считаются)");
    var rec = r["7a1c00000000400080000000000000c3"];
    Eq("Приёмная", rec.Name, "камера в формате SecChannel");
    Eq(14.0, rec.MaxDays ?? 0, "срок SecChannel");
    var s = rec.Settings ?? throw new Exception("нет настроек записи");
    Eq(true, s.ArchiveEnabled ?? false, "запись включена");
    Eq(2, s.ArchiveMode ?? -1, "код режима записи (enum)");
    Eq(1, s.ArchiveStream ?? -1, "код потока в архив");
    Eq(2, s.StreamFormat ?? -1, "кодек берётся из параметров потока, который пишется в архив (альтернативный)");
    Eq(12.0, s.ArchiveFpsLimit ?? 0, "лимит fps архива");
    Eq(true, s.ArchiveFpsLimitEnabled ?? false, "лимит fps включён");
    Eq(true, s.ThinningEnabled ?? false, "прореживание: включён второй слой");
    Eq(7, s.PostRecordSec ?? 0, "постзапись");
    Eq(false, s.UsesCameraFps ?? true, "свой fps");
    Eq(15.0, s.Fps ?? 0, "fps из Macroscop");
    Eq("постоянно · альтернативный поток · кодек 2 · ≤12 к/с · прореживание", SettingNames.Summary(s), "строка настроек в отчёте");
    var tree = MacroscopConfig.DumpTree(WriteTemp(FakeMacroscopConfig.Build(90, 14)));
    True(tree.Contains("== Корневой объект ==") && tree.Contains("secChannels"), "дерево файла конфигурации");
    True(!tree.Contains(FakeMacroscopConfig.Secret), "пароль не попал в дерево");
    var heavy = r["7a1c0000000040008000000000000037"];
    Eq(90.0, heavy.MaxDays ?? 0, "макс. срок камеры с полями в самом объекте");
    Eq(90.0, heavy.MinDays ?? 0, "мин. срок");
    Eq("Склад 1", heavy.Name, "имя камеры");
    var office = r["7a1c00000000400080000000000000a1"];
    Eq(14.0, office.MaxDays ?? 0, "14 дней у офисной камеры");
    var nested = r["0123456789abcdef0123456789abcdef"];
    Eq(30.0, nested.MinDays ?? 0, "мин. срок во вложенном объекте (поиск владельца)");
    Eq(60.0, nested.MaxDays ?? 0, "макс. срок во вложенном объекте");
    Eq("Вложенная", nested.Name, "имя из авто-свойства");
}

static string WriteTemp(byte[] data)
{
    var p = Path.Combine(Path.GetTempPath(), "mat-tree-" + Guid.NewGuid().ToString("N") + ".CmnConf");
    System.IO.File.WriteAllBytes(p, data);
    return p;
}

static void NrbfDumpHidesSecrets()
{
    var dir = Path.Combine(Path.GetTempPath(), "mat-dump-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        var f = Path.Combine(dir, "SingleServerConfig.CmnConf");
        System.IO.File.WriteAllBytes(f, FakeMacroscopConfig.Build(90, 14));
        var dump = MacroscopConfig.DumpStructure(f);
        True(!dump.Contains(FakeMacroscopConfig.Secret), "пароль камеры не попал в дамп");
        True(!dump.Contains("192.168.1.10"), "адрес камеры не попал в дамп");
        True(dump.Contains("Склад 1"), "имя камеры есть в дампе");
        True(dump.Contains("Распознано камер: 4"), "итог распознавания в дампе");
        True(dump.Contains("== Распределение кодов =="), "распределение кодов в дампе");
        True(dump.Contains("режим: 2: 1 кам."), "код режима в распределении");
    }
    finally { Directory.Delete(dir, true); }
}

static void RetentionFromBackups()
{
    using var fx = new FakeArchive();
    const string serverId = "5e7f0000000040008000000000000001";
    void Backup(string stamp, int heavyDays)
    {
        var p = Path.Combine(fx.Root1, "ConfigsBackups", stamp, serverId, "ServerConfigs", "ServerConfigs");
        Directory.CreateDirectory(p);
        System.IO.File.WriteAllBytes(Path.Combine(p, "SingleServerConfig.CmnConf"), FakeMacroscopConfig.Build(heavyDays, 14));
    }
    Backup("2026-10-20 12.00.00.000", 365);
    Backup("2026-10-21 12.40.56.354", 30);   // newest: heavy camera 30 days

    var names = CameraNames.FromLogsDir(fx.Logs);
    Eq("5e7f0000-0000-4000-8000-000000000001", names.ServerId, "id сервера из лога");
    var ret = MacroscopConfig.Load(new[] { fx.Root1, fx.Root2 }, names.ServerId);
    Eq(4, ret.ById.Count, "камер в копии");
    Eq(new DateTime(2026, 10, 21, 12, 40, 56, 354), ret.BackupTimeLocal ?? default, "выбрана новейшая копия");

    var cfg = new ToolConfig { CameraRetentionDays = new(StringComparer.OrdinalIgnoreCase) { ["Офис центр"] = 7 } };
    var r = new ArchiveScanner().Scan(new[] { fx.Root1, fx.Root2 }, names, cfg, macroscopRetention: ret);
    var heavy = r.Cameras.Single(c => c.Name == "Склад 1");
    Eq(30, heavy.RetentionDays ?? 0, "срок из конфигурации Macroscop");
    Eq("macroscop", heavy.RetentionSource, "источник — Macroscop");
    True(heavy.ExceedsRetention, "40 дн. при сроке 30 — сверх срока");
    var office = r.Cameras.Single(c => c.Name == "Офис центр");
    Eq(7, office.RetentionDays ?? 0, "mat.json важнее конфигурации");
    Eq("mat.json", office.RetentionSource, "источник — mat.json");
    True(r.RetentionSource.Contains("резервная копия от 21.10.2026 12:40"), "источник сроков в отчёте");
    True(HtmlReport.Render(new[] { r }).Contains("30 дн."), "срок в HTML");

    var broken = Path.Combine(fx.Root2, "ConfigsBackups", "2026-10-22 00.00.00.000", serverId, "ServerConfigs", "ServerConfigs");
    Directory.CreateDirectory(broken);
    System.IO.File.WriteAllBytes(Path.Combine(broken, "SingleServerConfig.CmnConf"), new byte[] { 1, 2, 3, 4, 5 });
    var fallback = MacroscopConfig.Load(new[] { fx.Root1, fx.Root2 }, names.ServerId);
    Eq(4, fallback.ById.Count, "битая новейшая копия пропущена, взята предыдущая");
    Eq(new DateTime(2026, 10, 21, 12, 40, 56, 354), fallback.BackupTimeLocal ?? default, "время предыдущей копии");
    True(fallback.Warnings.Count == 1 && fallback.Warnings[0].Contains("2026-10-22"), "предупреждение о пропущенной копии");

    foreach (var d in Directory.GetDirectories(Path.Combine(fx.Root1, "ConfigsBackups"))) Directory.Delete(d, true);
    var bad = MacroscopConfig.Load(new[] { fx.Root1, fx.Root2 }, names.ServerId);
    Eq(0, bad.ById.Count, "только битая копия — сроков нет");
    True(bad.Warnings.Count == 1 && bad.Warnings[0].Contains("mat.json"), "предупреждение и откат на mat.json");
}

partial class Program
{
    internal const string SampleLog = """
[2026-10-20 13:48:11,840 Thread=.NET Long Running Task]
apply singleServerConfig on current server:
	revId: 9c000000-0000-4000-8000-000000000001, id: 9c000000-0000-4000-8000-000000000002, name: Конфигурация 1
SERVER:
	id: 5e7f0000-0000-4000-8000-000000000001, port: 8080, serverUrl: http://10.0.0.5:8080, name: Cервер 1 isReplicationServer: False, useFastReserving: False
CHANNEL_BINDINGS:
	channelId: 7a1c0000-0000-4000-8000-0000000000a1(Офис центр), serverId: 5e7f0000-0000-4000-8000-000000000001(Cервер 1)
	channelId: 7a1c0000-0000-4000-8000-000000000037(Склад 1), serverId: 5e7f0000-0000-4000-8000-000000000001(Cервер 1)
	channelId: 7a1c0000-0000-4000-8000-0000000000b2(Парковка 2 (бывшая парковка 2 .34)), serverId: 5e7f0000-0000-4000-8000-000000000001(Cервер 1)
USERS:
	name: root, canConfigure: True
""";
}

/// <summary>Two fake archive drives laid out exactly like Macroscop 4.7.</summary>
sealed class FakeArchive : IDisposable
{
    public const long Blob = 2L * 1024 * 1024; // stands in for the 64 MiB containers
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "mat-selftest-" + Guid.NewGuid().ToString("N"));
    public string Root1 => Path.Combine(Dir, "A", "MacroscopArchive");
    public string Root2 => Path.Combine(Dir, "B", "MacroscopArchive");
    public string Logs => Path.Combine(Dir, "Logs_Macroscop");
    private readonly DateTime _now = DateTime.UtcNow;

    private const string Heavy = "7a1c0000000040008000000000000037";   // Склад 1, 90 дней
    private const string Office = "7a1c00000000400080000000000000a1";  // Офис центр, 14 дней
    private const string Unknown = "0123456789abcdef0123456789abcdef"; // нет в логе

    public FakeArchive()
    {
        // Drive A: active (fresh writes), heavy camera with 2 fragments + office camera.
        Fragment(Root1, Heavy, "f1", 8, _now.AddDays(-40));
        Fragment(Root1, Heavy, "f2", 2, _now.AddHours(-1));
        Fragment(Root1, Office, "o1", 1, _now.AddDays(-20));
        Fragment(Root1, Unknown, "u1", 1, _now.AddDays(-10));
        File(Path.Combine(Root1, "media", "0d5c0000000040008000000000000001.dsc"), 1000);
        File(Path.Combine(Root1, "ArchiveLogicDrive.xml"), 300);
        File(Path.Combine(Root1, "db", "PG_16_202307071", "22462", "1249"), 4096);

        // Drive B: no writes for 3 days.
        Fragment(Root2, Heavy, "f3", 2, _now.AddDays(-3));
        Directory.CreateDirectory(Path.Combine(Root2, "db"));

        Directory.CreateDirectory(Path.Combine(Logs, "2026-10-20#13_48_09"));
        System.IO.File.WriteAllText(Path.Combine(Logs, "2026-10-20#13_48_09", "ServerConfiguration.log"), Program.SampleLog, Encoding.UTF8);
    }

    public ScanReport Scan()
    {
        var cfg = new ToolConfig { CameraRetentionDays = new(StringComparer.OrdinalIgnoreCase) { ["Офис центр"] = 14 } };
        return new ArchiveScanner().Scan(new[] { Root1, Root2 }, CameraNames.FromLogsDir(Logs), cfg);
    }

    private static void Fragment(string root, string cam, string frag, int blobs, DateTime oldestUtc)
    {
        var dir = Path.Combine(root, "media", cam, "1", "1", frag);
        for (int i = 0; i < blobs; i++)
        {
            var p = Path.Combine(dir, $"blob{i}");
            File(p, Blob);
            System.IO.File.SetLastWriteTimeUtc(p, oldestUtc.AddMinutes(i * 30));
        }
        File(dir + ".fidx", 85);
    }

    private static void File(string path, long size)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = new FileStream(path, FileMode.Create);
        fs.SetLength(size);
    }

    public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
}

// ---- Fake Macroscop configuration, serialized with the real BinaryFormatter (test-only) ----
#pragma warning disable SYSLIB0011
[Serializable] sealed class FakeThinningLayer { public bool _layerEnabled = true; public double _minDeepnessInDays = 7; public double _maxDeepnessInDays = 14; public int _requiredFps = 1; }
[Serializable] sealed class FakeConnection { public string _host = "192.168.1.10"; public string _login = "admin"; public string _password = FakeMacroscopConfig.Secret; }
[Serializable] sealed class FakeChannelConfig
{
    public Guid _id; public string _name = ""; public FakeConnection _connection = new();
    public bool _arcRecTempEnabled = true; public double _archiveMinDeepnessInDays; public double _archiveMaxDeepnessInDays;
    public FakeThinningLayer[] _thinning = { new(), new() };
}
[Serializable] sealed class FakeArchiveSettings { public double _archiveMinDeepnessInDays; public double _archiveMaxDeepnessInDays; public TimeSpan _pre = TimeSpan.FromSeconds(1); }
[Serializable] sealed class FakeNestedChannel { public Guid Id { get; set; } public string Name { get; set; } = ""; public FakeArchiveSettings Archive { get; set; } = new(); }
enum FakeArchiveModes { AlwaysOn = 0, ByCommand = 1, ByMotion = 2 }
enum FakeStreamTypes { Main = 0, Alternative = 1 }
enum FakeVideoStreamFormats { Mjpeg = 0, Mpeg4 = 1, H264 = 2, H265 = 3 }
[Serializable] sealed class FakeSubStream { public FakeStreamTypes streamType; public FakeVideoStreamFormats streamFormat; public int liveFrameRate = 10; }
[Serializable] sealed class FakeThinningSettings { public FakeThinningLayer[] _layers = { new() { _layerEnabled = false }, new() { _layerEnabled = true } }; }
/// <summary>Same member names as Alarus.Config.SecChannel in Macroscop 4.7 (see dump-config).</summary>
[Serializable] sealed class FakeSecChannel
{
    public Guid id; public string name = ""; public string login = "admin"; public string password = FakeMacroscopConfig.Secret;
    public FakeVideoStreamFormats streamFormat = FakeVideoStreamFormats.Mjpeg;   // legacy field, 0 in real configs
    public FakeSubStream mainSubstreamParams = new() { streamType = FakeStreamTypes.Main, streamFormat = FakeVideoStreamFormats.H265 };
    public FakeSubStream altSubstreamParams = new() { streamType = FakeStreamTypes.Alternative, streamFormat = FakeVideoStreamFormats.H264 };
    public bool archiveEnabled = true; public FakeArchiveModes archiveMode = FakeArchiveModes.ByMotion;
    public int arcRecTemp = 12; public bool arcRecTempEnabled = true;
    public TimeSpan archiveDepth = TimeSpan.FromHours(10);
    public double _archiveMinDeepnessInDays = 14; public double _archiveMaxDeepnessInDays = 14;
    public int arcPreMDRecTime = 6; public int arcPostMDRecTime = 7;
    public FakeStreamTypes archiveStreamType = FakeStreamTypes.Alternative;
    public bool isMotionDetectorEnabled = true; public bool isCamMDEnabled = false;
    public bool isUsingCamFrameRateSetting = false; public int liveFrameRate = 15;
    public bool isUsingCamResolutionSetting = true;
    public FakeThinningSettings _archiveThinningSettings = new();
}
[Serializable] sealed class FakeServerConfig
{
    public Dictionary<Guid, FakeSecChannel> secChannels = new();
    public List<FakeChannelConfig> _channels = new();
    public Dictionary<Guid, FakeNestedChannel> _nested = new();
    public Dictionary<string, object> _misc = new() { ["port"] = 8080, ["ratio"] = 1.5m, ["when"] = new DateTime(2026, 10, 21), ["ch"] = 'Я', ["blob"] = new byte[] { 1, 2, 3 } };
    public int[,] _grid = new int[2, 3];
}

static class FakeMacroscopConfig
{
    public const string Secret = "SECRET-PASS-12345";
    public static readonly byte[] WrapperHeader = { 0x4D, 0x43, 0x46, 0x47, 0x01, 0x00, 0x00, 0x00, 0xFF, 0xFE };

    public static byte[] Build(int heavyDays, int officeDays)
    {
        var cfg = new FakeServerConfig();
        cfg._channels.Add(new FakeChannelConfig { _id = Guid.Parse("7a1c0000-0000-4000-8000-000000000037"), _name = "Склад 1", _archiveMinDeepnessInDays = heavyDays, _archiveMaxDeepnessInDays = heavyDays });
        cfg._channels.Add(new FakeChannelConfig { _id = Guid.Parse("7a1c0000-0000-4000-8000-0000000000a1"), _name = "Офис центр", _archiveMinDeepnessInDays = officeDays, _archiveMaxDeepnessInDays = officeDays });
        var nid = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        cfg._nested[nid] = new FakeNestedChannel { Id = nid, Name = "Вложенная", Archive = new FakeArchiveSettings { _archiveMinDeepnessInDays = 30, _archiveMaxDeepnessInDays = 60 } };
        var rid = Guid.Parse("7a1c0000-0000-4000-8000-0000000000c3");
        cfg.secChannels[rid] = new FakeSecChannel { id = rid, name = "Приёмная" };

        using var ms = new MemoryStream();
        ms.Write(WrapperHeader);
        new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter().Serialize(ms, cfg);
        ms.Write(new byte[] { 0xDE, 0xAD });
        return ms.ToArray();
    }
}
#pragma warning restore SYSLIB0011
