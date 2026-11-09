using System.Drawing.Drawing2D;

namespace DualSenseStudio;

internal static class StudioDrawing
{
    public static readonly Color Lavender = Color.FromArgb(236, 231, 255);
    public static readonly Color Sidebar = Color.FromArgb(239, 240, 248);
    public static readonly Color Border = Color.FromArgb(224, 225, 236);

    public static GraphicsPath Rounded(RectangleF area, float radius)
    {
        var path = new GraphicsPath();
        float diameter = Math.Min(radius * 2, Math.Min(area.Width, area.Height));
        path.AddArc(area.Left, area.Top, diameter, diameter, 180, 90);
        path.AddArc(area.Right - diameter, area.Top, diameter, diameter, 270, 90);
        path.AddArc(area.Right - diameter, area.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(area.Left, area.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void Card(Graphics graphics, RectangleF area, float radius, Color fill, Color border)
    {
        if (area.Width <= 1 || area.Height <= 1) return;
        using var path = Rounded(area, radius);
        using var brush = new SolidBrush(fill);
        graphics.FillPath(brush, path);
        if (border != Color.Transparent)
        {
            using var pen = new Pen(border);
            graphics.DrawPath(pen, path);
        }
    }

    public static void Controller(Graphics graphics, Color ink, float x, float y, float scale)
    {
        GraphicsState state = graphics.Save();
        graphics.TranslateTransform(x, y);
        graphics.ScaleTransform(scale, scale);
        using var outline = new Pen(ink, 2.2f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var shape = new GraphicsPath();
        shape.AddBezier(31, 9, 23, 7, 14, 10, 12, 18);
        shape.AddBezier(12, 18, 8, 28, 2, 46, 10, 51);
        shape.AddBezier(10, 51, 16, 55, 23, 43, 29, 38);
        shape.AddLine(29, 38, 67, 38);
        shape.AddBezier(67, 38, 73, 43, 80, 55, 86, 51);
        shape.AddBezier(86, 51, 94, 46, 88, 28, 84, 18);
        shape.AddBezier(84, 18, 82, 10, 73, 7, 65, 9);
        shape.CloseFigure();
        graphics.DrawPath(outline, shape);
        graphics.DrawLine(outline, 26, 17, 26, 29);
        graphics.DrawLine(outline, 20, 23, 32, 23);
        graphics.DrawEllipse(outline, 34, 26, 10, 10);
        graphics.DrawEllipse(outline, 52, 26, 10, 10);
        graphics.DrawLine(outline, 41, 14, 55, 14);
        using var dot = new SolidBrush(ink);
        foreach (Point point in new[] { new Point(70, 15), new Point(64, 21), new Point(76, 21), new Point(70, 27) })
            graphics.FillEllipse(dot, point.X, point.Y, 4, 4);
        graphics.Restore(state);
    }

    public static void Navigation(Graphics graphics, int item, Color ink, float dpi, float centerY)
    {
        GraphicsState state = graphics.Save();
        graphics.TranslateTransform(11 * dpi, centerY - 8 * dpi);
        graphics.ScaleTransform(dpi, dpi);
        using var pen = new Pen(ink, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        switch (item)
        {
            case 0:
                graphics.DrawArc(pen, 2, 1, 12, 12, 180, 180);
                graphics.DrawLines(pen, [new(2, 7), new(2, 12), new(10, 12), new(14, 7)]);
                break;
            case 1:
                graphics.DrawEllipse(pen, 1, 1, 14, 14);
                graphics.DrawEllipse(pen, 5, 5, 6, 6);
                break;
            case 2:
                foreach (Point point in new[] { new Point(6, 0), new Point(0, 6), new Point(12, 6), new Point(6, 12) })
                    graphics.DrawEllipse(pen, point.X, point.Y, 4, 4);
                break;
            case 3:
                graphics.DrawEllipse(pen, 4, 4, 8, 8);
                graphics.DrawLine(pen, 8, 0, 8, 2);
                graphics.DrawLine(pen, 8, 14, 8, 16);
                graphics.DrawLine(pen, 0, 8, 2, 8);
                graphics.DrawLine(pen, 14, 8, 16, 8);
                break;
            case 4:
                graphics.DrawLines(pen, [new(1, 14), new(1, 3), new(6, 3), new(8, 5), new(15, 5), new(15, 14), new(1, 14)]);
                break;
            case 5:
                graphics.DrawEllipse(pen, 2, 4, 12, 12);
                graphics.DrawLine(pen, 6, 1, 10, 1);
                graphics.DrawLine(pen, 8, 6, 8, 10);
                graphics.DrawLine(pen, 8, 10, 11, 10);
                break;
        }
        graphics.Restore(state);
    }
}

internal class StudioCard : Panel
{
    public StudioCard() => DoubleBuffered = true;

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        StudioDrawing.Card(e.Graphics, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1),
            14 * DeviceDpi / 96f, MainForm.Surface, StudioDrawing.Border);
        base.OnPaint(e);
    }
}

internal sealed class StudioHeading : Control
{
    private string section = "";
    private string subtitle = "";
    private int index;

    public StudioHeading() => DoubleBuffered = true;

    public void SetSection(string title, string summary, int order)
    {
        section = title;
        subtitle = summary;
        index = order;
        AccessibleName = title;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float dpi = DeviceDpi / 96f;
        e.Graphics.ScaleTransform(dpi, dpi);
        float width = Width / dpi;
        float height = Height / dpi;
        using var shape = StudioDrawing.Rounded(new RectangleF(0, 0, width - 1, height - 1), 16);
        using var gradient = new LinearGradientBrush(new RectangleF(0, 0, width, height),
            Color.FromArgb(244, 239, 255), Color.FromArgb(224, 223, 253), 12);
        e.Graphics.FillPath(gradient, shape);

        GraphicsState clipped = e.Graphics.Save();
        e.Graphics.SetClip(shape);
        using var rings = new Pen(Color.FromArgb(60, 111, 87, 216), 1);
        for (int ring = 0; ring < 3; ring++)
            e.Graphics.DrawEllipse(rings, width - 166 - ring * 15, -22 - ring * 15, 126 + ring * 30, 126 + ring * 30);
        StudioDrawing.Controller(e.Graphics, MainForm.Accent, width - 154, 12, 1.06f);
        e.Graphics.Restore(clipped);

        using var titleFont = new Font("Segoe UI Semibold", 23, GraphicsUnit.Pixel);
        using var captionFont = new Font("Segoe UI", 12, GraphicsUnit.Pixel);
        using var titleInk = new SolidBrush(MainForm.TextColor);
        using var detailInk = new SolidBrush(MainForm.Muted);
        using var accentInk = new SolidBrush(MainForm.Accent);
        e.Graphics.DrawString($"STUDIO   /   {index:00}", captionFont, accentInk, 17, 9);
        e.Graphics.DrawString(section, titleFont, titleInk, 16, 25);
        e.Graphics.DrawString(subtitle, captionFont, detailInk, 18, 58);
    }
}

internal sealed class StudioSidebar : Panel
{
    private readonly StudioButton[] buttons;
    public event Action<int>? Selected;

    public StudioSidebar(string[] entries)
    {
        Dock = DockStyle.Fill;
        Margin = Padding.Empty;
        Padding = new(13, 17, 13, 13);
        BackColor = StudioDrawing.Sidebar;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        layout.RowStyles.Add(new(SizeType.Absolute, 82));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 36));
        layout.Controls.Add(new StudioBrand { Dock = DockStyle.Fill }, 0, 0);

        var menu = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = entries.Length + 3, Margin = Padding.Empty };
        menu.Controls.Add(Category("CONTROLE"), 0, 0);
        menu.RowStyles.Add(new(SizeType.Absolute, 29));
        buttons = new StudioButton[entries.Length];
        for (int index = 0; index < entries.Length; index++)
        {
            if (index == 4)
            {
                menu.Controls.Add(Category("SESSÃO"), 0, index + 1);
                menu.RowStyles.Add(new(SizeType.Absolute, 34));
            }
            int entry = index;
            var button = new StudioButton(entries[index], icon: index) { Dock = DockStyle.Fill, Margin = new(0, 0, 0, 4) };
            buttons[index] = button;
            menu.RowStyles.Add(new(SizeType.Absolute, 43));
            menu.Controls.Add(button, 0, index + (index < 4 ? 1 : 2));
            button.Click += (_, _) => SelectPage(entry);
        }
        menu.RowStyles.Add(new(SizeType.Percent, 100));
        buttons[0].Selected = true;
        layout.Controls.Add(menu, 0, 1);
        var release = MainForm.LabelText("WINDOWS  /  USB", MainForm.Muted);
        release.Font = new("Segoe UI Semibold", 9);
        release.TextAlign = ContentAlignment.MiddleLeft;
        release.Dock = DockStyle.Fill;
        layout.Controls.Add(release, 0, 2);
        Controls.Add(layout);
    }

    private void SelectPage(int index)
    {
        for (int current = 0; current < buttons.Length; current++)
            buttons[current].Selected = current == index;
        Selected?.Invoke(index);
    }

    private static Label Category(string text)
    {
        var label = MainForm.LabelText(text, MainForm.Muted);
        label.Font = new("Segoe UI Semibold", 8);
        label.Margin = new(11, 0, 0, 5);
        label.TextAlign = ContentAlignment.BottomLeft;
        label.Dock = DockStyle.Fill;
        return label;
    }
}

internal sealed class StudioBrand : Control
{
    public StudioBrand()
    {
        AccessibleName = "DualSense Studio";
        DoubleBuffered = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float dpi = DeviceDpi / 96f;
        e.Graphics.ScaleTransform(dpi, dpi);
        StudioDrawing.Card(e.Graphics, new RectangleF(6, 11, 41, 41), 12, MainForm.Accent, Color.Transparent);
        StudioDrawing.Controller(e.Graphics, Color.White, 11, 21, .32f);
        using var brand = new Font("Segoe UI Semibold", 16, GraphicsUnit.Pixel);
        using var detail = new Font("Segoe UI", 12, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(MainForm.TextColor);
        using var muted = new SolidBrush(MainForm.Muted);
        e.Graphics.DrawString("DualSense", brand, ink, 55, 12);
        e.Graphics.DrawString("Studio", detail, muted, 57, 34);
    }
}

internal sealed class StudioButton : Button
{
    private readonly bool prominent;
    private readonly int? icon;
    private bool hovering;
    private bool pressed;
    private bool selected;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool Selected
    {
        get => selected;
        set { selected = value; Invalidate(); }
    }

    public StudioButton(string caption, bool prominent = false, int? icon = null)
    {
        Text = caption;
        this.prominent = prominent;
        this.icon = icon;
        AutoSize = icon is null;
        MinimumSize = new(0, 34);
        Padding = new(12, 0, 12, 0);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        DoubleBuffered = true;
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovering = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovering = false; pressed = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); pressed = e.Button == MouseButtons.Left; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        float dpi = DeviceDpi / 96f;
        e.Graphics.Clear(icon is null ? Parent?.BackColor == Color.Transparent ? MainForm.Surface : MainForm.Canvas : StudioDrawing.Sidebar);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Color fill = icon is not null ? selected || hovering ? StudioDrawing.Lavender : StudioDrawing.Sidebar
            : prominent && Enabled ? MainForm.Accent : hovering ? StudioDrawing.Lavender : MainForm.Surface;
        if (pressed && Enabled) fill = ControlPaint.Dark(fill, .12f);
        Color border = icon is null && (!prominent || !Enabled) ? StudioDrawing.Border : Color.Transparent;
        if (Focused && ShowFocusCues) border = MainForm.Accent;
        StudioDrawing.Card(e.Graphics, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 9 * dpi, fill, border);

        Color ink = !Enabled ? MainForm.Muted : prominent ? Color.White : selected ? MainForm.Accent : MainForm.TextColor;
        TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        var bounds = ClientRectangle;
        if (icon is null) flags |= TextFormatFlags.HorizontalCenter;
        else
        {
            StudioDrawing.Navigation(e.Graphics, icon.Value, ink, dpi, Height / 2f);
            bounds.X += (int)(38 * dpi);
            bounds.Width -= (int)(45 * dpi);
        }
        TextRenderer.DrawText(e.Graphics, Text, Font, bounds, ink, flags);
    }
}

internal sealed class LightPreview : StudioCard
{
    private Color light = Color.DeepSkyBlue;

    public LightPreview()
    {
        Size = new(689, 140);
        Margin = new(3, 2, 3, 12);
        BackColor = MainForm.Canvas;
        AccessibleName = "Prévia da barra de luz";
    }

    public void SetColor(Color color)
    {
        light = color;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        float dpi = DeviceDpi / 96f;
        e.Graphics.ScaleTransform(dpi, dpi);
        float width = Width / dpi;
        using var heading = new Font("Segoe UI Semibold", 19, GraphicsUnit.Pixel);
        using var caption = new Font("Segoe UI", 13, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(MainForm.TextColor);
        using var muted = new SolidBrush(MainForm.Muted);
        e.Graphics.DrawString("Barra de luz", heading, ink, 22, 25);
        e.Graphics.DrawString($"#{light.R:X2}{light.G:X2}{light.B:X2}", caption, muted, 24, 57);
        StudioDrawing.Card(e.Graphics, new RectangleF(24, 84, 115, 10), 5, light, StudioDrawing.Border);
        StudioDrawing.Card(e.Graphics, new RectangleF(width - 200, 17, 183, 106), 15, StudioDrawing.Lavender, Color.Transparent);
        StudioDrawing.Controller(e.Graphics, MainForm.Accent, width - 180, 23, 1.48f);
        StudioDrawing.Card(e.Graphics, new RectangleF(width - 121, 96, 26, 5), 2, light, Color.Transparent);
    }
}
