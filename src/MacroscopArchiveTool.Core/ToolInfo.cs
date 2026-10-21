using System.Reflection;
using System.Text.Json;

namespace MacroscopArchiveTool.Core;

public static class ToolInfo
{
    public static string Version { get; } =
        typeof(ToolInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? typeof(ToolInfo).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}

public static class ReportStore
{
    public static void SaveJson(ScanReport report, string path)
        => File.WriteAllText(path, JsonSerializer.Serialize(report, ToolConfig.JsonOptions));

    public static ScanReport LoadJson(string path)
        => JsonSerializer.Deserialize<ScanReport>(File.ReadAllText(path), ToolConfig.JsonOptions)
           ?? throw new InvalidDataException($"Пустой или неверный отчёт: {path}");

    /// <summary>File name like macroscop_Сервер-1_20261021_1355 (no extension).</summary>
    public static string BaseFileName(ScanReport r)
    {
        var name = string.IsNullOrWhiteSpace(r.ServerName) ? r.MachineName : r.ServerName;
        var safe = new string(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || char.IsWhiteSpace(ch) ? '-' : ch).ToArray());
        return $"macroscop_{safe}_{r.ScannedAtUtc.ToLocalTime():yyyyMMdd_HHmm}";
    }
}
