using MacroscopArchiveTool.Core;
using MacroscopArchiveTool.Core.Migration;

namespace MacroscopArchiveTool.Gui;

/// <summary>Start screen: archive report, retiring a disk, and disk retirements already in progress.</summary>
internal sealed class MainForm : Form
{
    private readonly ListView _jobs = new()
    {
        View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, Font = Ui.Base,
        Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    private readonly Button _openJob;

    private static readonly Label _scheduleStatus = Ui.Text("", Ui.Small, Ui.Muted, 300);

    private static void RefreshSchedule()
    {
        try
        {
            var st = ScanScheduler.State();
            _scheduleStatus.Text = !st.Exists ? "Плановый отчёт: выключен"
                : $"Плановый отчёт: {ScanScheduler.LoadSettings().Describe()}" + (st.NextRun is DateTime n ? $", следующий {n:dd.MM HH:mm}" : "");
        }
        catch (Exception) { _scheduleStatus.Text = ""; }
    }

    private readonly System.Windows.Forms.Timer _idleTimer = new() { Interval = 2000 };

    public static string StateDir => Path.Combine(WindowsSystemOps.DataDir, "retire");

    public MainForm()
    {
        Text = $"MacroscopArchiveTool {ToolInfo.Version}";
        Font = Ui.Base;
        if (Ui.AppIcon != null) Icon = Ui.AppIcon;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(980, 600);
        Size = new Size(1100, 680);
        BackColor = Color.White;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(24, 20, 24, 16) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var head = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 12) };
        head.Controls.Add(Ui.Text("Архив Macroscop", Ui.Title));
        head.Controls.Add(Ui.Text("Отчёт о заполнении дисков и безопасный вывод архивного диска из эксплуатации. " +
                                  "Программа не изменяет Macroscop: она работает с файлами архива и дисками Windows.", null, Ui.Muted, 800));
        root.Controls.Add(head, 0, 0);

        var cards = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true, Margin = new Padding(0, 0, 0, 16) };
        for (int i = 0; i < 3; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
        cards.Controls.Add(Card("Отчёт по архиву",
            "Объём и глубина архива по дискам, дням и камерам, сроки хранения из настроек Macroscop, «тяжёлые» камеры. " +
            "Можно добавить другие серверы — получится сводный отчёт. Только чтение.",
            "Сделать отчёт", (_, _) => RunReport(), ("Серверы…", (Action)(() => { using var f = new ServersForm(); f.ShowDialog(this); })),
            ("Расписание…", (Action)(() => { using var f = new ScheduleForm(); f.ShowDialog(this); RefreshSchedule(); }))), 0, 0);
        cards.Controls.Add(Card("Вывести диск из эксплуатации",
            "Перенести архив с диска, который нужно убрать (например, с ошибками SMART), на другой том или в файл виртуального диска. " +
            "Записи остаются доступны, пока не истечёт срок хранения.",
            "Начать…", (_, _) => OpenWizard(null)), 1, 0);
        cards.Controls.Add(Card("Очистка мусора",
            "Старые резервные копии конфигурации, неиспользуемая версия PostgreSQL, старые журналы — на всех серверах. " +
            "Сначала в карантин, вернуть можно.",
            "Проверить…", (_, _) => RunCleanup()), 2, 0);
        root.Controls.Add(cards, 0, 1);

        var jobsBox = new GroupBox { Text = "Начатые выводы дисков", Dock = DockStyle.Fill, Font = Ui.Base, Padding = new Padding(10) };
        _jobs.Columns.Add("Диск", 70);
        _jobs.Columns.Add("Куда", 330);
        _jobs.Columns.Add("Этап", 220);
        _jobs.Columns.Add("Сейчас", 240);
        _jobs.DoubleClick += (_, _) => OpenSelected();
        _jobs.SelectedIndexChanged += (_, _) => _openJob!.Enabled = _jobs.SelectedItems.Count > 0;
        _openJob = Ui.Button("Открыть", (_, _) => OpenSelected());
        _openJob.Enabled = false;
        var jobsLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        jobsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        jobsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        jobsLayout.Controls.Add(_jobs, 0, 0);
        jobsLayout.Controls.Add(Ui.Row(_openJob), 0, 1);
        jobsBox.Controls.Add(jobsLayout);
        root.Controls.Add(jobsBox, 0, 2);

        var link = new LinkLabel { Text = "Документация и исходный код: github.com/D3vil787/MacroscopArchiveTool", AutoSize = true, Font = Ui.Small, Margin = new Padding(0, 8, 0, 0) };
        link.LinkClicked += (_, _) => Ui.Open("https://github.com/D3vil787/MacroscopArchiveTool");
        var check = new LinkLabel { Text = "Проверить обновления", AutoSize = true, Font = Ui.Small, Margin = new Padding(24, 8, 0, 0) };
        check.LinkClicked += async (_, _) => await CheckUpdatesAsync(manual: true);
        var settings = new LinkLabel { Text = "Настройки", AutoSize = true, Font = Ui.Small, Margin = new Padding(24, 8, 0, 0) };
        settings.LinkClicked += (_, _) => { using var f = new SettingsForm(); f.ShowDialog(this); };
        root.Controls.Add(Ui.Row(link, check, settings), 0, 3);

        // Update banner above everything; hidden until GitHub has a newer release.
        _updateText = Ui.Text("", Ui.Subtitle, Color.White);
        _updateText.Margin = new Padding(0, 6, 16, 0);
        _updateButton = Ui.Button("Обновить", async (_, _) => await InstallUpdateAsync());
        _notesLink = new LinkLabel { Text = "Что нового", AutoSize = true, LinkColor = Color.White, Font = Ui.Base, Margin = new Padding(0, 8, 16, 0) };
        _notesLink.LinkClicked += (_, _) => { if (_update != null) Ui.Open(_update.PageUrl); };
        _banner = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = Ui.Accent, Padding = new Padding(24, 8, 24, 8), Visible = false, WrapContents = false };
        _banner.Controls.AddRange(new Control[] { _updateText, _notesLink, _updateButton });
        Controls.Add(_banner);
        Shown += async (_, _) =>
        {
            Updater.CleanupOldFiles(AppContext.BaseDirectory);
            if (WelcomeForm.ShouldShow())
            {
                using var w = new WelcomeForm();
                w.ShowDialog(this);
                if (w.AddServers) { using var f = new ServersForm(); f.ShowDialog(this); }
                if (w.StartReport || w.AddServers) RunReport();
            }
            if (Ui.LoadConfig().CheckUpdates) await CheckUpdatesAsync(manual: false);
        };

        Load += (_, _) => { RefreshJobs(); RefreshSchedule(); };
        Activated += (_, _) => RefreshJobs();
        FormClosing += OnMainClosing;
        _idleTimer.Tick += (_, _) => UpdateLive();
        _idleTimer.Start();
        // The window often stays open for days on the server: look for a new version periodically, not only at start.
        _updateTimer.Tick += async (_, _) =>
        {
            var cfg = Ui.LoadConfig();
            if (!cfg.CheckUpdates || cfg.UpdateCheckHours <= 0) return;
            if (DateTime.UtcNow - _lastUpdateCheckUtc < TimeSpan.FromHours(cfg.UpdateCheckHours)) return;
            await CheckUpdatesAsync(manual: false);
        };
        _updateTimer.Start();
    }

    private static Control Card(string title, string text, string button, EventHandler onClick, params (string Text, Action Click)[] links)
    {
        var p = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill, AutoSize = true,
            BackColor = Ui.PanelBack, Padding = new Padding(16), Margin = new Padding(0, 0, 12, 0),
        };
        p.Controls.Add(Ui.Text(title, Ui.Subtitle, Ui.Accent));
        p.Controls.Add(Ui.Text(text, null, null, 300));
        var b = Ui.Button(button, onClick);
        b.Margin = new Padding(0, 6, 16, 0);
        var row = Ui.Row(b);
        foreach (var l in links)
        {
            var ll = new LinkLabel { Text = l.Text, AutoSize = true, Font = Ui.Base, Margin = new Padding(0, 12, 12, 0) };
            ll.LinkClicked += (_, _) => l.Click();
            row.Controls.Add(ll);
        }
        p.Controls.Add(row);
        if (title == "Отчёт по архиву") p.Controls.Add(_scheduleStatus);
        return p;
    }

    private void RefreshJobs()
    {
        var selected = _jobs.SelectedItems.Count > 0 ? ((RetireState)_jobs.SelectedItems[0].Tag!).Id : null;
        _jobs.BeginUpdate();
        _jobs.Items.Clear();
        foreach (var s in RetireState.LoadAll(StateDir))
        {
            var target = s.TargetKind == TargetKind.Vhdx ? s.VhdxPath ?? "" : $"том {s.TargetVolumeLetter}:";
            var item = new ListViewItem(new[] { $"{s.SourceLetter}:", target, StageText(s.Stage), LiveText(s) }) { Tag = s };
            if (s.Stage == RetireStage.Finished) item.ForeColor = Ui.Muted;
            _jobs.Items.Add(item);
            if (s.Id == selected) item.Selected = true;
        }
        _jobs.EndUpdate();
    }

    private string LiveText(RetireState s)
    {
        var w = _wizards.FirstOrDefault(x => x.JobId == s.Id);
        if (w != null && w.LiveStatus.Length > 0) return w.LiveStatus + (w.Visible ? "" : " — в фоне");
        if (w != null && w.IsSwitching) return "Переключение…";
        return "обновлено " + s.UpdatedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
    }

    /// <summary>Refreshes the "now" column of jobs opened in this session without reloading the list.</summary>
    private void UpdateLive()
    {
        foreach (ListViewItem item in _jobs.Items)
            if (item.Tag is RetireState s && _wizards.Any(w => w.JobId == s.Id))
            {
                var text = LiveText(s);
                if (item.SubItems[3].Text != text) item.SubItems[3].Text = text;
            }
        if (_wizards.Any(w => w.JobId != null && !_jobs.Items.Cast<ListViewItem>().Any(i => ((RetireState)i.Tag!).Id == w.JobId))) RefreshJobs();
    }

    public static string StageText(RetireStage s) => s switch
    {
        RetireStage.Planned => "Проверка",
        RetireStage.TargetReady => "Новый диск создан",
        RetireStage.Copied => "Скопировано, ждёт переключения",
        RetireStage.Switched => "Работает с нового диска, ждёт опустения",
        RetireStage.Finished => "Завершён",
        _ => s.ToString(),
    };

    private void OpenSelected()
    {
        if (_jobs.SelectedItems.Count == 0) return;
        OpenWizard((RetireState)_jobs.SelectedItems[0].Tag!);
    }

    // Wizards are ordinary windows: copying runs in the background while reports are made and viewed.
    private readonly List<RetireWizard> _wizards = new();
    private bool _exitWhenIdle;

    private void OpenWizard(RetireState? state)
    {
        var open = state == null ? null : _wizards.FirstOrDefault(w => w.JobId == state.Id);
        if (open != null)
        {
            open.Show();
            if (open.WindowState == FormWindowState.Minimized) open.WindowState = FormWindowState.Normal;
            open.Activate();
            return;
        }
        var w = new RetireWizard(state);
        _wizards.Add(w);
        w.FormClosed += (_, _) => { _wizards.Remove(w); w.Dispose(); RefreshJobs(); if (_exitWhenIdle) Close(); };
        w.Show();
    }

    private CleanupForm? _cleanup;

    private void RunCleanup()
    {
        if (_cleanup is { IsDisposed: false }) { _cleanup.Activate(); return; }
        _cleanup = new CleanupForm();
        _cleanup.FormClosed += (_, _) => { _cleanup.Dispose(); _cleanup = null; };
        _cleanup.Show();
    }

    private ReportForm? _report;

    private void RunReport()
    {
        if (_report is { IsDisposed: false }) { _report.Activate(); return; }
        _report = new ReportForm();
        _report.FormClosed += (_, _) => { _report.Dispose(); _report = null; };
        _report.Show();
    }

    // ------------------------------------------------------------------ updates

    private readonly FlowLayoutPanel _banner;
    private readonly Label _updateText;
    private readonly Button _updateButton;
    private readonly LinkLabel _notesLink;
    private UpdateInfo? _update;
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 10 * 60 * 1000 };
    private DateTime _lastUpdateCheckUtc;
    private bool _checkingUpdates;

    private async Task CheckUpdatesAsync(bool manual)
    {
        if (_checkingUpdates || !_updateButton.Enabled) return;   // a check or an installation is already running
        _checkingUpdates = true;
        _lastUpdateCheckUtc = DateTime.UtcNow;
        try
        {
            var found = await Updater.CheckAsync(CancellationToken.None);
            if (found == null)
            {
                if (manual) Ui.Info(this, $"Установлена последняя версия ({ToolInfo.Version}).");
                return;
            }
            bool fresh = _update?.Version != found.Version;
            _update = found;
            _updateText.Text = $"Доступна новая версия {_update.Version} (у вас {ToolInfo.Version})";
            _banner.Visible = true;
            if (fresh && !manual && WindowState == FormWindowState.Minimized) FlashTaskbar();
        }
        catch (Exception ex)
        {
            // Servers often have no internet access: stay quiet unless the user asked.
            if (manual) Ui.Error(this, "Не удалось проверить обновления: " + ex.Message +
                                       $"\n\nНовую версию можно скачать вручную: https://github.com/{Updater.Repository}/releases");
        }
        finally { _checkingUpdates = false; }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FLASHWINFO info);

    /// <summary>Blinks the taskbar button until the window is opened (FLASHW_TRAY | FLASHW_TIMERNOFG).</summary>
    private void FlashTaskbar()
    {
        try
        {
            var fi = new FLASHWINFO { hwnd = Handle, dwFlags = 2 | 12, uCount = uint.MaxValue };
            fi.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(fi);
            FlashWindowEx(ref fi);
        }
        catch (Exception) { /* cosmetic */ }
    }

    private async Task InstallUpdateAsync()
    {
        if (_update == null) return;
        if (_wizards.Any(w => w.IsWorking))
        {
            Ui.Info(this, "Сейчас идёт копирование или переключение диска. Остановите копирование (оно продолжится с того же места после обновления) и повторите.");
            return;
        }
        if (!Ui.Confirm(this, $"Скачать и установить версию {_update.Version}? Программа перезапустится. Настройки, отчёты и начатые выводы дисков сохранятся.")) return;
        _updateButton.Enabled = false;
        try
        {
            var progress = new Progress<double>(p => _updateText.Text = $"Загрузка версии {_update.Version}… {p:0}%");
            var zip = await Updater.DownloadAsync(_update, progress, CancellationToken.None);
            _updateText.Text = "Установка…";
            var dir = AppContext.BaseDirectory;
            await Task.Run(() => Updater.Apply(zip, dir));
            foreach (var w in _wizards.ToList()) w.Close();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(dir, "MacroscopArchiveTool.exe")) { UseShellExecute = true, WorkingDirectory = dir });
            Application.Exit();
        }
        catch (Exception ex)
        {
            _updateText.Text = $"Доступна новая версия {_update.Version} (у вас {ToolInfo.Version})";
            _updateButton.Enabled = true;
            Ui.Error(this, "Обновление не установлено: " + ex.Message + "\n\nТекущая версия продолжает работать.");
        }
    }

    private void OnMainClosing(object? sender, FormClosingEventArgs e)
    {
        var working = _wizards.Where(w => w.IsWorking).ToList();
        if (working.Count == 0) { foreach (var w in _wizards.ToList()) w.Close(); return; }
        e.Cancel = true;
        if (working.Any(w => w.IsSwitching)) { Ui.Info(this, "Идёт переключение диска — дождитесь его окончания."); return; }
        if (!Ui.Confirm(this, "Идёт копирование. Закрыть программу?\n\nКопирование остановится и продолжится с того же места, когда вы снова откроете этот вывод диска.")) return;
        _exitWhenIdle = true;
        foreach (var w in working) w.StopWork();
        _idleTimer.Tick += (_, _) => { if (_wizards.All(w => !w.IsWorking)) { _idleTimer.Stop(); foreach (var w in _wizards.ToList()) w.Close(); Close(); } };
    }
}
