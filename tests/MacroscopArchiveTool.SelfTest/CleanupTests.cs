using MacroscopArchiveTool.Core.Cleanup;

partial class Program
{
    internal static void CleanupScanApplyRestorePurge()
    {
        using var fx = new CleanupFixture();
        var engine = new CleanupEngine(fx.Env) { UtcNow = () => fx.Now };
        var o = new CleanupOptions { BackupDays = 30, BackupsToKeep = 10, LogDays = 90, ReportDays = 180 };
        var scan = engine.Scan(o);
        string Cats() => string.Join(", ", scan.Items.Select(i => $"{i.Category}:{i.Title}"));

        var backups = scan.Items.Single(i => i.Category == CleanupCategory.ConfigBackups);
        R.Eq(5, backups.Paths.Count, "старые копии: 15 всего, 10 новейших остаются, из остальных все старше 30 дн.");
        R.True(backups.Paths.All(p => !p.Contains(fx.NewestBackup)), "новейшая копия не трогается");

        var tablespaces = scan.Items.Single(i => i.Category == CleanupCategory.OldPostgres && i.Action == CleanupAction.Quarantine);
        R.True(tablespaces.Paths.Single().EndsWith("PG_12_201909212"), "папка PG_12 на диске");
        var uninstall = scan.Items.Single(i => i.Action == CleanupAction.UninstallPostgres);
        R.True(uninstall.Uninstaller != null && uninstall.Paths.Count == 2, "PostgreSQL 12: деинсталлятор, программа и данные — " + string.Join(";", uninstall.Paths));
        R.True(!uninstall.Paths.Any(p => p.Contains("postgredb16")), "данные действующего PostgreSQL не трогаются");

        R.True(scan.Items.Any(i => i.Category == CleanupCategory.DbBackups && i.Action == CleanupAction.ShowOnly), "резервные копии базы — только просмотр");
        var orphan = scan.Items.Single(i => i.Category == CleanupCategory.OrphanEventDb);
        R.True(orphan.Title.Contains(fx.OrphanDb) && orphan.Action == CleanupAction.ShowOnly, "база отключённого диска показана: " + Cats());

        var logs = scan.Items.Single(i => i.Category == CleanupCategory.MacroscopLogs);
        R.Eq(1, logs.Paths.Count, "из журналов уходит только старый сеанс без ServerConfiguration.log");
        var temp = scan.Items.Single(i => i.Category == CleanupCategory.ToolTemp);
        R.Eq(3, temp.Paths.Count, "загрузка обновления, .old и старый отчёт");

        // A new backup appears between the scan and the apply: the plan still matches the group by its key.
        Directory.CreateDirectory(Path.Combine(fx.Backups, "2026-10-22 12.00.00.000"));
        var plan = new CleanupPlan { Options = o, ItemIds = scan.Items.Where(i => i.Recommended).Select(i => i.Id).ToList() };
        var after = engine.Apply(plan);
        R.True(after.Warnings.Count == 0, "без ошибок: " + string.Join("; ", after.Warnings));
        R.Eq(1, fx.Env.Uninstalled.Count, "деинсталлятор PostgreSQL 12 запущен");
        R.True(!Directory.Exists(Path.Combine(fx.Archive, "db", "PG_12_201909212")), "PG_12 перенесена");
        R.True(Directory.Exists(Path.Combine(fx.Archive, "db", "PG_16_202307071")), "PG_16 на месте");
        R.Eq(11, Directory.GetDirectories(fx.Backups).Length, "осталось 10 новейших копий + посторонняя папка");
        R.True(Directory.Exists(Path.Combine(fx.VolA, CleanupEngine.QuarantineFolder)), "карантин на том же диске");

        var sets = engine.ListQuarantine();
        R.True(sets.Count >= 2 && sets.All(s => s.SetId == sets[0].SetId), "один набор карантина на каждом томе");
        R.True(sets.Any(s => s.Notes.Count == 1), "удаление программы отмечено как невосстановимое");

        var problems = engine.Restore(sets[0].SetId);
        R.Eq(17, Directory.GetDirectories(fx.Backups).Length, "копии конфигурации восстановлены");
        R.True(Directory.Exists(Path.Combine(fx.Archive, "db", "PG_12_201909212")), "PG_12 восстановлена");
        R.True(problems.Any(p => p.Contains("Не восстанавливается")), "предупреждение про удалённую программу");

        // Clean again and purge for good.
        var again = engine.Scan(o);
        engine.Apply(new CleanupPlan { Options = o, ItemIds = again.Items.Where(i => i.Category == CleanupCategory.ConfigBackups).Select(i => i.Id).ToList() });
        R.True(engine.ListQuarantine().Count > 0, "есть что удалять");
        engine.UtcNow = () => fx.Now.AddDays(20);
        var freed = engine.Purge(null, 14);
        R.True(freed > 0 && engine.ListQuarantine().Count == 0, "карантин старше 14 дн. удалён");
    }
}

sealed class FakeCleanupEnv : ICleanupEnvironment
{
    public List<string> Roots = new();
    public string MacroscopDataDir { get; set; } = "";
    public string PostgresRoot { get; set; } = "";
    public string AppDir { get; set; } = "";
    public string TempDir { get; set; } = "";
    public List<PostgresService> Services = new();
    public List<(string, long)> Dbs = new();
    public List<string> Uninstalled = new();
    public IReadOnlyList<string> VolumeRoots() => Roots;
    public IReadOnlyList<PostgresService> PostgresServices() => Services;
    public IReadOnlyList<(string Name, long Bytes)>? EventDatabases(PostgresService active, string password, out string? error) { error = null; return Dbs; }
    public void RunUninstaller(string exe, Action<string> log)
    {
        Uninstalled.Add(exe);
        File.Delete(exe);   // the real uninstaller removes most of the program folder
        Services.RemoveAll(s => s.Version == 12);
    }
    public void DeleteService(string name, Action<string> log) => Services.RemoveAll(s => s.Name == name);
}

sealed class CleanupFixture : IDisposable
{
    public readonly string Root = Path.Combine(Path.GetTempPath(), "mat_clean_" + Guid.NewGuid().ToString("N"));
    public readonly FakeCleanupEnv Env = new();
    public readonly DateTime Now = new(2026, 10, 22, 6, 0, 0, DateTimeKind.Utc);
    public string VolA => Path.Combine(Root, "A") + Path.DirectorySeparatorChar;
    public string VolC => Path.Combine(Root, "C") + Path.DirectorySeparatorChar;
    public string Archive => Path.Combine(VolA, "MacroscopArchive");
    public string Backups => Path.Combine(Archive, "ConfigsBackups");
    public string NewestBackup = "";
    public readonly string OrphanDb = "archive_event_" + new string('9', 32);
    private const string DriveId = "11111111-2222-3333-4444-555555555555";

    public CleanupFixture()
    {
        Env.Roots.AddRange(new[] { VolA, VolC });
        Directory.CreateDirectory(Archive);
        File.WriteAllText(Path.Combine(Archive, "ArchiveLogicDrive.xml"), $"<ArchiveLogicDrive><ArchiveLogicDriveId>{DriveId}</ArchiveLogicDriveId></ArchiveLogicDrive>");

        for (int i = 0; i < 15; i++)
        {
            var when = Now.ToLocalTime().AddDays(i < 3 ? -i : -40 - i);
            var name = when.ToString("yyyy-MM-dd HH.mm.ss.fff");
            if (i == 0) NewestBackup = name;
            var d = Path.Combine(Backups, name, "server", "ServerConfigs");
            Directory.CreateDirectory(d);
            File.WriteAllBytes(Path.Combine(d, "SingleServerConfig.CmnConf"), new byte[1000]);
        }
        Directory.CreateDirectory(Path.Combine(Backups, "not a backup"));

        Put(Path.Combine(Archive, "db", "PG_12_201909212", "16384", "1"), 500, 300);
        Put(Path.Combine(Archive, "db", "PG_16_202307071", "16384", "1"), 500, 1);
        Put(Path.Combine(Archive, "db", "backups", "events.backup"), 700, 1);

        var pg = Path.Combine(VolC, "Program Files", "PostgreSQL");
        Env.PostgresRoot = pg;
        Put(Path.Combine(pg, "12", "bin", "pg_ctl.exe"), 100, 300);
        Put(Path.Combine(pg, "12", "uninstall-postgresql.exe"), 100, 300);
        Put(Path.Combine(pg, "16", "bin", "pg_ctl.exe"), 100, 1);
        Put(Path.Combine(VolC, "ProgramData", "postgredb", "base", "1"), 900, 300);
        Put(Path.Combine(VolC, "ProgramData", "postgredb16", "base", "1"), 900, 1);
        Env.Services.Add(new PostgresService("postgresql-x64-12", 12, false, Path.Combine(pg, "12"), Path.Combine(VolC, "ProgramData", "postgredb")));
        Env.Services.Add(new PostgresService("postgresql-x64-16", 16, true, Path.Combine(pg, "16"), Path.Combine(VolC, "ProgramData", "postgredb16")));
        Env.Dbs.Add(("archive_event_" + DriveId.Replace("-", "_"), 1000));
        Env.Dbs.Add((OrphanDb, 2000));

        var logs = Path.Combine(VolC, "ProgramData", "Macroscop", "Logs_Macroscop");
        Env.MacroscopDataDir = Path.Combine(VolC, "ProgramData", "Macroscop");
        Put(Path.Combine(logs, "2026-01-01#10_00_00", "Error.log.1"), 100, 290);
        Put(Path.Combine(logs, "2026-02-01#10_00_00", "ServerConfiguration.log"), 100, 260);
        Put(Path.Combine(logs, "2026-10-22#10_00_00", "Error.log.1"), 100, 0);

        Env.AppDir = Path.Combine(VolC, "app");
        Env.TempDir = Path.Combine(VolC, "tmp");
        Put(Path.Combine(Env.TempDir, "MacroscopArchiveTool_update_0.3.0", "x.zip"), 100, 3);
        Put(Path.Combine(Env.AppDir, "MacroscopArchiveTool.exe.old"), 100, 3);
        Put(Path.Combine(Env.AppDir, "reports", "macroscop_old.json"), 100, 400);
        Put(Path.Combine(Env.AppDir, "reports", "macroscop_new.json"), 100, 1);
    }

    private void Put(string path, int size, int daysAgo)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        File.SetLastWriteTimeUtc(path, Now.AddDays(-daysAgo));
    }

    public void Dispose() { try { Directory.Delete(Root, true); } catch (Exception) { } }
}
