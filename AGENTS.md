# AGENTS.md — Lưu ý bắt buộc khi làm việc trong repo XDM

> Đây là nguồn duy nhất. `CLAUDE.md` chỉ import file này — **không** chép lại nội dung.

## 1. Ngôn ngữ

- **Toàn bộ tài liệu trong repo (spec, plan, research, quickstart, tasks, docs, ghi chú) và toàn bộ phản hồi cho người dùng đều PHẢI viết bằng Tiếng Việt.**
- Giữ nguyên tiếng Anh cho: mã nguồn, định danh (class/method/biến), log message, khoá resource, commit message (theo lịch sử repo).

## 2. Repo là gì

XDM (Xtreme Download Manager) — ứng dụng desktop, C# / .NET 6, fork từ `subhra74/xdm`.

```
app/XDM/XDM_CoreFx.sln
├── XDM.Core/            # shared project (.projitems) — dùng CHUNG cho WPF và GTK
├── XDM.Wpf.UI/          # UI Windows (WPF), DefineConstants=TRACE;WINDOWS
├── XDM.Gtk.UI/          # UI Linux (GTK), AssemblyName=xdm-app, DefineConstants=LINUX;TRACE
├── XDM.WinForms.IntegrationUI/   XDM.App.Host/ (native messaging)   NativeMessagingHost/
├── XDM.Tests/           # NUnit (net6.0) — unit test
├── XDM_Tests/           # XDM.SystemTests — test hệ thống
├── MockServer/          # server giả cho test tải
├── Lang/                # chuỗi dịch: KEY=Value, mỗi ngôn ngữ 1 file .txt + index.txt
└── Translations/        # TextResource — nạp chuỗi theo ngôn ngữ
specs/NNN-ten-feature/   # spec-driven (SDD)
.specify/                # cấu hình + template của quy trình SDD
docs/                    # GitHub Pages (trang web), KHÔNG phải tài liệu dev
translation-generator/   # app React phụ trợ, độc lập với XDM
```

## 3. Toolchain và lệnh (đã kiểm chứng trên máy này)

- .NET SDK **6.0.428 nằm ở `~/.dotnet`, KHÔNG có trên `PATH`** → luôn gọi `~/.dotnet/dotnet`.
- **Không** có native GTK3 (`pkg-config --modversion gtk+-3.0` fail), không có `mvn`. Có `podman`/`docker`.
- CI `.github/workflows/xdm-wpf-build.yml` chỉ restore/build/test **XDM.Wpf.UI** trên `windows-latest` → **thay đổi ở GTK không được CI phủ**, phải tự verify cục bộ.

```bash
# Build UI Linux
~/.dotnet/dotnet build app/XDM/XDM.Gtk.UI/XDM.Gtk.UI.csproj -c Release

# Unit test
~/.dotnet/dotnet test app/XDM/XDM.Tests/XDM.Tests.csproj

# Smoke run bản đã trim (project có PublishTrimmed=true, TrimMode=Link)
~/.dotnet/dotnet publish app/XDM/XDM.Gtk.UI/XDM.Gtk.UI.csproj -c Release -r linux-x64 --self-contained
XDM_DEBUG_MODE=1 app/XDM/XDM.Gtk.UI/bin/Release/net6.0/xdm-app --background
```

- Log của app GTK: `~/.xdm-app-data/log.txt`.
- App có `InvariantGlobalization=true`.

## 4. Quy trình spec-driven (SDD)

- `.specify/feature.json` → `feature_directory` trỏ tới feature đang làm (hiện tại: `specs/001-background-tray-icon`).
- Mỗi feature: `spec.md`, `plan.md`, `research.md`, `data-model.md`, `quickstart.md`, `tasks.md`, `contracts/`, `checklists/`. Template ở `.specify/templates/`. Repo **không** có `.specify/memory/constitution.md`.
- Trước khi sửa code: đọc `spec.md` + `plan.md` + `tasks.md` của feature liên quan; cập nhật `tasks.md` khi hoàn thành.
- Verify theo `quickstart.md` (thủ công trên desktop thật) — **không** chạy cả test suite cho mỗi thay đổi.
- Ngoài phạm vi spec thì không làm: spec ghi rõ Out of Scope (ví dụ: không thêm setting cho người dùng, không đụng build macOS).

## 5. Ràng buộc và cạm bẫy (đã trả giá, đừng lặp lại)

- **`DefineConstants` của project GTK phải giữ `TRACE`** (`LINUX;TRACE`). Nếu chỉ `LINUX`, mọi `Log.Debug(...)` (có `[Conditional("TRACE")]`) bị compiler loại bỏ và `log.txt` không bao giờ được ghi. WPF dùng `TRACE;WINDOWS`.
- **`XDM.Core` là shared project dùng chung WPF + GTK**: đổi public API ở đó ảnh hưởng cả hai build → tránh, trừ khi được yêu cầu rõ (feature 001 có gate G1: không đụng `XDM.Core/**` và `XDM.Wpf.UI/**`).
- **Trimming**: project GTK publish với `PublishTrimmed=true`/`TrimMode=Link` → code chỉ chạy qua reflection sẽ bị cắt. Tạo handler trực tiếp, và kiểm tra bằng một lần `publish` + chạy thật.
- **`Tmds.DBus.Protocol`: `MessageWriter` là `ref struct`** mang vị trí ghi. Helper ghi reply phải nhận `ref MessageWriter`; truyền theo giá trị làm body sai và daemon ngắt kết nối.
- **i18n**: thêm khoá mới **chỉ** vào `app/XDM/Lang/English.txt` — ngôn ngữ khác kế thừa tiếng Anh (`TextResource` nạp `English.txt` trước, ngôn ngữ được chọn ghi đè theo khoá). Thêm ngôn ngữ mới thì phải thêm dòng vào `Lang/index.txt`. Không tạo file ngôn ngữ mới chỉ để dịch một khoá.
- **Single instance**: mutex `Global\XDM_Active_Instance` (`XDM.Core/SingleInstance.cs`). Nếu đã có XDM chạy, lần chạy sau chỉ gửi args cho instance cũ rồi `Environment.Exit(0)`. Muốn chạy bản build mới: dừng bản đang chạy, hoặc chạy với `HOME`/`XDG_CONFIG_HOME` riêng để không đụng dữ liệu người dùng.
- **Lỗi có sẵn, không phải do bạn**: `XDM.Tests/JsonParsingTest.cs:156` đọc đường dẫn Windows `C:\Users\subhro\Desktop\message.json` → luôn fail trên Linux. Project GTK còn `HintPath` kiểu `D:\gtksharp\...` (GtkSourceSharp) → warning có sẵn.
- **Tray icon (feature 001)**: host status-notifier là điều kiện bắt buộc để thấy icon — KDE Plasma có sẵn; GNOME cần extension AppIndicator; phiên không có bus/host thì app **ở ẩn và chỉ ghi log**, không tự mở cửa sổ.

## 6. Trạng thái feature hiện tại

- `specs/001-background-tray-icon`: icon khay hệ thống cho build Linux (StatusNotifierItem + DBusMenu, fallback `Gtk.StatusIcon` cho XEmbed). Code: `app/XDM/XDM.Gtk.UI/Utils/Tray/`. Đã commit trên `master` (`0aa5ed6`); T001–T024 xong.
- Kết quả verify, defect đã sửa và hạn chế đã biết: xem mục **Verification results** ở cuối `specs/001-background-tray-icon/tasks.md`. Còn lại: focus bàn phím không được cấp trên KDE Wayland; nhánh legacy XEmbed chưa verify.
