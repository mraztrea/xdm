using System;
using System.Collections.Generic;
using TraceLog;
using Translations;
using XDM.Core;
using XDM.Core.UI;

namespace XDM.GtkUI.Utils.Tray
{
    /// <summary>
    /// The system tray icon of a running XDM instance.
    ///
    /// The icon is published once, right after the application context is configured, and lives as long
    /// as the process: it never appears or disappears with the main window (see
    /// specs/001-background-tray-icon/spec.md FR-001). Exactly one backend is used, so a session never
    /// shows two icons.
    ///
    /// Attach never throws and never shows the main window; a tray that cannot be published is logged
    /// and the application keeps running hidden (FR-010).
    /// </summary>
    public sealed class TrayIcon : IDisposable
    {
        private static readonly int[] IconSizes = { 22, 44 };

        private readonly IApplicationWindow window;
        private readonly IApplication application;
        private readonly IApplicationCore core;
        private readonly SniMenuLayout menuLayout;
        private readonly TrayIconPixmap[] pixmaps;

        private ITrayBackend? backend;
        private bool disposed;

        public TrayIconState State { get; private set; } = TrayIconState.Created;
        public TrayBackendKind BackendKind { get; private set; } = TrayBackendKind.None;

        /// <summary>Raised after the main window has been asked to come to the front.</summary>
        public event EventHandler? Activated;

        /// <summary>Raised when the menu's exit entry has been chosen, before the exit is carried out.</summary>
        public event EventHandler? ExitRequested;

        private TrayIcon(IApplicationWindow window, IApplication application, IApplicationCore core)
        {
            this.window = window;
            this.application = application;
            this.core = core;
            this.menuLayout = TrayMenuLayout.Build(
                1,
                TextResource.GetText("MSG_RESTORE"),
                TextResource.GetText("MENU_EXIT"));
            this.pixmaps = TryLoadPixmaps();
        }

        /// <summary>
        /// Publishes the tray icon. Must be called after ApplicationContext.Configure(), so that a
        /// duplicate launch has already exited and the single-instance handoff stays intact.
        /// </summary>
        public static TrayIcon Attach(IApplicationWindow window, IApplication application, IApplicationCore core)
        {
            var tray = new TrayIcon(window, application, core);
            try
            {
                tray.AttachBackend();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, ex.Message);
                tray.State = TrayIconState.Unavailable;
            }
            return tray;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            try
            {
                backend?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, ex.Message);
            }
            backend = null;
            State = TrayIconState.Disposed;
        }

        private void AttachBackend()
        {
            if (TryAttach(TrayBackendKind.Sni))
            {
                return;
            }
            if (TryAttach(TrayBackendKind.LegacyStatusIcon))
            {
                return;
            }
            State = TrayIconState.Unavailable;
            Log.Debug("Tray icon unavailable: no status notifier host and no usable system tray");
        }

        private bool TryAttach(TrayBackendKind kind)
        {
            ITrayBackend? candidate = null;
            try
            {
                candidate = kind == TrayBackendKind.Sni
                    ? new SniTrayIcon(menuLayout, pixmaps, HandleAction)
                    : new LegacyStatusIconTray(menuLayout, HandleAction);
                if (!candidate.Attach())
                {
                    Log.Debug($"Tray backend {kind} could not publish an icon");
                    candidate.Dispose();
                    return false;
                }
                backend = candidate;
                BackendKind = kind;
                State = TrayIconState.Attached;
                Log.Debug($"Tray icon attached using {kind}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, $"Tray backend {kind} failed");
                try
                {
                    candidate?.Dispose();
                }
                catch (Exception inner)
                {
                    Log.Debug(inner, inner.Message);
                }
                return false;
            }
        }

        private void HandleAction(TrayMenuItemAction action)
        {
            switch (action)
            {
                case TrayMenuItemAction.RestoreWindow:
                    RestoreWindow();
                    break;
                case TrayMenuItemAction.ExitApplication:
                    RequestExit();
                    break;
            }
        }

        private void RestoreWindow()
        {
            try
            {
                application.RunOnUiThread(() =>
                {
                    window.ShowAndActivate();
                    Activated?.Invoke(this, EventArgs.Empty);
                });
            }
            catch (Exception ex)
            {
                Log.Debug(ex, ex.Message);
            }
        }

        private void RequestExit()
        {
            try
            {
                application.RunOnUiThread(() =>
                {
                    ExitRequested?.Invoke(this, EventArgs.Empty);
                    if (!ConfirmExitWhileDownloading())
                    {
                        return;
                    }
                    Dispose();
                    Gtk.Application.Quit();
                    Environment.Exit(0);
                });
            }
            catch (Exception ex)
            {
                Log.Debug(ex, ex.Message);
            }
        }

        /// <summary>
        /// Asks before stopping downloads in progress; without downloads (or without a prompt text) the
        /// exit proceeds without a dialog.
        /// </summary>
        private bool ConfirmExitWhileDownloading()
        {
            try
            {
                if (core.ActiveDownloadCount <= 0)
                {
                    return true;
                }
                var prompt = TextResource.GetText("MSG_QUIT_ACTIVE_DOWNLOADS");
                if (string.IsNullOrEmpty(prompt))
                {
                    return true;
                }
                return window.Confirm(null, prompt);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, ex.Message);
                return true;
            }
        }

        private static TrayIconPixmap[] TryLoadPixmaps()
        {
            try
            {
                var result = new List<TrayIconPixmap>(IconSizes.Length);
                foreach (var size in IconSizes)
                {
                    result.Add(TrayIconPixmap.FromPixbuf(GtkHelper.LoadSvg("xdm-logo", size)));
                }
                return result.ToArray();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Tray icon artwork could not be loaded");
                return Array.Empty<TrayIconPixmap>();
            }
        }
    }
}
