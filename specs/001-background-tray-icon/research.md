# Phase 0 Research: Background Instance Tray Icon

**Feature**: `specs/001-background-tray-icon/spec.md` | **Plan**: [plan.md](./plan.md) | **Date**: 2026-09-20

All `NEEDS CLARIFICATION` items from Technical Context are resolved below. Every code fact carries a `path:line` reference from the repository at `001-background-tray-icon`.

---

## D1 — Tray transport: freedesktop StatusNotifierItem + DBusMenu over the session bus

**Decision**: Implement the tray icon as a StatusNotifierItem (SNI) object registered with the StatusNotifierWatcher, with its menu exposed through the `com.canonical.dbusmenu` (v3) interface.

**Rationale**:
- The GTK build already creates a tray icon (`app/XDM/XDM.Gtk.UI/MainWindow.cs:41,132-133`), but it is `Gtk.StatusIcon`, which is implemented with the legacy XEmbed host protocol. GNOME Shell does not host XEmbed icons at all (X11 or Wayland), and no Wayland session can embed one, which is exactly the reported "running in the background with no icon" symptom.
- SNI is the protocol modern hosts consume: KDE Plasma natively, GNOME Shell through the AppIndicator extension, XFCE/Cinnamon/MATE through their SNI applets. It also carries the tooltip, icon pixmaps and a DBusMenu-backed context menu the spec requires.
- Reference: freedesktop StatusNotifierItem specification — `org.kde.StatusNotifierItem` at object path `/StatusNotifierItem`, registered with `org.kde.StatusNotifierWatcher.RegisterStatusNotifierItem` (specification also defines the `org.freedesktop.*` alias names).

**Alternatives considered**:
- `Gtk.StatusIcon` only (status quo): cannot satisfy FR-001/FR-003 on the target desktops; kept only as an X11 fallback (D3).
- `libayatana-appindicator3` via P/Invoke: adds a native runtime dependency that is not present on every distribution, is marked obsolete upstream in favour of `libayatana-appindicator-glib`, and its items are menu-only (`ItemIsMenu` behaviour), so a primary click would open the menu instead of restoring the window — a direct FR-003 violation.
- `System.Windows.Forms.NotifyIcon` (used by the WPF build): Windows-only implementation; unavailable on the GTK target.

## D2 — D-Bus client library: `Tmds.DBus.Protocol` 0.21.3

**Decision**: Add `Tmds.DBus.Protocol` version `0.21.3` to `app/XDM/XDM.Gtk.UI/XDM.Gtk.UI.csproj`.

**Rationale**:
- Package metadata (nuget.org `tmds.dbus.protocol` 0.21.3 nuspec) declares a `net6.0` target group depending only on `System.IO.Pipelines 8.0.0`, so it fits the project's `net6.0` framework.
- Upstream documents NativeAOT/trimming compatibility (methods that are not compatible are marked `Obsolete`/`RequiresUnreferencedCode`) — required because the project publishes with `PublishTrimmed=true` / `TrimMode=Link`.
- Object export is explicit: an `IPathMethodHandler` implementation receives method calls and replies with a `MessageWriter`, which is what the SNI and DBusMenu objects need; no reflection or code generation is required for our side.

**Alternatives considered**:
- `Tmds.DBus` (the older high-level package): its object/proxy generation relies on `Reflection.Emit`, which conflicts with the project's trimming settings.
- `dbus-sharp`/`NDesk.DBus`: unmaintained, no net6.0 support.
- Hand-rolled `libdbus-1` P/Invoke: hundreds of lines of manual marshalling plus a native dependency, for no benefit over the managed client.
- Shelling out to `dbus-send` (already used in `app/XDM/XDM.Core/Util/PlatformHelper.cs:563-608` for other purposes): cannot receive incoming `Activate`/`Event` method calls, which the icon needs.

## D3 — Backend selection: SNI when a host exists, legacy `Gtk.StatusIcon` otherwise; never both

**Decision**: At startup, after `ApplicationContext` is configured, decide one backend: SNI if `org.kde.StatusNotifierWatcher` is present **and** reports `IsStatusNotifierHostRegistered` and registration succeeds; otherwise the existing `Gtk.StatusIcon` (kept, but moved out of `MainWindow` and given a tooltip and menu). `TrayBackendKind` records the outcome.

**Rationale**:
- FR-001 demands exactly one icon; creating both would show two icons on Plasma (which hosts SNI *and* keeps a legacy XEmbed tray).
- Keeping the legacy backend preserves the icon that those desktops already show today — dropping it would be a regression for XEmbed-only trays on X11.
- FR-010 requires that an unusable tray is logged and non-fatal, which is exactly the `None`/fallback path.

**Alternatives considered**: SNI-only (regresses XEmbed-only trays); legacy-only (the current, broken behaviour); running both and hiding one conditionally (window-visibility-dependent icon presence would violate FR-001's "must not appear or disappear").

## D4 — Attachment point and ordering

**Decision**: Create and attach the tray in `app/XDM/XDM.Gtk.UI/Program.cs` immediately after `ApplicationContext.Configurer()…Configure()` and before `Gtk.Application.Run()`; remove the tray construction from `MainWindow`'s constructor.

**Rationale**:
- Attaching after `Configure()` means a second launch has already exited: `ApplicationContext.Configure()` calls `SingleInstance.Ensure()` (`app/XDM/XDM.Core/ApplicationContext.cs:168-184`), which for a duplicate instance posts its args to `http://127.0.0.1:8597/args` and calls `Environment.Exit(0)` (`app/XDM/XDM.Core/SingleInstance.cs:20-32,39-47`). Attaching earlier would briefly register a second icon and break SC-004.
- Language files are loaded before `Configure()` in `Program.cs`, so menu labels are already in the user's language when the menu layout is built (FR-009).
- Window restore needs `ApplicationContext.MainWindow` (`IApplicationWindow.ShowAndActivate`, `app/XDM/XDM.Core/UI/IApplicationWindow.cs:90`), which is only usable after `Configure()` sets `s_Init`.

**Alternatives considered**: keeping creation in the `MainWindow` constructor (today's code) — the window contract gives no tray lifecycle and would register an icon before the single-instance check.

## D5 — Activation semantics

**Decision**: Publish `ItemIsMenu=false`. `Activate(x,y)` and `SecondaryActivate(x,y)` both marshal `ApplicationContext.MainWindow.ShowAndActivate()` onto the GTK main thread through `ApplicationContext.Application.RunOnUiThread(...)`; `ContextMenu(x,y)` and `Scroll(delta,orientation)` are accepted and ignored.

**Rationale**:
- FR-003 requires a primary click to restore, un-minimize, raise and focus the window. `MainWindow.ShowAndActivate()` does `Show()` when hidden plus `Present()` (`app/XDM/XDM.Gtk.UI/MainWindow.cs:1315-1322`), and `RunOnUIThread` marshals via `Application.Invoke` (`:1073-1081`). Both already exist; no Core change is needed.
- `ItemIsMenu=false` is the protocol's signal that the item is activatable rather than menu-only, which distinguishes us from appindicator-style items.
- FR-004 (reuse the existing window, no second instance) is satisfied by routing through the same `ShowAndActivate()` used by `--restore-window`.

**Alternatives considered**: `ItemIsMenu=true` (hosts would send `ContextMenu` instead of `Activate` and the click would only open the menu — violates FR-003).

## D6 — Menu: DBusMenu v3 at `/MenuBar` with two items

**Decision**: Serve `com.canonical.dbusmenu` version 3 at object path `/MenuBar`, root id 0, with two children — id 1 "Restore Window" (`TextResource.GetText("MSG_RESTORE")`) and id 2 "Exit" (`TextResource.GetText("MENU_EXIT")`). Handle `GetLayout`, `GetGroupProperties`, `GetProperty`, `Event`, `AboutToShow`, `AboutToShowGroup`; `Event(id, "clicked", …)` dispatches the action.

**Rationale**:
- FR-005 requires a menu with restore and exit; FR-009 requires translated labels. Both keys already exist in `app/XDM/Lang/English.txt` (`MSG_RESTORE` at `:204`, `MENU_EXIT` at `:12`) and in the other language files, so no translation work is needed for the menu itself.
- SNI's `Menu` property is an object path that must implement DBusMenu; without it, hosts show no context menu at all.

**Alternatives considered**: no menu (violates FR-005); a native `Gtk.Menu` popup triggered by `ContextMenu()` (hosts such as GNOME's extension ignore it and rely on the DBusMenu path; also mixes GTK popups into a protocol designed to avoid them).

## D7 — Exit flow with confirmation when downloads are running

**Decision**: A single tray-exit routine: if `ApplicationContext.CoreService.ActiveDownloadCount > 0`, ask `ApplicationContext.MainWindow.Confirm(null, TextResource.GetText("MSG_QUIT_ACTIVE_DOWNLOADS"))`; when the user cancels, return without touching anything; otherwise dispose the tray (unregister + close the bus connection), then run the existing `Application.Quit(); Environment.Exit(0);`. The in-app Exit menu item (`app/XDM/XDM.Gtk.UI/MainWindow.cs:255-259`) is left unchanged, per clarification Q5.

**Rationale**:
- `ActiveDownloadCount` counts live plus queued downloads (`app/XDM/XDM.Core/ApplicationCore.cs:39-40`, exposed at `app/XDM/XDM.Core/IApplicationCore.cs:37-39`) and is the idiom already used elsewhere for "are downloads running" (`app/XDM/XDM.Core/Application.cs:152`, `app/XDM/XDM.Core/CallbackActions.cs:51`).
- `MainWindow.Confirm` already renders a modal GTK `YesNo` question dialog (`app/XDM/XDM.Gtk.UI/MainWindow.cs:1063-1071`), so no new dialog class is needed — only one new language key.
- The app relies on `Environment.Exit(0)` with no pre-exit hook anywhere in Core (`app/XDM/XDM.Core/ArgsProcessor.cs:102-103`, `SingleInstance.cs:28-29`, `NativeMessagingHostHandler.cs:43`), so the tray must remove itself before exiting rather than expecting framework cleanup.

**Alternatives considered**: exit immediately (clarification Q3 rejected it); adding a Core-level pre-exit hook (scope creep for a GTK-only feature).

## D8 — Icon artwork and tooltip

**Decision**: Rasterise the shipped SVG (`GtkHelper.LoadSvg("xdm-logo", n)`, `app/XDM/XDM.Gtk.UI/Utils/GtkHelper.cs:296-300`) at 22 px and 44 px and publish both as `IconPixmap` entries of ARGB32 bytes in network byte order; publish `Id="xdm"`, `Title="Xtreme Download Manager"`, `Category="ApplicationStatus"`, `Status="Active"`, `WindowId=0`, and `ToolTip=("", [], "XDM", "Xtreme Download Manager")`. The legacy backend sets `TooltipText = "XDM"` and uses the same `Gdk.Pixbuf`.

**Rationale**:
- FR-008 needs a tooltip naming the application; the SNI tooltip is a four-tuple whose title carries the name.
- No branding asset is added (spec assumption): the existing SVG/PNG assets are reused.
- 22 px and 44 px cover standard and HiDPI panels; ARGB32 in network byte order is what the specification's "Icon pixmap" chapter mandates.

**Alternatives considered**: `IconName` with a themed icon (XDM's icon is not installed into an icon theme, so hosts would render a blank placeholder); loading `xdm-logo.ico` (ico decoding needs an extra dependency; PNG/SVG rasterisation is already available through `Gdk.Pixbuf`).

## D9 — Failure handling and observability (FR-010)

**Decision**: Every attach step (bus connect, name/handler registration, watcher lookup, watcher call, icon rasterisation) runs inside a guard that logs with `Log.Debug` and degrades: SNI → legacy backend → no icon. No exception propagates to `Program.Main`, no code path shows the window from a failure handler, and the instance keeps running.

**Rationale**:
- FR-010 and clarification Q4: stay hidden, log only, never auto-show.
- `Log.Debug` writes to `Config.AppDir/log.txt` once `XDM_DEBUG_MODE=1` initialises file tracing (`app/XDM/XDM.Gtk.UI/Program.cs:26-27`, `app/XDM/XDM.Core/TraceLog/Log.cs:8-18,24-32`); the call compiles in release builds, so the diagnostic is available when the user enables it.
- SC-006 requires the log evidence for a session without a tray host.

**Alternatives considered**: letting the exception escape (would violate FR-010 and probably kill a background start); falling back to showing the window (explicitly rejected by clarification Q4).

## D10 — Localisation of the new confirmation text

**Decision**: Add exactly one key to `app/XDM/Lang/English.txt`: `MSG_QUIT_ACTIVE_DOWNLOADS=Downloads are in progress. Exit XDM and stop them?`. Do not touch the other ~25 language files.

**Rationale**:
- `TextResource`'s static constructor loads `English.txt` first and the selected language is loaded on top with `texts[key] = value` (`app/XDM/XDM.Core/Translations/TextResource.cs:11-14,35-45`), so keys missing from a translation inherit the English text instead of showing an empty dialog. Leaving a key out of `English.txt` would render an empty prompt.
- FR-009 is satisfied for the menu entries by existing keys; the new prompt naturally shows English until translated, which matches how the repository already ships partial translations.

**Alternatives considered**: reusing an existing generic key (no suitable string exists for a quit-with-active-downloads prompt); adding machine-translated strings to every language file (translation work outside this feature; the repo has a dedicated translation generator).

## D11 — Removing the old icon from `MainWindow`

**Decision**: Delete `private StatusIcon statusIcon;` (`app/XDM/XDM.Gtk.UI/MainWindow.cs:41`), its construction and `Activate` wiring (`:132-133`) and the `StatusIcon_Activate` handler (`:136-139`); nothing else references them.

**Rationale**: Verified by grep — the only references in `app/XDM/XDM.Gtk.UI` are those four lines. Clean cutover: no alias, no dead field, and the legacy backend re-creates the icon in the tray component when it is selected.
**Alternatives considered**: leaving the old icon in place (would produce two icons on XEmbed hosts → FR-001 violation).

## D12 — Verification strategy

**Decision**: `app/XDM/XDM.Tests` (net6.0, NUnit) gains a `ProjectReference` to `XDM.Gtk.UI` and one test file covering the two pure helpers — `TrayIconPixmap` (ARGB32, network byte order, alpha handling) and `TrayMenuLayout` (item ids, labels, ordering). Everything else is verified on a live desktop with the scenarios in [quickstart.md](./quickstart.md), including inspection of the registered SNI service with `busctl`/`gdbus` and the log file for the no-host case.

**Rationale**:
- The two pure helpers are the only parts with logic whose failure is silent (a wrong byte order yields a broken icon, not an exception) — exactly the "one runnable check" this feature needs.
- The behavioural criteria (icon visible, click restores, exactly one icon, prompt on exit) are desktop interactions; the WPF-only CI job (`.github/workflows/xdm-wpf-build.yml`, `runs-on: windows-latest`, working directory `app/XDM/XDM.Wpf.UI`) cannot execute them.
- The current workstation has no `dotnet` SDK, so Phase F runs where the Linux build is produced.

**Alternatives considered**: a full DBus integration test harness (requires a session bus and a fake host — disproportionate for this feature); no automated checks at all (leaves the byte-order and layout logic unguarded).

---

## Resolved unknowns summary

| Technical Context item | Resolution |
|---|---|
| Tray mechanism on Linux/Wayland | SNI + DBusMenu over the session bus (D1) |
| Which D-Bus client | `Tmds.DBus.Protocol` 0.21.3, trim-compatible (D2) |
| Backend selection | SNI first, legacy `Gtk.StatusIcon` fallback, never both (D3) |
| Where to attach | `Program.cs` after `ApplicationContext.Configure()` (D4) |
| Click semantics | `ItemIsMenu=false`, `Activate` → `ShowAndActivate()` (D5) |
| Menu contents/source of strings | DBusMenu v3, existing `MSG_RESTORE`/`MENU_EXIT` keys (D6) |
| Exit confirmation | `ActiveDownloadCount` + `MainWindow.Confirm` + one new key (D7, D10) |
| Icon data | SVG rasterised at 22/44 px → ARGB32 + tooltip (D8) |
| Failure behaviour | Guarded, logged, hidden, never auto-shown (D9) |
| Old icon removal | Delete the four `StatusIcon` lines in `MainWindow` (D11) |
| Verification | NUnit for pure helpers + desktop quickstart (D12) |

No `[NEEDS CLARIFICATION]` markers remain in the plan.

---

## D13 — Findings from implementation and live verification (2026-09-20)

Recorded because each of these would otherwise be re-discovered the hard way (details and evidence in [tasks.md](./tasks.md) → "Verification results").

| Finding | Detail | Consequence |
|---|---|---|
| The Linux build never logged | `XDM.Gtk.UI.csproj` overrode the SDK's constants with `<DefineConstants>LINUX</DefineConstants>`, dropping `TRACE`; `Log.Debug` is `[Conditional("TRACE")]`, so all logging was compiled out and `~/.xdm-app-data/log.txt` was never written | FR-010 ("record the failure in the application log") was unsatisfiable until `LINUX;TRACE` was set, mirroring `XDM.Wpf.UI.csproj` (`TRACE;WINDOWS`) |
| `MessageWriter` must be passed by `ref` | It is a `ref struct` carrying the write position; passing it by value to a helper leaves the caller's position stale and the reply body is rejected by the daemon (`dbus-broker: Peer … is being disconnected as it sent a message with an invalid body`), which drops the whole connection | All reply helpers take `ref MessageWriter`; the constraint is documented at the top of `SniTrayIcon.cs` and `SniMenu.cs` |
| Host behaviour on primary click | On KDE Plasma the host honours `ItemIsMenu=false` and calls `Activate`, so the window is restored from the tray | FR-003's restore works as specified; the "Restore Window" menu entry remains the fallback for hosts that open the menu instead |
| Wayland focus policy | `ShowAndActivate()` restores and raises the window, but the compositor did not grant keyboard focus for a tray-initiated request (no activation token available) | FR-003/SC-002 are satisfied for "shown/restored"; the focus half depends on compositor policy and should be verified on X11 when possible |
| Trimming is safe | The published trimmed build (`PublishTrimmed`, `TrimMode=Link`) registers the item, answers `Properties.GetAll` and serves the DBusMenu unchanged | No `TrimmerRootAssembly`/`DynamicDependency` needed for the tray code |
| `-1` arguments in CLI tools | `gdbus call`/`busctl call` parse `-1` (the "all depths" argument of `GetLayout`) as an option | Use `dbus-send … int32:-1` when checking the menu layout by hand (noted in [quickstart.md](./quickstart.md)) |
