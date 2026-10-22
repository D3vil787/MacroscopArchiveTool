using MacroscopArchiveTool.Core;
using MacroscopArchiveTool.Core.Nrbf;

#pragma warning disable SYSLIB0011
[Serializable] sealed class FakeArchiveDriveSettings
{
    public string driveName = ""; public Guid driveId = Guid.NewGuid(); public string path = "";
    public long minFreeSpace = 102400; public long maxArchiveSize = 866341;
    public bool _emergencyDrive; public bool _duplicateDrive; public bool _enableDatabaseOnDrive; public bool _enableArchiveOnDrive = true;
}
[Serializable] sealed class FakeServerSettings { public List<FakeArchiveDriveSettings> archiveDrivesList = new(); public int serverPort = 8080; }

partial class Program
{
    internal static void DriveRolesAndDrainForecast()
    {
        var s = new FakeServerSettings();
        s.archiveDrivesList.Add(new FakeArchiveDriveSettings { driveName = "C:\\", _enableArchiveOnDrive = false, _enableDatabaseOnDrive = true });
        s.archiveDrivesList.Add(new FakeArchiveDriveSettings { driveName = "A:\\" });
        s.archiveDrivesList.Add(new FakeArchiveDriveSettings { driveName = "J:\\", _emergencyDrive = true });
        using var ms = new MemoryStream();
        new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter().Serialize(ms, s);
        var (g, _) = NrbfReader.ReadAll(ms.ToArray());
        var drives = MacroscopConfig.ExtractDrives(g);
        R.Eq(3, drives.Count, "дисков в настройках");
        R.Eq("Только БД", drives[0].Role, "роль C:");
        R.Eq("Только архив", drives[1].Role, "роль A:");
        R.Eq("Резервный архив", drives[2].Role, "роль J:");
        R.Eq(100L * 1024 * 1024 * 1024, drives[1].MinFreeBytes ?? 0, "мин. свободное место в байтах (МБ в конфигурации)");

        var report = new ScanReport { ScannedAtUtc = new DateTime(2026, 10, 22, 6, 0, 0, DateTimeKind.Utc), DefaultRetentionDays = 90 };
        var a = new DriveStat { Name = "A:", ArchiveRoot = "A:\\MacroscopArchive", IsActive = true };
        var j = new DriveStat { Name = "J:", ArchiveRoot = "J:\\MacroscopArchive", MediaBytes = 250 };
        j.DayCameraBytes["2026-09-24"] = new() { ["cam90"] = 100, ["cam14"] = 50 };
        j.DayCameraBytes["2026-09-20"] = new() { ["cam90"] = 100 };
        report.Drives.AddRange(new[] { a, j });
        report.Cameras.Add(new CameraStat { Id = "cam90", RetentionDays = 90 });
        report.Cameras.Add(new CameraStat { Id = "cam14", RetentionDays = 14 });
        ArchiveScanner.ApplyDriveConfig(report.Drives, drives);
        ReportAnalyzer.Analyze(report, 2.5);
        R.Eq("Резервный архив", j.Role, "роль подставлена в отчёт");
        R.True(a.DrainForecastUtc == null, "у активного диска прогноза нет");
        R.Eq(new DateTime(2026, 12, 24), j.DrainForecastUtc!.Value.ToLocalTime().Date, "J: опустеет: 24.09 + 90 дн. + 1");
        R.True(HtmlReport.Render(new[] { report }).Contains("опустеет ≈ 24.12.2026"), "прогноз в отчёте");
    }
}
#pragma warning restore SYSLIB0011
