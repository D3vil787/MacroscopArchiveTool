using System.Runtime.InteropServices;
using MacroscopArchiveTool.Core;
using MacroscopArchiveTool.Core.Remote;

namespace MacroscopArchiveTool.Gui;

/// <summary>Automatic archive report: when, which servers, and how the last run went.</summary>
internal sealed class ScheduleForm : Form
{
    private static readonly string[] DayNames = { "воскресенье", "понедельник", "вторник", "среда", "четверг", "пятница", "суббота" };

    private readonly CheckBox _enabled = new() { Text = "Делать отчёт по архиву автоматически", AutoSize = true, Font = Ui.Subtitle };
    private readonly RadioButton _daily = new() { Text = "Каждый день", AutoSize = true };
    private readonly RadioButton _weekly = new() { Text = "Раз в неделю:", AutoSize = true };
    private readonly ComboBox _day = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly DateTimePicker _time = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 80 };
    private readonly CheckBox _all = new() { AutoSize = true };
    private readonly Label _allNote = Ui.Text("", Ui.Small, Ui.Muted, 600);
    private readonly TextBox _password = new() { UseSystemPasswordChar = true, Width = 260 };
    private readonly Label _passwordLabel = Ui.Text("Пароль учётной записи Windows:");
    private readonly Label _state = Ui.Text("", null, null, 600);
    private readonly Button _runNow;
    private readonly string _user = $"{Environment.UserDomainName}\\{Environment.UserName}";
    private readonly int _servers = ServerStore.Load(ServersForm.StorePath).Servers.Count(s => s.Enabled);

    public static string ReportsDir => Path.Combine(AppContext.BaseDirectory, "reports");

    public ScheduleForm()
    {
        Text = "Плановый отчёт по архиву";
        Font = Ui.Base;
        if (Ui.AppIcon != null) Icon = Ui.AppIcon;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.White;

        _day.Items.AddRange(DayNames);
        _all.Text = $"Все серверы: этот и {_servers} из списка «Серверы» — сводный отчёт";
        _all.Enabled = _servers > 0;
        _allNote.Text = $"Задание будет выполняться от вашей учётной записи ({_user}): от имени SYSTEM другие серверы недоступны. " +
                        "Windows один раз сохранит пароль в Планировщике заданий; программа его не хранит. Если пароль сменится — сохраните расписание заново.";

        var s = ScanScheduler.LoadSettings();
        var st = ScanScheduler.State();
        _enabled.Checked = st.Exists;
        _daily.Checked = s.Daily; _weekly.Checked = !s.Daily;
        _day.SelectedIndex = (int)s.Day;
        _time.Value = DateTime.Today.Add(s.Time);
        _all.Checked = s.AllServers && _servers > 0;

        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(22) };
        p.Controls.Add(Ui.Text("Плановый отчёт по архиву", Ui.Title));
        p.Controls.Add(Ui.Text($"Только чтение, Macroscop может работать. Отчёты и журнал — в папке {ReportsDir}. " +
                               "После каждого скана обновляется metrics.json для Zabbix и раздел «Динамика».", null, Ui.Muted, 600));
        p.Controls.Add(_enabled);
        p.Controls.Add(Ui.Row(_daily, _weekly, _day));
        p.Controls.Add(Ui.Row(Ui.Text("Время:"), _time));
        p.Controls.Add(_all);
        p.Controls.Add(_allNote);
        p.Controls.Add(Ui.Row(_passwordLabel, _password));
        p.Controls.Add(Ui.Text("Состояние", Ui.Subtitle));
        p.Controls.Add(_state);

        var save = Ui.Button("Сохранить", (_, _) => Save());
        _runNow = Ui.Button("Запустить сейчас", (_, _) => RunNow());
        var log = Ui.Button("Журнал", (_, _) =>
        {
            var f = Path.Combine(ReportsDir, "scheduled_scan.log");
            if (File.Exists(f)) Ui.Open(f); else Ui.Info(this, "Журнала ещё нет: плановый скан пока не запускался.");
        });
        var close = Ui.Button("Закрыть", (_, _) => Close());
        p.Controls.Add(Ui.Row(save, _runNow, log, close));
        Controls.Add(p);
        CancelButton = close;

        foreach (var c in new Control[] { _enabled, _daily, _weekly, _all }) if (c is CheckBox cb) cb.CheckedChanged += (_, _) => Toggle(); else ((RadioButton)c).CheckedChanged += (_, _) => Toggle();
        Toggle();
        ShowState(st);
    }

    private void Toggle()
    {
        bool on = _enabled.Checked;
        _daily.Enabled = _weekly.Enabled = _time.Enabled = on;
        _day.Enabled = on && _weekly.Checked;
        _all.Enabled = on && _servers > 0;
        _allNote.Visible = _passwordLabel.Visible = _password.Visible = on && _all.Checked;
    }

    private void ShowState(ScheduleState st)
    {
        if (!st.Exists) { _state.Text = "Плановый скан не настроен."; _state.ForeColor = Ui.Muted; _runNow.Enabled = false; return; }
        var last = st.LastRun is DateTime l
            ? $"последний — {l:dd.MM.yyyy HH:mm}, " + st.LastResult switch
              {
                  0 => "успешно",
                  2 => "часть серверов не ответила (см. журнал)",
                  267009 => "выполняется",
                  267011 => "ещё не запускался",
                  int code => $"ошибка, код {code} (см. журнал)",
                  null => "",
              }
            : "ещё не запускался";
        _state.Text = $"{ScanScheduler.LoadSettings().Describe()}; от имени {st.RunAs ?? "?"}. " +
                      (st.NextRun is DateTime n ? $"Следующий запуск — {n:dd.MM.yyyy HH:mm}; " : "") + last + ".";
        _state.ForeColor = st.LastResult is null or 0 or 267009 or 267011 ? SystemColors.ControlText : Ui.Warn;
        _runNow.Enabled = true;
    }

    private void Save()
    {
        try
        {
            if (!_enabled.Checked)
            {
                ScanScheduler.Remove();
                ShowState(ScanScheduler.State());
                Ui.Info(this, "Плановый скан выключен.");
                return;
            }
            var s = new ScheduleSettings
            {
                Daily = _daily.Checked, Day = (DayOfWeek)Math.Max(0, _day.SelectedIndex),
                Time = new TimeSpan(_time.Value.Hour, _time.Value.Minute, 0),
                AllServers = _all.Checked && _servers > 0, User = _user,
            };
            if (s.AllServers && _password.Text.Length == 0) { Ui.Error(this, "Введите пароль своей учётной записи Windows — он нужен Планировщику, чтобы запускать скан без входа в систему."); return; }
            UseWaitCursor = true;
            ScanScheduler.Set(s, s.AllServers ? _password.Text : null, AppContext.BaseDirectory);
            _password.Clear();
            ShowState(ScanScheduler.State());
            Ui.Info(this, $"Готово: {s.Describe()}.");
        }
        catch (COMException ex) when ((uint)ex.HResult == 0x8007052E)
        {
            Ui.Error(this, "Windows не принял пароль для этой учётной записи. Проверьте пароль и повторите.");
        }
        catch (Exception ex) { Ui.Error(this, "Не удалось сохранить расписание: " + ex.Message); }
        finally { UseWaitCursor = false; }
    }

    private void RunNow()
    {
        try { ScanScheduler.RunNow(); Ui.Info(this, "Скан запущен в фоне. Результат появится в папке отчётов, ход — в журнале."); ShowState(ScanScheduler.State()); }
        catch (Exception ex) { Ui.Error(this, "Не удалось запустить: " + ex.Message); }
    }
}
