using System.Text;

namespace MacroscopArchiveTool.Core;

/// <summary>One read-only archive scan with JSON + HTML output, shared by the window and the command line.</summary>
public static class ReportRunner
{
    public sealed record Result(ScanReport Report, string JsonPath, string? HtmlPath);

    public static Result Run(ToolConfig config, IReadOnlyList<string>? roots, string? logsDir, string outDir,
                             bool html, IProgress<string>? progress, CancellationToken ct)
    {
        var list = roots is { Count: > 0 } ? roots.ToList()
                 : config.ArchiveRoots.Count > 0 ? config.ArchiveRoots : ArchiveScanner.FindArchiveRoots();
        if (list.Count == 0) throw new InvalidOperationException("Не найдено ни одной папки MacroscopArchive.");
        var names = CameraNames.FromLogsDir(logsDir ?? config.LogsDir ?? ToolConfig.DefaultLogsDir);
        progress?.Report($"Папок архива: {list.Count}; имена камер: {(names.SourceFile != null ? names.ById.Count.ToString() : "не найдены")}");

        RetentionSettings? retention = null;
        if (config.RetentionFromMacroscop)
        {
            retention = MacroscopConfig.Load(list, names.ServerId);
            progress?.Report(retention.ById.Count > 0
                ? $"Сроки хранения: {retention.ById.Count} камер из конфигурации Macroscop"
                : "Сроки хранения: из mat.json (конфигурация Macroscop не прочитана)");
        }
        var report = new ArchiveScanner().Scan(list, names, config, progress, ct, retention);

        Directory.CreateDirectory(outDir);
        var baseName = Path.Combine(outDir, ReportStore.BaseFileName(report));
        ReportStore.SaveJson(report, baseName + ".json");
        string? htmlPath = null;
        if (html)
        {
            htmlPath = baseName + ".html";
            File.WriteAllText(htmlPath, HtmlReport.Render(new[] { report }), new UTF8Encoding(true));
        }
        return new Result(report, baseName + ".json", htmlPath);
    }
}
