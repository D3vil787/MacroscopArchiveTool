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

    public static string StateDir => Path.Combine(WindowsSystemOps.DataDir, "retire");

    public MainForm()
    {
        Text = $"MacroscopArchiveTool {ToolInfo.Version}";
        Font = Ui.Base;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 560);
        Size = new Size(880, 640);
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

        var cards = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 0, 0, 16) };
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        cards.Controls.Add(Card("Отчёт по архиву",
            "Объём и глубина архива по дискам и камерам, сроки хранения из настроек Macroscop, «тяжёлые» камеры. Только чтение.",
            "Сделать отчёт", (_, _) => RunReport()), 0, 0);
        cards.Controls.Add(Card("Вывести диск из эксплуатации",
            "Перенести архив с диска, который нужно убрать (например, с ошибками SMART), на другой том или в файл виртуального диска. " +
            "Записи остаются доступны, пока не истечёт срок хранения.",
            "Начать…", (_, _) => OpenWizard(null)), 1, 0);
        root.Controls.Add(cards, 0, 1);

        var jobsBox = new GroupBox { Text = "Начатые выводы дисков", Dock = DockStyle.Fill, Font = Ui.Base, Padding = new Padding(10) };
        _jobs.Columns.Add("Диск", 70);
        _jobs.Columns.Add("Куда", 330);
        _jobs.Columns.Add("Этап", 220);
        _jobs.Columns.Add("Обновлено", 140);
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
        root.Controls.Add(link, 0, 3);

        Load += (_, _) => RefreshJobs();
        Activated += (_, _) => RefreshJobs();
    }

    private static Control Card(string title, string text, string button, EventHandler onClick)
    {
        var p = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill, AutoSize = true,
            BackColor = Ui.PanelBack, Padding = new Padding(16), Margin = new Padding(0, 0, 12, 0),
        };
        p.Controls.Add(Ui.Text(title, Ui.Subtitle, Ui.Accent));
        p.Controls.Add(Ui.Text(text, null, null, 360));
        var b = Ui.Button(button, onClick);
        b.Margin = new Padding(0, 6, 0, 0);
        p.Controls.Add(b);
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
            var item = new ListViewItem(new[] { $"{s.SourceLetter}:", target, StageText(s.Stage), s.UpdatedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm") }) { Tag = s };
            if (s.Stage == RetireStage.Finished) item.ForeColor = Ui.Muted;
            _jobs.Items.Add(item);
            if (s.Id == selected) item.Selected = true;
        }
        _jobs.EndUpdate();
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

    private void OpenWizard(RetireState? state)
    {
        using var w = new RetireWizard(state);
        w.ShowDialog(this);
        RefreshJobs();
    }

    private void RunReport()
    {
        using var f = new ReportForm();
        f.ShowDialog(this);
    }
}
