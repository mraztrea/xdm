# Quickstart & Validation: Background Instance Tray Icon

**Feature**: `specs/001-background-tray-icon/spec.md` | **Plan**: [plan.md](./plan.md) | **Contracts**: [contracts/](./contracts/)

Everything here proves the feature on a running desktop. Implementation detail lives in [contracts/](./contracts/) and [data-model.md](./data-model.md).

> **No binary is produced on the current workstation**: it has no `dotnet` SDK and no native GTK, so run these steps on the Linux machine where the GTK build is produced.

## 1. Prerequisites

| Requirement | Check | Notes |
|---|---|---|
| .NET 6 SDK | `dotnet --list-sdks` | The GTK project targets `net6.0` |
| GTK 3 native libraries | `pkg-config --modversion gtk+-3.0` | Fedora `gtk3`, Debian/Ubuntu `libgtk-3-0` |
| A status-notifier host on the session | see §3.1 | KDE Plasma: built in. GNOME: install the AppIndicator extension. XFCE/Cinnamon/MATE: SNI applet enabled |
| Session bus available | `echo $DBUS_SESSION_BUS_ADDRESS` | A session without a bus is the SC-006 path |
| No XDM already running | `pgrep -a xdm-app` | The app is single-instance; a second launch only hands its args to the first |

## 2. Build and run

```bash
# build the GTK front-end
dotnet build app/XDM/XDM.Gtk.UI/XDM.Gtk.UI.csproj -c Release

# run it the way autostart does (no window, tray icon expected)
XDM_DEBUG_MODE=1 app/XDM/XDM.Gtk.UI/bin/Release/net6.0/xdm-app --background

# in another terminal: the log this feature writes to
tail -n 50 ~/.xdm-app-data/log.txt
```

Trimmed-publish smoke run (the project publishes with `PublishTrimmed=true`):

```bash
dotnet publish app/XDM/XDM.Gtk.UI/XDM.Gtk.UI.csproj -c Release -r linux-x64 --self-contained
XDM_DEBUG_MODE=1 <publish-dir>/xdm-app --background
```

Unit checks for the pure helpers:

```bash
dotnet test app/XDM/XDM.Tests/XDM.Tests.csproj
```

## 3. Inspecting the published tray item (SNI backend)

### 3.1 Is a host registered?

```bash
WATCHER=org.kde.StatusNotifierWatcher
gdbus call --session --dest $WATCHER --object-path /StatusNotifierWatcher \
  --method org.freedesktop.DBus.Properties.Get $WATCHER IsStatusNotifierHostRegistered
# expected: (<true>,)   -> SNI backend is used
# (<false>,) or "name has no owner" -> SC-006 path (log only, no window)
```

### 3.2 Our item and its properties

```bash
SNI=$(busctl --user list --no-legend | awk '/StatusNotifierItem/{print $1; exit}')
gdbus call --session --dest "$SNI" --object-path /StatusNotifierItem \
  --method org.freedesktop.DBus.Properties.GetAll org.kde.StatusNotifierItem
# expected: Status='Active', Id='xdm', ItemIsMenu=false, Menu=/MenuBar,
#           ToolTip title 'XDM', IconPixmap with 22x22 and 44x44 entries
```

### 3.3 The menu

```bash
gdbus call --session --dest "$SNI" --object-path /MenuBar \
  --method com.canonical.dbusmenu.GetLayout 0 -1 "[]"
# expected: two items — 1 = the localised "Restore Window", 2 = the localised "Exit"
```

### 3.4 Driving the icon without a mouse

```bash
# same code path as a primary click
gdbus call --session --dest "$SNI" --object-path /StatusNotifierItem \
  --method org.kde.StatusNotifierItem.Activate 0 0

# same code path as choosing "Exit" in the menu (variant syntax varies by gdbus version;
# the GUI right-click route is the primary way to check this)
gdbus call --session --dest "$SNI" --object-path /MenuBar \
  --method com.canonical.dbusmenu.Event 2 clicked "<@v></@v>" 0
```

## 4. Validation scenarios

| # | Criterion | Steps | Expected result |
|---|-----------|-------|-----------------|
| V1 | SC-001, FR-001, FR-002 | Start with `--background`, watch the panel's status area for 5 s, then check §3.2 | Exactly one XDM icon appears within 5 s; properties match §3.2 |
| V2 | FR-001, clarification Q2 | With the icon visible, open the window from the tray; watch the panel | The icon stays present while the window is open (no appear/disappear) |
| V3 | FR-003, FR-004, SC-002 | Hide the window (close it), then click the icon once | The existing main window returns to the foreground, focused, in under 2 s; no second window or process |
| V4 | FR-003 | Minimize the window, then click the icon | The window is restored from minimized and focused |
| V5 | FR-005, FR-009 | Open the icon's context menu | Two entries: the localised "Restore Window" and "Exit" (switch `Config.Language` and restart to confirm the labels follow the language) |
| V6 | FR-008 | Hover the icon | A tooltip naming XDM |
| V7 | FR-006, SC-005, SC-007 | (a) with no download running choose Exit; (b) start a download, choose Exit, answer **Cancel**; (c) start a download, choose Exit, answer **OK** | (a) exits immediately, no icon left; (b) instance keeps running, downloads continue, icon still there; (c) prompt shown, then process and icon gone within 5 s and a relaunch works |
| V8 | FR-007 | Exit through the in-app menu item and through the tray; also kill the process with `kill -9` | No stale icon remains in any case |
| V9 | FR-001, SC-004 | Launch the binary 10 times in a row while an instance runs | Exactly one icon and exactly one window; the extra launches only surface the existing window |
| V10 | FR-010, SC-006 | `pkill xdm-app`, then `dbus-run-session -- env XDM_DEBUG_MODE=1 <binary> --background` (private bus, no watcher), then run `<binary>` again | No window opens on start, the log records the failed step, the process keeps running, and the second launch surfaces the window |
| V11 | FR-001 (legacy path) | On an X11 desktop whose tray is XEmbed-only (no SNI host), start with `--background` | One icon via `Gtk.StatusIcon`, with tooltip and the same two menu entries |
| V12 | Gate G3 | Run the trimmed publish from §2 and repeat V1/V3 | Identical behaviour to the untrimmed build (no trimming casualties) |

## 5. Troubleshooting

| Symptom | Likely cause | Check |
|---|---|---|
| No icon, log shows the watcher lookup failing | No status-notifier host (bare GNOME/Wayland without the extension) | §3.1; this is the SC-006 path, not a defect |
| No icon and no log lines at all | `XDM_DEBUG_MODE=1` not set, so nothing is written to `~/.xdm-app-data/log.txt` | Rerun with the variable set |
| Two icons while a download is running | Both backends published (must never happen) | `busctl --user list` plus the panel; report as a defect against [data-model.md](./data-model.md) invariant I1 |
| Icon present but a click opens the menu instead of the window | Host ignores `ItemIsMenu=false` | Expected on those hosts; "Restore Window" is the first menu entry (see [plan.md](./plan.md) risks) |
| Click does nothing | Window already visible and focused | Focus another window first, then click |
