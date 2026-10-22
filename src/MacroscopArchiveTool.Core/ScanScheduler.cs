using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace MacroscopArchiveTool.Core;

public sealed class ScheduleSettings
{
    public bool Daily { get; set; } = true;
    /// <summary>For weekly: 0 = Sunday … 6 = Saturday (System.DayOfWeek).</summary>
    public DayOfWeek Day { get; set; } = DayOfWeek.Monday;
    public TimeSpan Time { get; set; } = new(5, 30, 0);
    /// <summary>Also scan the servers from the «Серверы» list and build a combined report (runs as the user, not SYSTEM).</summary>
    public bool AllServers { get; set; }
    /// <summary>Account the task runs as when <see cref="AllServers"/> is on (DOMAIN\user or COMPUTER\user).</summary>
    public string? User { get; set; }

    public string Describe() =>
        (Daily ? "каждый день" : "каждую неделю, " + System.Globalization.CultureInfo.GetCultureInfo("ru-RU").DateTimeFormat.GetDayName(Day))
        + $" в {Time:hh\\:mm}" + (AllServers ? ", все серверы" : ", этот сервер");
}

public sealed record ScheduleState(bool Exists, bool Enabled, DateTime? NextRun, DateTime? LastRun, int? LastResult, string? RunAs);

/// <summary>
/// The scheduled archive scan, created through the Windows Task Scheduler API (so no password ever appears on a
/// command line): «\MacroscopArchiveTool\ScheduledScan» runs scheduled_scan.bat next to the program.
/// This server only → runs as SYSTEM; all servers → runs as the user (Windows keeps the password).
/// </summary>
[SupportedOSPlatform("windows")]
public static class ScanScheduler
{
    public const string Folder = "MacroscopArchiveTool";
    public const string TaskName = "ScheduledScan";
    private const string OldTaskName = "WeeklyScan";
    private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MacroscopArchiveTool", "schedule.json");

    public static ScheduleSettings LoadSettings()
    {
        try { return File.Exists(SettingsPath) ? JsonSerializer.Deserialize<ScheduleSettings>(File.ReadAllText(SettingsPath), ToolConfig.JsonOptions) ?? new() : new(); }
        catch (Exception) { return new(); }
    }

    public static void Set(ScheduleSettings s, string? password, string appDir)
    {
        var bat = Path.Combine(appDir, "scheduled_scan.bat");
        Directory.CreateDirectory(Path.Combine(appDir, "reports"));
        File.WriteAllText(bat, "@echo off\r\ncd /d \"%~dp0\"\r\n" +
            $"mat.exe {(s.AllServers ? "scan-all" : "scan")} >> \"%~dp0reports\\scheduled_scan.log\" 2>&1\r\n", Encoding.ASCII);

        dynamic svc = Service();
        dynamic folder = GetOrCreateFolder(svc);
        dynamic td = svc.NewTask(0);
        td.RegistrationInfo.Description = "MacroscopArchiveTool: read-only scan of the Macroscop archive (" + s.Describe() + "). Reports: " + Path.Combine(appDir, "reports");
        td.Settings.Enabled = true;
        td.Settings.StartWhenAvailable = true;
        td.Settings.DisallowStartIfOnBatteries = false;
        td.Settings.StopIfGoingOnBatteries = false;
        td.Settings.ExecutionTimeLimit = "PT6H";
        td.Settings.MultipleInstances = 2;          // TASK_INSTANCES_IGNORE_NEW
        td.Principal.RunLevel = 1;                  // TASK_RUNLEVEL_HIGHEST

        dynamic trigger = td.Triggers.Create(s.Daily ? 2 : 3);   // TASK_TRIGGER_DAILY / WEEKLY
        trigger.StartBoundary = DateTime.Today.Add(s.Time).ToString("yyyy-MM-dd'T'HH:mm:ss");
        if (s.Daily) trigger.DaysInterval = (short)1;
        else { trigger.WeeksInterval = (short)1; trigger.DaysOfWeek = (short)(1 << (int)s.Day); }

        dynamic action = td.Actions.Create(0);      // TASK_ACTION_EXEC
        action.Path = bat;
        action.WorkingDirectory = appDir;

        if (s.AllServers)
        {
            if (string.IsNullOrEmpty(s.User) || string.IsNullOrEmpty(password)) throw new ArgumentException("Для скана всех серверов нужны учётная запись и пароль.");
            folder.RegisterTaskDefinition(TaskName, td, 6, s.User, password, 1, null);         // TASK_LOGON_PASSWORD
        }
        else
        {
            s.User = null;
            folder.RegisterTaskDefinition(TaskName, td, 6, "SYSTEM", null, 5, null);           // TASK_LOGON_SERVICE_ACCOUNT
        }
        try { folder.DeleteTask(OldTaskName, 0); } catch (Exception) { /* not there */ }

        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s, ToolConfig.JsonOptions));
    }

    public static void Remove()
    {
        dynamic svc = Service();
        try
        {
            dynamic folder = svc.GetFolder("\\" + Folder);
            foreach (var name in new[] { TaskName, OldTaskName }) { try { folder.DeleteTask(name, 0); } catch (Exception) { } }
        }
        catch (Exception) { /* no folder */ }
    }

    public static ScheduleState State()
    {
        try
        {
            dynamic svc = Service();
            dynamic folder = svc.GetFolder("\\" + Folder);
            dynamic? task = null;
            foreach (var name in new[] { TaskName, OldTaskName }) { try { task = folder.GetTask(name); break; } catch (Exception) { } }
            if (task == null) return new ScheduleState(false, false, null, null, null, null);
            DateTime next = task.NextRunTime, last = task.LastRunTime;
            int result = task.LastTaskResult;
            string? user = null;
            try { user = task.Definition.Principal.UserId; } catch (Exception) { }
            bool enabled = task.Enabled;
            DateTime? nextRun = next.Year > 2000 ? next : null, lastRun = last.Year > 2000 ? last : null;
            int? lastResult = lastRun != null ? result : null;
            return new ScheduleState(true, enabled, nextRun, lastRun, lastResult, user);
        }
        catch (Exception) { return new ScheduleState(false, false, null, null, null, null); }
    }

    public static void RunNow()
    {
        dynamic svc = Service();
        dynamic task = svc.GetFolder("\\" + Folder).GetTask(TaskName);
        task.Run(null);
    }

    private static dynamic Service()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Планировщик заданий Windows недоступен.");
        dynamic svc = Activator.CreateInstance(type)!;
        svc.Connect();
        return svc;
    }

    private static dynamic GetOrCreateFolder(dynamic svc)
    {
        try { return svc.GetFolder("\\" + Folder); }
        catch (Exception) { return svc.GetFolder("\\").CreateFolder(Folder, null); }
    }
}
