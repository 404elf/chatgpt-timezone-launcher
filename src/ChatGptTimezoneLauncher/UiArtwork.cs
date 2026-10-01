using System.Drawing;
using System.Drawing.Drawing2D;

namespace ChatGptTimezoneLauncher;

internal static class UiArtwork
{
    public static Icon LoadIcon()
    {
        using var stream = typeof(UiArtwork).Assembly.GetManifestResourceStream("launcher.ico")!;
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }
    public static Image LoadImage()
    {
        using var stream = typeof(UiArtwork).Assembly.GetManifestResourceStream("launcher.png")!;
        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }
}

internal sealed class UiCard : Panel
{
    public UiCard() { DoubleBuffered = true; BackColor = Color.White; }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Color.FromArgb(244, 246, 252));
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        using var path = new GraphicsPath();
        const int diameter = 24;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90); path.CloseFigure();
        using var brush = new SolidBrush(BackColor); e.Graphics.FillPath(brush, path);
        using var pen = new Pen(Color.FromArgb(228, 233, 245)); e.Graphics.DrawPath(pen, path);
    }
}
