# Contract: StatusNotifierItem surface exposed to the desktop shell

**Feature**: `specs/001-background-tray-icon/spec.md` | **Plan**: [plan.md](../plan.md) | **Research**: [research.md](../research.md)

This is the D-Bus surface `SniTrayIcon`/`SniMenu` (see `contracts/tray-icon-component.md`) publishes when `TrayBackendKind == Sni`. Signatures use D-Bus type notation. Both the `org.kde.*` names (used by real hosts and by GNOME's AppIndicator extension) and the specification's `org.freedesktop.*` aliases are answered on the same object.

## 1. Registration handshake

| Step | Call | Failure handling |
|------|------|------------------|
| 1 | Connect to the session bus | → `Unavailable`, log, try legacy backend |
| 2 | Register object `/StatusNotifierItem` (SNI) and `/MenuBar` (DBusMenu) | → `Unavailable`, log |
| 3 | `org.kde.StatusNotifierWatcher` at `/StatusNotifierWatcher`, property `IsStatusNotifierHostRegistered` (`b`) | Absent/false → `Unavailable`, try legacy backend (SC-006 path) |
| 4 | Method `RegisterStatusNotifierItem(s serviceName)` with this process's unique bus name | Error reply → `Unavailable`, log |
| 5 | Teardown on exit: unregister/close the connection | Host removes the item when the connection drops (FR-007) |

Watcher name resolution order: `org.kde.StatusNotifierWatcher`, then `org.freedesktop.StatusNotifierWatcher` (specification name). The item is served at the fixed path `/StatusNotifierItem` required when a bus name is passed to the watcher.

## 2. `org.kde.StatusNotifierItem` at `/StatusNotifierItem`

### 2.1 Properties (readable, via `org.freedesktop.DBus.Properties.Get`/`GetAll`)

| Property | Signature | Value | Requirement |
|----------|-----------|-------|-------------|
| `Category` | `s` | `ApplicationStatus` | Metadata |
| `Id` | `s` | `xdm` | Stable per application |
| `Title` | `s` | `Xtreme Download Manager` | Metadata |
| `Status` | `s` | `Active` (never `Passive`, so hosts do not hide the icon) | FR-001 |
| `WindowId` | `u` | `0` | No window association required |
| `IconName` | `s` | `""` (empty; pixmaps are authoritative) | FR-008 |
| `IconPixmap` | `a(iiay)` | 22×22 and 44×44 ARGB32 (network byte order) | FR-008 |
| `OverlayIconName` | `s` | `""` | — |
| `OverlayIconPixmap` | `a(iiay)` | `[]` | — |
| `AttentionIconName` | `s` | `""` | — |
| `AttentionIconPixmap` | `a(iiay)` | `[]` | — |
| `AttentionMovieName` | `s` | `""` | — |
| `ToolTip` | `(sa(iiay)ss)` | `("", [], "XDM", "Xtreme Download Manager")` | FR-008 |
| `ItemIsMenu` | `b` | `false` — the item is activatable, not menu-only | FR-003 |
| `Menu` | `o` | `/MenuBar` | FR-005 |

Properties are constant for the session; no `PropertiesChanged` is emitted for them.

### 2.2 Methods

| Method | Signature | Behaviour | Requirement |
|--------|-----------|-----------|-------------|
| `Activate` | `ii` → `` | Marshal `IApplicationWindow.ShowAndActivate()` onto the GTK main thread (show if hidden, un-minimize, raise, focus). Ignore coordinates. | FR-003, FR-004 |
| `SecondaryActivate` | `ii` → `` | Same as `Activate` (middle click is a second way back to the window) | FR-003 (extension) |
| `ContextMenu` | `ii` → `` | Accepted and ignored: the menu is served through `/MenuBar` | FR-005 |
| `Scroll` | `is` → `` | Accepted and ignored | — |

`Activate` never starts a process or window: it calls the same `ShowAndActivate()` implementation used by `--restore-window` (`app/XDM/XDM.Core/ArgsProcessor.cs:124-126`), which reuses the single main window.

### 2.3 Signals

`NewIcon`, `NewToolTip`, `NewStatus`, `NewTitle`, `NewAttentionIcon` are declared in the introspection data for host compatibility but not emitted while the icon is `Active` and constant.

## 3. `com.canonical.dbusmenu` at `/MenuBar`

Version `3`. Layout: root id `0` (`type=root`), children:

| Id | Label | `enabled`/`visible` | Action | Requirement |
|----|-------|---------------------|--------|-------------|
| `1` | `TextResource.GetText("MSG_RESTORE")` | `true`/`true` | `ShowAndActivate()` (same as `Activate`) | FR-005 |
| `2` | `TextResource.GetText("MENU_EXIT")` | `true`/`true` | Tray exit flow (see `tray-icon-component.md` §4) | FR-005, FR-006 |

### 3.1 Properties

| Property | Signature | Value |
|----------|-----------|-------|
| `Version` | `u` | `3` |
| `Status` | `s` | `normal` |
| `TextDirection` | `s` | `ltr` |
| `IconThemePath` | `as` | `[]` |

### 3.2 Methods

| Method | Signature | Behaviour |
|--------|-----------|-----------|
| `GetLayout` | `iias` → `(u a(ia{sv}av))` | Return `(revision, layout)` for the requested parent/recursion depth, including `label`, `enabled`, `visible` on each item |
| `GetGroupProperties` | `aias` → `a(ia{sv})` | Properties for the requested ids |
| `GetProperty` | `is` → `v` | Single property for an id |
| `Event` | `isvu` → `` | `id ∈ {1,2}` with event `clicked` dispatches the action above; unknown id/event is ignored |
| `EventGroup` | `a(isvu)` → `ai` | Process each entry as `Event`; return the ids that failed (empty) |
| `AboutToShow` | `i` → `b` | Return `false` (no lazy population) |
| `AboutToShowGroup` | `ai` → `(ai ai)` | Return `([], [])` |

### 3.3 Signals

`LayoutUpdated(u i)` and `ItemsPropertiesUpdated(a(ia{sv}) a(ia{sv}))` are declared for host compatibility; the layout is static, so neither is emitted during a session.

## 4. Error and degradation contract

| Condition | Observable behaviour | Requirement |
|-----------|----------------------|-------------|
| No session bus, no watcher, no host registered, registration error, icon rasterisation failure | `TrayIconState = Unavailable`, one `Log.Debug` line naming the failed step, process keeps running, window stays hidden (background start), main window still reachable by launching XDM again | FR-010, SC-006 |
| Host only supports the menu (ignores `ItemIsMenu=false`) | Primary click opens the menu; "Restore Window" is the first entry | FR-003 best effort (documented in [plan.md](../plan.md) risks) |
| Item unregistered or connection closed | Host removes the icon; no stale entry remains | FR-007 |
| Duplicate launch while an instance runs | The duplicate exits during `SingleInstance.Ensure()` before any tray attach; one icon remains | FR-001, SC-004 |
