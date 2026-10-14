using System;
using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Lidiflow.UI;

/// The notification-area icon (the macOS menu bar item).
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ToolStripMenuItem statusItem;

    public TrayIcon(Action open, Action copyLast, Action quit)
    {
        statusItem = new Forms.ToolStripMenuItem("Iniciando…") { Enabled = false };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(statusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Abrir Lidiflow", null, (_, _) => open());
        menu.Items.Add("Copiar última transcrição", null, (_, _) => copyLast());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Sair do Lidiflow", null, (_, _) => quit());

        icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Lidiflow",
            ContextMenuStrip = menu,
            Visible = true,
        };
        icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) open();
        };
    }

    public string Status
    {
        set
        {
            statusItem.Text = value;
            // NotifyIcon tooltips are limited to 63 characters.
            var tip = "Lidiflow — " + value;
            icon.Text = tip.Length > 63 ? tip[..62] + "…" : tip;
        }
    }

    private static Icon LoadIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/AppIcon.png"));
            if (resource != null)
            {
                using var source = new Bitmap(resource.Stream);
                using var small = new Bitmap(source, new System.Drawing.Size(32, 32));
                return Icon.FromHandle(small.GetHicon());
            }
        }
        catch
        {
            // Fall back to the generic application icon.
        }
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
    }
}
