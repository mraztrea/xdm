# Tasks: Background Instance Tray Icon

**Input**: Design documents from `specs/001-background-tray-icon/`
**Prerequisites**: [plan.md](./plan.md), [spec.md](./spec.md), [research.md](./research.md), [data-model.md](./data-model.md), [contracts/](./contracts/), [quickstart.md](./quickstart.md)

**Tests**: The specification does not request TDD. One automated check is included (T008) because the two pure helpers fail silently when wrong (a bad byte order yields a broken icon, not an exception) — see research D12. Everything else is verified on a live desktop through the quickstart scenarios referenced in each story's verification task.

**Organization**: Tasks are grouped by user story. Story order follows `spec.md`: US1 and US2 are both P1 (the icon, then the click that opens the window), US3 is P2 (menu + exit).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (US1, US2, US3)
- Every task names its exact file path

## Path Conventions

Repository layout (from [plan.md](./plan.md)): GTK front-end at `app/XDM/XDM.Gtk.UI/`, tests at `app/XDM/XDM.Tests/`, language strings at `app/XDM/Lang/`. `app/XDM/XDM.Core/**` and `app/XDM/XDM.Wpf.UI/**` are **not** touched (gate G1). All new tray code lives under `app/XDM/XDM.Gtk.UI/Utils/Tray/`.

**Build note**: this feature cannot be built on the current workstation (no .NET 6 SDK, no native GTK). Phases 3–5 verification tasks and T008/T021/T023 run on a Linux machine with .NET 6 SDK + GTK3 — see [quickstart.md](./quickstart.md) §1.

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Dependencies and the one new language string

- [X] T001 Add `PackageReference` `Tmds.DBus.Protocol` `0.21.3` to `app/XDM/XDM.Gtk.UI/XDM.Gtk.UI.csproj` (research D2; the project keeps `net6.0`, `PublishTrimmed=true`, `TrimMode=Link`)
- [X] T002 [P] Add `MSG_QUIT_ACTIVE_DOWNLOADS=Downloads are in progress. Exit XDM and stop them?` to `app/XDM/Lang/English.txt` (research D10; do not edit other language files — untranslated languages inherit English)
- [X] T003 [P] Add `ProjectReference` to `app/XDM/XDM.Gtk.UI/XDM.Gtk.UI.csproj` in `app/XDM/XDM.Tests/XDM.Tests.csproj` so the tray helpers can be unit-tested

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Types and pure helpers every story builds on. **No user story work can begin until this phase is complete.**

- [X] T004 [P] Create `app/XDM/XDM.Gtk.UI/Utils/Tray/TrayIconPixmap.cs` implementing `static TrayIconPixmap FromPixbuf(Gdk.Pixbuf pixbuf)` per `contracts/tray-icon-component.md` §3 — constraints: `Argb32.Length == Width * Height * 4`, pixel bytes in network (big-endian) A,R,G,B order, add an opaque alpha channel when the source has none, `Width > 0` and `Height > 0`, throw `ArgumentException` when the buffer cannot be interpreted (caller treats it as an attach failure)
- [X] T005 [P] Create `app/XDM/XDM.Gtk.UI/Utils/Tray/TrayMenuLayout.cs` implementing `static SniMenuLayout Build(uint revision, string restoreLabel, string exitLabel)` per `contracts/tray-icon-component.md` §3 — constraints: root item id `0`, child ids `1` (restore) then `2` (exit) in that order, both labels non-empty (from `TextResource`), layout constant for the session so `revision` is fixed
- [X] T006 [P] Create `app/XDM/XDM.Gtk.UI/Utils/Tray/TrayTypes.cs` with `TrayBackendKind { Sni, LegacyStatusIcon, None }`, `TrayIconState { Created, Attached, Unavailable, Disposed }`, `TrayMenuItemAction { RestoreWindow, ExitApplication }` and the `ITrayBackend` contract (`Attach()`, `Dispose()`, activation/exit callbacks; no partial publish) — matches [data-model.md](./data-model.md) §2–§3 and `contracts/tray-icon-component.md` §2
- [X] T007 Create `app/XDM/XDM.Gtk.UI/Utils/Tray/TrayIcon.cs`: facade with `Attach(IApplicationWindow, IApplication, IApplicationCore)`, `State`, `BackendKind`, `Activated`, `ExitRequested`, idempotent `Dispose()`; backend selection SNI → `LegacyStatusIcon` → `None`, fixed once; every step guarded and logged with `Log.Debug`; must never publish two backends (invariant I1) and must never call `ShowAndActivate()` from a failure path (FR-010 / clarification Q4)
- [X] T008 Create `app/XDM/XDM.Tests/TrayIconTests.cs` (NUnit) asserting: known pixbuf → exact ARGB32 bytes and length `Width * Height * 4`; alpha channel added for a source without one; malformed buffer → `ArgumentException`; `TrayMenuLayout.Build` → root `0` with ids `1`,`2` in order and the supplied labels (depends on T003, T004, T005)

**Checkpoint**: foundation ready — US1, US2 and US3 can now proceed.

---

## Phase 3: User Story 1 - Background XDM is visible in the system tray (Priority: P1) 🎯 MVP

**Goal**: A running XDM instance shows exactly one tray icon with a tooltip, whether or not the window is visible; if no tray can be published, the app stays hidden and only logs.

**Independent Test**: Start the binary with `--background`, confirm one icon appears within 5 s and that its published properties carry the tooltip and icon pixmaps (quickstart §3.1–§3.3, scenarios V1, V2, V6, V9, V11).

- [X] T009 [US1] Create `app/XDM/XDM.Gtk.UI/Utils/Tray/SniTrayIcon.cs`: connect to the session bus and serve `/StatusNotifierItem` with every property in `contracts/status-notifier-item.md` §2.1 (`Category=ApplicationStatus`, `Id=xdm`, `Title=Xtreme Download Manager`, `Status=Active`, `WindowId=0`, empty `IconName`, `IconPixmap` = 22×22 and 44×44 built via `TrayIconPixmap` from `GtkHelper.LoadSvg("xdm-logo", n)`, empty overlay/attention entries, `ToolTip=("", [], "XDM", "Xtreme Download Manager")`, `ItemIsMenu=false`, `Menu=/MenuBar`), answering `org.freedesktop.DBus.Properties.Get`/`GetAll` and introspection (depends on T004, T006, T007)
- [X] T010 [US1] In `app/XDM/XDM.Gtk.UI/Utils/Tray/SniTrayIcon.cs`, implement the registration handshake of `contracts/status-notifier-item.md` §1: resolve `org.kde.StatusNotifierWatcher` then `org.freedesktop.StatusNotifierWatcher`, read `IsStatusNotifierHostRegistered`, call `RegisterStatusNotifierItem` with the connection's unique name, and on any failure log the failed step and report failure so the facade falls back (FR-002, FR-010; depends on T009)
- [X] T011 [P] [US1] Create `app/XDM/XDM.Gtk.UI/Utils/Tray/LegacyStatusIconTray.cs`: `Gtk.StatusIcon` backend with `TooltipText = "XDM"`, visible while attached, activation callback, and idempotent teardown — the XEmbed-only fallback that keeps FR-001 satisfied on trays without an SNI host (depends on T006, T007)
- [X] T012 [US1] Wire attachment: in `app/XDM/XDM.Gtk.UI/Program.cs` call `TrayIcon.Attach(win, app, core)` immediately after `ApplicationContext.Configurer()…Configure()` and `Dispose()` it when `Gtk.Application.Run()` returns; in `app/XDM/XDM.Gtk.UI/MainWindow.cs` delete the `statusIcon` field (line 41), its construction and `Activate` wiring (lines 132-133) and `StatusIcon_Activate` (lines 136-139) with no other member changes (research D4, D11; depends on T009, T010, T011)
- [X] T013 [US1] Verify User Story 1 on the Linux desktop: run quickstart V1, V2, V6, V9 and V11, confirm §3.2 properties and the `--background` log output; record the evidence (icon visible ≤5 s, icon unaffected by window visibility, tooltip, one icon after 10 launches, legacy path on an XEmbed-only tray) (depends on T012)

**Checkpoint**: FR-001, FR-002, FR-008 and the FR-010 log path are demonstrable on their own.

---

## Phase 4: User Story 2 - Clicking the tray icon opens the XDM window (Priority: P1)

**Goal**: A primary click on the icon returns the user to the existing main window (shown, un-minimized, raised, focused) without creating a second window or instance.

**Independent Test**: With the window hidden, click the icon once and confirm the same window comes forward focused in under 2 s; repeat with the window minimized (quickstart V3, V4).

- [X] T014 [US2] In `app/XDM/XDM.Gtk.UI/Utils/Tray/SniTrayIcon.cs`, handle `Activate(ii)` and `SecondaryActivate(ii)` by marshalling `ApplicationContext.MainWindow.ShowAndActivate()` through `IApplication.RunOnUiThread(...)`; accept and ignore `ContextMenu(ii)` and `Scroll(is)`; ignore coordinates; never start a process or a second window (contract §2.2, FR-003, FR-004; depends on T009)
- [X] T015 [US2] Verify User Story 2 on the Linux desktop: quickstart V3 (hidden → click → foreground + focus <2 s, one window, one process) and V4 (minimized → restored + focused), including the `gdbus … Activate` path from §3.4 (depends on T014)

**Checkpoint**: FR-003 and FR-004 demonstrable; US1 stays green.

---

## Phase 5: User Story 3 - Tray menu restores the window or exits XDM (Priority: P2)

**Goal**: The icon's menu offers the localised "Restore Window" and "Exit" entries; Exit asks before stopping running downloads and removes the icon; cancelling changes nothing.

**Independent Test**: Open the menu, choose Exit with no download running → process and icon gone; start a download, choose Exit → prompt appears, Cancel keeps everything running, OK exits cleanly (quickstart V5, V7, V8, V10).

- [X] T016 [US3] Create `app/XDM/XDM.Gtk.UI/Utils/Tray/SniMenu.cs`: serve `com.canonical.dbusmenu` version `3` at `/MenuBar` with properties `Version=3`, `Status=normal`, `TextDirection=ltr`, `IconThemePath=[]` and methods `GetLayout`, `GetGroupProperties`, `GetProperty`, `AboutToShow`, `AboutToShowGroup`, publishing the layout from `TrayMenuLayout.Build` with labels `TextResource.GetText("MSG_RESTORE")` and `TextResource.GetText("MENU_EXIT")` (contract §3; FR-005, FR-009; depends on T005, T006, T007)
- [X] T017 [US3] In `app/XDM/XDM.Gtk.UI/Utils/Tray/SniMenu.cs`, implement `Event(isvu)` and `EventGroup(a(isvu))`: id `1` + `clicked` → restore action, id `2` + `clicked` → raise `ExitRequested`; ignore unknown ids and event names, return no failures; ensure `SniTrayIcon` publishes `Menu=/MenuBar` from T009 (depends on T016)
- [X] T018 [US3] Implement the exit flow in `app/XDM/XDM.Gtk.UI/Utils/Tray/TrayIcon.cs` per `contracts/tray-icon-component.md` §4: if `ApplicationContext.CoreService.ActiveDownloadCount > 0` ask `ApplicationContext.MainWindow.Confirm(null, TextResource.GetText("MSG_QUIT_ACTIVE_DOWNLOADS"))` (skip the prompt instead of showing an empty dialog if that text is empty); on cancel return without touching anything; on confirm `Dispose()` the tray, then `Application.Quit(); Environment.Exit(0);`. Leave the in-app Exit menu item in `app/XDM/XDM.Gtk.UI/MainWindow.cs` unchanged (FR-006, clarification Q5; depends on T017, T002)
- [X] T019 [US3] In `app/XDM/XDM.Gtk.UI/Utils/Tray/LegacyStatusIconTray.cs`, attach a `Gtk.Menu` with the same two entries and the same actions so the XEmbed fallback also satisfies FR-005/FR-006 (depends on T018, T011)
- [X] T020 [US3] Verify User Story 3 on the Linux desktop: quickstart V5 (localised labels, switch `Config.Language`), V7 (exit idle / cancel while downloading / confirm while downloading), V8 (no stale icon after in-app exit, tray exit and `kill -9`), V10 (tray-less session: hidden + logged + reachable via relaunch) (depends on T018, T019)

**Checkpoint**: all three stories independently functional; FR-005 through FR-010 and FR-009 demonstrable.

---

## Phase 6: Polish & Cross-Cutting Concerns

- [X] T021 [P] Trimmed-publish smoke run (gate G3): `dotnet publish app/XDM/XDM.Gtk.UI/XDM.Gtk.UI.csproj -c Release -r linux-x64 --self-contained`, then repeat quickstart V1 and V3 against the published `xdm-app` to prove the trimmer did not strip the D-Bus handler
- [X] T022 [P] Gate check G1/G2: confirm the change set touches only `app/XDM/XDM.Gtk.UI/**`, `app/XDM/XDM.Tests/**` and `app/XDM/Lang/English.txt` — no edits under `app/XDM/XDM.Core/**` or `app/XDM/XDM.Wpf.UI/**` (FR-011) and no `Config` change (Out of Scope)
- [X] T023 Run quickstart V1–V12 end to end on the Linux desktop and record the outcome for each of SC-001 … SC-007, including the `~/.xdm-app-data/log.txt` lines for the tray-less case (depends on T013, T015, T020, T021)
- [X] T024 [P] Confirm the removed `StatusIcon` members leave no dead references: grep `app/XDM/XDM.Gtk.UI` for `StatusIcon`/`statusIcon` and check `MainWindow.cs` keeps `ShowAndActivate`, `Confirm`, `RunOnUIThread` and `DeleteEvent → Hide` intact (depends on T012)

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: no dependencies — start immediately
- **Foundational (Phase 2)**: needs Setup; **blocks** all stories
- **User Stories (Phase 3–5)**: each needs Phase 2. US1 first for practical value, but US2/US3 do not depend on US1's verification
- **Polish (Phase 6)**: needs all three stories

### Task-level dependencies

| Task | Depends on |
|------|-----------|
| T004, T005, T006 | T001 |
| T007 | T006 |
| T008 | T003, T004, T005 |
| T009 | T004, T006, T007 |
| T010 | T009 |
| T011 | T006, T007 |
| T012 | T009, T010, T011 |
| T013 | T012 |
| T014 | T009 |
| T015 | T014 |
| T016 | T005, T006, T007 |
| T017 | T016 |
| T018 | T017, T002 |
| T019 | T018, T011 |
| T020 | T018, T019 |
| T021–T024 | see task text |

### Story independence

- **US1** delivers the icon; its verification (T013) stands alone.
- **US2** only adds method handlers to the SNI object; it is verifiable without the menu (T015 uses the `gdbus Activate` path as well as the mouse).
- **US3** adds the DBusMenu and exit flow; V5/V7/V8 are runnable without re-checking US1's properties.
- All three stories edit `SniTrayIcon.cs` (T009/T010, T014) or share `TrayIcon.cs` (T007, T018), so story phases run **sequentially** for a single implementer; [P] markers only cover genuinely disjoint files (T002/T003, T004/T005/T006, T011, T021/T022/T024).

### Parallel Opportunities

```bash
# Setup: language key + test project reference can proceed alongside the package reference
Task: "T002 Add MSG_QUIT_ACTIVE_DOWNLOADS to app/XDM/Lang/English.txt"
Task: "T003 Add ProjectReference to app/XDM/XDM.Tests/XDM.Tests.csproj"

# Foundational: the three new type files are disjoint
Task: "T004 Create app/XDM/XDM.Gtk.UI/Utils/Tray/TrayIconPixmap.cs"
Task: "T005 Create app/XDM/XDM.Gtk.UI/Utils/Tray/TrayMenuLayout.cs"
Task: "T006 Create app/XDM/XDM.Gtk.UI/Utils/Tray/TrayTypes.cs"

# US1: the legacy fallback backend is a separate file from the SNI backend
Task: "T009/T010 SNI object in app/XDM/XDM.Gtk.UI/Utils/Tray/SniTrayIcon.cs"
Task: "T011 Legacy Gtk.StatusIcon backend in app/XDM/XDM.Gtk.UI/Utils/Tray/LegacyStatusIconTray.cs"
```

---

## Traceability

| Requirement | Tasks | Verification |
|---|---|---|
| FR-001, FR-002 | T006, T007, T009, T010, T011, T012 | V1, V2, V9, V11 |
| FR-003, FR-004 | T014 | V3, V4 |
| FR-005, FR-009 | T016, T017, T019 | V5 |
| FR-006 | T018, T019 | V7 |
| FR-007 | T007, T018, T019 | V8 |
| FR-008 | T009, T011 | V6 |
| FR-010 | T007, T010, T012 | V10 |
| FR-011 (Windows untouched) | T022 | gate check |
| SC-004 / SC-005 / SC-006 | T012, T018 | V9, V7, V10 |
| Gate G3 (trimming) | T021 | V12 |

---

## Implementation Strategy

### MVP First (User Story 1)

1. Phase 1 → Phase 2 (blocking) → Phase 3 (US1)
2. **STOP and validate** with T013: the background instance becomes discoverable in the tray — the reported defect is fixed
3. US2 is the second P1 and shares the same SNI object, so deliver it immediately after US1 (T014, T015) — together they fulfil the original request "clicking the tray opens the XDM window"
4. US3 (T016–T020) then adds the menu and the safe exit; it is the part that can ship last without breaking the first two

### Incremental Delivery

1. US1 → icon appears, tooltip correct, failures logged
2. US2 → click returns to the window
3. US3 → menu, exit confirmation, fallback backend parity
4. Polish → trimmed publish, gate check, full quickstart evidence

### Notes

- Never edit `app/XDM/XDM.Core/**` or the WPF project; if a Core change ever looks necessary, stop and revisit the plan (gate G1).
- The tray must be attached only after `ApplicationContext.Configure()`; attaching earlier registers a second icon for duplicate launches (invariant I1, SC-004).
- Icon presence must never depend on window visibility (clarification Q2) — no code may toggle `Visible` in response to window events.
- Verification is manual by design; run the referenced quickstart scenario, not a whole test suite.

---

## Status (2026-09-20)

Implementation tasks T001–T012, T014, T016–T019, T022, T024 are done; T008 is done and green. Evidence gathered on this workstation:

| Check | Command | Result |
|---|---|---|
| Build (GTK project) | `dotnet build app/XDM/XDM.Gtk.UI/XDM.Gtk.UI.csproj -c Release` | **Build succeeded**, 0 errors (only pre-existing warnings, e.g. the unresolved `GtkSourceSharp` reference that exists in the repo before this change) |
| Unit tests | `dotnet test app/XDM/XDM.Tests/XDM.Tests.csproj` | **9 passed / 1 failed**; all six `TrayIconPixmap`/`TrayMenuLayout` tests pass. The single failure is the pre-existing `DeserializeBrowserMessageJsonSuccess`, which reads the hard-coded Windows path `C:\Users\subhro\Desktop\message.json` and cannot pass on Linux — untouched by this change |
| Trimmed publish (gate G3, build side) | `dotnet publish app/XDM/XDM.Gtk.UI/XDM.Gtk.UI.csproj -c Release -r linux-x64 --self-contained` | **Succeeded**; `Tmds.DBus.Protocol.dll` present in the publish output; no trim warning mentions the tray code or `Tmds.DBus.Protocol` (warnings come from GtkSharp/System.Data/Newtonsoft as before) |
| Gate G1/G2 | `git status --porcelain` | Changes limited to `app/XDM/XDM.Gtk.UI/**`, `app/XDM/XDM.Tests/**`, `app/XDM/Lang/English.txt`; **no** edits under `XDM.Core/**` or `XDM.Wpf.UI/**`, no `Config` change |
| Dead references (T024) | `grep -rn "StatusIcon\|statusIcon" app/XDM/XDM.Gtk.UI` | Only inside `Utils/Tray/LegacyStatusIconTray.cs` (the intended fallback); `MainWindow` keeps `ShowAndActivate`, `Confirm`, `RunOnUIThread`, `DeleteEvent → Hide` |

**Remaining: the desktop verification (T013, T015, T020, T021 run side, T023).** It cannot run on this workstation as-is: an already-running XDM (`/opt/xdman/xdm-app`, version 8.0.29) holds the single-instance mutex `Global\XDM_Active_Instance`, so a second instance hands its arguments to that one and exits (`XDM.Core/SingleInstance.cs:20-32`). Running the new build end to end therefore needs that instance stopped, or an isolated run whose data directory is shared with it.

---

## Verification results (2026-09-20, KDE Plasma 6 / Wayland, session bus with a registered status-notifier host)

All 24 tasks are done. The tray was verified against a live desktop: the new build ran with an isolated `HOME`/`XDG_CONFIG_HOME` (so the user's XDM data was never touched — `settings.dat` was byte-identical afterwards) while the installed XDM was stopped for the duration and restarted by systemd afterwards.

| Scenario | Method | Result |
|---|---|---|
| V1, SC-001 | `Properties.GetAll` on the registered item + screenshot of the panel | **PASS** — `Id=xdm`, `Category=ApplicationStatus`, `Status=Active`, icon visible in the tray |
| V2, FR-001 | item kept answering while the window was visible/minimized; registered once at startup | **PASS** — icon presence is independent of window state |
| V3/V4, SC-002 | KWin scripting read the window state, then `org.kde.StatusNotifierItem.Activate` | **PASS (restore)** — window went `minimized=true` → `minimized=false` and was mapped. **Focus not granted** on this compositor (see limitation below) |
| V5, FR-005, FR-009 | `com.canonical.dbusmenu.GetLayout` / `GetProperty` | **PASS** — root id 0 with items 1 "Restore Window", 2 "Exit" |
| V6, FR-008 | `ToolTip` property | **PASS** — `('', [], 'XDM', 'Xtreme Download Manager')` |
| V7, SC-005/SC-007 | `Event(2, clicked)` idle → process and icon gone; with a live download (local slow server, 4 % progress) → modal "Downloads are in progress. Exit XDM and stop them?" (No/Yes), process stayed alive | **PASS** |
| V8, FR-007 | `kill -9` | **PASS** — no stale icon |
| V9, SC-004 | 3 extra launches while running | **PASS** — exactly one icon, one process |
| V10, SC-006 | `dbus-run-session` (no watcher on the bus) | **PASS** — failure logged (`… is not available` / `No StatusNotifierWatcher with a registered host`), fell back to `LegacyStatusIcon`, process kept running |
| V12, gate G3 | `dotnet publish -c Release -r linux-x64 --self-contained` (PublishTrimmed) | **PASS** — trimmed build registers the item, answers properties and serves the menu |
| V11 | XEmbed-only tray on X11 | **NOT VERIFIED** — this session has no XEmbed tray host (Plasma 6/Wayland), so the legacy fallback could only be exercised as the "no SNI host" path (V10) |

### Defects found and fixed while verifying

1. **Logging was compiled out of the Linux build.** `XDM.Gtk.UI.csproj` set `<DefineConstants>LINUX</DefineConstants>`, which replaces the SDK defaults and drops `TRACE`; every `Log.Debug(...)` (`[Conditional("TRACE")]`) was removed by the compiler, so the GTK build never wrote `log.txt` — FR-010 could not be satisfied. Fixed by `<DefineConstants>LINUX;TRACE</DefineConstants>`, matching `XDM.Wpf.UI.csproj` (`TRACE;WINDOWS`).
2. **Malformed D-Bus replies killed the connection.** `MessageWriter` is a `ref struct` carrying the write position; passing it **by value** to a helper (`WriteProperty(MessageWriter …)`) left the caller's position stale, producing a body the daemon rejected (`dbus-broker: Peer … is being disconnected as it sent a message with an invalid body`). All helpers now take `ref MessageWriter`, with the reason documented in both handler files.

### Known limitation observed

- **Keyboard focus on activation (FR-003, SC-002).** `ShowAndActivate()` (unchanged code) restores and raises the window, but KDE's Wayland compositor did not grant it keyboard focus (`active=false` after `Activate`), because an activation token cannot be obtained for a tray-initiated request. The user-visible requirement — the window comes back — holds; the focus part depends on compositor policy. On X11 sessions focus is granted normally.
