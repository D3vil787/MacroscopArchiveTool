using System.Text;

namespace MacroscopArchiveTool.Core.Remote;

/// <summary>What the remote scan needs from Windows; real implementation: <see cref="WindowsRemoteHost"/>.</summary>
public interface IRemoteHost : IDisposable
{
    /// <summary>Opens the SMB session (with the stored account if any). Later file and task calls use it.</summary>
    void Connect();
    /// <summary>Checks access (admin share, Task Scheduler) and returns a short description; throws when there is no access.</summary>
    string Check();
    /// <summary>UNC path of a folder given as a local path on the remote server (C:\X → \\host\C$\X).</summary>
    string Unc(string remoteLocalPath);
    /// <summary>Creates a one-time task running as SYSTEM and starts it.</summary>
    void RunTask(string taskName, string remoteCommand);
    void EndTask(string taskName);
    void DeleteTask(string taskName);
}

/// <summary>
/// Runs the read-only archive scan on another server without installing anything there permanently and without WinRM:
/// copies mat.exe to C:\ProgramData\MacroscopArchiveTool\remote over the admin share, starts it once as SYSTEM through
/// the Task Scheduler, follows its log, and brings back the JSON report. The task and run files are removed afterwards.
/// </summary>
public sealed class RemoteScanner
{
    public const string RemoteDir = @"C:\ProgramData\MacroscopArchiveTool\remote";
    public const string TaskName = @"\MacroscopArchiveTool\RemoteScan";

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(3);
    /// <summary>Folder with mat.exe (and optionally mat.json) to send to the server.</summary>
    public string ToolDir { get; set; } = AppContext.BaseDirectory;

    public ScanReport Run(IRemoteHost host, IProgress<string>? progress, CancellationToken ct)
    {
        void Say(string m) => progress?.Report(m);
        Say("Подключение…");
        host.Connect();

        var dir = host.Unc(RemoteDir);
        var reports = Path.Combine(dir, "reports");
        Directory.CreateDirectory(reports);

        var exe = Path.Combine(ToolDir, "mat.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("Рядом с программой нет mat.exe — он нужен для скана на другом сервере.", exe);
        if (CopyIfChanged(exe, Path.Combine(dir, "mat.exe"))) Say("Скопирован mat.exe");
        var cfg = Path.Combine(ToolDir, "mat.json");
        if (File.Exists(cfg)) CopyIfChanged(cfg, Path.Combine(dir, "mat.json"));

        var id = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var outDir = Path.Combine(reports, id);
        var log = Path.Combine(reports, id + ".log");
        var done = Path.Combine(reports, id + ".done");
        var bat = Path.Combine(dir, $"run_{id}.bat");
        File.WriteAllText(bat,
            "@echo off\r\n" +
            "cd /d \"%~dp0\"\r\n" +
            $"mat.exe scan --out \"%~dp0reports\\{id}\" --no-html > \"%~dp0reports\\{id}.log\" 2>&1\r\n" +
            $">\"%~dp0reports\\{id}.done\" echo %errorlevel%\r\n", Encoding.ASCII);

        bool started = false;
        try
        {
            host.RunTask(TaskName, $"{RemoteDir}\\run_{id}.bat");
            started = true;
            Say("Сканирование запущено на сервере");

            int shown = 0;
            while (!File.Exists(done))
            {
                if (ct.IsCancellationRequested) { host.EndTask(TaskName); ct.ThrowIfCancellationRequested(); }
                shown = Tail(log, shown, Say);
                Thread.Sleep(PollInterval);
            }
            Thread.Sleep(200);
            Tail(log, shown, Say);

            var code = ReadShared(done).Trim();
            if (code != "0") throw new InvalidOperationException($"Скан на сервере завершился с кодом {code}. Последние строки журнала: {LastLines(log, 5)}");
            var json = Directory.Exists(outDir) ? Directory.GetFiles(outDir, "*.json").OrderBy(f => f).LastOrDefault() : null;
            if (json == null) throw new FileNotFoundException("Скан завершился, но отчёт не найден.");
            var report = ReportStore.LoadJson(json);
            Say($"Готово: {report.Cameras.Count} камер, {report.Drives.Count} дисков");
            return report;
        }
        finally
        {
            if (started) { try { host.DeleteTask(TaskName); } catch (Exception) { } }
            foreach (var p in new[] { bat, done, log }) { try { File.Delete(p); } catch (Exception) { } }
            try { if (Directory.Exists(outDir)) Directory.Delete(outDir, true); } catch (Exception) { }
        }
    }

    private static bool CopyIfChanged(string src, string dst)
    {
        var s = new FileInfo(src);
        var d = new FileInfo(dst);
        if (d.Exists && d.Length == s.Length && d.LastWriteTimeUtc == s.LastWriteTimeUtc) return false;
        File.Copy(src, dst, overwrite: true);
        File.SetLastWriteTimeUtc(dst, s.LastWriteTimeUtc);
        return true;
    }

    /// <summary>Reports log lines written since the last call; returns the new number of lines shown.</summary>
    private static int Tail(string log, int shown, Action<string> say)
    {
        if (!File.Exists(log)) return shown;
        var lines = ReadShared(log).Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        for (int i = shown; i < lines.Count; i++)
            if (lines[i].Trim().Length > 0) say(lines[i].Trim());
        return Math.Max(shown, lines.Count);
    }

    private static string LastLines(string log, int n)
        => File.Exists(log) ? string.Join(" | ", ReadShared(log).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(n)) : "(журнала нет)";

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs, Encoding.UTF8);
        return r.ReadToEnd();
    }
}
