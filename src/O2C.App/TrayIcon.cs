using System.Drawing;
using System.Drawing.Drawing2D;
using O2C.Core;
using Forms = System.Windows.Forms;

namespace O2C.App;

/// <summary>
/// Значок в области уведомлений: цвет — состояние подключения, меню — подключить/отключить, открыть, выход.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _toggle;
    private readonly Dictionary<Color, Icon> _icons = [];

    public TrayIcon(Action open, Action toggleConnection, Action exit)
    {
        _toggle = new Forms.ToolStripMenuItem("Подключить", null, (_, _) => toggleConnection());
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_toggle);
        menu.Items.Add(new Forms.ToolStripMenuItem("Открыть", null, (_, _) => open()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Выход (подключение останется)", null, (_, _) => exit()));

        _icon = new Forms.NotifyIcon
        {
            Text = "OpenToConnect",
            ContextMenuStrip = menu,
            Icon = MakeIcon(Color.Gray),
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                open();
        };
    }

    public void Update(ConnectionState state, bool serviceAvailable, string text)
    {
        var color = !serviceAvailable ? Color.Gray : state switch
        {
            ConnectionState.Connected => Color.FromArgb(0x2E, 0xA0, 0x43),
            ConnectionState.Error => Color.FromArgb(0xD0, 0x30, 0x30),
            ConnectionState.Disconnected => Color.Gray,
            _ => Color.FromArgb(0xE0, 0xA0, 0x00),
        };
        _icon.Icon = MakeIcon(color);
        // Подсказка значка ограничена 63 символами.
        var tip = $"OpenToConnect: {text}";
        _icon.Text = tip.Length > 63 ? tip[..63] : tip;
        bool active = state is ConnectionState.Connecting or ConnectionState.Connected or ConnectionState.Reconnecting;
        _toggle.Text = active ? "Отключить" : "Подключить";
        _toggle.Enabled = serviceAvailable && state != ConnectionState.Disconnecting;
    }

    /// <summary>Круг цвета состояния со знаком питания — тот же рисунок, что у иконки программы.</summary>
    private Icon MakeIcon(Color color)
    {
        if (_icons.TryGetValue(color, out var cached))
            return cached;

        const int s = 32;
        using var bmp = new Bitmap(s, s);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(color);
            g.FillEllipse(fill, 0, 0, s - 1, s - 1);
            using var pen = new Pen(Color.White, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            const float r = s * 0.27f, c = s / 2f;
            g.DrawArc(pen, c - r, c - r + 1, 2 * r, 2 * r, -60, 300);
            g.DrawLine(pen, c, s * 0.18f, c, s * 0.48f);
        }
        var icon = Icon.FromHandle(bmp.GetHicon());
        _icons[color] = icon;
        return icon;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var icon in _icons.Values)
            icon.Dispose();
    }
}
