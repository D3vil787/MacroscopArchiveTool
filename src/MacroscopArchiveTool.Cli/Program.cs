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
        var report = new ArchiveScanner().Scan(roots, names, config, progress, cts.Token);

        var outDir = a.Get("--out") ?? Path.Combine(AppContext.BaseDirectory, "reports");
        Directory.CreateDirectory(outDir);
        var baseName = Path.Combine(outDir, ReportStore.BaseFileName(report));
        ReportStore.SaveJson(report, baseName + ".json");
        Console.WriteLine();
        Console.WriteLine($"Готово за {report.ScanSeconds} с. Камер: {report.Cameras.Count}, дисков: {report.Drives.Count}.");
        Console.WriteLine($"Видео в архиве: {HtmlReport.Bytes(report.Drives.Sum(d => d.MediaBytes))}, пишется в сутки: {HtmlReport.Bytes((long)ReportAnalyzer.DailyRate(report))}");
        foreach (var w in report.Warnings) Console.WriteLine("  ! " + w);
        Console.WriteLine($"JSON: {baseName}.json");

        if (!a.Has("--no-html"))
        {
            File.WriteAllText(baseName + ".html", HtmlReport.Render(new[] { report }), new UTF8Encoding(true));
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
        File.WriteAllText(output, HtmlReport.Render(reports), new UTF8Encoding(true));
        Console.WriteLine($"Сводный отчёт по {reports.Count} серверам: {output}");
        if (a.Has("--open")) OpenFile(output);
        return 0;
    }

    // init-config [--path FILE]
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
  version       Показать версию
""");

    /// <summary>Tiny argument parser: --key value, boolean --flags and positional values.</summary>
    private sealed class Args
    {
        private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase) { "--no-html", "--open" };
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
