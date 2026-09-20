# Contract: tray component (internal API of `XDM.Gtk.UI`)

**Feature**: `specs/001-background-tray-icon/spec.md` | **Plan**: [plan.md](../plan.md) | **Research**: [research.md](../research.md)

Internal C# contract for the tray code added under `app/XDM/XDM.Gtk.UI/Utils/Tray/`. It is the only part of the feature the rest of the GTK project depends on; the D-Bus surface it publishes is specified in [status-notifier-item.md](./status-notifier-item.md).

## 1. `TrayIcon` (facade)

| Member | Signature | Contract |
|--------|-----------|----------|
| `Attach` | `static TrayIcon Attach(IApplicationWindow window, IApplication application, IApplicationCore core)` | Selects one backend, publishes the icon, returns an instance in state `Attached` or `Unavailable`. Never throws; never shows the window (FR-010) |
| `State` | `TrayIconState { get; }` | One of `Created`, `Attached`, `Unavailable`, `Disposed` |
| `BackendKind` | `TrayBackendKind { get; }` | `Sni`, `LegacyStatusIcon` or `None` |
| `Dispose` | `void Dispose()` | Idempotent; unregisters the item and closes the bus connection; moves the instance to `Disposed` (FR-007) |
| `Activated` | `event EventHandler` | Raised when the user activates the icon and the component does not handle it itself (used for logging/tests) |
| `ExitRequested` | `event EventHandler` | Raised when the menu's exit item is chosen; the handler runs the exit flow in §4 |

**Ordering contract**: `Attach` is called from `Program.Main` after `ApplicationContext.Configurer()…Configure()` and before `Gtk.Application.Run()`; `Dispose` is called from the exit flow and, defensively, from the GTK shutdown path.

**Threading contract**: protocol callbacks arrive on D-Bus threads. Any interaction with `IApplicationWindow` (`ShowAndActivate`, `Confirm`) goes through `IApplication.RunOnUiThread(...)`; no GTK type is touched off the main thread. Icon rasterisation happens on the calling thread before publishing.

**Failure contract**: every step (backend selection, bus connect, name/handler registration, watcher lookup and call, pixmap build) is guarded; a failure logs one `Log.Debug` line naming the step and degrades SNI → `LegacyStatusIcon` → `None`. Exceptions never escape `Attach`, and no failure path calls `ShowAndActivate()`.

## 2. Backend contract (`ITrayBackend`)

| Member | Contract |
|--------|----------|
| `Attach()` | Publish the icon; return success/failure. Must not partially publish (an icon is either complete with tooltip and menu, or not published at all) |
| `Dispose()` | Remove the icon; idempotent |
| `Actions` | Callbacks the backend invokes for activation and exit; the facade supplies them, so backends never call Core directly |

Two implementations:

| Implementation | When used | Menu | Tooltip | Primary click |
|----------------|-----------|------|---------|---------------|
| `SniTrayIcon` (+`SniMenu`) | A status-notifier host is registered | DBusMenu at `/MenuBar` | SNI `ToolTip` | `Activate` → restore |
| `LegacyStatusIconTray` | No SNI host; XEmbed tray may host `Gtk.StatusIcon` | `Gtk.Menu` with the same two items, shown from the icon's `popup-menu` signal | `TooltipText` | `Activate` event → restore |

Exactly one backend is constructed per process (`TrayBackendKind`), so exactly one icon is published (FR-001).

## 3. Pure helpers (unit-tested)

| Type | API | Contract |
|------|-----|----------|
| `TrayIconPixmap` | `static TrayIconPixmap FromRgba(byte[] pixels, int width, int height, int rowStride, bool hasAlpha)` (pure) and `static TrayIconPixmap FromPixbuf(Gdk.Pixbuf pixbuf)` (wrapper) | Convert to ARGB32 bytes in network byte order; `Argb32.Length == Width * Height * 4`; add an opaque alpha channel when the source has none; honour `rowStride` padding; throw `ArgumentException` when the buffer cannot be interpreted — the caller treats that as an attach failure |
| `TrayMenuLayout` | `static SniMenuLayout Build(uint revision, string restoreLabel, string exitLabel)` | Item ids `1` (restore) and `2` (exit) in that order, root id `0`; invoked once per session, so `revision` is constant. An empty label (a language file without the key) is replaced by the English text, so no menu entry can be blank |

## 4. Exit flow (single implementation, tray-initiated only)

```
ExitRequested handler:
  RunOnUiThread:
    if CoreService.ActiveDownloadCount > 0
       and not MainWindow.Confirm(null, TextResource.GetText("MSG_QUIT_ACTIVE_DOWNLOADS")):
        return                       # cancel: instance keeps running, downloads untouched (SC-007)
    tray.Dispose()                   # remove icon before the process dies (FR-007)
    Application.Quit()
    Environment.Exit(0)              # same termination used by the existing in-app Exit
```

**Contract notes**
- The in-app Exit menu item (`app/XDM/XDM.Gtk.UI/MainWindow.cs:255-259`) is deliberately not routed through this flow (clarification Q5); it stays immediate.
- `Dispose()` runs before `Environment.Exit(0)` because the application has no pre-exit hook; termination without `Dispose()` (crash, logoff, in-app Exit) is still covered by the bus connection dropping, after which hosts drop the item.
- Language key `MSG_QUIT_ACTIVE_DOWNLOADS` must exist in `app/XDM/Lang/English.txt` (concrete string in [research.md](../research.md) D10); if the key were missing, `TextResource.GetText` returns an empty string, which the contract forbids — the tray-exit code asserts the prompt text is non-empty and, if not, skips the prompt instead of showing an empty dialog.

## 5. Wiring changes outside the tray folder

| File | Change |
|------|--------|
| `app/XDM/XDM.Gtk.UI/Program.cs` | After `Configure()`: `tray = TrayIcon.Attach(win, app, core)`; on `Gtk.Application.Run()` returning: `tray?.Dispose()` |
| `app/XDM/XDM.Gtk.UI/MainWindow.cs` | Delete the `StatusIcon` field, its construction/`Activate` wiring and `StatusIcon_Activate` (lines 41, 132-133, 136-139). No other member changes; `ShowAndActivate`, `Confirm`, `RunOnUIThread`, `DeleteEvent → Hide` stay as they are |
| `app/XDM/XDM.Gtk.UI/XDM.Gtk.UI.csproj` | Add `PackageReference` `Tmds.DBus.Protocol` `0.21.3` |
| `app/XDM/Lang/English.txt` | Add `MSG_QUIT_ACTIVE_DOWNLOADS=…` |
| `app/XDM/XDM.Tests/XDM.Tests.csproj` + `TrayIconTests.cs` | Project reference to `XDM.Gtk.UI`; tests for `TrayIconPixmap` and `TrayMenuLayout` |
| `app/XDM/XDM.Core/**`, `app/XDM/XDM.Wpf.UI/**` | **No changes** (FR-011 gate) |
