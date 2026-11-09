using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DualSenseStudio;

internal sealed class AxisEditor : StudioCard
{
    private readonly NumericUpDown inner = MainForm.Percentage(0);
    private readonly NumericUpDown end = MainForm.Percentage(100);
    private readonly NumericUpDown anti = MainForm.Percentage(0);
    private readonly ComboBox curve = MainForm.ListBox(["Linear", "Suave", "Agressiva", "Larga", "Extra larga"]);
    private readonly ComboBox shape = MainForm.ListBox(["Radial", "Axial", "Quadrada"]);
    private bool setting;

    public event EventHandler? SettingsChanged;
    public AxisSettings Settings => new((int)inner.Value, (int)end.Value, (int)anti.Value, (ResponseCurve)curve.SelectedIndex);
    public DeadzoneShape Shape => (DeadzoneShape)shape.SelectedIndex;

    public AxisEditor(string name, bool hasShape)
    {
        Width = 337;
        Height = hasShape ? 255 : 218;
        BackColor = MainForm.Canvas;
        Margin = new(3, 0, 12, 14);
        Padding = new(16, 11, 15, 11);
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = hasShape ? 6 : 5, Margin = Padding.Empty, BackColor = Color.Transparent };
        table.ColumnStyles.Add(new(SizeType.Percent, 56));
        table.ColumnStyles.Add(new(SizeType.Percent, 44));
        for (int row = 0; row < table.RowCount; row++) table.RowStyles.Add(new(SizeType.Percent, 100f / table.RowCount));
        var title = MainForm.LabelText(name, MainForm.Accent);
        title.Font = new("Segoe UI Semibold", 14f);
        table.Controls.Add(title, 0, 0);
        table.SetColumnSpan(title, 2);
        AddRow(table, 1, "Zona morta interna %", inner);
        AddRow(table, 2, "Saída máxima em %", end);
        AddRow(table, 3, "Anti-zona morta %", anti);
        AddRow(table, 4, "Curva", curve);
        if (hasShape) AddRow(table, 5, "Formato", shape);
        Controls.Add(table);
        inner.Maximum = 99;
        end.Minimum = 1;
        anti.Maximum = 99;
        curve.Width = 115;
        shape.Width = 115;
        inner.ValueChanged += (_, _) =>
        {
            if (setting) return;
            if (inner.Value >= end.Value) end.Value = inner.Value + 1;
            Changed();
        };
        end.ValueChanged += (_, _) =>
        {
            if (setting) return;
            if (end.Value <= inner.Value) inner.Value = end.Value - 1;
            Changed();
        };
        anti.ValueChanged += (_, _) => Changed();
        curve.SelectedIndexChanged += (_, _) => Changed();
        shape.SelectedIndexChanged += (_, _) => Changed();
    }

    public void Set(AxisSettings settings, DeadzoneShape format = DeadzoneShape.Axial)
    {
        setting = true;
        try
        {
            inner.Value = Math.Clamp(settings.Inner, 0, 99);
            end.Value = Math.Clamp(settings.End, (int)inner.Value + 1, 100);
            anti.Value = Math.Clamp(settings.Anti, 0, 99);
            curve.SelectedIndex = Math.Clamp((int)settings.Curve, 0, curve.Items.Count - 1);
            shape.SelectedIndex = Math.Clamp((int)format, 0, shape.Items.Count - 1);
        }
        finally { setting = false; }
    }

    private void Changed()
    {
        if (!setting) SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void AddRow(TableLayoutPanel layout, int row, string text, Control editor)
    {
        var label = MainForm.LabelText(text, MainForm.TextColor);
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(label, 0, row);
        layout.Controls.Add(editor, 1, row);
    }
}

internal sealed class TriggerView : StudioCard
{
    private int raw;
    private int output;

    public TriggerView()
    {
        Size = new(689, 144);
        Margin = new(3, 0, 0, 10);
        BackColor = MainForm.Canvas;
        DoubleBuffered = true;
        AccessibleName = "Leitura e saída simulada dos gatilhos";
    }

    public void UpdateValues(int input, int mapped)
    {
        if (input == raw && mapped == output) return;
        raw = input;
        output = mapped;
        if (Visible) Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        float dpi = DeviceDpi / 96f;
        e.Graphics.ScaleTransform(dpi, dpi);
        float halfWidth = Width / dpi / 2;
        using var divider = new Pen(StudioDrawing.Border);
        e.Graphics.DrawLine(divider, halfWidth, 18, halfWidth, Height / dpi - 18);
        Draw(e.Graphics, 16, halfWidth - 32, "L2 ao vivo", raw & 255, output & 255);
        Draw(e.Graphics, halfWidth + 16, halfWidth - 32, "R2 ao vivo", (raw >> 8) & 255, (output >> 8) & 255);
    }

    private void Draw(Graphics g, float x, float width, string name, int input, int mapped)
    {
        using var title = new SolidBrush(MainForm.TextColor);
        using var muted = new SolidBrush(MainForm.Muted);
        using var heading = new Font("Segoe UI Semibold", 15, GraphicsUnit.Pixel);
        using var detail = new Font("Segoe UI", 13, GraphicsUnit.Pixel);
        using var percentage = new Font("Segoe UI Semibold", 13, GraphicsUnit.Pixel);
        using var alignment = new StringFormat { Alignment = StringAlignment.Far };
        g.DrawString(name, heading, title, x, 13);
        DrawValue("Físico", input, 45, Color.FromArgb(132, 143, 183));
        DrawValue("Ajustado", mapped, 88, MainForm.Accent);

        void DrawValue(string label, int value, int y, Color color)
        {
            g.DrawString(label, detail, muted, x, y);
            g.DrawString($"{value / 255d:P0}", percentage, title, new PointF(x + width, y), alignment);
            StudioDrawing.Card(g, new RectangleF(x, y + 20, width, 8), 4, StudioDrawing.Lavender, Color.Transparent);
            StudioDrawing.Card(g, new RectangleF(x, y + 20, width * value / 255f, 8), 4, color, Color.Transparent);
        }
    }
}

internal sealed class StickView : StudioCard
{
    private long raw;
    private long output;

    public StickView()
    {
        Size = new(689, 169);
        Margin = new(3, 0, 0, 0);
        BackColor = MainForm.Canvas;
        DoubleBuffered = true;
        AccessibleName = "Posição física e ajustada dos analógicos";
    }

    public void UpdateValues(long input, long mapped)
    {
        if (input == raw && mapped == output) return;
        raw = input;
        output = mapped;
        if (Visible) Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        Draw(e.Graphics, 86, 0, "Esquerdo");
        Draw(e.Graphics, 257, 2, "Direito");
        using var actual = new SolidBrush(Color.FromArgb(133, 151, 195));
        using var adjusted = new SolidBrush(MainForm.Accent);
        e.Graphics.DrawString("●  Posição física", Font, actual, 352, 57);
        e.Graphics.DrawString("●  Posição ajustada", Font, adjusted, 352, 85);
    }

    private void Draw(Graphics g, float centerX, int axis, string name)
    {
        const float centerY = 69;
        const float radius = 50;
        using var line = new Pen(Color.FromArgb(83, 100, 130));
        using var actual = new SolidBrush(Color.FromArgb(133, 151, 195));
        using var adjusted = new SolidBrush(MainForm.Accent);
        using var text = new SolidBrush(MainForm.TextColor);
        g.DrawEllipse(line, centerX - radius, centerY - radius, radius * 2, radius * 2);
        g.DrawLine(line, centerX - radius, centerY, centerX + radius, centerY);
        g.DrawLine(line, centerX, centerY - radius, centerX, centerY + radius);
        Dot(g, actual, raw, axis, centerX, centerY, radius, 6);
        Dot(g, adjusted, output, axis, centerX, centerY, radius, 4);
        g.DrawString(name, Font, text, centerX - 31, 130);
    }

    private static void Dot(Graphics g, Brush brush, long axes, int index, float x, float y, float radius, float dot)
    {
        x += (short)(axes >> (16 * index)) / 32768f * radius;
        y -= (short)(axes >> (16 * (index + 1))) / 32768f * radius;
        g.FillEllipse(brush, x - dot, y - dot, dot * 2, dot * 2);
    }
}

internal static class GameProcesses
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

    public static string? ForegroundExe()
    {
        if (GetWindowThreadProcessId(GetForegroundWindow(), out uint pid) == 0) return null;
        try
        {
            using Process process = Process.GetProcessById((int)pid);
            return process.ProcessName + ".exe";
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    public static string Summary()
    {
        string[] launchers = ["Steam", "EpicGamesLauncher", "RocketLeague"];
        var open = new List<string>();
        foreach (string name in launchers)
        {
            Process[] matches = Process.GetProcessesByName(name);
            if (matches.Length > 0) open.Add(name switch { "EpicGamesLauncher" => "Epic Games", "RocketLeague" => "Rocket League", _ => name });
            foreach (Process match in matches) match.Dispose();
        }
        return open.Count == 0 ? "Launchers e Rocket League não detectados." : $"Processos abertos: {string.Join(" · ", open)}";
    }

    public static string[] LauncherPaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in new[] { "Steam", "EpicGamesLauncher", "RocketLeague" })
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        if (process.MainModule?.FileName is { } path) paths.Add(path);
                    }
                    catch (System.ComponentModel.Win32Exception) { }
                    catch (InvalidOperationException) { }
                }
            }
        }
        return [.. paths];
    }
}
