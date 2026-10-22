using System.Text.Json;
using MacroscopArchiveTool.Core;
using MacroscopArchiveTool.Core.Cleanup;
using MacroscopArchiveTool.Core.Migration;
using MacroscopArchiveTool.Core.Remote;

namespace MacroscopArchiveTool.Gui;

/// <summary>
/// Finds what Macroscop leaves behind on every server (old config backups, an unused PostgreSQL version, old logs, …),
/// lets the user pick what to remove and moves it to a quarantine folder on the same disk; quarantined files can be
/// restored or deleted for good.
/// </summary>
internal sealed class CleanupForm : Form
{
    private static string OptionsPath => Path.Combine(WindowsSystemOps.DataDir, "cleanup.json");

    private readonly CleanupOptions _options = LoadOptions();
    private readonly List<ICleanupTarget> _targets = new();
    private readonly Dictionary<ICleanupTarget, CleanupScan> _scans = new();

    private readonly TreeView _tree = new() { CheckBoxes = true, Dock = DockStyle.Fill, Font = Ui.Base, HideSelection = false, ShowNodeToolTips = true };
    private readonly ListView _quarantine = new()
    {
        View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, Font = Ui.Base,
        Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = Ui.Small, BackColor = Color.White };
    private readonly Label _status = Ui.Text("", Ui.Subtitle);
    private readonly ProgressBar _bar = new() { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Width = 200, Height = 16, Visible = false, Margin = new Padding(8, 8, 0, 0) };
    private readonly Button _scan, _apply, _restore, _purge;
    private readonly NumericUpDown _backupDays, _keep, _logDays, _quarantineDays;
    private bool _busy, _updatingChecks;
    private CancellationTokenSource? _cts;

    public CleanupForm()
    {
        Text = "Очистка мусора Macroscop";
        Font = Ui.Base;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1100, 820);
        MinimumSize = new Size(860, 640);
        BackColor = Color.White;

        _targets.Add(new LocalCleanupTarget());
        foreach (var s in ServerStore.Load(ServersForm.StorePath).Servers.Where(s => s.Enabled)) _targets.Add(new RemoteCleanupTarget(s));

        NumericUpDown Num(int value, int max) => new() { Minimum = 0, Maximum = max, Value = Math.Min(max, value), Width = 70, Margin = new Padding(4, 2, 4, 0) };
        _backupDays = Num(_options.BackupDays, 3650);
        _keep = Num(_options.BackupsToKeep, 1000); _keep.Minimum = 1;
        _logDays = Num(_options.LogDays, 3650);
        _quarantineDays = Num(_options.QuarantineDays, 365);

        _scan = Ui.Button("Проверить", async (_, _) => await ScanAsync());
        _apply = Ui.Button("Перенести выбранное в карантин", async (_, _) => await ApplyAsync());
        _restore = Ui.Button("Вернуть на место", async (_, _) => await QuarantineAsync("restore"));
        _purge = Ui.Button("Удалить окончательно", async (_, _) => await QuarantineAsync("purge"));
        _apply.Enabled = _restore.Enabled = _purge.Enabled = false;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(20, 16, 20, 12) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 22));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 16));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var head = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Fill };
        head.Controls.Add(Ui.Text("Очистка мусора Macroscop", Ui.Title));
        head.Controls.Add(Ui.Text(
            $"Проверяются этот сервер{(_targets.Count > 1 ? $" и ещё {_targets.Count - 1} из списка «Серверы»" : "")}. Видеоархив, действующая база и текущие настройки не трогаются. " +
            "Выбранное переносится в папку карантина на том же диске (мгновенно, без копирования) и удаляется окончательно позже — до этого всё можно вернуть.",
            null, Ui.Muted, 1020));
        root.Controls.Add(head, 0, 0);

        var opts = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 6) };
        opts.Controls.AddRange(new Control[]
        {
            Inline("Копии конфигурации старше"), _backupDays, Inline("дн., оставлять не меньше"), _keep, Inline("на каждом диске.   Журналы старше"), _logDays,
            Inline("дн.   Карантин удалять через"), _quarantineDays, Inline("дн.  "), _scan, _bar,
        });
        root.Controls.Add(opts, 0, 1);

        _tree.AfterCheck += OnAfterCheck;
        _tree.BeforeCheck += (_, e) => { if (e.Node?.Tag is CleanupItem { Action: CleanupAction.ShowOnly }) e.Cancel = true; };
        root.Controls.Add(_tree, 0, 2);
        root.Controls.Add(Ui.Row(_status, _apply), 0, 3);

        var qBox = new GroupBox { Text = "Карантин", Dock = DockStyle.Fill, Font = Ui.Base, Padding = new Padding(8) };
        _quarantine.Columns.Add("Сервер", 220);
        _quarantine.Columns.Add("Когда", 150);
        _quarantine.Columns.Add("Диски", 220);
        _quarantine.Columns.Add("Объём", 110);
        _quarantine.Columns.Add("Удалится окончательно", 200);
        _quarantine.SelectedIndexChanged += (_, _) => UpdateButtons();
        var qLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        qLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        qLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        qLayout.Controls.Add(_quarantine, 0, 0);
        qLayout.Controls.Add(Ui.Row(_restore, _purge), 0, 1);
        qBox.Controls.Add(qLayout);
        root.Controls.Add(qBox, 0, 4);
        root.Controls.Add(_log, 0, 5);
        Controls.Add(root);

        Shown += async (_, _) => await ScanAsync();
        FormClosing += (_, e) =>
        {
            if (!_busy) { SaveOptions(); return; }
            e.Cancel = true;
            Ui.Info(this, "Дождитесь окончания операции.");
        };
    }

    private static Label Inline(string text) { var l = Ui.Text(text); l.Margin = new Padding(0, 4, 0, 0); return l; }

    // ------------------------------------------------------------------ scan

    private void ReadOptions()
    {
        _options.BackupDays = (int)_backupDays.Value;
        _options.BackupsToKeep = (int)_keep.Value;
        _options.LogDays = (int)_logDays.Value;
        _options.QuarantineDays = (int)_quarantineDays.Value;
    }

    private async Task ScanAsync()
    {
        ReadOptions();
        SaveOptions();
        await RunOnAll("Проверка…", "scan", _ => new CleanupPlan { Options = _options });
    }

    private async Task RunOnAll(string title, string command, Func<ICleanupTarget, CleanupPlan?> planFor)
    {
        SetBusy(true, title);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var jobs = _targets.Select(t => (Target: t, Plan: planFor(t))).Where(x => x.Plan != null).Select(x =>
        {
            var progress = new Progress<string>(m => Append($"[{x.Target.Name}] {m}"));
            return Task.Run(() =>
            {
                try { return (x.Target, Scan: (CleanupScan?)x.Target.Run(command, x.Plan!, progress, token), Error: (string?)null); }
                catch (Exception ex) { return (x.Target, Scan: (CleanupScan?)null, Error: (string?)ex.Message); }
            });
        }).ToList();
        foreach (var r in await Task.WhenAll(jobs))
        {
            if (r.Scan != null) _scans[r.Target] = r.Scan;
            else { _scans.Remove(r.Target); Append($"[{r.Target.Name}] ОШИБКА: {r.Error}"); }
        }
        Fill();
        SetBusy(false, null);
    }

    private void Fill()
    {
        _updatingChecks = true;
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        foreach (var t in _targets)
        {
            if (!_scans.TryGetValue(t, out var scan)) { _tree.Nodes.Add(new TreeNode($"{t.Name} — нет данных (см. журнал)") { ForeColor = Ui.Bad }); continue; }
            var cleanable = scan.Items.Where(i => i.Action != CleanupAction.ShowOnly).ToList();
            var server = new TreeNode($"{t.Name} — можно освободить {Ui.Bytes(cleanable.Sum(i => i.Bytes))}") { Tag = t, NodeFont = Ui.Subtitle };
            foreach (var group in scan.Items.GroupBy(i => i.Category).OrderBy(g => g.Key))
            {
                bool showOnly = group.All(i => i.Action == CleanupAction.ShowOnly);
                var cat = new TreeNode($"{CleanupText.Category(group.Key)} — {Ui.Bytes(group.Sum(i => i.Bytes))}") { ForeColor = showOnly ? Ui.Muted : SystemColors.ControlText };
                foreach (var i in group)
                {
                    var when = i.OldestUtc != null && i.NewestUtc != null ? $", {Ui.When(i.OldestUtc)}–{Ui.When(i.NewestUtc)}" : "";
                    var node = new TreeNode($"{i.Title} — {Ui.Bytes(i.Bytes)}{(i.Files > 0 ? $", {i.Files:N0} файлов" : "")}{when}")
                    {
                        Tag = i, Checked = i.Recommended && i.Action != CleanupAction.ShowOnly, ToolTipText = i.Note + "\n\n" + string.Join("\n", i.Paths.Take(15)) + (i.Paths.Count > 15 ? $"\n… ещё {i.Paths.Count - 15}" : ""),
                        ForeColor = i.Action == CleanupAction.ShowOnly ? Ui.Muted : i.Action == CleanupAction.UninstallPostgres ? Ui.Warn : SystemColors.ControlText,
                    };
                    cat.Nodes.Add(node);
                }
                cat.Checked = cat.Nodes.Count > 0 && cat.Nodes.Cast<TreeNode>().All(n => n.Checked);
                server.Nodes.Add(cat);
            }
            foreach (var w in scan.Warnings) server.Nodes.Add(new TreeNode("⚠ " + w) { ForeColor = Ui.Warn });
            if (scan.Items.Count == 0) server.Nodes.Add(new TreeNode("Мусора не найдено") { ForeColor = Ui.Ok });
            server.Checked = server.Nodes.Cast<TreeNode>().Where(n => n.Nodes.Count > 0).All(n => n.Checked);
            _tree.Nodes.Add(server);
            server.ExpandAll();
        }
        _tree.EndUpdate();
        _updatingChecks = false;

        _quarantine.Items.Clear();
        foreach (var t in _targets)
        {
            if (!_scans.TryGetValue(t, out var scan)) continue;
            foreach (var g in scan.Quarantine.GroupBy(q => q.SetId))
            {
                var created = g.Min(q => q.CreatedUtc);
                var purgeAt = _options.QuarantineDays > 0 ? Ui.When(created.AddDays(_options.QuarantineDays)) + " (при проверке после этой даты)" : "только вручную";
                _quarantine.Items.Add(new ListViewItem(new[]
                {
                    t.Name, created.ToLocalTime().ToString("dd.MM.yyyy HH:mm"),
                    string.Join(" ", g.Select(q => Path.GetPathRoot(q.Folder)?.TrimEnd('\\'))), Ui.Bytes(g.Sum(q => q.Bytes)), purgeAt,
                }) { Tag = (t, g.Key) });
            }
        }
        UpdateSelection();
    }

    private void OnAfterCheck(object? sender, TreeViewEventArgs e)
    {
        if (_updatingChecks || e.Node == null) return;
        _updatingChecks = true;
        void Down(TreeNode n) { foreach (TreeNode c in n.Nodes) { if (c.Tag is CleanupItem { Action: CleanupAction.ShowOnly }) continue; c.Checked = n.Checked; Down(c); } }
        Down(e.Node);
        for (var p = e.Node.Parent; p != null; p = p.Parent)
            p.Checked = p.Nodes.Cast<TreeNode>().Where(c => c.Tag is not CleanupItem { Action: CleanupAction.ShowOnly } && (c.Nodes.Count > 0 || c.Tag is CleanupItem)).All(c => c.Checked);
        _updatingChecks = false;
        UpdateSelection();
    }

    private IEnumerable<(ICleanupTarget Target, CleanupItem Item)> Selected()
    {
        foreach (TreeNode server in _tree.Nodes)
            foreach (TreeNode cat in server.Nodes)
                foreach (TreeNode n in cat.Nodes)
                    if (n.Checked && n.Tag is CleanupItem { Action: not CleanupAction.ShowOnly } i && server.Tag is ICleanupTarget t) yield return (t, i);
    }

    private void UpdateSelection()
    {
        var sel = Selected().ToList();
        _status.Text = sel.Count == 0 ? "Ничего не выбрано" : $"Выбрано: {sel.Count} пунктов, {Ui.Bytes(sel.Sum(x => x.Item.Bytes))}";
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _scan.Enabled = !_busy;
        _apply.Enabled = !_busy && Selected().Any();
        _restore.Enabled = _purge.Enabled = !_busy && _quarantine.SelectedItems.Count > 0;
    }

    // ------------------------------------------------------------------ actions

    private async Task ApplyAsync()
    {
        var sel = Selected().ToList();
        if (sel.Count == 0) return;
        var uninstall = sel.Where(x => x.Item.Action == CleanupAction.UninstallPostgres).ToList();
        var text = $"Перенести в карантин {sel.Count} пунктов ({Ui.Bytes(sel.Sum(x => x.Item.Bytes))})?\n\n" +
                   string.Join("\n", sel.GroupBy(x => x.Target.Name).Select(g => $"• {g.Key}: {Ui.Bytes(g.Sum(x => x.Item.Bytes))}")) +
                   (uninstall.Count > 0 ? "\n\nВнимание: " + string.Join("; ", uninstall.Select(x => x.Item.Title)) +
                                          " — будет запущен деинсталлятор PostgreSQL. Удаление программы из карантина не отменить (данные вернуть можно)." : "") +
                   $"\n\nОкончательно файлы удалятся через {_options.QuarantineDays} дн. (при очередной проверке) или кнопкой «Удалить окончательно».";
        if (!Ui.Confirm(this, text)) return;
        ReadOptions();
        var byTarget = sel.GroupBy(x => x.Target).ToDictionary(g => g.Key, g => g.Select(x => x.Item.Id).ToList());
        await RunOnAll("Перенос в карантин…", "apply", t => byTarget.TryGetValue(t, out var ids) ? new CleanupPlan { Options = _options, ItemIds = ids } : null);
        foreach (var t in byTarget.Keys) if (_scans.TryGetValue(t, out var s)) foreach (var w in s.Warnings) Append($"[{t.Name}] ! {w}");
    }

    private async Task QuarantineAsync(string command)
    {
        if (_quarantine.SelectedItems.Count == 0) return;
        var (target, setId) = ((ICleanupTarget, string))_quarantine.SelectedItems[0].Tag!;
        var size = _quarantine.SelectedItems[0].SubItems[3].Text;
        var question = command == "restore"
            ? $"Вернуть на место всё, что перенесено в карантин {setId} на сервере «{target.Name}» ({size})?"
            : $"Удалить окончательно карантин {setId} на сервере «{target.Name}» ({size})? Вернуть эти файлы будет нельзя.";
        if (!Ui.Confirm(this, question)) return;
        await RunOnAll(command == "restore" ? "Восстановление…" : "Удаление…", command,
            t => t == target ? new CleanupPlan { Options = _options, SetId = setId } : null);
    }

    // ------------------------------------------------------------------ helpers

    private void SetBusy(bool busy, string? title)
    {
        _busy = busy;
        _bar.Visible = busy;
        if (title != null) _status.Text = title;
        UseWaitCursor = busy;
        UpdateButtons();
        if (!busy) UpdateSelection();
    }

    private void Append(string line) => _log.AppendText(line + Environment.NewLine);

    private static CleanupOptions LoadOptions()
    {
        try { return File.Exists(OptionsPath) ? JsonSerializer.Deserialize<CleanupOptions>(File.ReadAllText(OptionsPath), CleanupCommands.Json) ?? new() : new(); }
        catch (Exception) { return new(); }
    }

    private void SaveOptions()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(OptionsPath)!); File.WriteAllText(OptionsPath, JsonSerializer.Serialize(_options, CleanupCommands.Json)); }
        catch (Exception) { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cts?.Dispose();
        base.Dispose(disposing);
    }
}
