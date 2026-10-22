using System.Runtime.Versioning;
using System.Text.Json;
using MacroscopArchiveTool.Core.Remote;

namespace MacroscopArchiveTool.Core.Cleanup;

/// <summary>Runs cleanup commands on this server (in-process) or on another one (mat.exe cleanup … through <see cref="RemoteScanner"/>).</summary>
public interface ICleanupTarget
{
    string Name { get; }
    CleanupScan Run(string command, CleanupPlan plan, IProgress<string>? progress, CancellationToken ct);
}

/// <summary>Shared by the window, the command line and remote runs: one entry point for every cleanup command.</summary>
public static class CleanupCommands
{
    public static readonly string[] Names = { "scan", "apply", "restore", "purge" };

    public static CleanupScan Execute(CleanupEngine engine, string command, CleanupPlan plan, IProgress<string>? progress, CancellationToken ct)
    {
        var log = new List<string>();
        IProgress<string> sink = new SyncSink(m => { log.Add(m); progress?.Report(m); });
        CleanupScan result;
        switch (command)
        {
            case "scan":
                if (plan.Options.QuarantineDays > 0)
                {
                    var freed = engine.Purge(null, plan.Options.QuarantineDays, sink);
                    if (freed > 0) sink.Report($"Карантин старше {plan.Options.QuarantineDays} дн. удалён окончательно: {HtmlReport.Bytes(freed)}");
                }
                result = engine.Scan(plan.Options, sink, ct);
                break;
            case "apply":
                result = engine.Apply(plan, sink, ct);
                break;
            case "restore":
                foreach (var problem in engine.Restore(plan.SetId ?? throw new ArgumentException("Не указан набор карантина."), sink)) sink.Report("! " + problem);
                result = engine.Scan(plan.Options, null, ct);
                break;
            case "purge":
                var bytes = engine.Purge(plan.SetId, plan.SetId == null ? plan.OlderThanDays ?? 0 : null, sink);
                sink.Report($"Освобождено {HtmlReport.Bytes(bytes)}");
                result = engine.Scan(plan.Options, null, ct);
                break;
            default:
                throw new ArgumentException($"Неизвестная команда очистки: {command}");
        }
        result.Log = log.Concat(result.Log.Where(l => !log.Contains(l))).ToList();
        return result;
    }

    private sealed class SyncSink : IProgress<string>
    {
        private readonly Action<string> _a;
        public SyncSink(Action<string> a) => _a = a;
        public void Report(string value) => _a(value);
    }

    public static readonly JsonSerializerOptions Json = new(ToolConfig.JsonOptions) { WriteIndented = true };
}

[SupportedOSPlatform("windows")]
public sealed class LocalCleanupTarget : ICleanupTarget
{
    public string Name => $"Этот сервер ({Environment.MachineName})";
    public CleanupScan Run(string command, CleanupPlan plan, IProgress<string>? progress, CancellationToken ct)
        => CleanupCommands.Execute(new CleanupEngine(new WindowsCleanupEnvironment()), command, plan, progress, ct);
}

[SupportedOSPlatform("windows")]
public sealed class RemoteCleanupTarget : ICleanupTarget
{
    private readonly RemoteServer _server;
    public RemoteCleanupTarget(RemoteServer server) { _server = server; }
    public string Name => _server.Display;

    public CleanupScan Run(string command, CleanupPlan plan, IProgress<string>? progress, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "mat_cleanup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var planFile = Path.Combine(tmp, "plan.json");
            File.WriteAllText(planFile, JsonSerializer.Serialize(plan, CleanupCommands.Json));
            using var host = new WindowsRemoteHost(_server);
            return new RemoteScanner().RunCommand(host, $"cleanup {command} --plan \"{{in}}\\plan.json\" --out \"{{out}}\\result.json\"", new[] { planFile },
                outDir => JsonSerializer.Deserialize<CleanupScan>(File.ReadAllText(Path.Combine(outDir, "result.json")), CleanupCommands.Json)
                          ?? throw new InvalidDataException("Пустой ответ сервера."),
                progress, ct);
        }
        finally { try { Directory.Delete(tmp, true); } catch (Exception) { } }
    }
}
