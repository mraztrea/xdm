# Feature Specification: Background Instance Tray Icon

**Feature Branch**: `001-background-tray-icon`
**Created**: 2026-09-20
**Status**: Draft
**Input**: User description: "Hãy sửa code: Nếu có instance XDM chạy nền thì sẽ hiển thị icon ở khay hệ thống. Bấm vào khay đó sẽ mở windows XDM"

## Clarifications

### Session 2026-09-20

- Q: Which platforms must the tray-icon behaviour cover in this change? → A: Linux/GTK only — the Linux desktop build is fixed; the Windows build's existing tray behaviour stays as it is and is smoke-checked only; macOS is out of scope.
- Q: When should the XDM icon be visible in the system tray? → A: Always while XDM runs — the icon is present for the whole lifetime of the instance, including while the main window is visible; it does not appear/disappear as the window is shown, hidden, or minimized, and no user setting is added.
- Q: What must happen when the user chooses Exit from the tray while downloads are still running? → A: Confirm if downloading — exit is immediate when idle, but when downloads are in progress XDM asks for confirmation first and offers cancelling, which leaves the instance running with all downloads continuing.
- Q: If the tray icon cannot be created on a session that started XDM in the background, should XDM show its main window instead or stay hidden and just log the failure? → A: Stay hidden, log only — the instance keeps running without opening a window and the failure is recorded in the log; the main window stays reachable by launching XDM again through the existing single-instance handoff.
- Q: On the Linux build, should the existing in-app Exit menu item also ask for confirmation while downloads are running, or should only the new tray exit do that? → A: Only tray exit confirms — the in-app Exit menu item keeps its current immediate behaviour, so the two exit routes intentionally differ.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Background XDM is visible in the system tray (Priority: P1)

A user starts XDM in the background (at system logon, or by launching it without wanting the window) and closes or never opens the main window. XDM keeps running. The user looks at the system tray (also called the notification area or status area) and sees the XDM icon, which tells them the application is still running and listening for downloads.

**Why this priority**: The icon is the only evidence that a headless instance is alive and the prerequisite for every other tray action. Today on Linux the background instance is invisible and unreachable after the window is closed — the user cannot tell whether XDM is running at all.

**Independent Test**: Start XDM so that no main window is shown, then inspect the system tray of the desktop session; the XDM icon is present and identifies XDM (icon artwork plus a tooltip naming the application).

**Acceptance Scenarios**:

1. **Given** XDM is running with no visible main window, **When** the user inspects the system tray, **Then** exactly one XDM icon is visible there.
2. **Given** XDM was launched in background mode at logon, **When** the desktop session finishes loading, **Then** the XDM icon is visible in the system tray without any user action.
3. **Given** the main window is open and XDM is running, **When** the user hides or minimizes the main window, **Then** the XDM icon is still present in the system tray.
4. **Given** the XDM icon is present, **When** the user hovers over it, **Then** a tooltip names the application (XDM).
5. **Given** the main window is visible on screen, **When** the user inspects the system tray, **Then** the XDM icon is still present and stays present while the window remains open.

---

### User Story 2 - Clicking the tray icon opens the XDM window (Priority: P1)

The user sees the XDM icon in the system tray and clicks it to get back into the application — typically to start a download, check progress, or change settings.

**Why this priority**: This is the core of the request: the tray icon must be a reliable way back into the window. Without it the background instance is a dead end for users who closed the window.

**Independent Test**: With XDM running and the main window hidden, click the tray icon once; the main window appears in the foreground with keyboard focus, and the same window (not a second one) is used.

**Acceptance Scenarios**:

1. **Given** XDM is running with the main window hidden, **When** the user clicks the tray icon, **Then** the main window becomes visible, is brought to the foreground, and receives keyboard focus.
2. **Given** XDM is running with the main window minimized, **When** the user clicks the tray icon, **Then** the window is restored to its normal (non-minimized) size and focused.
3. **Given** the main window is already visible, **When** the user clicks the tray icon, **Then** the same window is brought to the front and focused; no additional window or application instance is created.
4. **Given** downloads are in progress while the window is hidden, **When** the user clicks the tray icon, **Then** the window opens showing the current download state and no download is interrupted.
5. **Given** the user clicks the tray icon, **When** the window opens, **Then** the icon remains available in the system tray for later use.

---

### User Story 3 - Tray menu restores the window or exits XDM (Priority: P2)

The user right-clicks (or otherwise opens the context menu of) the XDM icon and chooses between restoring the main window and exiting the application, so a background instance can be shut down without a terminal or a task manager.

**Why this priority**: A tray icon for an always-running background application is not usable without a way to quit it from the same place; this is the standard expectation for system-tray icons. It is secondary to the icon itself and to click-to-restore.

**Independent Test**: With XDM running in the background and nothing downloading, open the tray menu, choose "Exit"; the icon disappears and no XDM process remains running; relaunching XDM afterwards starts normally.

**Acceptance Scenarios**:

1. **Given** the XDM icon is present, **When** the user opens its context menu, **Then** the menu offers at least "restore/open window" and "exit".
2. **Given** the user chooses the restore entry, **Then** the main window behaves exactly as described for a tray click (visible, foreground, focused).
3. **Given** no download is in progress, **When** the user chooses exit, **Then** XDM stops immediately, the icon is removed, and a later launch starts a fresh instance successfully.
4. **Given** downloads are in progress, **When** the user chooses exit, **Then** XDM asks for confirmation before doing anything; if the user cancels, XDM keeps running with all downloads unaffected and the icon still present, and if the user confirms, XDM stops and the icon is removed.

---

### Edge Cases

- **System tray unavailable**: on a session without a usable system tray (for example a minimal desktop, a remote/headless session, or a shell that does not host status icons), XDM must still start and keep running: it must not crash, the failure is recorded in the log, the instance stays hidden as started, and the window is reachable by launching XDM again.
- **Icon creation fails** (missing/invalid icon resource, desktop refuses the icon): the failure is recorded in the log and the running instance keeps working; a hidden instance must not become permanently unreachable as a result.
- **Repeated launches**: launching XDM again while a background instance exists must surface the existing window (existing single-instance behaviour) and must never produce a second icon.
- **Icon teardown**: when the instance ends — via the tray exit action, the in-app exit action, an OS session logoff, or a crash — the icon disappears and no stale icon remains in the system tray.
- **High-DPI and light/dark system trays**: the icon stays legible and correctly sized.
- **Multiple desktop sessions / fast user switching**: each user session shows the icon for its own instance; signing out of one session does not remove the other session's icon.
- **Window hidden while a modal dialog is open**: restoring the window must not leave the user with an unusable or orphaned dialog.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: While an XDM instance is running (whether or not its window is visible), the system tray MUST show exactly one XDM icon — no zero-icon state and no duplicates. The icon MUST NOT appear or disappear as the main window is shown, hidden, or minimized.
- **FR-002**: The icon MUST be present no later than the moment XDM is running in the background, i.e. when it starts without showing the window, and after the main window is closed, hidden, or minimized while the process continues.
- **FR-003**: Activating the icon with the primary (left) click MUST return the user to the application: show the main window if hidden, un-minimize it if minimized, bring it to the foreground, and give it keyboard focus.
- **FR-004**: Activation MUST reuse the existing main window; it MUST NOT start a second instance of XDM or open a duplicate window.
- **FR-005**: The icon's context menu MUST offer at least "restore/open window" and "exit".
- **FR-006**: The tray exit action MUST stop the running instance: no XDM process remains and the application can be started again afterwards without error, with interrupted downloads resumable. When downloads are in progress, the tray exit MUST ask for confirmation before exiting and MUST offer cancelling: cancelling leaves XDM running with every download continuing unaffected, while confirming stops the instance. When no download is in progress, the tray exit takes effect immediately without a prompt. The existing in-app Exit menu item keeps its current immediate behaviour and is not changed by this feature.
- **FR-007**: The icon MUST be removed when the instance it represents terminates for any reason, including the tray exit action and session logoff.
- **FR-008**: Hovering the icon MUST display a tooltip identifying the application as XDM.
- **FR-009**: Tray labels and menu texts MUST be shown in the application's currently selected language.
- **FR-010**: If the system tray is unavailable or the icon cannot be created, XDM MUST continue to run, MUST record the failure in the application log, and MUST NOT terminate. In that situation the instance MAY stay hidden as started, and the main window MUST remain reachable by launching XDM again through the existing single-instance handoff — it MUST NOT be shown automatically as a fallback.
- **FR-011**: The tray facility MUST be implemented in the Linux desktop build, which today shows no tray icon at all, so that background instances are equally discoverable there; the Windows build's existing tray behaviour MUST NOT regress.

### Out of Scope

- Tray-based progress display, download start/pause controls, or per-download menu entries; the menu stays limited to restoring the window and exiting.
- User-configurable options such as "always show tray icon" or "minimize to tray instead of taskbar"; the icon simply appears whenever XDM runs.
- Changes to how downloads, browser integration, or notifications work; existing notification behaviour must not regress.
- Behaviour changes to the Windows and macOS builds: the Windows build keeps its current tray behaviour (smoke-checked for no regression only) and macOS is not part of this change.
- The existing in-app Exit menu item on the Linux build: it keeps its current immediate behaviour, so only the tray exit prompts before stopping running downloads.

### Key Entities *(include if feature involves data)*

- **Running instance**: a single running copy of XDM per user session, identified by its background/foreground window state (window visible, hidden, or minimized) and its lifetime (start, still running, terminated).
- **Tray icon**: the user-visible representation of a running instance — icon artwork, tooltip text, visibility state (shown/removed), and its menu actions (restore window, exit).

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Starting XDM in the background on a Linux desktop session results in a visible tray icon within 5 seconds, on 10 out of 10 attempts.
- **SC-002**: A primary click on the tray icon brings the main window to the foreground, focused, in under 2 seconds, on 10 out of 10 attempts.
- **SC-003**: In every tested hidden-window situation — background start, window closed during an active download, window minimized — the user can reach the main window through the tray icon, 10 out of 10 attempts per situation.
- **SC-004**: After 10 successive launch attempts while an instance is already running, the system tray shows exactly one XDM icon and exactly one main window.
- **SC-005**: After choosing exit from the tray menu while no download is running, no XDM process and no XDM icon remain within 5 seconds, 10 out of 10 attempts, and a subsequent launch succeeds.
- **SC-006**: On a session without a usable system tray, XDM still starts without opening a window and without terminating, the failure is recorded in the log, and launching XDM again surfaces the existing main window — 10 out of 10 attempts.
- **SC-007**: With downloads in progress, choosing exit from the tray prompts for confirmation every time (10 out of 10 attempts); cancelling leaves XDM running with downloads unaffected in 10 out of 10 attempts, and confirming removes both the process and the icon within 5 seconds in 10 out of 10 attempts.

## Assumptions

- XDM already runs as a single instance per user session, already supports starting without a window (logon autostart / background launch), and already keeps running after the main window is closed. This feature adds the missing discoverability for that running instance rather than changing those behaviours.
- "Open the window" means the existing main window is shown, un-minimized, moved to the foreground, and focused — not a newly created window and not a new instance.
- The system tray / status area of the user's desktop session is the surface in question; no additional standalone window or dock item is introduced.
- The confirmation prompt shown when exiting with downloads in progress reuses the application's existing dialog style and translated language resources.
- Scope of this change is the Linux desktop build running common Linux desktop sessions (GNOME/KDE/XFCE class); the Windows build is only smoke-checked for no regression, and macOS is out of scope.
- Terminology: "system tray" is the desktop region that hosts status icons (called the notification area or status area on some desktops) and "tray icon" is the XDM icon shown there; these canonical terms are used consistently throughout this specification.
- Icon artwork already shipped with XDM is reused; no new branding assets are required.
