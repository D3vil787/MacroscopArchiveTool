using System.Diagnostics;
using System.Text;
using MacroscopArchiveTool.Core;

namespace MacroscopArchiveTool.Cli;

internal static class Program
{
    private const string ConfigFileName = "mat.json";

    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* redirected output */ }

        if (args.Length == 0 || args[0] is "help" or "-h" or "--help" or "/?") { PrintHelp(); return 0; }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "scan" => Scan(new Args(args.Skip(1))),
                "merge" => Merge(new Args(args.Skip(1))),
                "init-config" => InitConfig(new Args(args.Skip(1))),
                "dump-config" => DumpConfig(new Args(args.Skip(1))),
                "cleanup" => Cleanup(new Args(args.Skip(1))),
                "schedule" => Schedule(new Args(args.Skip(1))),
                "unschedule" => Unschedule(),
                "version" or "--version" => Print(ToolInfo.Version),
                _ => Fail($"Неизвестная команда: {args[0]}. Справка: MacroscopArchiveTool help"),
            };
        }
        catch (ArgumentException ex) { return Fail(ex.Message); }
        catch (Exception ex) { return Fail($"Ошибка: {ex.Message}"); }
    }

    // scan [--out DIR] [--config FILE] [--roots A;B] [--logs DIR] [--threads N] [--no-html] [--open]
    private static int Scan(Args a)
    {
        var config = LoadConfig(a.Get("--config"));
        if (a.Get("--threads") is { } t) config.ScanThreads = int.Parse(t);

        var roots = a.Get("--roots")?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                    ?? (config.ArchiveRoots.Count > 0 ? config.ArchiveRoots : ArchiveScanner.FindArchiveRoots());
        if (roots.Count == 0) return Fail("Не найдено ни одной папки MacroscopArchive. Укажите --roots или archiveRoots в mat.json.");

        var logs = a.Get("--logs") ?? config.LogsDir ?? ToolConfig.DefaultLogsDir;
        var names = CameraNames.FromLogsDir(logs);

        Console.WriteLine($"MacroscopArchiveTool {ToolInfo.Version} — сканирование архива (только чтение)");
        Console.WriteLine($"Папок архива: {roots.Count}; имена камер: {(names.SourceFile != null ? names.ById.Count + " из " + names.SourceFile : "не найдены")}");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); Console.WriteLine("Остановка..."); };
        var progress = new Progress<string>(m => Console.WriteLine("  " + m));
        RetentionSettings? retention = null;
        if (config.RetentionFromMacroscop)
        {
            retention = MacroscopConfig.Load(roots, names.ServerId);
            Console.WriteLine(retention.ById.Count > 0
                ? $"Сроки хранения: {retention.ById.Count} камер из конфигурации Macroscop ({retention.SourceFile})"
                : "Сроки хранения: из mat.json (конфигурация Macroscop не прочитана — подробности в отчёте)");
        }
        var report = new ArchiveScanner().Scan(roots, names, config, progress, cts.Token, retention);

        var outDir = a.Get("--out") ?? Path.Combine(AppContext.BaseDirectory, "reports");
        Directory.CreateDirectory(outDir);
        var baseName = Path.Combine(outDir, ReportStore.BaseFileName(report));
        ReportStore.SaveJson(report, baseName + ".json");
        try { MetricsWriter.Write(report); } catch (Exception ex) { Console.WriteLine("  ! metrics.json не записан: " + ex.Message); }
        Console.WriteLine();
        Console.WriteLine($"Готово за {report.ScanSeconds} с. Камер: {report.Cameras.Count}, дисков: {report.Drives.Count}.");
        Console.WriteLine($"Видео в архиве: {HtmlReport.Bytes(report.Drives.Sum(d => d.MediaBytes))}, пишется в сутки: {HtmlReport.Bytes((long)ReportAnalyzer.DailyRate(report))}");
        foreach (var w in report.Warnings) Console.WriteLine("  ! " + w);
        Console.WriteLine($"JSON: {baseName}.json");

        if (!a.Has("--no-html"))
        {
            var history = new Dictionary<string, List<HistoryPoint>> { [report.MachineName] = ReportHistory.Load(outDir, report.MachineName) };
            File.WriteAllText(baseName + ".html", HtmlReport.Render(new[] { report }, history), new UTF8Encoding(true));
            Console.WriteLine($"HTML: {baseName}.html");
            if (a.Has("--open")) OpenFile(baseName + ".html");
        }
        return 0;
    }

    // merge FILE1.json FILE2.json ... [-o OUT.html] [--open]
    private static int Merge(Args a)
    {
        var inputs = a.Positional.ToList();
        if (inputs.Count == 0) return Fail("Укажите JSON-отчёты: merge server1.json server2.json -o сводный.html");
        var config = LoadConfig(a.Get("--config"));
        var reports = inputs.Select(ReportStore.LoadJson).ToList();
        foreach (var r in reports) ReportAnalyzer.Analyze(r, config.HeavyFactor);   // same rules for every server
        var output = a.Get("-o") ?? a.Get("--out") ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(inputs[0]))!,
            $"macroscop_сводный_{DateTime.Now:yyyyMMdd_HHmm}.html");
        var dir = Path.GetDirectoryName(Path.GetFullPath(inputs[0]))!;
        var history = reports.Select(r => r.MachineName).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(m => m, m => ReportHistory.Load(dir, m), StringComparer.OrdinalIgnoreCase);
        File.WriteAllText(output, HtmlReport.Render(reports, history), new UTF8Encoding(true));
        Console.WriteLine($"Сводный отчёт по {reports.Count} серверам: {output}");
        if (a.Has("--open")) OpenFile(output);
        return 0;
    }

    // init-config [--path FILE]
    // dump-config [--file FILE] [--out FILE] [--config FILE]
    private static int DumpConfig(Args a)
    {
        string? path = a.Get("--file");
        if (path == null)
        {
            var config = LoadConfig(a.Get("--config"));
            var roots = config.ArchiveRoots.Count > 0 ? config.ArchiveRoots : ArchiveScanner.FindArchiveRoots();
            var names = CameraNames.FromLogsDir(config.LogsDir ?? ToolConfig.DefaultLogsDir);
            var backup = MacroscopConfig.FindNewestBackup(roots) ?? throw new ArgumentException("Резервные копии конфигурации (ConfigsBackups) не найдены. Укажите --file.");
            path = MacroscopConfig.FindConfigFile(backup, names.ServerId)?.FullName ?? throw new ArgumentException($"В копии {backup.FullName} нет файла конфигурации.");
        }
        var text = a.Has("--tree") ? MacroscopConfig.DumpTree(path) : MacroscopConfig.DumpStructure(path);
        var output = a.Get("--out") ?? Path.Combine(AppContext.BaseDirectory, $"dump_config_{DateTime.Now:yyyyMMdd_HHmm}.txt");
        File.WriteAllText(output, text, new UTF8Encoding(true));
        Console.WriteLine(text.Length > 3000 ? text[..3000] + "\n…" : text);
        Console.WriteLine($"Полный вывод: {output}");
        Console.WriteLine("В выводе нет строковых значений, кроме имён камер, — логины и пароли камер в него не попадают.");
        return 0;
    }

    // cleanup scan|apply|restore|purge [--plan FILE] [--out FILE] [--backup-days N] [--keep N] [--log-days N] [--set ID] [--older-than N]
    private static int Cleanup(Args a)
    {
        if (!OperatingSystem.IsWindows()) return Fail("Очистка доступна только в Windows.");
        var command = a.Positional.FirstOrDefault()?.ToLowerInvariant() ?? "scan";
        if (!MacroscopArchiveTool.Core.Cleanup.CleanupCommands.Names.Contains(command)) return Fail("cleanup scan|apply|restore|purge");
        var plan = a.Get("--plan") is { } pf
            ? System.Text.Json.JsonSerializer.Deserialize<MacroscopArchiveTool.Core.Cleanup.CleanupPlan>(File.ReadAllText(pf), MacroscopArchiveTool.Core.Cleanup.CleanupCommands.Json)!
            : new MacroscopArchiveTool.Core.Cleanup.CleanupPlan();
        if (a.Get("--backup-days") is { } bd) plan.Options.BackupDays = int.Parse(bd);
        if (a.Get("--keep") is { } k) plan.Options.BackupsToKeep = int.Parse(k);
        if (a.Get("--log-days") is { } ld) plan.Options.LogDays = int.Parse(ld);
        if (a.Get("--set") is { } set) plan.SetId = set;
        if (a.Get("--older-than") is { } ot) plan.OlderThanDays = int.Parse(ot);
        if (command == "apply" && plan.ItemIds.Count == 0) return Fail("Для apply нужен --plan с выбранными пунктами (его готовит окно программы).");

        var progress = new Progress<string>(m => Console.WriteLine("  " + m));
        var result = new MacroscopArchiveTool.Core.Cleanup.LocalCleanupTarget().Run(command, plan, progress, CancellationToken.None);
        var json = System.Text.Json.JsonSerializer.Serialize(result, MacroscopArchiveTool.Core.Cleanup.CleanupCommands.Json);
        if (a.Get("--out") is { } outFile)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
            File.WriteAllText(outFile, json);
        }
        else
        {
            foreach (var i in result.Items)
                Console.WriteLine($"{(i.Action == MacroscopArchiveTool.Core.Cleanup.CleanupAction.ShowOnly ? "  (просмотр)" : $"  [{i.Id}]")} {MacroscopArchiveTool.Core.Cleanup.CleanupText.Category(i.Category)}: {i.Title} — {HtmlReport.Bytes(i.Bytes)}");
            foreach (var q in result.Quarantine) Console.WriteLine($"  карантин {q.SetId}: {Path.GetPathRoot(q.Folder)} {HtmlReport.Bytes(q.Bytes)}");
        }
        foreach (var w in result.Warnings) Console.WriteLine("  ! " + w);
        return 0;
    }

    private const string TaskName = @"\MacroscopArchiveTool\WeeklyScan";

    // schedule [--day MON] [--time 06:00]
    private static int Schedule(Args a)
    {
        if (!OperatingSystem.IsWindows()) return Fail("Расписание доступно только в Windows.");
        var day = (a.Get("--day") ?? "MON").ToUpperInvariant();
        var time = a.Get("--time") ?? "06:00";
        if (!System.Text.RegularExpressions.Regex.IsMatch(day, "^(MON|TUE|WED|THU|FRI|SAT|SUN|DAILY)$")) return Fail("--day: MON, TUE, WED, THU, FRI, SAT, SUN или DAILY (каждый день)");
        if (!TimeOnly.TryParse(time, out _)) return Fail("--time в формате ЧЧ:ММ");

        // schtasks /TR is limited to 261 characters, so the task runs a small .bat next to the exe.
        var bat = Path.Combine(AppContext.BaseDirectory, "scheduled_scan.bat");
        File.WriteAllText(bat, "@echo off\r\ncd /d \"%~dp0\"\r\nmat.exe scan >> \"%~dp0reports\\scheduled_scan.log\" 2>&1\r\n", Encoding.ASCII);
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "reports"));

        var code = RunSchtasks($"/Create /TN \"{TaskName}\" /TR \"\\\"{bat}\\\"\" {(day == "DAILY" ? "/SC DAILY" : $"/SC WEEKLY /D {day}")} /ST {time} /RU SYSTEM /RL HIGHEST /F");
        if (code != 0) return Fail("Не удалось создать задание. Запустите от имени администратора.");
        Console.WriteLine($"Создано задание Планировщика «{TaskName}»: скан {(day == "DAILY" ? "каждый день" : $"каждую неделю, {day}")} в {time}, от имени SYSTEM.");
        Console.WriteLine($"Отчёты и журнал: {Path.Combine(AppContext.BaseDirectory, "reports")}");
        return 0;
    }

    private static int Unschedule()
    {
        if (!OperatingSystem.IsWindows()) return Fail("Расписание доступно только в Windows.");
        var code = RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
        Console.WriteLine(code == 0 ? "Задание удалено." : "Задание не найдено или нет прав.");
        return code;
    }

    private static int RunSchtasks(string arguments)
    {
        var psi = new ProcessStartInfo("schtasks.exe", arguments) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout.Trim());
        if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.WriteLine(stderr.Trim());
        return p.ExitCode;
    }

    private static int InitConfig(Args a)
    {
        var path = a.Get("--path") ?? Path.Combine(AppContext.BaseDirectory, ConfigFileName);
        if (File.Exists(path)) return Fail($"Файл уже существует: {path}");
        var cfg = new ToolConfig
        {
            CameraRetentionDays = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Пример: имя камеры"] = 14,
            },
        };
        cfg.Save(path);
        Console.WriteLine($"Создан {path}. Впишите сроки хранения камер, отличающиеся от defaultRetentionDays.");
        return 0;
    }

    private static ToolConfig LoadConfig(string? explicitPath)
    {
        var path = explicitPath ?? Path.Combine(AppContext.BaseDirectory, ConfigFileName);
        if (File.Exists(path)) return ToolConfig.Load(path);
        if (explicitPath != null) throw new ArgumentException($"Файл настроек не найден: {explicitPath}");
        return new ToolConfig();
    }

    private static void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Console.WriteLine($"Не удалось открыть файл: {ex.Message}"); }
    }

    private static int Print(string s) { Console.WriteLine(s); return 0; }

    private static int Fail(string message) { Console.Error.WriteLine(message); return 1; }

    private static void PrintHelp() => Console.WriteLine($"""
MacroscopArchiveTool {ToolInfo.Version} — анализ архива Macroscop (только чтение)

Команды:
  scan          Просканировать архив этого сервера и сделать отчёт (JSON + HTML)
                  --out DIR       папка для отчётов (по умолчанию .\reports рядом с exe)
                  --config FILE   файл настроек (по умолчанию mat.json рядом с exe)
                  --roots A;B     папки MacroscopArchive вручную (по умолчанию ищутся на всех дисках)
                  --logs DIR      папка логов Macroscop для имён камер
                  --threads N     сколько дисков сканировать параллельно (по умолчанию 4)
                  --no-html       только JSON
                  --open          открыть HTML после сканирования
  merge         Свести JSON-отчёты нескольких серверов в один HTML
                  merge s1.json s2.json [-o сводный.html] [--config FILE] [--open]
  init-config   Создать mat.json с настройками сроков хранения
  dump-config   Показать структуру конфигурации Macroscop и распознанные сроки камер (без паролей)
                  [--file FILE] [--out FILE] [--tree]   (--tree: всё дерево файла, напр. Server.SrvConf)
  cleanup       Очистка мусора Macroscop через карантин (удобнее — кнопкой в окне программы)
                  cleanup scan [--backup-days 30] [--keep 10] [--log-days 90]   что можно удалить
                  cleanup apply --plan FILE                                    перенести выбранное в карантин
                  cleanup restore --set ID | cleanup purge --set ID|--older-than N
  schedule      Еженедельный скан через Планировщик Windows (от администратора)
                  [--day MON|…|SUN|DAILY] [--time 06:00]   (для Zabbix удобнее DAILY: metrics.json обновляется после каждого скана)
  unschedule    Удалить еженедельный скан
  version       Показать версию
""");

    /// <summary>Tiny argument parser: --key value, boolean --flags and positional values.</summary>
    private sealed class Args
    {
        private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase) { "--no-html", "--open", "--tree" };
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Positional { get; } = new();

        public Args(IEnumerable<string> args)
        {
            var list = args.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                if (Flags.Contains(s)) _flags.Add(s);
                else if (s.StartsWith('-'))
                {
                    if (i + 1 >= list.Count) throw new ArgumentException($"Нет значения для {s}");
                    _values[s] = list[++i];
                }
                else Positional.Add(s);
            }
        }

        public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;
        public bool Has(string flag) => _flags.Contains(flag);
    }
}
