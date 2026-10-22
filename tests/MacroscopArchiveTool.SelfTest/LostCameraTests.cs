using MacroscopArchiveTool.Core;

partial class Program
{
    internal static void LostCameraKinds()
    {
        var now = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        var r = new ScanReport { ScannedAtUtc = now, DefaultRetentionDays = 90, ConfigRead = true, BindingsRead = true, SilentDaysContinuous = 1, SilentDaysMotion = 7 };
        CameraStat Cam(string name, double silentDays, int mode, bool inConfig = true, bool boundHere = true, bool disabled = false, long bytes = 1000)
        {
            var c = new CameraStat
            {
                Id = name, Name = name, Bytes = bytes, InConfig = inConfig, BoundHere = boundHere, RetentionDays = 90,
                OldestUtc = bytes > 0 ? now.AddDays(-80) : null, NewestUtc = bytes > 0 ? now.AddDays(-silentDays) : null,
                Settings = new CameraSettings { ArchiveEnabled = true, ArchiveMode = mode, Disabled = disabled },
            };
            r.Cameras.Add(c);
            return c;
        }
        var ok = Cam("пишет", 0.1, 2);
        var quietMotion = Cam("по движению 3 дня тишины", 3, 1);
        var silentCont = Cam("постоянная запись, 2 дня тишины", 2, 2);
        var silentMotion = Cam("по движению, 10 дней тишины", 10, 1);
        var gone = Cam("снята давно", 0, 2, bytes: 0);
        var deleted = Cam("удалена", 20, 2, inConfig: false, boundHere: false);
        var disabled = Cam("отключена", 30, 2, disabled: true);
        var moved = Cam("переехала", 30, 2, boundHere: false);
        ReportAnalyzer.Analyze(r, 2.5);

        R.Eq(null, ok.LostKind, "пишущая камера в порядке");
        R.Eq(null, quietMotion.LostKind, "по движению 3 дня тишины — ещё норма");
        R.Eq("silent", silentCont.LostKind, "постоянная запись без новых записей > 1 дня");
        R.Eq("silent", silentMotion.LostKind, "по движению без записей > 7 дней");
        R.Eq("no-archive", gone.LostKind, "нет ни одной записи за срок хранения");
        R.Eq("deleted", deleted.LostKind, "удалена из конфигурации, остался архив");
        R.Eq("disabled", disabled.LostKind, "отключена в Macroscop");
        R.Eq("moved", moved.LostKind, "привязана к другому серверу");
        R.Eq(now.AddDays(-2 + 90).Date, silentCont.ArchiveUntilUtc!.Value.Date, "архив хранится до: последняя запись + срок");

        var html = HtmlReport.Render(new[] { r });
        R.True(html.Contains("за весь срок хранения нет ни одной записи") && html.Contains("освободится лицензия"), "рекомендация для потерянной камеры");
        var m = System.Text.Json.JsonSerializer.Serialize(MetricsWriter.Build(r));
        R.True(m.Contains("\"cameras_no_archive\":1") && m.Contains("\"cameras_silent\":2"), "счётчики для Zabbix: " + m);
    }
}
