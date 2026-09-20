# Implementation Plan: Background Instance Tray Icon

**Branch**: `001-background-tray-icon` | **Date**: 2026-09-20 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `specs/001-background-tray-icon/spec.md`

## Summary

The Linux GTK build (`app/XDM/XDM.Gtk.UI`, assembly `xdm-app`) must show a system-tray icon for the whole lifetime of a running XDM instance: primary click returns the user to the main window, the icon's menu offers "Restore Window" and "Exit", Exit asks for confirmation while downloads are running, and every tray failure is logged instead of crashing or auto-showing the window.

Today the GTK build creates a deprecated `Gtk.StatusIcon` inside the `MainWindow` constructor (`app/XDM/XDM.Gtk.UI/MainWindow.cs:41,132-133`). That is the legacy XEmbed protocol: GNOME Shell (X11 and Wayland), and any Wayland session, cannot display it, and it carries no tooltip and no menu — so the P1 behaviours of the spec are not met. The plan replaces it with a tray component that speaks the freedesktop **StatusNotifierItem** protocol plus **com.canonical.dbusmenu** (v3) over the session bus via `Tmds.DBus.Protocol`, keeps a mutually exclusive `Gtk.StatusIcon` fallback for XEmbed-only trays on X11 (so existing behaviour on those desktops is not lost and exactly one icon is ever shown), and wires activation/exit to the existing `IApplicationWindow`/`IApplicationCore` APIs.

## Technical Context

**Language/Version**: C# 10 / .NET 6 (`net6.0`). Tray code belongs to `app/XDM/XDM.Gtk.UI` (`AssemblyName=xdm-app`, `DefineConstants=LINUX`, `PublishTrimmed=true`, `TrimMode=Link`, `InvariantGlobalization=true`).
**Primary Dependencies**: existing `GtkSharp 3.24.24.38`; **new** `Tmds.DBus.Protocol 0.21.3` (net6.0 target group; upstream documents NativeAOT/trimming compatibility; transitive `System.IO.Pipelines 8.0.0`). No native library is added.
**Storage**: none new. No configuration key is added (spec Out of Scope forbids a tray setting). Menu labels come from `app/XDM/Lang/*.txt` (`KEY=Value`), one new key `MSG_QUIT_ACTIVE_DOWNLOADS` added to `app/XDM/Lang/English.txt`; other languages inherit it (English is loaded first in `TextResource`'s static constructor and the selected language overrides by key).
**Testing**: NUnit project `app/XDM/XDM.Tests` (net6.0) for the pure helpers (ARGB32 icon encoding, DBusMenu layout shape) plus the manual desktop validation in [quickstart.md](./quickstart.md). CI (`.github/workflows/xdm-wpf-build.yml`) only restores/builds/tests `app/XDM/XDM.Wpf.UI` on windows-latest, so it does not cover the GTK project; GTK verification is local.
**Target Platform**: Linux desktop, X11 and Wayland, with a StatusNotifierItem host present (KDE Plasma natively; GNOME Shell with the AppIndicator extension; XFCE/Cinnamon/MATE SNI applets). XEmbed-only trays on X11 are covered by the legacy fallback backend.
**Project Type**: desktop application, multi-project (WPF UI + GTK UI + shared `XDM.Core` shared project) — this feature touches the GTK UI only.
**Performance Goals**: tray icon registered within 5 s of a background start (SC-001); window in the foreground and focused under 2 s after a click (SC-002).
**Constraints**: `XDM.Core` public API and the WPF build stay untouched (FR-011, no regression); trimmed publish must keep working; the tray is registered once and never toggles with window visibility (FR-001); exactly one icon per user session (SC-004); no new user setting (spec Out of Scope); when no tray host exists the app stays hidden and only logs (FR-010). The current workstation has no `dotnet` SDK and no native GTK, so building and desktop verification must run on a Linux machine with the .NET 6 SDK and GTK3 installed.
**Scale/Scope**: one instance per user session, one icon, a two-item menu, ~5 new source files plus one language key and one test file.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

`.specify/memory/constitution.md` does not exist in this repository, so no project constitution gates are defined and none are violated. The spec-derived gates below are applied instead:

| Gate | Status | Evidence |
|------|--------|----------|
| G1 — Windows/WPF build untouched (FR-011) | PASS | All source edits are inside `app/XDM/XDM.Gtk.UI`; the only shared file touched is `app/XDM/Lang/English.txt` (additive key) |
| G2 — No new user setting (Out of Scope) | PASS | No `Config` change; icon presence is unconditional |
| G3 — Trimmed publish keeps working | PASS (design) | `Tmds.DBus.Protocol` is documented trim/NativeAOT compatible; the exported handler is instantiated directly, not reflectively |
| G4 — Exactly one icon per session (FR-001/SC-004) | PASS (design) | Attach happens after `SingleInstance.Ensure()` (duplicates exit before registering); SNI and legacy backends are mutually exclusive |
| G5 — No new native dependency | PASS | Pure managed D-Bus client; the rejected alternative (`libayatana-appindicator3`) would have added a runtime native dependency |
| G6 — Failure never crashes and never auto-shows the window (FR-010) | PASS (design) | Every attach step is guarded and logged via `Log.Debug`; no code path calls `ShowAndActivate()` from the failure handler |

**Post-Phase 1 re-check**: unchanged — design adds no new gate violations (see [research.md](./research.md) D2/D3 and [contracts/](./contracts/)).

## Project Structure

### Documentation (this feature)

```text
specs/001-background-tray-icon/
├── plan.md              # This file
├── spec.md              # Feature specification
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/           # Phase 1 output
│   ├── status-notifier-item.md
│   └── tray-icon-component.md
├── checklists/
│   └── requirements.md
└── tasks.md             # Phase 2 output (/speckit.tasks — not created here)
```

### Source Code (repository root)

```text
app/XDM/
├── XDM.Gtk.UI/
│   ├── Program.cs                        # attach the tray after Configure(), before Gtk.Application.Run()
│   ├── MainWindow.cs                     # drop the StatusIcon field/ctor lines (41, 132-133, 136-139)
│   ├── Utils/
│   │   └── Tray/
│   │       ├── TrayIcon.cs               # backend-agnostic facade: Attach/Dispose + Activated/ExitRequested
│   │       ├── TrayBackendKind.cs        # Sni | LegacyStatusIcon | None
│   │       ├── SniTrayIcon.cs            # StatusNotifierItem object + watcher registration
│   │       ├── SniMenu.cs                # com.canonical.dbusmenu v3 menu at /MenuBar
│   │       ├── LegacyStatusIconTray.cs   # Gtk.StatusIcon fallback (XEmbed/X11), tooltip + Gtk.Menu
│   │       ├── TrayIconPixmap.cs         # Gdk.Pixbuf → ARGB32 network-order bytes (pure)
│   │       └── TrayMenuLayout.cs         # DBusMenu layout shape builder (pure)
│   └── XDM.Gtk.UI.csproj                 # + PackageReference Tmds.DBus.Protocol
├── XDM.Tests/
│   ├── XDM.Tests.csproj                  # + ProjectReference to XDM.Gtk.UI
│   └── TrayIconTests.cs                  # ARGB32 encoding + menu layout
└── Lang/
    └── English.txt                       # + MSG_QUIT_ACTIVE_DOWNLOADS
```

**Structure Decision**: single-project change inside the existing GTK desktop project; a small `Utils/Tray/` folder keeps the protocol code out of the 1,300-line `MainWindow.cs`, following the existing `Utils/` convention. `XDM.Core` is not modified: the tray reuses `ApplicationContext.MainWindow.ShowAndActivate()` (`IApplicationWindow`, `app/XDM/XDM.Core/UI/IApplicationWindow.cs:90`), `IApplicationWindow.RunOnUIThread` and `IApplicationWindow.Confirm` (implemented at `app/XDM/XDM.Gtk.UI/MainWindow.cs:1063-1081`), `ApplicationContext.CoreService.ActiveDownloadCount` (`app/XDM/XDM.Core/IApplicationCore.cs:37-39`) and `TextResource.GetText` — no new Core API is required.

## Phased Delivery

| Phase | Deliverable | Spec coverage |
|-------|-------------|---------------|
| A | Pure helpers: `TrayIconPixmap` (ARGB32) + `TrayMenuLayout`, with NUnit tests | FR-008, FR-009 (menu shape), supports FR-001 |
| B | `SniTrayIcon`: object at `/StatusNotifierItem`, properties, `Activate`/`SecondaryActivate`, watcher registration and teardown; `TrayIcon` facade with `Dispose` | FR-001, FR-002, FR-003, FR-004, FR-008 |
| C | `SniMenu`: DBusMenu v3 at `/MenuBar`, 2 items, `Event` dispatch | FR-005, FR-009 |
| D | Wiring: attach in `Program.cs` after `Configure()`, delete the `MainWindow` `StatusIcon`, exit flow with confirmation | FR-006, FR-007, FR-010 |
| E | `LegacyStatusIconTray` fallback + host detection/selection in `TrayIcon` | FR-001 (fallback coverage), preserves current XEmbed behaviour |
| F | Desktop verification per [quickstart.md](./quickstart.md), trimmed publish smoke run | SC-001 … SC-007 |

## Risks & Mitigations

| Risk | Impact | Mitigation |
|------|--------|-----------|
| GNOME's AppIndicator extension may open the menu instead of sending `Activate` on primary click | FR-003 best-effort on that host | `ItemIsMenu=false` requests activation; "Restore Window" is the first menu item so one extra click always works; FR-003 verified on hosts that send `Activate` (KDE, XFCE) and recorded per host in quickstart |
| Trimmer strips something the D-Bus handler needs | Tray silently absent in published builds | Handler type is instantiated directly (no reflection); verify with a `dotnet publish` (PublishTrimmed) smoke run in Phase F |
| Session bus unavailable / registration rejected | No icon | Guarded, logged to `Config.AppDir/log.txt`, fall back to the legacy backend if possible, never auto-show the window (FR-010, SC-006) |
| ARGB32 byte order wrong → invisible or garbled icon | Icon looks broken | Unit test asserts exact bytes for a known pixbuf in `TrayIconTests` |
| Both backends showing an icon at once | Violates FR-001/SC-004 | One `TrayBackendKind` decided once at startup; SC-004 launch-repetition check in quickstart |
| No CI coverage for the GTK project | Regressions escape CI | Plan requires local build + quickstart evidence; CI file left unchanged |

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| New NuGet dependency (`Tmds.DBus.Protocol`) | StatusNotifierItem and DBusMenu are D-Bus protocols; a managed client is required to speak them | `libayatana-appindicator3` P/Invoke adds a runtime **native** dependency (absent on many systems → app-visible failure), is marked obsolete upstream, and maps primary click to the menu, violating FR-003. Hand-rolled `libdbus-1` P/Invoke: same native dependency plus several hundred lines of marshalling code. |
| Two tray backends instead of one | FR-001 requires exactly one icon; keeping `Gtk.StatusIcon` for XEmbed-only trays on X11 avoids regressing desktops that show the icon today | SNI-only would remove a currently working icon on XEmbed-only trays; legacy-only is what fails today on GNOME/Wayland (the reported bug) |
| Protocol plumbing (`SniTrayIcon` + `SniMenu`) instead of a library | No maintained .NET SNI/DBusMenu package targets this stack; `Tmds.DBus.Protocol` covers the wire protocol, the item/menu interfaces are ~2 files of explicit handlers | Reusing the WPF-only `NotifyIcon`/`System.Windows.Forms` tray is impossible on Linux; `Gtk.StatusIcon` alone cannot satisfy FR-003/FR-005 on the target desktops |
