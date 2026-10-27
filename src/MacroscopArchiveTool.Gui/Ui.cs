using System.Diagnostics;
using MacroscopArchiveTool.Core;

namespace MacroscopArchiveTool.Gui;

/// <summary>Small layout helpers so every window looks the same without a designer.</summary>
internal static class Ui
{
    public static readonly Font Base = new("Segoe UI", 10f);
    public static readonly Font Title = new("Segoe UI Semibold", 15f);
    public static readonly Font Subtitle = new("Segoe UI Semibold", 11f);
    public static readonly Font Small = new("Segoe UI", 9f);
    public static readonly Color Accent = Color.FromArgb(0, 95, 184);

    /// <summary>Program icon for every window (title bar, taskbar, Alt+Tab); the exe itself gets it via ApplicationIcon.</summary>
    public static readonly Icon? AppIcon = LoadIcon();

    private static Icon? LoadIcon()
    {
        try
        {
            using var s = typeof(Ui).Assembly.GetManifestResourceStream("app.ico");
            return s != null ? new Icon(s) : null;
        }
        catch (Exception) { return null; }
    }
    public static readonly Color Muted = Color.FromArgb(96, 96, 96);
    public static readonly Color Ok = Color.FromArgb(16, 124, 16);
    public static readonly Color Warn = Color.FromArgb(157, 93, 0);
    public static readonly Color Bad = Color.FromArgb(196, 43, 28);
    public static readonly Color PanelBack = Color.FromArgb(243, 246, 250);

    public static Label Text(string text, Font? font = null, Color? color = null, int width = 0)
    {
        var l = new Label { Text = text, Font = font ?? Base, AutoSize = true, ForeColor = color ?? SystemColors.ControlText, Margin = new Padding(0, 0, 0, 8) };
        if (width > 0) l.MaximumSize = new Size(width, 0);
        return l;
    }

    public static Button Button(string text, EventHandler onClick, int width = 0)
    {
        var b = new Button { Text = text, Font = Base, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 4, 10, 4), Margin = new Padding(0, 0, 8, 0) };
        if (width > 0) { b.AutoSize = false; b.Size = new Size(width, 36); }
        b.Click += onClick;
        return b;
    }

    /// <summary>Vertical stack that wraps long labels to its width.</summary>
    public static FlowLayoutPanel Stack()
        => new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(0) };

    public static FlowLayoutPanel Row(params Control[] controls)
    {
        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 8) };
        p.Controls.AddRange(controls);
        return p;
    }

    public static string Bytes(long b) => HtmlReport.Bytes(b);

    public static void Info(IWin32Window? owner, string text) => MessageBox.Show(owner, text, "MacroscopArchiveTool", MessageBoxButtons.OK, MessageBoxIcon.Information);
    public static void Error(IWin32Window? owner, string text) => MessageBox.Show(owner, text, "MacroscopArchiveTool", MessageBoxButtons.OK, MessageBoxIcon.Error);
    public static bool Confirm(IWin32Window? owner, string text)
        => MessageBox.Show(owner, text, "MacroscopArchiveTool", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    public static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Error(null, $"Не удалось открыть {path}: {ex.Message}"); }
    }

    public static ToolConfig LoadConfig()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "mat.json");
        return File.Exists(path) ? ToolConfig.Load(path) : new ToolConfig();
    }

    public static string When(DateTime? utc) => utc is DateTime d ? d.ToLocalTime().ToString("dd.MM.yyyy") : "—";

    public static string Duration(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours} ч {t.Minutes} мин" : t.TotalMinutes >= 1 ? $"{t.Minutes} мин {t.Seconds} с" : $"{t.Seconds} с";
}
