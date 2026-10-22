using MacroscopArchiveTool.Core;
using MacroscopArchiveTool.Core.Remote;

partial class Program
{
    internal static void RemoteScanRoundTrip()
    {
        var root = Path.Combine(Path.GetTempPath(), "mat_remote_" + Guid.NewGuid().ToString("N"));
        try
        {
            var tool = Path.Combine(root, "tool"); Directory.CreateDirectory(tool);
            File.WriteAllText(Path.Combine(tool, "mat.exe"), "exe");
            var host = new FakeRemoteHost(Path.Combine(root, "server2"));
            var lines = new List<string>();
            var scanner = new RemoteScanner { ToolDir = tool, PollInterval = TimeSpan.FromMilliseconds(20) };
            var r = scanner.Run(host, new SyncProgress<string>(lines.Add), CancellationToken.None);

            R.Eq("Сервер 2", r.ServerName, "отчёт с удалённого сервера");
            R.True(host.Connected, "подключение открыто");
            R.True(File.Exists(Path.Combine(host.Unc(RemoteScanner.RemoteDir), "mat.exe")), "mat.exe скопирован");
            R.True(lines.Any(l => l.Contains("Сканирую")), "строки журнала скана показаны: " + string.Join(" | ", lines));
            R.Eq(0, host.Tasks.Count, "задача удалена");
            var reports = Path.Combine(host.Unc(RemoteScanner.RemoteDir), "reports");
            R.Eq(0, Directory.GetFileSystemEntries(reports).Length, "временные файлы удалены");
            R.Eq(0, Directory.GetFiles(host.Unc(RemoteScanner.RemoteDir), "run_*.bat").Length, "run-файл удалён");

            host.ExitCode = 3;
            R.Throws(() => scanner.Run(host, null, CancellationToken.None), "ошибка скана на сервере");
            R.Eq(0, host.Tasks.Count, "задача удалена и после ошибки");
        }
        finally { try { Directory.Delete(root, true); } catch (Exception) { } }
    }
}

/// <summary>"C:\" of the remote server is a temp folder; the scheduled task is simulated on a background thread.</summary>
sealed class FakeRemoteHost : IRemoteHost
{
    private readonly string _c;
    public bool Connected;
    public int ExitCode;
    public readonly HashSet<string> Tasks = new();

    public FakeRemoteHost(string cDrive) { _c = cDrive; Directory.CreateDirectory(cDrive); }

    public void Connect() => Connected = true;
    public string Check() => "ok";
    public string Unc(string p) => Path.Combine(_c, Path.Combine(p[3..].Split('\\')));

    public void RunTask(string taskName, string remoteCommand)
    {
        Tasks.Add(taskName);
        var id = Path.GetFileNameWithoutExtension(remoteCommand.Split('\\').Last())["run_".Length..];
        var reports = Path.Combine(Unc(RemoteScanner.RemoteDir), "reports");
        var code = ExitCode;
        new Thread(() =>
        {
            File.WriteAllText(Path.Combine(reports, id + ".log"), "Сканирую A:\\MacroscopArchive ...\r\n");
            Thread.Sleep(60);
            if (code == 0)
            {
                Directory.CreateDirectory(Path.Combine(reports, id));
                ReportStore.SaveJson(new ScanReport { ServerName = "Сервер 2" }, Path.Combine(reports, id, "macroscop_s2.json"));
                File.AppendAllText(Path.Combine(reports, id + ".log"), "Готово\r\n");
            }
            File.WriteAllText(Path.Combine(reports, id + ".done"), code + " \r\n");
        }).Start();
    }

    public void EndTask(string taskName) { }
    public void DeleteTask(string taskName) => Tasks.Remove(taskName);
    public void Dispose() => Connected = false;
}
