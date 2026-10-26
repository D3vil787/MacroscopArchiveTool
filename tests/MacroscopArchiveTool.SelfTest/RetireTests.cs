using MacroscopArchiveTool.Core.Migration;

/// <summary>Disk retirement scenario on a fake operating system: drive letters are folders, nothing touches real disks.</summary>
partial class Program
{
    internal static void RetireHappyPath()
    {
        using var fx = new RetireFixture();
        var o = fx.Orchestrator();
        var s = o.Plan('J', TargetKind.Vhdx, 'E', new CopyOptions { RetryDelay = TimeSpan.Zero });
        R.Eq("11111111-2222-3333-4444-555555555555", s.ArchiveLogicDriveId, "id логического диска");
        R.True(s.VhdxPath!.Contains("_MacroscopRetired_J_"), "имя файла VHDX: " + s.VhdxPath);
        R.True(s.VhdxMaxBytes > s.SourceArchiveBytes, "запас размера VHDX");
        R.True(s.DrainForecastUtc == null || s.DrainForecastUtc > DateTime.UtcNow.AddDays(-200), "прогноз");
        var checks = o.Precheck(s);
        R.True(checks.All(c => c.Level != CheckLevel.Error), "проверки без ошибок: " + string.Join("; ", checks.Select(c => c.Text)));

        o.CreateTarget(s);
        R.Eq(RetireStage.TargetReady, s.Stage, "этап после создания");
        R.True(fx.Ops.Letters.ContainsKey(s.TempLetter), "VHDX на временной букве");

        var r = o.CopyLive(s, null, CancellationToken.None);
        R.Eq(RetireStage.Copied, s.Stage, "этап после копирования");
        R.True(!Directory.Exists(Path.Combine(fx.Ops.RootOf(s.TempLetter), "MacroscopArchive", "db")), "база событий не копируется на ходу");

        // Macroscop changes something after the live copy; the switch must pick it up.
        File.WriteAllBytes(Path.Combine(fx.SourceArchive, "db", "PG_16", "16400"), new byte[] { 9, 9, 9 });
        int asked = 0;
        o.Switch(s, running => { asked++; fx.Ops.MacroscopRunning = false; return true; }, null, CancellationToken.None);
        R.Eq(1, asked, "пользователя попросили остановить Macroscop");
        R.Eq(RetireStage.Switched, s.Stage, "этап после переключения");
        R.Eq(s.TargetVolumeId, fx.Ops.Letters['J'], "буква J теперь у нового тома");
        R.True(!fx.Ops.Letters.ContainsKey(s.TempLetter), "временная буква освобождена");
        R.True(!fx.Ops.Letters.ContainsValue(s.SourceVolumeId!), "старый диск без буквы");
        R.True(fx.Ops.Tasks.Contains(s.AttachTaskName!), "задача автоподключения");
        R.Eq("MacroscopServer", fx.Ops.AttachStartsService, "задача запускает Macroscop после подключения диска");
        R.Eq("Manual", fx.Ops.ServiceMode, "служба Macroscop на ручном запуске");
        R.True(fx.Ops.OfflineDisks.Contains("disk-" + s.SourceVolumeId) && s.SourceDiskOffline, "старый диск вне сети");
        R.Eq(string.Join(",", fx.Ops.PostgresServices), string.Join(",", fx.Ops.Started), "PostgreSQL запущен обратно");
        R.Eq(3, File.ReadAllBytes(Path.Combine(fx.Ops.RootOf('J'), "MacroscopArchive", "db", "PG_16", "16400")).Length, "база скопирована при переключении");
        R.True(o.Remaining(s).Bytes > 0, "записи ещё на диске");

        var reloaded = RetireState.LoadAll(fx.StateDir).Single();
        R.Eq(RetireStage.Switched, reloaded.Stage, "состояние сохранено");
        R.True(reloaded.Log.Count > 5, "журнал сохранён");

        R.Throws(() => o.Finish(s, true), "нельзя завершить, пока есть записи");
        Directory.Delete(Path.Combine(fx.Ops.RootOf('J'), "MacroscopArchive", "media"), true);   // retention did its job
        Directory.CreateDirectory(Path.Combine(fx.Ops.RootOf('J'), "MacroscopArchive", "media"));
        o.Finish(s, deleteVhdx: false);
        R.Eq(RetireStage.Finished, s.Stage, "вывод завершён");
        R.True(!fx.Ops.Letters.ContainsKey('J'), "буква J свободна");
        R.True(fx.Ops.Tasks.Count == 0, "задача удалена");
        R.True(fx.Ops.Detached.Contains(s.VhdxPath), "VHDX отключён");
        R.Eq("Auto", fx.Ops.ServiceMode, "режим запуска службы возвращён после завершения");
    }

    internal static void RetireSwitchFailureRollsBack()
    {
        using var fx = new RetireFixture();
        var o = fx.Orchestrator();
        var s = o.Plan('J', TargetKind.Vhdx, 'E', new CopyOptions { RetryDelay = TimeSpan.Zero });
        o.CreateTarget(s);
        o.CopyLive(s, null, CancellationToken.None);
        // Simulated reboot: Windows detached the VHDX; switching must mount it again.
        fx.Ops.Unmounted = fx.Ops.Letters[s.TempLetter];
        fx.Ops.Letters.Remove(s.TempLetter);
        o.CopyLive(s, null, CancellationToken.None);
        R.Eq(1, fx.Ops.Mounts, "VHDX подключён заново после перезагрузки");
        var srcVol = fx.Ops.Letters['J'];
        fx.Ops.FailAssignTo = 'J';
        fx.Ops.MacroscopRunning = false;
        R.Throws(() => o.Switch(s, _ => true, null, CancellationToken.None), "ошибка назначения буквы");
        R.Eq(srcVol, fx.Ops.Letters['J'], "исходный диск снова J");
        R.True(fx.Ops.Letters.ContainsKey(s.TempLetter), "копия снова на временной букве");
        R.Eq(RetireStage.Copied, s.Stage, "этап не сдвинулся");
        R.Eq(0, fx.Ops.OfflineDisks.Count, "при сбое старый диск остаётся в сети");
        R.Eq(string.Join(",", fx.Ops.PostgresServices), string.Join(",", fx.Ops.Started), "PostgreSQL запущен после ошибки");

        // A second attempt works and a manual rollback restores the old disk.
        fx.Ops.FailAssignTo = null; fx.Ops.Started.Clear();
        o.Switch(s, _ => true, null, CancellationToken.None);
        R.Eq(RetireStage.Switched, s.Stage, "повтор удался");
        o.Rollback(s);
        R.Eq(srcVol, fx.Ops.Letters['J'], "откат: исходный диск J");
        R.Eq(0, fx.Ops.Tasks.Count, "откат: задача удалена");
        R.Eq(0, fx.Ops.OfflineDisks.Count, "откат: старый диск снова в сети");
        R.Eq("Auto", fx.Ops.ServiceMode, "откат: режим запуска службы возвращён");
    }

    internal static void RetirePrecheckAndCancel()
    {
        using var fx = new RetireFixture(activeSource: true);
        var o = fx.Orchestrator();
        var s = o.Plan('J', TargetKind.Vhdx, 'E', new CopyOptions());
        bool Blocked(RetireState st) => o.Precheck(st).Any(c => c.Level == CheckLevel.Error && c.Text.Contains("последняя запись"));
        R.True(Blocked(s), "активный диск — ошибка");
        R.True(o.Precheck(s).Any(c => c.Text.Contains("пройдут сутки") && c.Text.Contains("после ")), "в тексте сказано, когда станет можно");
        var saved = s.SourceNewestUtc;
        s.SourceNewestUtc = DateTime.UtcNow.AddHours(-3);
        R.True(Blocked(s), "роль неизвестна, 3 ч без записи — ещё ждём сутки");
        s.SourceRole = "Резервный архив";
        R.True(!Blocked(s), "резервный архив и 3 ч без записи — можно");
        s.SourceNewestUtc = DateTime.UtcNow.AddMinutes(-10);
        R.True(Blocked(s) && o.Precheck(s).Any(c => c.Text.Contains("пройдёт час")), "резервный архив, но запись 10 мин назад — ждём час");
        s.SourceRole = "Только архив";
        R.True(o.Precheck(s).Any(c => c.Text.Contains("поставьте диску роль")), "активная роль — подсказка сменить роль");
        s.AllowActiveSource = true;
        R.True(!Blocked(s), "разрешение переноса активного диска делает ошибку предупреждением");
        s.AllowActiveSource = false; s.SourceRole = null; s.SourceNewestUtc = DateTime.UtcNow.AddDays(-5);
        o.Refresh(s);
        R.True(s.SourceNewestUtc > DateTime.UtcNow.AddHours(-1), "«Проверить снова» перечитывает время последней записи");
        R.Eq(saved.HasValue, s.SourceNewestUtc.HasValue, "время записи есть");
        fx.Ops.Free = 1;
        R.True(o.Precheck(s).Any(c => c.Level == CheckLevel.Error && c.Text.Contains("Свободно")), "мало места — ошибка");
        var same = o.Plan('J', TargetKind.Volume, 'J', new CopyOptions());
        R.True(o.Precheck(same).Any(c => c.Level == CheckLevel.Error && c.Text.Contains("сам на себя")), "сам на себя");

        fx.Ops.Free = long.MaxValue / 4;
        o.CreateTarget(s);
        o.CopyLive(s, null, CancellationToken.None);
        R.Throws(() => o.Switch(s, _ => false, null, CancellationToken.None), "отмена ожидания остановки");
        R.Eq(RetireStage.Copied, s.Stage, "после отмены — этап копирования");
        R.Eq(0, fx.Ops.Started.Count, "PostgreSQL не трогали");
    }
}

static class R
{
    public static void Eq<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what}: ожидалось «{expected}», получено «{actual}»");
    }
    public static void True(bool c, string what) { if (!c) throw new Exception(what); }
    public static void Throws(Action a, string what)
    {
        try { a(); } catch (Exception) { return; }
        throw new Exception(what + ": ожидалось исключение");
    }
}

sealed class RetireFixture : IDisposable
{
    public readonly string Root = Path.Combine(Path.GetTempPath(), "mat_retire_" + Guid.NewGuid().ToString("N"));
    public readonly FakeSystemOps Ops;
    public string StateDir => Path.Combine(Root, "state");
    public string SourceArchive => Path.Combine(Ops.RootOf('J'), "MacroscopArchive");

    public RetireFixture(bool activeSource = false)
    {
        Ops = new FakeSystemOps(Path.Combine(Root, "vol"));
        Ops.AddVolume('J'); Ops.AddVolume('E'); Ops.AddVolume('C');
        var a = SourceArchive;
        Directory.CreateDirectory(a);
        File.WriteAllText(Path.Combine(a, "ArchiveLogicDrive.xml"),
            "<ArchiveLogicDrive><ArchiveLogicDriveId>11111111-2222-3333-4444-555555555555</ArchiveLogicDriveId></ArchiveLogicDrive>");
        var when = activeSource ? DateTime.UtcNow.AddMinutes(-5) : DateTime.UtcNow.AddDays(-30);
        var frag = Path.Combine(a, "media", "7a1c0000000040008000000000000037", "1", "1", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(frag);
        for (int i = 0; i < 3; i++)
        {
            var f = Path.Combine(frag, $"{i:D8}");
            File.WriteAllBytes(f, new byte[2 * 1024 * 1024 + i]);
            File.SetLastWriteTimeUtc(f, when.AddMinutes(-i));
        }
        Directory.CreateDirectory(Path.Combine(a, "db", "PG_16"));
        File.WriteAllBytes(Path.Combine(a, "db", "PG_16", "16400"), new byte[] { 1 });
    }

    public RetireOrchestrator Orchestrator() => new(Ops, StateDir) { LogsDir = Path.Combine(Root, "no-logs") };

    public void Dispose() { try { Directory.Delete(Root, true); } catch (Exception) { } }
}

/// <summary>Volumes are folders; a drive letter maps to a volume id.</summary>
sealed class FakeSystemOps : ISystemOps
{
    private readonly string _base;
    public readonly Dictionary<string, string> Volumes = new();          // id -> folder
    public readonly Dictionary<char, string> Letters = new();            // letter -> id
    public readonly HashSet<string> Tasks = new();
    public readonly List<string> Detached = new();
    public readonly List<string> PostgresServices = new() { "postgresql-x64-16" };
    public readonly List<string> Started = new();
    public bool MacroscopRunning = true;
    public char? FailAssignTo;
    public long Free = long.MaxValue / 4;

    public FakeSystemOps(string baseDir) { _base = baseDir; }

    public string AddVolume(char letter)
    {
        var id = $@"\\?\Volume{{{Guid.NewGuid()}}}\";
        var dir = Path.Combine(_base, Volumes.Count.ToString());
        Directory.CreateDirectory(dir);
        Volumes[id] = dir;
        Letters[letter] = id;
        return id;
    }

    public readonly HashSet<string> OfflineDisks = new();
    public int Mounts;

    public void EnsureVhdxMounted(string vhdxPath, char letter, Action<string> log)
    {
        if (Letters.ContainsKey(letter)) return;
        Mounts++;
        Letters[letter] = Unmounted ?? throw new IOException("нечего подключать");
        Unmounted = null;
    }
    /// <summary>A volume detached by a simulated reboot.</summary>
    public string? Unmounted;

    public DiskInfo? DiskOf(char letter) => Letters.TryGetValue(letter, out var id) ? new DiskInfo("disk-" + id, 3, "Port 2: Bus 0: Target 3: LUN 1", 0) : null;
    public void SetDiskOffline(string uniqueId, bool offline, Action<string> log)
    {
        if (offline) OfflineDisks.Add(uniqueId); else OfflineDisks.Remove(uniqueId);
    }

    public bool IsAdministrator() => true;
    public string RootOf(char letter) => Letters.TryGetValue(char.ToUpperInvariant(letter), out var id) ? Volumes[id] : Path.Combine(_base, "unmounted-" + letter);
    public IReadOnlyList<VolumeInfo> GetVolumes() => Letters.Select(l => new VolumeInfo(l.Key, "", "NTFS", Free, Free, false, null)).ToList();
    public char FindFreeLetter(IEnumerable<char> avoid)
    {
        for (var c = 'Z'; c >= 'F'; c--) if (!Letters.ContainsKey(c) && !avoid.Contains(c)) return c;
        throw new InvalidOperationException();
    }
    public void CreateVhdx(string vhdxPath, long maxBytes, string label, char letter, Action<string> log)
    {
        File.WriteAllText(vhdxPath, "vhdx");
        AddVolume(letter);
    }
    public void AttachVhdx(string vhdxPath, Action<string> log) { }
    public void DetachVhdx(string vhdxPath, Action<string> log) => Detached.Add(vhdxPath);
    public string GetVolumeId(char letter) => Letters[letter];
    public void RemoveLetter(char letter, Action<string> log)
    {
        if (!Letters.Remove(letter)) throw new IOException($"{letter}: не назначена");
    }
    public void AssignLetter(char letter, string volumeId, Action<string> log)
    {
        if (FailAssignTo == letter && Volumes[volumeId] != Volumes.Values.First()) { FailAssignTo = null; throw new IOException("сбой mountvol"); }
        if (Letters.ContainsKey(letter)) throw new IOException($"{letter}: занята");
        Letters[letter] = volumeId;
    }
    public IReadOnlyList<string> RunningMacroscop() => MacroscopRunning ? new[] { "Macroscop.exe (PID 1)" } : Array.Empty<string>();
    public bool TryStopMacroscopService(Action<string> log) => false;
    public IReadOnlyList<string> StopPostgres(Action<string> log) => PostgresServices.ToList();
    public void StartServices(IEnumerable<string> names, Action<string> log) => Started.AddRange(names);
    public void CreateAttachTask(string taskName, string vhdxPath, char letter, string? startServiceAfter, Action<string> log) { Tasks.Add(taskName); AttachStartsService = startServiceAfter; }
    public string? AttachStartsService;
    public string ServiceMode = "Auto";
    public (string Name, string StartMode)? MacroscopService() => ("MacroscopServer", ServiceMode);
    public void SetServiceStartMode(string service, string mode, Action<string> log) => ServiceMode = mode;
    public void DeleteTask(string taskName, Action<string> log) => Tasks.Remove(taskName);
    public bool TaskExists(string taskName) => Tasks.Contains(taskName);
}
