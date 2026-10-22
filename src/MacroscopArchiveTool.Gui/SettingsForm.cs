using System.Globalization;
using MacroscopArchiveTool.Core;

namespace MacroscopArchiveTool.Gui;

/// <summary>Everything from mat.json, with explanations, so nobody has to edit JSON by hand.</summary>
internal sealed class SettingsForm : Form
{
    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "mat.json");

    private readonly ToolConfig _cfg = Ui.LoadConfig();
    private readonly CheckBox _fromMacroscop = new() { Text = "Брать сроки хранения камер из настроек Macroscop (рекомендуется)", AutoSize = true };
    private readonly NumericUpDown _defaultDays = Num(1, 3650, 0);
    private readonly DataGridView _exceptions = new()
    {
        Width = 560, Height = 140, AllowUserToAddRows = true, AllowUserToDeleteRows = true, RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
    };
    private readonly NumericUpDown _heavy = Num(1.5m, 20, 1, 0.5m);
    private readonly NumericUpDown _silentCont = Num(0.5m, 365, 1, 0.5m);
    private readonly NumericUpDown _silentMotion = Num(1, 365, 0);
    private readonly NumericUpDown _threads = Num(1, 32, 0);
    private readonly TextBox _roots = new() { Width = 560 };
    private readonly TextBox _logs = new() { Width = 560 };
    private readonly CheckBox _updates = new() { Text = "Проверять обновления на GitHub при запуске", AutoSize = true };

    public SettingsForm()
    {
        Text = "Настройки";
        Font = Ui.Base;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.White;

        _exceptions.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Камера (имя или GUID)", FillWeight = 75 });
        _exceptions.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Срок, дней", FillWeight = 25 });

        _fromMacroscop.Checked = _cfg.RetentionFromMacroscop;
        _defaultDays.Value = Math.Clamp(_cfg.DefaultRetentionDays, 1, 3650);
        foreach (var (cam, days) in _cfg.CameraRetentionDays) _exceptions.Rows.Add(cam, days.ToString(CultureInfo.InvariantCulture));
        _heavy.Value = Math.Clamp((decimal)_cfg.HeavyFactor, 1.5m, 20);
        _silentCont.Value = Math.Clamp((decimal)_cfg.SilentDaysContinuous, 0.5m, 365);
        _silentMotion.Value = Math.Clamp((decimal)_cfg.SilentDaysMotion, 1, 365);
        _threads.Value = Math.Clamp(_cfg.ScanThreads, 1, 32);
        _roots.Text = string.Join("; ", _cfg.ArchiveRoots);
        _roots.PlaceholderText = "пусто — найти MacroscopArchive на всех дисках";
        _logs.Text = _cfg.LogsDir ?? "";
        _logs.PlaceholderText = ToolConfig.DefaultLogsDir;
        _updates.Checked = _cfg.CheckUpdates;

        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(22, 16, 22, 16) };
        p.Controls.Add(Ui.Text("Настройки", Ui.Title));
        p.Controls.Add(Ui.Text("Сохраняются в mat.json рядом с программой и при скане других серверов копируются туда же.", null, Ui.Muted, 600));

        p.Controls.Add(Section("Сроки хранения"));
        p.Controls.Add(_fromMacroscop);
        p.Controls.Add(Ui.Row(Ui.Text("Срок, если настройки Macroscop прочитать не удалось:"), _defaultDays, Ui.Text("дн.")));
        p.Controls.Add(Ui.Text("Исключения: свой срок для отдельных камер (важнее настроек Macroscop). Обычно не нужны.", Ui.Small, Ui.Muted, 600));
        p.Controls.Add(_exceptions);

        p.Controls.Add(Section("Отметки в отчёте"));
        p.Controls.Add(Ui.Row(Ui.Text("«Тяжёлая» камера пишет больше медианной камеры сервера в"), _heavy, Ui.Text("раза")));
        p.Controls.Add(Ui.Row(Ui.Text("«Не пишет»: нет новых записей дольше"), _silentCont, Ui.Text("дн. при постоянной записи и"), _silentMotion, Ui.Text("дн. по движению")));

        p.Controls.Add(Section("Сканирование (для опытных)"));
        p.Controls.Add(Ui.Row(Ui.Text("Дисков сканировать одновременно:"), _threads));
        p.Controls.Add(Ui.Text("Папки архива через «;»:", Ui.Small));
        p.Controls.Add(_roots);
        p.Controls.Add(Ui.Text("Папка журналов Macroscop (имена камер):", Ui.Small));
        p.Controls.Add(_logs);

        p.Controls.Add(Section("Обновления"));
        p.Controls.Add(_updates);

        var save = Ui.Button("Сохранить", (_, _) => Save());
        var cancel = Ui.Button("Отмена", (_, _) => Close());
        p.Controls.Add(Ui.Row(save, cancel));
        Controls.Add(p);
        CancelButton = cancel;
    }

    private static Label Section(string text) { var l = Ui.Text(text, Ui.Subtitle, Ui.Accent); l.Margin = new Padding(0, 12, 0, 6); return l; }

    private static NumericUpDown Num(decimal min, decimal max, int decimals, decimal step = 1)
        => new() { Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = step, Width = 80, Margin = new Padding(4, 2, 4, 0) };

    private void Save()
    {
        var exceptions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (DataGridViewRow row in _exceptions.Rows)
        {
            if (row.IsNewRow) continue;
            var cam = (row.Cells[0].Value as string ?? "").Trim();
            var daysText = (row.Cells[1].Value as string ?? "").Trim();
            if (cam.Length == 0 && daysText.Length == 0) continue;
            if (cam.Length == 0 || !int.TryParse(daysText, out var days) || days <= 0)
            {
                Ui.Error(this, $"Исключение «{cam}»: укажите имя камеры (или GUID) и срок в днях целым числом.");
                return;
            }
            exceptions[cam] = days;
        }
        var roots = _roots.Text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        foreach (var r in roots)
            if (!Directory.Exists(r)) { Ui.Error(this, $"Папка не найдена: {r}"); return; }

        _cfg.RetentionFromMacroscop = _fromMacroscop.Checked;
        _cfg.DefaultRetentionDays = (int)_defaultDays.Value;
        _cfg.CameraRetentionDays = exceptions;
        _cfg.HeavyFactor = (double)_heavy.Value;
        _cfg.SilentDaysContinuous = (double)_silentCont.Value;
        _cfg.SilentDaysMotion = (double)_silentMotion.Value;
        _cfg.ScanThreads = (int)_threads.Value;
        _cfg.ArchiveRoots = roots;
        _cfg.LogsDir = _logs.Text.Trim().Length > 0 ? _logs.Text.Trim() : null;
        _cfg.CheckUpdates = _updates.Checked;
        try
        {
            _cfg.Save(ConfigPath);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) { Ui.Error(this, "Не удалось сохранить mat.json: " + ex.Message); }
    }
}

/// <summary>Shown once: what the program does, what it never does, where to start.</summary>
internal sealed class WelcomeForm : Form
{
    private static string FlagPath => Path.Combine(Core.Migration.WindowsSystemOps.DataDir, "welcome.done");
    public bool StartReport { get; private set; }
    public bool AddServers { get; private set; }

    public static bool ShouldShow()
    {
        try { return !File.Exists(FlagPath) && !Directory.Exists(Path.Combine(AppContext.BaseDirectory, "reports")); }
        catch (Exception) { return false; }
    }

    public WelcomeForm()
    {
        Text = "Добро пожаловать";
        Font = Ui.Base;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.White;

        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(24, 18, 24, 18) };
        p.Controls.Add(Ui.Text("MacroscopArchiveTool", Ui.Title));
        p.Controls.Add(Ui.Text("Помощник администратора видеоархива Macroscop. Программа работает с файлами архива и дисками Windows; " +
                               "сам Macroscop, его программы и базы она не изменяет.", null, null, 620));
        foreach (var (title, text) in new[]
        {
            ("Отчёт по архиву", "Сколько места занимают диски, дни и камеры, сроки хранения, «тяжёлые» и потерянные камеры. Только чтение — можно делать в любое время."),
            ("Несколько серверов и расписание", "Добавьте другие серверы — отчёт станет сводным. Расписание делает его автоматически и обновляет данные для Zabbix."),
            ("Вывод диска из эксплуатации", "Перенос архива со сбойного диска на другой том или в виртуальный диск: с проверкой копии, продолжением после перерыва и откатом."),
            ("Очистка мусора", "Старые копии конфигурации, неиспользуемый PostgreSQL, старые журналы. Сначала в карантин — всё можно вернуть."),
        })
        {
            p.Controls.Add(Ui.Text(title, Ui.Subtitle, Ui.Accent));
            p.Controls.Add(Ui.Text(text, null, Ui.Muted, 620));
        }
        p.Controls.Add(Ui.Text("Всё, что меняет диски или файлы, выполняется только после вашего подтверждения.", Ui.Subtitle, null, 620));

        var report = Ui.Button("Сделать первый отчёт", (_, _) => { StartReport = true; Done(); });
        var servers = Ui.Button("Сначала добавить другие серверы", (_, _) => { AddServers = true; Done(); });
        var close = Ui.Button("Закрыть", (_, _) => Done());
        p.Controls.Add(Ui.Row(report, servers, close));
        Controls.Add(p);
        AcceptButton = report;
        CancelButton = close;
    }

    private void Done()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(FlagPath)!); File.WriteAllText(FlagPath, DateTime.Now.ToString("s")); } catch (Exception) { }
        Close();
    }
}
