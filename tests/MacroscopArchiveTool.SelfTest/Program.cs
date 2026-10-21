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
