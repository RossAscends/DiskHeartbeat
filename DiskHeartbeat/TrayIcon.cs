using Forms = System.Windows.Forms;
using System.Drawing;
using System.Runtime.InteropServices;
namespace DiskHeartbeat;

// Owns all native tray resources; the window keeps polling while hidden.
internal sealed class TrayIcon : IDisposable
{
    readonly Forms.NotifyIcon icon;
    readonly Icon artwork;
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
    public TrayIcon(Action show, Action exit, Action refresh)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb(16, 24, 32));
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var pen = new Pen(Color.FromArgb(135, 225, 180), 3);
            graphics.DrawLines(pen, new[] { new Point(2, 17), new Point(9, 17), new Point(13, 7), new Point(18, 26), new Point(23, 14), new Point(30, 14) });
        }
        var handle = bitmap.GetHicon();
        try { artwork = (Icon)Icon.FromHandle(handle).Clone(); } finally { DestroyIcon(handle); }
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Disk Heartbeat", null, (_, _) => show());
        menu.Items.Add("Exit", null, (_, _) => exit());
        icon = new Forms.NotifyIcon { Icon = artwork, Text = "Disk Heartbeat", ContextMenuStrip = menu, Visible = true };
        icon.DoubleClick += (_, _) => show();
        icon.MouseMove += (_, _) => refresh();
    }
    public void Update(string text) => icon.Text = text.Length > 127 ? text[..127] : text;
    public string Text => icon.Text;
    public void Dispose() { icon.Visible = false; icon.ContextMenuStrip?.Dispose(); icon.Dispose(); artwork.Dispose(); }
}
