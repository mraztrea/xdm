using System;
using TraceLog;

namespace XDM.GtkUI.Utils.Tray
{
    /// <summary>
    /// Fallback for desktops whose tray only hosts XEmbed icons (X11 sessions): the legacy
    /// Gtk.StatusIcon, with the same tooltip and the same two menu entries as the SNI backend.
    /// </summary>
    internal sealed class LegacyStatusIconTray : ITrayBackend
    {
        private const string Tooltip = "XDM";

        private readonly SniMenuLayout layout;
        private readonly Action<TrayMenuItemAction> onAction;

        private Gtk.StatusIcon? statusIcon;
        private Gtk.Menu? menu;

        public LegacyStatusIconTray(SniMenuLayout layout, Action<TrayMenuItemAction> onAction)
        {
            this.layout = layout;
            this.onAction = onAction;
        }

        public bool Attach()
        {
            statusIcon = new Gtk.StatusIcon(GtkHelper.LoadSvg("xdm-logo", 128))
            {
                TooltipText = Tooltip,
                Visible = true
            };
            statusIcon.Activate += (_, _) => onAction(TrayMenuItemAction.RestoreWindow);

            menu = new Gtk.Menu();
            foreach (var item in layout.Items)
            {
                var menuItem = new Gtk.MenuItem(item.Label);
                var action = item.Action;
                menuItem.Activated += (_, _) => onAction(action);
                menu.Append(menuItem);
            }
            menu.ShowAll();
            statusIcon.PopupMenu += (_, _) => menu.PopupAtPointer(null);
            return true;
        }

        public void Dispose()
        {
            try
            {
                statusIcon?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, ex.Message);
            }
            statusIcon = null;
            try
            {
                menu?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, ex.Message);
            }
            menu = null;
        }
    }
}
