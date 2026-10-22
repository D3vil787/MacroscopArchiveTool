using MacroscopArchiveTool.Core.Migration;

namespace MacroscopArchiveTool.Gui;

/// <summary>
/// Step-by-step retirement of an archive disk: choose the disk, choose where to move it, settings, checks,
/// live copy with progress, the short switch-over, and the final step once retention has emptied the disk.
/// The state is saved after every step, so the wizard can be closed and reopened from the start screen.
/// </summary>
internal sealed class RetireWizard : Form
{
    private enum Page { Source, Target, Settings, Check, Copy, Switch, Done }
    private static readonly string[] StepNames =
        { "Диск", "Куда перенести", "Настройки", "Проверка", "Копирование", "Переключение", "Завершение" };
    private const int W = 700;

    private readonly ISystemOps _ops = new WindowsSystemOps();
    private readonly RetireOrchestrator _orc;
    private RetireState? _state;
    private Page _page;
    private bool _needPlan;

    private readonly Label[] _steps = new Label[StepNames.Length];
    private readonly Panel _host = new() { Dock = DockStyle.Fill, Padding = new Padding(24, 18, 16, 8) };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = Ui.Small, BackColor = Color.White };
    private readonly Button _back, _next, _close;
    private Func<bool> _canNext = () => false;

    // choices
    private List<VolumeInfo> _volumes = new();
    private char? _source, _target;
    private TargetKind _kind = TargetKind.Vhdx;
    private readonly CopyOptions _options = new();
    private bool _allowActive;

    // long operations
    private bool _busy, _switching;
    private CancellationTokenSource? _cts;
    private readonly ManualResetEventSlim _pause = new(true);
    private volatile bool _stopWaiting;

    public RetireWizard(RetireState? existing)
    {
        _orc = new RetireOrchestrator(_ops, MainForm.StateDir) { LogsDir = Ui.LoadConfig().LogsDir ?? Core.ToolConfig.DefaultLogsDir };
        _orc.Logged += line => { if (IsHandleCreated) BeginInvoke(() => AppendLog(line)); };

        Text = "Вывод архивного диска из эксплуатации";
        Font = Ui.Base;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1040, 780);
        MinimumSize = new Size(900, 640);
        BackColor = Color.White;

        var sidebar = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Ui.PanelBack, Padding = new Padding(16, 20, 8, 8) };
        sidebar.Controls.Add(Ui.Text("Шаги", Ui.Subtitle, Ui.Muted));
        for (int i = 0; i < StepNames.Length; i++)
        {
            _steps[i] = Ui.Text($"{i + 1}. {StepNames[i]}");
            _steps[i].Margin = new Padding(0, 6, 0, 6);
            sidebar.Controls.Add(_steps[i]);
        }

        _back = Ui.Button("< Назад", (_, _) => GoBack(), 110);
        _next = Ui.Button("Далее >", (_, _) => GoNext(), 110);
        _close = Ui.Button("Закрыть", (_, _) => Close(), 110);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, 8, 8, 8) };
        buttons.Controls.Add(_close);
        buttons.Controls.Add(_next);
        buttons.Controls.Add(_back);

        var logBox = new GroupBox { Text = "Журнал", Dock = DockStyle.Fill, Font = Ui.Small, Padding = new Padding(6) };
        logBox.Controls.Add(_log);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 140));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(sidebar, 0, 0);
        root.SetRowSpan(sidebar, 3);
        root.Controls.Add(_host, 1, 0);
        root.Controls.Add(logBox, 1, 1);
        root.Controls.Add(buttons, 1, 2);
        Controls.Add(root);

        _state = existing;
        if (existing != null)
        {
            _source = existing.SourceLetter;
            _target = existing.TargetVolumeLetter;
            _kind = existing.TargetKind;
            _allowActive = existing.AllowActiveSource;
            CopyInto(existing.Options, _options);
            foreach (var l in existing.Log) AppendLog(l);
        }

        Shown += async (_, _) =>
        {
            if (!_ops.IsAdministrator()) Ui.Error(this, "Нужны права администратора: закройте программу и запустите её через «Запуск от имени администратора».");
            if (_state == null) { ShowPage(Page.Source); await LoadVolumesAsync(); }
            else ShowPage(_state.Stage switch
            {
                RetireStage.Planned => Page.Check,
                RetireStage.TargetReady or RetireStage.Copied => Page.Copy,
                _ => Page.Done,
            });
        };
        FormClosing += OnClosing;
    }

    // ================================================================== navigation

    private void ShowPage(Page page)
    {
        _page = page;
        for (int i = 0; i < _steps.Length; i++)
        {
            _steps[i].Font = i == (int)page ? Ui.Subtitle : Ui.Base;
            _steps[i].ForeColor = i == (int)page ? Ui.Accent : i < (int)page ? Ui.Ok : Ui.Muted;
            _steps[i].Text = (i < (int)page ? "✓ " : $"{i + 1}. ") + StepNames[i];
        }
        _host.SuspendLayout();
        var old = _host.Controls.Cast<Control>().ToArray();
        _host.Controls.Clear();
        foreach (var c in old) c.Dispose();
        _canNext = () => false;
        var content = page switch
        {
            Page.Source => BuildSource(),
            Page.Target => BuildTarget(),
            Page.Settings => BuildSettings(),
            Page.Check => BuildCheck(),
            Page.Copy => BuildCopy(),
            Page.Switch => BuildSwitch(),
            _ => BuildDone(),
        };
        _host.Controls.Add(content);
        _host.ResumeLayout();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool planning = _state == null || _state.Stage == RetireStage.Planned;
        _back.Visible = _page is Page.Target or Page.Settings or Page.Check && planning;
        _next.Visible = _page is Page.Source or Page.Target or Page.Settings or Page.Check or Page.Copy;
        _back.Enabled = !_busy;
        _next.Enabled = !_busy && _canNext();
        _close.Enabled = !_switching;
    }

    private void GoBack()
    {
        if (_page > Page.Source && _page <= Page.Check) ShowPage(_page - 1);
    }

    private void GoNext()
    {
        if (!_canNext()) return;
        switch (_page)
        {
            case Page.Source: ShowPage(Page.Target); break;
            case Page.Target: ShowPage(Page.Settings); break;
            case Page.Settings: _needPlan = true; ShowPage(Page.Check); break;
            case Page.Check: ShowPage(Page.Copy); break;
            case Page.Copy: ShowPage(Page.Switch); break;
        }
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_switching && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Ui.Info(this, "Дождитесь окончания переключения."); return; }
        if (_busy && e.CloseReason == CloseReason.UserClosing)
        {
            // Long work goes on in the background; the start screen shows its progress and reopens this window.
            e.Cancel = true;
            Hide();
            return;
        }
        if (_busy) StopWork();
        DiscardUnstartedPlan();
    }

    /// <summary>Retirement this window works on (null until the plan is made).</summary>
    public string? JobId => _state?.Id;
    /// <summary>Copy, checks or switch-over running right now.</summary>
    public bool IsWorking => _busy || _switching;
    public bool IsSwitching => _switching;
    /// <summary>One line for the start screen: what is happening now.</summary>
    public string LiveStatus { get; private set; } = "";

    /// <summary>Stops copying at the next file boundary; it resumes from there next time.</summary>
    public void StopWork()
    {
        _pause.Set();
        _cts?.Cancel();
    }

    /// <summary>A plan that changed nothing on disk is not worth keeping in the list.</summary>
    private void DiscardUnstartedPlan()
    {
        if (_state?.Stage != RetireStage.Planned) return;
        try { File.Delete(Path.Combine(MainForm.StateDir, _state.Id + ".json")); } catch (Exception) { }
        _state = null;
    }

    // ================================================================== 1. source

    private ListView _sourceList = null!;

    private Control BuildSource()
    {
        var s = Ui.Stack();
        s.Controls.Add(Ui.Text("Какой диск выводим из эксплуатации?", Ui.Title));
        s.Controls.Add(Ui.Text("Выберите диск с архивом Macroscop, который нужно убрать.", null, null, W));
        s.Controls.Add(Hint("Перед переносом откройте Конфигуратор Macroscop → «Архив» и назначьте этому диску роль «Резервный архив». " +
                            "Тогда Macroscop перестанет записывать на него новое видео, но старые записи останутся доступны."));
        _sourceList = List(("Диск", 70), ("Метка", 160), ("Размер", 110), ("Занято", 110), ("Свободно", 110));
        _sourceList.ItemSelectionChanged += (_, e) => { if (e.IsSelected && e.Item!.Tag is char c && c != ' ') { _source = c; UpdateButtons(); } };
        s.Controls.Add(_sourceList);
        FillSourceList();
        _canNext = () => _source != null;
        return s;
    }

    private void FillSourceList()
    {
        if (_page != Page.Source || _sourceList.IsDisposed) return;
        _sourceList.Items.Clear();
        foreach (var v in _volumes.Where(HasArchive))
        {
            var item = new ListViewItem(new[] { $"{v.Letter}:", v.Label, Ui.Bytes(v.TotalBytes), Ui.Bytes(v.TotalBytes - v.FreeBytes), Ui.Bytes(v.FreeBytes) }) { Tag = v.Letter };
            _sourceList.Items.Add(item);
            if (v.Letter == _source) item.Selected = true;
        }
        if (_volumes.Count > 0 && _sourceList.Items.Count == 0)
            _sourceList.Items.Add(new ListViewItem("Дисков с папкой MacroscopArchive не найдено") { ForeColor = Ui.Muted, Tag = ' ' });
    }

    private async Task LoadVolumesAsync()
    {
        try
        {
            UseWaitCursor = true;
            _volumes = (await Task.Run(() => _ops.GetVolumes())).OrderBy(v => v.Letter).ToList();
        }
        catch (Exception ex) { Ui.Error(this, "Не удалось получить список дисков: " + ex.Message); }
        finally { UseWaitCursor = false; }
        FillSourceList();
    }

    private bool HasArchive(VolumeInfo v) => Directory.Exists(Path.Combine(_ops.RootOf(v.Letter), Core.ArchiveScanner.ArchiveFolderName));

    // ================================================================== 2. target

    private Control BuildTarget()
    {
        var s = Ui.Stack();
        s.Controls.Add(Ui.Text($"Куда перенести архив диска {_source}:?", Ui.Title));
        var vhdx = new RadioButton { Text = "В файл виртуального диска (VHDX) на другом томе", AutoSize = true, Font = Ui.Subtitle, Checked = _kind == TargetKind.Vhdx, Margin = new Padding(0, 4, 0, 0) };
        s.Controls.Add(vhdx);
        s.Controls.Add(Ui.Text("Подходит, когда нового диска нет, а на другом томе много свободного места. Файл хорошо виден в корне тома, " +
                               "на него ничего не записывается, а когда записи на нём истекут по сроку хранения, файл удаляется.", null, Ui.Muted, W));
        var volume = new RadioButton { Text = "На пустой том (новый физический или iSCSI-диск)", AutoSize = true, Font = Ui.Subtitle, Checked = _kind == TargetKind.Volume, Margin = new Padding(0, 4, 0, 0) };
        s.Controls.Add(volume);
        s.Controls.Add(Ui.Text("Новый диск получит букву старого и будет работать вместо него.", null, Ui.Muted, W));

        s.Controls.Add(Ui.Text("Выберите том:", Ui.Subtitle));
        var list = List(("Диск", 70), ("Метка", 160), ("Свободно", 120), ("Размер", 120), ("Файловая система", 140));
        var used = _volumes.FirstOrDefault(v => v.Letter == _source) is { } src ? src.TotalBytes - src.FreeBytes : 0;
        foreach (var v in _volumes.Where(v => v.Letter != _source))
        {
            var item = new ListViewItem(new[] { $"{v.Letter}:", v.Label, Ui.Bytes(v.FreeBytes), Ui.Bytes(v.TotalBytes), v.FileSystem }) { Tag = v.Letter };
            if (v.FreeBytes < used) item.ForeColor = Ui.Muted;
            list.Items.Add(item);
            if (v.Letter == _target) item.Selected = true;
        }
        s.Controls.Add(list);
        var info = Ui.Text("", null, Ui.Accent, W);
        s.Controls.Add(info);

        void Describe()
        {
            if (_target is not char t) { info.Text = $"Архив на {_source}: занимает около {Ui.Bytes(used)}."; return; }
            info.Text = _kind == TargetKind.Vhdx
                ? $"Будет создан файл {t}:\\_MacroscopRetired_{_source}_{DateTime.Now:yyyy-MM-dd}.vhdx. Он растёт по мере копирования, " +
                  $"максимум ≈ {Ui.Bytes(RetireOrchestrator.RequiredBytes(used))}. После переключения он станет диском {_source}:."
                : $"Архив будет скопирован на {t}:, затем том {t}: получит букву {_source}:. Том должен быть пустым.";
        }
        vhdx.CheckedChanged += (_, _) => { if (vhdx.Checked) { _kind = TargetKind.Vhdx; Describe(); } };
        volume.CheckedChanged += (_, _) => { if (volume.Checked) { _kind = TargetKind.Volume; Describe(); } };
        list.ItemSelectionChanged += (_, e) => { if (e.IsSelected) { _target = (char)e.Item!.Tag!; Describe(); UpdateButtons(); } };
        Describe();
        _canNext = () => _target != null;
        return s;
    }

    // ================================================================== 3. settings

    private Control BuildSettings()
    {
        var s = Ui.Stack();
        s.Controls.Add(Ui.Text("Настройки переноса", Ui.Title));
        s.Controls.Add(Ui.Text("Значения по умолчанию подходят для большинства случаев.", null, Ui.Muted, W));

        var verify = new CheckBox { Text = "Проверять каждый скопированный файл (рекомендуется)", Checked = _options.Verify, AutoSize = true, Font = Ui.Subtitle, Margin = new Padding(0, 10, 0, 0) };
        verify.CheckedChanged += (_, _) => _options.Verify = verify.Checked;
        s.Controls.Add(verify);
        s.Controls.Add(Ui.Text("Копия перечитывается и сравнивается с оригиналом по контрольной сумме. Медленнее, зато ошибка записи не останется незамеченной.", null, Ui.Muted, W));

        s.Controls.Add(Ui.Text("Ограничение скорости чтения со старого диска", Ui.Subtitle));
        var speed = new NumericUpDown { Minimum = 0, Maximum = 2000, Increment = 10, Width = 100, Value = Math.Min(2000, _options.SpeedLimitBytesPerSec / (1024 * 1024)) };
        speed.ValueChanged += (_, _) => _options.SpeedLimitBytesPerSec = (long)speed.Value * 1024 * 1024;
        s.Controls.Add(Ui.Row(speed, Ui.Text("МБ/с   (0 — без ограничения)")));
        s.Controls.Add(Ui.Text("Снижает нагрузку на неисправный диск и сеть iSCSI, пока Macroscop работает и пишет на другие диски.", null, Ui.Muted, W));

        s.Controls.Add(Ui.Text("Повторов при ошибке чтения", Ui.Subtitle));
        var retries = new NumericUpDown { Minimum = 0, Maximum = 10, Width = 100, Value = _options.Retries };
        retries.ValueChanged += (_, _) => _options.Retries = (int)retries.Value;
        s.Controls.Add(Ui.Row(retries));
        s.Controls.Add(Ui.Text("Если файл так и не прочитался, он пропускается и попадает в список потерь: перенос не останавливается из-за одного плохого участка.", null, Ui.Muted, W));

        var active = new CheckBox { Text = "Разрешить перенос, даже если на диск ещё идёт запись (не рекомендуется)", Checked = _allowActive, AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
        active.CheckedChanged += (_, _) => _allowActive = active.Checked;
        s.Controls.Add(active);
        _canNext = () => true;
        return s;
    }

    // ================================================================== 4. checks

    private Control BuildCheck()
    {
        var s = Ui.Stack();
        s.Controls.Add(Ui.Text("Проверка", Ui.Title));
        var status = Ui.Text("", null, Ui.Muted, W);
        var bar = new ProgressBar { Style = ProgressBarStyle.Marquee, Width = W, Height = 14, MarqueeAnimationSpeed = 30 };
        var results = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0, 8, 0, 8) };
        Button again = null!;
        again = Ui.Button("Проверить снова", async (_, _) => await RunChecks(false));
        s.Controls.Add(status);
        s.Controls.Add(bar);
        s.Controls.Add(results);
        s.Controls.Add(again);

        async Task RunChecks(bool plan)
        {
            _busy = true; UpdateButtons();
            again.Enabled = false; bar.Visible = true; results.Controls.Clear();
            List<Check> checks = new();
            try
            {
                if (plan || _state == null)
                {
                    status.Text = $"Подсчитываю объём архива на {_source}: и срок, когда он опустеет. На большом диске это займёт несколько минут…";
                    DiscardUnstartedPlan();
                    char src = _source!.Value, tgt = _target!.Value; var kind = _kind; bool allow = _allowActive; var opts = CloneOptions();
                    _state = await Task.Run(() => _orc.Plan(src, kind, tgt, opts, allow));
                }
                status.Text = "Проверяю условия…";
                var st = _state!;
                checks = await Task.Run(() => _orc.Precheck(st));
            }
            catch (Exception ex) { checks.Add(new Check(CheckLevel.Error, ex.Message)); }
            finally { _busy = false; bar.Visible = false; again.Enabled = true; }

            foreach (var c in checks)
            {
                var (mark, color) = c.Level switch { CheckLevel.Ok => ("✔", Ui.Ok), CheckLevel.Warning => ("⚠", Ui.Warn), _ => ("✖", Ui.Bad) };
                results.Controls.Add(Ui.Text($"{mark}  {c.Text}", null, color, W));
            }
            bool ok = checks.All(c => c.Level != CheckLevel.Error);
            if (_state != null)
                status.Text = ok
                    ? $"Всё готово. Будет перенесено {Ui.Bytes(_state.SourceArchiveBytes)}. Нажмите «Далее», чтобы перейти к копированию — пока это ничего не меняет на дисках."
                    : "Исправьте отмеченные пункты и нажмите «Проверить снова» (или вернитесь назад и измените выбор).";
            status.ForeColor = ok ? Ui.Ok : Ui.Bad;
            _canNext = () => ok && _state != null;
            UpdateButtons();
        }

        var needPlan = _needPlan;
        _needPlan = false;
        BeginInvoke(async () => await RunChecks(needPlan));
        return s;
    }

    // ================================================================== 5. copy

    private Control BuildCopy()
    {
        var st = _state!;
        var s = Ui.Stack();
        s.Controls.Add(Ui.Text("Копирование архива", Ui.Title));
        s.Controls.Add(Ui.Text($"{st.Title}", null, Ui.Accent, W));
        s.Controls.Add(Hint("Копирование идёт, пока Macroscop работает: запись на другие диски не прерывается. " +
                            "Его можно приостановить или закрыть программу — при следующем запуске оно продолжится с того же места. " +
                            "Сначала копируются самые новые записи."));

        var bar = new ProgressBar { Width = W, Height = 22, Maximum = 1000 };
        var phase = Ui.Text("", Ui.Subtitle);
        var amount = Ui.Text("");
        var speed = Ui.Text("", null, Ui.Muted);
        var file = Ui.Text("", Ui.Small, Ui.Muted, W);
        var losses = Ui.Text("", null, Ui.Warn, W);
        Button start = null!, pause = null!, stop = null!;
        start = Ui.Button(st.Stage == RetireStage.Copied ? "Докопировать изменения" : "Начать копирование", async (_, _) => await RunCopy());
        pause = Ui.Button("Пауза", (_, _) =>
        {
            if (_pause.IsSet) { _pause.Reset(); pause.Text = "Продолжить"; phase.Text = "Пауза"; LiveStatus = "Пауза"; }
            else { _pause.Set(); pause.Text = "Пауза"; }
        });
        stop = Ui.Button("Остановить", (_, _) => { _pause.Set(); _cts?.Cancel(); });
        pause.Enabled = stop.Enabled = false;

        s.Controls.Add(Ui.Row(start, pause, stop));
        s.Controls.Add(phase);
        s.Controls.Add(bar);
        s.Controls.Add(amount);
        s.Controls.Add(speed);
        s.Controls.Add(file);
        s.Controls.Add(losses);

        void ShowDone()
        {
            if (st.Stage == RetireStage.Copied)
            {
                phase.Text = "Копия готова. Нажмите «Далее», чтобы перейти к переключению.";
                phase.ForeColor = Ui.Ok;
                bar.Value = bar.Maximum;
            }
            amount.Text = st.BytesCopied > 0 ? $"Всего скопировано: {Ui.Bytes(st.BytesCopied)}" : "";
            losses.Text = st.Losses.Count == 0 ? "" :
                $"Не удалось прочитать {st.Losses.Count} файлов ({Ui.Bytes(st.Losses.Sum(l => l.Size))}) — это повреждённые участки старого диска, " +
                "эти фрагменты записей будут недоступны. Список — в журнале и в файле состояния.";
        }

        void OnProgress(CopyProgress p)
        {
            LiveStatus = _pause.IsSet
                ? $"Копирование {p.Percent:0.0}%" + (p.Eta is TimeSpan left ? $", осталось ≈ {Ui.Duration(left)}" : "")
                : $"Пауза ({p.Percent:0.0}%)";
            if (!_pause.IsSet) return;
            phase.Text = p.Phase; phase.ForeColor = SystemColors.ControlText;
            bar.Value = (int)Math.Clamp(p.Percent * 10, 0, 1000);
            amount.Text = $"{Ui.Bytes(p.BytesDone)} из {Ui.Bytes(p.BytesTotal)} ({p.Percent:0.0}%) · файлов {p.FilesDone:N0} из {p.FilesTotal:N0}" +
                          (p.FilesSkipped > 0 ? $", уже были актуальны {p.FilesSkipped:N0}" : "");
            speed.Text = p.BytesPerSec > 0 ? $"Скорость {Ui.Bytes((long)p.BytesPerSec)}/с" + (p.Eta is TimeSpan eta ? $" · осталось ≈ {Ui.Duration(eta)}" : "") : "";
            file.Text = p.CurrentFile;
            losses.Text = p.Losses > 0 ? $"Не прочитано файлов: {p.Losses}" : "";
        }

        async Task RunCopy()
        {
            _busy = true; _cts = new CancellationTokenSource(); _pause.Set();
            start.Enabled = false; pause.Enabled = stop.Enabled = true; pause.Text = "Пауза";
            UpdateButtons();
            try
            {
                if (st.Stage == RetireStage.Planned)
                {
                    phase.Text = st.TargetKind == TargetKind.Vhdx ? "Создаю виртуальный диск…" : "Подготовка тома…";
                    await Task.Run(() => _orc.CreateTarget(st));
                }
                var progress = new Progress<CopyProgress>(OnProgress);
                var token = _cts.Token;
                var r = await Task.Run(() => _orc.CopyLive(st, progress, token, _pause));
                if (r.Cancelled) { phase.Text = "Остановлено. Нажмите «Продолжить копирование», когда будете готовы."; phase.ForeColor = Ui.Warn; }
            }
            catch (OperationCanceledException) { phase.Text = "Остановлено."; phase.ForeColor = Ui.Warn; }
            catch (Exception ex) { phase.Text = "Ошибка: " + ex.Message; phase.ForeColor = Ui.Bad; AppendLog("ОШИБКА: " + ex.Message); }
            finally
            {
                _busy = false; _cts.Dispose(); _cts = null; LiveStatus = "";
                pause.Enabled = stop.Enabled = false;
                start.Enabled = true;
                start.Text = st.Stage == RetireStage.Copied ? "Докопировать изменения" : "Продолжить копирование";
                if (st.Stage == RetireStage.Copied || !phase.Text.StartsWith("Ошибка")) ShowDone();
                UpdateButtons();
            }
        }

        ShowDone();
        if (st.Stage == RetireStage.TargetReady) { phase.Text = "Копирование было прервано — его можно продолжить."; phase.ForeColor = Ui.Warn; }
        _canNext = () => st.Stage == RetireStage.Copied;
        return s;
    }

    // ================================================================== 6. switch

    private Control BuildSwitch()
    {
        var st = _state!;
        var s = Ui.Stack();
        s.Controls.Add(Ui.Text("Переключение на новый диск", Ui.Title));
        s.Controls.Add(Ui.Text("Это короткое окно обслуживания, обычно несколько минут. Что произойдёт:", null, null, W));
        s.Controls.Add(Ui.Text(
            "1.  Macroscop нужно остановить. Если он работает как служба Windows, программа остановит её сама; " +
            "если как приложение — программа попросит вас закрыть его.\n" +
            "2.  Остановятся службы PostgreSQL, будут докопированы последние изменения и база событий диска.\n" +
            "3.  Копия будет сверена с исходным диском.\n" +
            $"4.  Новый диск получит букву {st.SourceLetter}:, а старый будет переведён в режим «Вне сети» — Windows не будет его подключать, " +
            "другие LUN той же цели iSCSI продолжат работать. Данные на старом диске не изменяются — переключение можно откатить.\n" +
            (st.TargetKind == TargetKind.Vhdx ? "5.  Будет создана задача, которая подключает виртуальный диск при каждой загрузке Windows. Если Macroscop работает как служба, " +
              "она будет запускаться этой задачей — только после того, как диск подключён.\n" : "") +
            "6.  PostgreSQL снова запустится. Macroscop после этого вы запускаете сами.", null, null, W));

        var status = Ui.Text("", Ui.Subtitle, Ui.Accent, W);
        var bar = new ProgressBar { Width = W, Height = 18, Maximum = 1000, Visible = false };
        var detail = Ui.Text("", Ui.Small, Ui.Muted, W);
        Button go = null!, cancel = null!;
        cancel = Ui.Button("Отменить", (_, _) => _stopWaiting = true);
        cancel.Visible = false;
        go = Ui.Button("Переключить", async (_, _) =>
        {
            if (!Ui.Confirm(this, $"Начать переключение диска {st.SourceLetter}:? Macroscop будет остановлен на несколько минут.")) return;
            _switching = true; _busy = true; _stopWaiting = false;
            go.Enabled = false; UpdateButtons();
            bool Wait(IReadOnlyList<string> running)
            {
                BeginInvoke(() =>
                {
                    status.Text = "Закройте Macroscop: щёлкните правой кнопкой по его значку в области уведомлений и выберите «Выход» " +
                                  "(или остановите сервер Macroscop другим привычным способом). Жду…";
                    status.ForeColor = Ui.Warn;
                    detail.Text = "Сейчас работает: " + string.Join(", ", running);
                    cancel.Visible = true;
                });
                for (int i = 0; i < 4; i++) { if (_stopWaiting) return false; Thread.Sleep(500); }
                return !_stopWaiting;
            }
            var progress = new Progress<CopyProgress>(p =>
            {
                cancel.Visible = false;
                status.Text = "Финальная синхронизация…"; status.ForeColor = Ui.Accent;
                bar.Visible = true; bar.Value = (int)Math.Clamp(p.Percent * 10, 0, 1000);
                detail.Text = $"{p.FilesDone:N0} из {p.FilesTotal:N0} файлов · {p.CurrentFile}";
            });
            try
            {
                status.Text = "Останавливаю Macroscop…";
                await Task.Run(() => _orc.Switch(st, Wait, progress, CancellationToken.None));
                _switching = false; _busy = false;
                ShowPage(Page.Done);
                Ui.Info(this, $"Готово. Диск {st.SourceLetter}: теперь работает с нового места.\n\nЗапустите Macroscop и выполните проверку из следующего окна.");
                return;
            }
            catch (OperationCanceledException) { status.Text = "Переключение отменено. Ничего не изменено, PostgreSQL запущен."; status.ForeColor = Ui.Warn; }
            catch (Exception ex)
            {
                status.Text = "Переключение не выполнено: " + ex.Message;
                status.ForeColor = Ui.Bad;
                Ui.Error(this, ex.Message + "\n\nБуквы дисков возвращены как были, PostgreSQL запущен. Подробности — в журнале.");
            }
            _switching = false; _busy = false;
            cancel.Visible = false; bar.Visible = false; go.Enabled = true;
            UpdateButtons();
        });
        s.Controls.Add(Ui.Row(go, cancel));
        s.Controls.Add(status);
        s.Controls.Add(bar);
        s.Controls.Add(detail);
        return s;
    }

    // ================================================================== 7. done / finish

    private Control BuildDone()
    {
        var st = _state!;
        var s = Ui.Stack();
        if (st.Stage == RetireStage.Finished)
        {
            s.Controls.Add(Ui.Text($"Вывод диска {st.SourceLetter}: завершён", Ui.Title, Ui.Ok));
            s.Controls.Add(Ui.Text("Записи на диске истекли по сроку хранения, диск отключён. Подробности — в журнале.", null, null, W));
            return s;
        }

        s.Controls.Add(Ui.Text($"Архив диска {st.SourceLetter}: работает с нового места", Ui.Title));
        s.Controls.Add(Ui.Text(st.TargetKind == TargetKind.Vhdx ? $"Файл: {st.VhdxPath}" : $"Новый том получил букву {st.SourceLetter}:", null, Ui.Accent, W));
        s.Controls.Add(Ui.Text("Что сделать сейчас:", Ui.Subtitle));
        s.Controls.Add(Ui.Text(
            "1.  Запустите Macroscop.\n" +
            $"2.  В Конфигураторе → «Архив» убедитесь, что у диска {st.SourceLetter}: роль «Резервный архив». Если Macroscop показывает его как новый диск — назначьте эту роль.\n" +
            "3.  Откройте в клиенте архив любой камеры за период, который хранился на этом диске, и проверьте воспроизведение.\n" +
            (st.SourceDiskOffline
                ? $"4.  Старый диск{(string.IsNullOrWhiteSpace(st.SourceDiskLocation) ? "" : $" ({st.SourceDiskLocation})")} уже в режиме «Вне сети». " +
                  "Когда убедитесь, что всё работает, его можно убрать на стороне СХД. Не стирайте его хотя бы неделю — на случай отката."
                : "4.  Переведите старый диск в режим «Вне сети» в «Управлении дисками» (подробности — в журнале). Не стирайте его хотя бы неделю — на случай отката."), null, null, W));

        s.Controls.Add(Ui.Text("Когда диск опустеет", Ui.Subtitle));
        var remaining = Ui.Text("Считаю, сколько записей осталось…", null, Ui.Muted, W);
        s.Controls.Add(remaining);
        s.Controls.Add(Ui.Text("Macroscop сам удаляет записи старше срока хранения. Когда на диске ничего не останется, " +
                               "назначьте ему в Конфигураторе роль «Не используется» и нажмите «Завершить вывод диска».", null, Ui.Muted, W));

        async Task Refresh()
        {
            remaining.Text = "Считаю, сколько записей осталось…";
            try
            {
                var (bytes, newest) = await Task.Run(() => _orc.Remaining(st));
                remaining.Text = bytes == 0
                    ? "Записей на диске не осталось — можно завершать."
                    : $"Осталось записей: {Ui.Bytes(bytes)}, самая новая — {Ui.When(newest)}." +
                      (st.DrainForecastUtc is DateTime f ? $" По срокам хранения камер диск опустеет примерно {Ui.When(f)}." : "");
                remaining.ForeColor = bytes == 0 ? Ui.Ok : SystemColors.ControlText;
            }
            catch (Exception ex) { remaining.Text = "Не удалось посчитать: " + ex.Message; remaining.ForeColor = Ui.Bad; }
        }

        var refresh = Ui.Button("Обновить", async (_, _) => await Refresh());
        var rollback = Ui.Button("Откатить переключение…", async (_, _) =>
        {
            if (!Ui.Confirm(this, $"Вернуть букву {st.SourceLetter}: старому диску?\n\nСначала закройте Macroscop. Старый диск должен быть подключён к серверу (если он «Вне сети», программа вернёт его в сеть). " +
                                  "Записи, сделанные после переключения, на старом диске отсутствуют.")) return;
            try { await Task.Run(() => _orc.Rollback(st)); Ui.Info(this, "Откат выполнен. Запустите Macroscop."); ShowPage(Page.Copy); }
            catch (Exception ex) { Ui.Error(this, ex.Message); }
        });
        var finish = Ui.Button("Завершить вывод диска…", async (_, _) =>
        {
            using var dlg = new FinishDialog(st);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try { await Task.Run(() => _orc.Finish(st, dlg.DeleteVhdx)); ShowPage(Page.Done); }
            catch (Exception ex) { Ui.Error(this, ex.Message); }
        });
        s.Controls.Add(Ui.Row(refresh, finish, rollback));
        BeginInvoke(async () => await Refresh());
        return s;
    }

    // ================================================================== helpers

    private static ListView List(params (string Title, int Width)[] columns)
    {
        var l = new ListView
        {
            View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, Font = Ui.Base,
            Size = new Size(W, 200), HeaderStyle = ColumnHeaderStyle.Nonclickable, Margin = new Padding(0, 4, 0, 10),
        };
        foreach (var (t, w) in columns) l.Columns.Add(t, w);
        return l;
    }

    private static Control Hint(string text)
    {
        var p = new Panel { BackColor = Ui.PanelBack, Padding = new Padding(12, 10, 12, 4), AutoSize = true, Margin = new Padding(0, 4, 0, 12), MaximumSize = new Size(W, 0) };
        p.Controls.Add(Ui.Text("ℹ  " + text, null, null, W - 30));
        return p;
    }

    private void AppendLog(string line) => _log.AppendText(line + Environment.NewLine);

    private CopyOptions CloneOptions()
    {
        var o = new CopyOptions();
        CopyInto(_options, o);
        return o;
    }

    private static void CopyInto(CopyOptions from, CopyOptions to)
    {
        to.Verify = from.Verify;
        to.Retries = from.Retries;
        to.SpeedLimitBytesPerSec = from.SpeedLimitBytesPerSec;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _pause.Dispose(); _cts?.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>Confirmation for the last step: the disk is empty and switched off in Macroscop.</summary>
internal sealed class FinishDialog : Form
{
    private readonly CheckBox _role, _delete;
    public bool DeleteVhdx => _delete.Checked;

    public FinishDialog(RetireState st)
    {
        Text = "Завершение вывода диска";
        Font = Ui.Base;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.White;

        var s = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(20) };
        s.Controls.Add(Ui.Text($"Диск {st.SourceLetter}: будет отключён" + (st.TargetKind == TargetKind.Vhdx ? ", виртуальный диск — отсоединён." : "."), Ui.Subtitle, null, 520));
        _role = new CheckBox { Text = $"В Конфигураторе Macroscop диску {st.SourceLetter}: назначена роль «Не используется», Macroscop закрыт", AutoSize = true, MaximumSize = new Size(520, 0) };
        _delete = new CheckBox { Text = $"Удалить файл {st.VhdxPath}", AutoSize = true, Checked = true, Visible = st.TargetKind == TargetKind.Vhdx, MaximumSize = new Size(520, 0) };
        s.Controls.Add(_role);
        s.Controls.Add(_delete);
        var ok = Ui.Button("Завершить", (_, _) => { DialogResult = DialogResult.OK; Close(); });
        ok.Enabled = false;
        _role.CheckedChanged += (_, _) => ok.Enabled = _role.Checked;
        var cancel = Ui.Button("Отмена", (_, _) => { DialogResult = DialogResult.Cancel; Close(); });
        s.Controls.Add(Ui.Row(ok, cancel));
        Controls.Add(s);
        CancelButton = cancel;
    }
}
