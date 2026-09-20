using System;

namespace XDM.GtkUI.Utils.Tray
{
    /// <summary>Which mechanism is presenting the tray icon. Decided once per process.</summary>
    public enum TrayBackendKind
    {
        None,
        Sni,
        LegacyStatusIcon
    }

    /// <summary>Lifecycle of the tray icon, see specs/001-background-tray-icon/data-model.md.</summary>
    public enum TrayIconState
    {
        Created,
        Attached,
        Unavailable,
        Disposed
    }

    public enum TrayMenuItemAction
    {
        RestoreWindow,
        ExitApplication
    }

    /// <summary>
    /// A way of publishing the tray icon. Implementations must publish the icon completely (artwork,
    /// tooltip, menu) or not at all, and must never be used concurrently with another backend.
    /// </summary>
    internal interface ITrayBackend
    {
        /// <summary>Publishes the icon. Returns false when the backend cannot present an icon.</summary>
        bool Attach();

        /// <summary>Removes the icon. Must be idempotent.</summary>
        void Dispose();
    }
}
