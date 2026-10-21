using System.Globalization;
using System.Text.RegularExpressions;

namespace MacroscopArchiveTool.Core.Migration;

public enum CheckLevel { Ok, Warning, Error }

public sealed record Check(CheckLevel Level, string Text);

/// <summary>
/// Retires a Macroscop archive disk by moving its whole MacroscopArchive folder to a new volume and giving that volume
/// the old drive letter — the documented way to move an archive disk. Macroscop then keeps reading the old recordings
/// until retention removes them; nothing is written to the disk if its role is "Резервный архив".
/// Steps: Plan → Precheck → CreateTarget → CopyLive (Macroscop running) → Switch (short stop) → Finish (when empty).
/// Every step is logged and saved, so the process can be continued after closing the tool or a reboot.
/// </summary>
public sealed class RetireOrchestrator
{
    public const string TaskFolder = @"\MacroscopArchiveTool\";
    private const string DbFolder = "db";
    /// <summary>Extra space reserved on top of the archive size for the VHDX maximum size.</summary>
    private const double SizeMargin = 0.15;
    private const long MinExtraBytes = 10L * 1024 * 1024 * 1024;

    private readonly ISystemOps _ops;
    private readonly string _stateDir;
    public event Action<string>? Logged;

    public Func<ArchiveCopier> CopierFactory { get; set; } = () => new ArchiveCopier();
    /// <summary>Macroscop log folder and retention source used for the "disk will be empty on …" forecast.</summary>
    public string LogsDir { get; set; } = ToolConfig.DefaultLogsDir;

    public RetireOrchestrator(ISystemOps ops, string stateDir) { _ops = ops; _stateDir = stateDir; }

    // ------------------------------------------------------------------ plan & checks

    public RetireState Plan(char source, TargetKind kind, char targetVolume, CopyOptions options, bool allowActiveSource = false)
    {
        source = char.ToUpperInvariant(source); targetVolume = char.ToUpperInvariant(targetVolume);
        var archive = ArchiveRoot(source);
        long bytes = Directory.Exists(archive) ? SafeSize(archive) : 0;
        var s = new RetireState
        {
            Id = $"retire_{source}_{DateTime.Now:yyyyMMdd_HHmmss}",
            CreatedUtc = DateTime.UtcNow,
            Stage = RetireStage.Planned,
            SourceLetter = source,
            SourceArchiveBytes = bytes,
            ArchiveLogicDriveId = ReadLogicDriveId(archive),
            TargetKind = kind,
            TargetVolumeLetter = targetVolume,
            TargetLabel = $"RETIRED-{source}",
            Options = options,
            AllowActiveSource = allowActiveSource,
        };
        if (kind == TargetKind.Vhdx)
        {
            s.VhdxPath = Path.Combine(_ops.RootOf(targetVolume), $"_MacroscopRetired_{source}_{DateTime.Now:yyyy-MM-dd}.vhdx");
            s.VhdxMaxBytes = RequiredBytes(bytes);
        }
        Forecast(s);
        Log(s, $"План: {s.Title}. Архив на {source}: {HtmlReport.Bytes(bytes)}.");
        s.Save(_stateDir);
        return s;
    }

    public static long RequiredBytes(long archiveBytes) => (long)(archiveBytes * (1 + SizeMargin)) + MinExtraBytes;

    public List<Check> Precheck(RetireState s)
    {
        var c = new List<Check>();
        if (!_ops.IsAdministrator()) c.Add(new(CheckLevel.Error, "Запустите программу от имени администратора."));

        var archive = ArchiveRoot(s.SourceLetter);
        if (!Directory.Exists(archive)) { c.Add(new(CheckLevel.Error, $"На диске {s.SourceLetter}: нет папки {ArchiveScanner.ArchiveFolderName}.")); return c; }
        if (s.ArchiveLogicDriveId == null) c.Add(new(CheckLevel.Warning, "Не найден ArchiveLogicDrive.xml — убедитесь, что это архивный диск Macroscop."));
        else c.Add(new(CheckLevel.Ok, $"Архивный диск Macroscop, идентификатор {s.ArchiveLogicDriveId}."));

        if (s.SourceNewestUtc is DateTime newest && newest > DateTime.UtcNow.AddHours(-24))
        {
            c.Add(new(s.AllowActiveSource ? CheckLevel.Warning : CheckLevel.Error,
                $"На {s.SourceLetter}: идёт запись (последняя {newest.ToLocalTime():dd.MM.yyyy HH:mm}). " +
                "Сначала в Конфигураторе Macroscop поставьте диску роль «Резервный архив» и подождите, пока запись прекратится."));
        }
        else if (s.SourceNewestUtc is DateTime n2)
            c.Add(new(CheckLevel.Ok, $"На {s.SourceLetter}: запись не ведётся (последняя {n2.ToLocalTime():dd.MM.yyyy HH:mm})."));

        var volumes = _ops.GetVolumes();
        var src = volumes.FirstOrDefault(v => v.Letter == s.SourceLetter);
        var tgt = volumes.FirstOrDefault(v => v.Letter == s.TargetVolumeLetter);
        if (s.TargetVolumeLetter == s.SourceLetter) c.Add(new(CheckLevel.Error, "Нельзя переносить диск сам на себя."));
        if (tgt == null) { c.Add(new(CheckLevel.Error, $"Том {s.TargetVolumeLetter}: не найден.")); return c; }
        if (src?.IsVirtual == true && string.Equals(src.BackingFile?.Substring(0, 1), s.TargetVolumeLetter.ToString(), StringComparison.OrdinalIgnoreCase))
            c.Add(new(CheckLevel.Warning, "Исходный диск сам является виртуальным диском на выбранном томе."));
        if (!string.Equals(tgt.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
            c.Add(new(CheckLevel.Error, $"Том {tgt.Letter}: должен быть NTFS (сейчас {tgt.FileSystem})."));

        var need = RequiredBytes(s.SourceArchiveBytes);
        if (s.TargetKind == TargetKind.Vhdx)
        {
            if (s.Stage == RetireStage.Planned && s.VhdxPath != null && File.Exists(s.VhdxPath))
                c.Add(new(CheckLevel.Error, $"Файл {s.VhdxPath} уже существует."));
            c.Add(new(tgt.FreeBytes >= need ? CheckLevel.Ok : CheckLevel.Error,
                $"Свободно на {tgt.Letter}: {HtmlReport.Bytes(tgt.FreeBytes)}, нужно до {HtmlReport.Bytes(need)} (файл растёт по мере копирования)."));
            if (Directory.Exists(ArchiveRoot(tgt.Letter)))
                c.Add(new(CheckLevel.Ok, $"На {tgt.Letter}: есть свой архив Macroscop — файл VHDX ляжет рядом, в корень тома, и архиву не помешает. Macroscop учтёт занятое место через «Мин. своб. место»."));
        }
        else
        {
            var tArchive = ArchiveRoot(tgt.Letter);
            if (Directory.Exists(tArchive) && Directory.EnumerateFileSystemEntries(tArchive).Any() && s.Stage == RetireStage.Planned)
                c.Add(new(CheckLevel.Error, $"На {tgt.Letter}: уже есть архив Macroscop. Для переноса нужен пустой том."));
            c.Add(new(tgt.FreeBytes >= s.SourceArchiveBytes ? CheckLevel.Ok : CheckLevel.Error,
                $"Свободно на {tgt.Letter}: {HtmlReport.Bytes(tgt.FreeBytes)}, нужно {HtmlReport.Bytes(s.SourceArchiveBytes)}."));
        }

        if (s.DrainForecastUtc is DateTime f)
            c.Add(new(CheckLevel.Ok, $"По срокам хранения камер диск опустеет примерно {f.ToLocalTime():dd.MM.yyyy}."));
        return c;
    }

    // ------------------------------------------------------------------ steps

    public void CreateTarget(RetireState s)
    {
        Require(s, RetireStage.Planned);
        if (s.TargetKind == TargetKind.Vhdx)
        {
            s.TempLetter = _ops.FindFreeLetter(new[] { s.SourceLetter, s.TargetVolumeLetter });
            Log(s, $"Создаю виртуальный диск {s.VhdxPath} (до {HtmlReport.Bytes(s.VhdxMaxBytes)}, растущий) и подключаю как {s.TempLetter}:");
            _ops.CreateVhdx(s.VhdxPath!, s.VhdxMaxBytes, s.TargetLabel, s.TempLetter, m => Log(s, m));
        }
        else
        {
            s.TempLetter = s.TargetVolumeLetter;
            Log(s, $"Использую пустой том {s.TempLetter}:");
        }
        s.Stage = RetireStage.TargetReady;
        Log(s, "Целевой том готов.");
        s.Save(_stateDir);
    }

    /// <summary>Copies everything except the PostgreSQL tablespace while Macroscop keeps running. Can be repeated.</summary>
    public CopyResult CopyLive(RetireState s, IProgress<CopyProgress>? progress, CancellationToken ct, ManualResetEventSlim? pause = null)
    {
        if (s.Stage is not (RetireStage.TargetReady or RetireStage.Copied)) throw new InvalidOperationException($"Неверный этап: {s.Stage}");
        var opt = Clone(s.Options);
        opt.ExcludePrefixes.Add(DbFolder);
        Log(s, $"Копирование {ArchiveRoot(s.SourceLetter)} → {ArchiveRoot(s.TempLetter)} (Macroscop работает; база событий — на этапе переключения).");
        var r = CopierFactory().Run(ArchiveRoot(s.SourceLetter), ArchiveRoot(s.TempLetter), opt, progress, ct, pause);
        Account(s, r, "Копирование");
        if (!r.Cancelled) s.Stage = RetireStage.Copied;
        s.Save(_stateDir);
        return r;
    }

    /// <summary>
    /// Short maintenance window: Macroscop and PostgreSQL stopped, final sync including the event database,
    /// the target takes the old drive letter, start-up attach task for a VHDX, services started again.
    /// <paramref name="waitForMacroscopStop"/> is called while Macroscop is still running (Standalone must be stopped
    /// by the user); return false to abort. Any failure restores the original letters and restarts the services.
    /// </summary>
    public CopyResult Switch(RetireState s, Func<IReadOnlyList<string>, bool> waitForMacroscopStop,
                             IProgress<CopyProgress>? progress, CancellationToken ct)
    {
        Require(s, RetireStage.Copied);
        Log(s, "Переключение: начало окна обслуживания.");

        if (_ops.RunningMacroscop().Count > 0 && _ops.TryStopMacroscopService(m => Log(s, m)))
            Log(s, "Служба Macroscop остановлена.");
        while (_ops.RunningMacroscop() is { Count: > 0 } running)
        {
            if (!waitForMacroscopStop(running)) { Log(s, "Переключение отменено пользователем до остановки Macroscop."); throw new OperationCanceledException(); }
        }
        Log(s, "Macroscop остановлен.");

        s.StoppedServices = _ops.StopPostgres(m => Log(s, m)).ToList();
        Log(s, s.StoppedServices.Count > 0 ? "Остановлены: " + string.Join(", ", s.StoppedServices) : "PostgreSQL не был запущен.");
        s.Save(_stateDir);

        bool swapped = false;
        try
        {
            var opt = Clone(s.Options);
            opt.ForcePrefixes.Add(DbFolder);
            Log(s, "Финальная синхронизация, включая базу событий диска.");
            var r = CopierFactory().Run(ArchiveRoot(s.SourceLetter), ArchiveRoot(s.TempLetter), opt, progress, ct);
            Account(s, r, "Финальная синхронизация");
            if (r.Cancelled) throw new OperationCanceledException();
            var dbLosses = r.Losses.Where(l => l.RelativePath.StartsWith(DbFolder, StringComparison.OrdinalIgnoreCase)).ToList();
            if (dbLosses.Count > 0)
                throw new IOException($"Не удалось прочитать базу событий диска ({dbLosses.Count} файлов). Переключение отменено, всё возвращено как было.");

            var check = CopierFactory().Run(ArchiveRoot(s.SourceLetter), ArchiveRoot(s.TempLetter), new CopyOptions { Verify = false, Retries = 0 }, null, ct);
            if (check.FilesCopied != 0 || check.FilesDeleted != 0)
                throw new IOException($"Контрольная сверка нашла расхождения ({check.FilesCopied} изменено, {check.FilesDeleted} удалено). Переключение отменено.");
            Log(s, "Сверка: копия совпадает с исходным диском.");

            s.SourceVolumeId = _ops.GetVolumeId(s.SourceLetter);
            s.TargetVolumeId = _ops.GetVolumeId(s.TempLetter);
            Log(s, $"Исходный том {s.SourceVolumeId}, новый том {s.TargetVolumeId}.");
            _ops.RemoveLetter(s.SourceLetter, m => Log(s, m));
            swapped = true;
            if (s.TempLetter != s.SourceLetter) _ops.RemoveLetter(s.TempLetter, m => Log(s, m));
            _ops.AssignLetter(s.SourceLetter, s.TargetVolumeId, m => Log(s, m));
            Log(s, $"Новый том теперь {s.SourceLetter}:. Старый диск отключён от буквы и не используется.");

            if (s.TargetKind == TargetKind.Vhdx)
            {
                s.AttachTaskName = TaskFolder + $"Attach_{s.SourceLetter}";
                _ops.CreateAttachTask(s.AttachTaskName, s.VhdxPath!, s.SourceLetter, m => Log(s, m));
                Log(s, "Создана задача автоподключения виртуального диска при загрузке Windows.");
            }

            s.Stage = RetireStage.Switched;
            s.Save(_stateDir);
            return r;
        }
        catch (Exception ex)
        {
            Log(s, "ОШИБКА: " + ex.Message);
            if (swapped) RestoreLetters(s);
            s.Save(_stateDir);
            throw;
        }
        finally
        {
            try { _ops.StartServices(s.StoppedServices, m => Log(s, m)); Log(s, "Службы PostgreSQL запущены."); }
            catch (Exception ex) { Log(s, "Не удалось запустить PostgreSQL: " + ex.Message + ". Запустите службу вручную."); }
            s.Save(_stateDir);
        }
    }

    /// <summary>Undo a finished switch: the old disk gets its letter back (Macroscop must be stopped by the caller).</summary>
    public void Rollback(RetireState s)
    {
        Require(s, RetireStage.Switched);
        if (_ops.RunningMacroscop().Count > 0) throw new InvalidOperationException("Сначала остановите Macroscop.");
        RestoreLetters(s);
        if (s.AttachTaskName != null && _ops.TaskExists(s.AttachTaskName)) _ops.DeleteTask(s.AttachTaskName, m => Log(s, m));
        s.Stage = RetireStage.Copied;
        Log(s, "Откат выполнен: исходный диск снова на своей букве.");
        s.Save(_stateDir);
    }

    /// <summary>How much video is still on the retired disk (after the switch it lives on the target).</summary>
    public (long Bytes, DateTime? Newest) Remaining(RetireState s)
    {
        var media = Path.Combine(ArchiveRoot(s.SourceLetter), "media");
        if (!Directory.Exists(media)) return (0, null);
        long bytes = 0; DateTime? newest = null;
        foreach (var f in new DirectoryInfo(media).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            if (f.Length < 1024 * 1024) continue;   // index files stay behind; video blobs are what matters
            bytes += f.Length;
            if (newest == null || f.LastWriteTimeUtc > newest) newest = f.LastWriteTimeUtc;
        }
        return (bytes, newest);
    }

    /// <summary>
    /// Final step once retention has emptied the disk and its role in Macroscop is «Не используется»:
    /// removes the start-up task, unmounts the volume and (for a VHDX) detaches and optionally deletes the file.
    /// </summary>
    public void Finish(RetireState s, bool deleteVhdx)
    {
        Require(s, RetireStage.Switched);
        if (_ops.RunningMacroscop().Count > 0) throw new InvalidOperationException("Сначала остановите Macroscop.");
        var (left, _) = Remaining(s);
        if (left > 0) throw new InvalidOperationException($"На диске ещё {HtmlReport.Bytes(left)} записей. Дождитесь, пока Macroscop удалит их по сроку хранения.");
        if (s.AttachTaskName != null && _ops.TaskExists(s.AttachTaskName)) _ops.DeleteTask(s.AttachTaskName, m => Log(s, m));
        _ops.RemoveLetter(s.SourceLetter, m => Log(s, m));
        if (s.TargetKind == TargetKind.Vhdx && s.VhdxPath != null)
        {
            _ops.DetachVhdx(s.VhdxPath, m => Log(s, m));
            if (deleteVhdx) { File.Delete(s.VhdxPath); Log(s, $"Файл {s.VhdxPath} удалён."); }
        }
        s.Stage = RetireStage.Finished;
        Log(s, "Вывод диска завершён.");
        s.Save(_stateDir);
    }

    // ------------------------------------------------------------------ helpers

    private void RestoreLetters(RetireState s)
    {
        try
        {
            if (s.TargetVolumeId != null && s.SourceVolumeId != null)
            {
                try { _ops.RemoveLetter(s.SourceLetter, m => Log(s, m)); } catch (Exception) { /* letter may be free already */ }
                _ops.AssignLetter(s.SourceLetter, s.SourceVolumeId, m => Log(s, m));
                if (s.TempLetter != s.SourceLetter) _ops.AssignLetter(s.TempLetter, s.TargetVolumeId, m => Log(s, m));
                Log(s, $"Буквы возвращены: исходный диск — {s.SourceLetter}:, копия — {s.TempLetter}:.");
            }
        }
        catch (Exception ex)
        {
            Log(s, $"ВНИМАНИЕ: не удалось вернуть буквы автоматически ({ex.Message}). Исходный том: {s.SourceVolumeId}; " +
                   $"назначьте ему {s.SourceLetter}: в «Управлении дисками» или командой mountvol {s.SourceLetter}:\\ {s.SourceVolumeId}");
            throw;
        }
    }

    private void Forecast(RetireState s)
    {
        try
        {
            var root = ArchiveRoot(s.SourceLetter);
            if (!Directory.Exists(root)) return;
            var names = CameraNames.FromLogsDir(LogsDir);
            var retention = MacroscopConfig.Load(new[] { root }, names.ServerId);
            var report = new ArchiveScanner().Scan(new[] { root }, names, new ToolConfig { ScanThreads = 1 }, macroscopRetention: retention);
            s.SourceNewestUtc = report.Drives.FirstOrDefault()?.NewestUtc;
            s.DrainForecastUtc = report.Cameras
                .Where(c => c.NewestUtc.HasValue && c.RetentionDays.HasValue)
                .Select(c => (DateTime?)c.NewestUtc!.Value.AddDays(c.RetentionDays!.Value))
                .DefaultIfEmpty(null).Max();
        }
        catch (Exception ex) { Log(s, "Прогноз опустения диска недоступен: " + ex.Message); }
    }

    private void Account(RetireState s, CopyResult r, string what)
    {
        s.BytesCopied += r.BytesCopied;
        foreach (var l in r.Losses)
            if (!s.Losses.Any(x => x.RelativePath == l.RelativePath)) s.Losses.Add(l);
        Log(s, $"{what}: скопировано {r.FilesCopied} файлов ({HtmlReport.Bytes(r.BytesCopied)}), без изменений {r.FilesSkipped}, " +
               $"удалено по сроку хранения {r.FilesVanished + r.FilesDeleted}, не прочитано {r.Losses.Count}, за {r.Elapsed:hh\\:mm\\:ss}" +
               (r.Cancelled ? " — остановлено, можно продолжить." : "."));
    }

    private string ArchiveRoot(char letter) => Path.Combine(_ops.RootOf(letter), ArchiveScanner.ArchiveFolderName);

    private static string? ReadLogicDriveId(string archiveRoot)
    {
        try
        {
            var f = Path.Combine(archiveRoot, "ArchiveLogicDrive.xml");
            if (!File.Exists(f)) return null;
            var m = Regex.Match(File.ReadAllText(f), @"<ArchiveLogicDriveId>([0-9a-fA-F-]{36})</ArchiveLogicDriveId>");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch (Exception) { return null; }
    }

    private static long SafeSize(string dir)
        => new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Sum(f => f.Length);

    private static CopyOptions Clone(CopyOptions o) => new()
    {
        SpeedLimitBytesPerSec = o.SpeedLimitBytesPerSec, Verify = o.Verify, Retries = o.Retries, RetryDelay = o.RetryDelay,
        Mirror = o.Mirror, BufferSize = o.BufferSize,
        ExcludePrefixes = new List<string>(o.ExcludePrefixes), ForcePrefixes = new List<string>(o.ForcePrefixes),
    };

    private static void Require(RetireState s, RetireStage stage)
    {
        if (s.Stage != stage) throw new InvalidOperationException($"Этот шаг выполняется на этапе «{stage}», сейчас — «{s.Stage}».");
    }

    private void Log(RetireState s, string message)
    {
        var line = $"{DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture)}  {message}";
        s.Log.Add(line);
        Logged?.Invoke(line);
    }
}
