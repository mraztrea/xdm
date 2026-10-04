# Implementation Plan: Chuyển video .ts sang .mp4 khi tải (remux, không mã hoá lại)

**Branch Git hiện tại**: `master` | **Feature được chọn**: `002-ts-to-mp4-remux` | **Date**: 2026-10-04 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `specs/002-ts-to-mp4-remux/spec.md`

## Summary

Khi file đích của một lượt tải là `.ts`, hai hộp thoại tải (tải thường, tải video) trên cả GTK và WPF hiện checkbox **Convert to MP4**, được tick sẵn mỗi lần mở.

Lúc downloader ghép xong file `.ts`, nếu cờ bật và tên cuối cùng có đuôi `.ts`, helper nhận diện nội dung bằng FFmpeg trước khi remux. Kiểm tra sync trong buffer đầu chỉ là bằng chứng hỗ trợ, không phải điều kiện loại bỏ: TS có byte dư ở đầu vẫn được chuyển nếu FFmpeg đọc được video. Remux dùng `-f mpegts -i <infile> -map 0:v? -map 0:a? -c copy <outfile>` sau khi xác nhận đầu vào, không mã hoá lại.

- **Thành công**: báo tên và kích thước `.mp4`, thực hiện xoá `.ts`; lỗi xoá thì giữ cả hai file, mục tải vẫn hoàn tất với `.mp4`, cảnh báo kèm lý do và đường dẫn `.ts`.
- **Thất bại**: giữ `.ts`, hoàn tất với `.ts`, thực hiện xoá `.mp4` dở dang và báo lý do; lỗi xoá thì báo thêm lý do và đường dẫn file còn sót, không dùng nó làm kết quả tải.
- **Dừng giữa chừng**: mục tải về trạng thái dừng và giữ dữ liệu đã tải để tiếp tục. Đường dẫn output thuộc lượt này được lưu trong `.state`; nếu xoá lỗi, Resume/khởi động lại thử dọn chúng trước khi chọn tên hay ghép lại. Vẫn xoá lỗi thì giữ Stopped và cảnh báo, không tạo bản trùng.

Cờ được lưu trong file `.state` theo kiểu tương thích ngược.

Toàn bộ logic chuyển đổi nằm trong một helper dùng chung ở `XDM.Core`, gọi từ hai chỗ: `SingleSourceHTTPDownloader` và nhánh HLS muxed của `MultiSourceDownloaderBase`. Chi tiết quyết định: [research.md](./research.md).

## Technical Context

**Language/Version**: GTK và `XDM.Tests` target `net6.0`; WPF thực tế target `net4.7.2`, `LangVersion=9.0`. `XDM.Core` là shared project (`XDM.Core.projitems`), biên dịch trong GTK (`LangVersion=latest`, `DefineConstants=LINUX;TRACE`, `PublishTrimmed=true`, `TrimMode=Link`) và WPF (`TRACE;WINDOWS`). Code Core phải dùng C# 9 và API khả dụng trên .NET Framework 4.7.2. Giữ nhánh `NET5_0_OR_GREATER` cho `ProcessStartInfo.ArgumentList`, helper quoting hiện có cho WPF; không dùng `WaitForExitAsync`, `Kill(true)` hoặc API chỉ có trên .NET 6.
**Primary Dependencies**: FFmpeg ngoài, tìm như hiện có qua `FFmpegMediaProcessor.FindFFmpegBinary()` (thư mục app → `FFMPEG_HOME` → `PATH`). Không thêm package NuGet nào. GtkSharp 3.24 (Gtk.Builder + glade) cho GTK; WPF XAML cho Windows.
**Storage**: Append `ConvertTsToMp4` rồi hai cặp `PendingRemuxTsPath/Stamp`, `PendingRemuxMp4Path/Stamp` vào `.state` (SingleHttp, Hls). State cũ mặc định false/rỗng; checkpoint bị cắt hỏng không được coi là rỗng. Path lưu trước ghi; stamp metadata chốt sau đóng handle. Chỉ retry dọn file thuộc lượt tải khớp stamp và đủ chunks; mismatch giữ Stopped. Không đổi schema SQLite. Thêm 3 khoá chuỗi trong `English.txt`; cảnh báo cleanup dùng được cả Completed và Stopped.
**Testing**: NUnit `app/XDM/XDM.Tests` cho hàm thuần, parser metadata/mapping FFmpeg, IO `.state`, các nhánh lỗi dọn tệp và remux thật với TS chuẩn/có prefix (tự Ignore khi không có FFmpeg). Kiểm chứng GTK đã trim và WPF trên Windows theo [quickstart.md](./quickstart.md); CI không phủ hành vi UI GTK. Chỉ chạy bộ test chuyên biệt.
**Target Platform**: Linux desktop (GTK) và Windows (WPF). macOS ngoài phạm vi.
**Project Type**: desktop application, nhiều project (shared `XDM.Core` + `XDM.Gtk.UI` + `XDM.Wpf.UI`).
**Performance Goals**: Đo lượt xử lý thực tế trong XDM, gồm nhận diện/probe/fallback/remux/dọn tệp: ≤ 2 lần thời gian copy trên cùng ổ với mẫu 1 GB (SC-002). Hai mẫu chênh dung lượng ≤5%, thời lượng khác ít nhất 2 lần phải có tỷ lệ median thời gian trên mỗi byte `R <= 1,25`; nhóm 3 lượt có độ dao động `(max-min)/median > 0,20` thì chưa kết luận, cần đo lại. Tổng CPU của mọi process FFmpeg probe/remux ≤ 5% thời lượng video (SC-003). Có log `elapsed_ms` và từng `cpu_ms`; công thức và quy trình trong research D15/quickstart. Chưa có số đo hiệu năng XDM sau triển khai.
**Constraints**:
- Không mã hoá lại (FR-004).
- Không mất dữ liệu: SC-005.
- Không ghi đè im lặng: FR-011.
- File `.state` cũ phải resume được.
- Bản publish trim phải chạy được với widget mới.
- Không thêm setting chung (Assumption).
- Không đụng macOS.
- Không bỏ qua TS chỉ vì không có sync tại byte 0; không thêm dependency `ffprobe` khi chạy ứng dụng.
- Checkbox cập nhật khi người dùng sửa tên ở cả bốn hộp thoại; giữ trạng thái tick trong cùng lượt mở.
**Scale/Scope**:
- 1 file mới trong Core (`TsToMp4Remuxer.cs`).
- Sửa các file Core/UI được liệt kê bên dưới; không thêm package hoặc đổi target framework.
- 3 khoá chuỗi.
- 1 file test mới.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

`.specify/memory/constitution.md` tồn tại nhưng chỉ chứa template chưa điền; chưa có nguyên tắc được thông qua để xác nhận tuân thủ constitution. Không coi ví dụ trong comment là quy định. Plan áp dụng các gate có hiệu lực từ spec và `AGENTS.md`:

| Gate | Trạng thái | Bằng chứng |
|---|---|---|
| G1: Không mã hoá lại; lỗi thì không tự chuyển sang mã hoá lại (FR-004) | PASS | Tham số cố định `-c copy` (contract remuxer). Lỗi → `Failed`, giữ `.ts` |
| G2: Không mất dữ liệu đã tải (SC-005, FR-016) | PASS (design) | Xoá `.ts` sau khi `.mp4` hợp lệ hoặc khi huỷ có đủ chunks. Checkpoint output lưu bền; cleanup lỗi chặn Resume trước rename/assemble; mọi caller kiểm tra cancellation trước OnFinished/DeleteFileParts (research D4/D16) |
| G3: Đổi `XDM.Core` có kiểm soát (AGENTS.md §5: "tránh, trừ khi được yêu cầu rõ") | PASS (design, có lý do) | Người dùng chọn GTK + WPF, dùng chung logic. API thêm gồm tham số mặc định, hai property + sự kiện đổi tên trong mỗi interface dialog, phương thức probe/remux trong BaseMediaProcessor; kiểm tra mọi implementation và build cả hai nền tảng |
| G4: Tương thích ngược file `.state` | PASS (design) | Append bool rồi bốn string path/stamp; EOF trước bool → false, EOF ngay sau bool → checkpoint rỗng. Bản ghi có checkpoint nhưng bị cắt hỏng không được coi là rỗng (research D8/D16); có unit test |
| G5: Trimmed publish vẫn chạy | PASS (design), cần verify | Không reflection mới ngoài field `[UI]` cùng cơ chế với field có sẵn. Quickstart chạy bản `publish` |
| G6: Không thêm setting chung | PASS | Chỉ checkbox theo từng lần tải; không đổi `Config` |
| G7: i18n đúng quy tắc | PASS | Khoá mới chỉ trong `English.txt`; không tạo file ngôn ngữ mới |
| G8: `DefineConstants` GTK giữ `TRACE` | PASS | Không đụng csproj GTK; log dùng `Log.Debug` |
| G9: Phạm vi TS có prefix (FR-003, SC-001) | PASS (design) | Detector chỉ là hint; hint false + auto-probe không xác nhận video thì forced fallback, xác nhận codec/độ phân giải; fixture prefix 187, 65.537 và 1.048.577 byte; không cắt prefix |
| G10: Lỗi dọn tệp không đổi file kết quả (FR-008, FR-015) | PASS (design) | Converted vẫn trỏ .mp4; Failed vẫn hoàn tất .ts; cảnh báo chứa lý do và đường dẫn, có test lỗi File.Delete |
| G11: Nghiệm thu hiệu năng định lượng (SC-002) | PASS (design) | Median 3 lượt, hai mẫu 1 GB khác thời lượng, R ≤1,25, nhiễu >20% là chưa kết luận; không thay bằng benchmark FFmpeg riêng |

**Post-Phase 1 re-check**: G1–G11 đều đạt ở mức thiết kế, chưa phải kết quả chạy ứng dụng. Đã xử lý ba finding của lượt analyze: prefix vượt buffer, ngưỡng hiệu năng theo thời lượng, cleanup lỗi khi Stop/Resume. Không thay spec hoặc tasks trong lượt này; tasks phải sinh lại theo thiết kế mới. Bằng chứng FFmpeg độc lập nằm trong research D12; nghiệm thu GTK/WPF, checkpoint và số đo XDM thực hiện sau triển khai.

## Project Structure

### Documentation (this feature)

```text
specs/002-ts-to-mp4-remux/
├── plan.md                          # File này
├── spec.md                          # Đặc tả
├── research.md                      # Phase 0: quyết định D1–D16
├── data-model.md                    # Phase 1: cờ, vòng đời file, kết quả remux
├── quickstart.md                    # Phase 1: kiểm chứng thủ công
├── contracts/
│   ├── ts-to-mp4-remuxer.md         # helper remux + chỗ gọi trong downloader
│   └── download-dialog-option.md    # checkbox trên 4 hộp thoại + chuỗi
├── checklists/
│   └── requirements.md
└── tasks.md                         # Đã tồn tại; cần sinh lại bằng /speckit-tasks sau plan này
```

### Source Code (repository root)

```text
app/XDM/
├── XDM.Core/
│   ├── XDM.Core.projitems                                  # + Compile TsToMp4Remuxer.cs
│   ├── IApplicationCore.cs                                 # StartDownload(..., bool convertTsToMp4 = true)
│   ├── ApplicationCore.cs                                  # truyền cờ vào ctor SingleHttp/Hls
│   ├── IVideoTracker.cs                                    # StartVideoDownload(..., bool convertTsToMp4 = true)
│   ├── BrowserMonitoring/VideoTracker.cs                   # truyền cờ cho nhánh http + hls
│   ├── BrowserMonitoring/CapturedVideoTracker.cs           # như trên
│   ├── IO/DownloadStateIO.cs                               # append bool + checkpoint output (SingleHttp, Hls)
│   ├── Downloader/
│   │   ├── Progressive/HTTPDownloaderBase.cs              # guard cancellation trước Finished ở caller assemble
│   │   ├── MediaProcessor/
│   │   │   ├── BaseMediaProcessor.cs                       # + ProbeTs / RemuxTsToMp4
│   │   │   ├── FFmpegMediaProcessor.cs                     # + probe, args, parser stream/mapping, CPU log
│   │   │   └── TsToMp4Remuxer.cs                           # MỚI: nhận diện/fallback, Run, reserve và cleanup checkpoint
│   │   ├── Progressive/SingleHttp/SingleSourceHTTPDownloader.cs  # checkpoint, gate Resume/OnFinished, Run sau dispose outfs
│   │   ├── Adaptive/MultiSourceDownloaderBase.cs           # checkpoint, gate Resume/OnFinished; Run nhánh muxed
│   │   └── Adaptive/Hls/MultiSourceHLSDownloader.cs        # ctor param → _state.ConvertTsToMp4
│   └── UI/
│       ├── INewDownloadDialog.cs                           # + 2 property checkbox, FileNameChangedEvent
│       ├── INewVideoDownloadDialog.cs                      # như trên
│       ├── NewDownloadDialogUIController.cs                # hiện/ẩn, mặc định tick, truyền cờ
│       └── NewVideoDownloadDialogUIController.cs           # như trên
├── XDM.Gtk.UI/
│   ├── Dialogs/NewDownload/NewDownloadWindow.cs            # [UI] CheckButton ChkConvertToMp4 + 2 property
│   ├── Dialogs/NewVideoDownload/NewVideoDownloadWindow.cs  # như trên
│   └── glade/{new-download-window,new-video-download-window}.glade  # + GtkCheckButton
├── XDM.Wpf.UI/
│   ├── Dialogs/NewDownload/NewDownloadWindow.xaml(.cs)     # + CheckBox ChkConvertToMp4
│   └── Dialogs/NewVideoDownload/NewVideoDownloadWindow.xaml(.cs)
├── XDM.Tests/
│   └── TsToMp4RemuxerTests.cs                              # MỚI
└── Lang/English.txt                                        # + LBL_CONVERT_TO_MP4, MSG_TS_TO_MP4_FAILED, MSG_TS_TO_MP4_CLEANUP_FAILED
```

**Structure Decision**: Giữ cấu trúc multi-project hiện có. Logic dùng chung đặt cạnh `FFmpegMediaProcessor` trong `XDM.Core/Downloader/MediaProcessor/`, vì đó là nơi mọi bước hậu xử lý FFmpeg đang sống. Mỗi UI chỉ thêm một widget và hai property theo mẫu checkbox MP3 sẵn có. Không động vào `DualSourceHTTPDownloader` và `MultiSourceDASHDownloader`, vì chúng không bao giờ cho ra `.ts`.

**Thứ tự triển khai gợi ý** (để `/speckit-tasks` chia việc; không coi tasks hiện có là đã cập nhật):
1. Core: nhận diện có prefix, FFmpeg probe/remux, parser log luồng bị bỏ, test.
2. State IO: bool + path/stamp checkpoint, test tương thích/cắt hỏng (research D8/D16).
3. Hai downloader: reserve/checkpoint, dispose `outfs`, retry cleanup trước rename; guard các caller Finished/OnComplete và test Stop/Resume/restart (research D5/D16).
4. Truyền cờ: `ApplicationCore`, VideoTracker.
5. Interface và controller hộp thoại, sự kiện đổi tên và trạng thái tick trong cùng dialog.
6. GTK UI.
7. WPF UI.
8. Chuỗi `English.txt`.
9. Kiểm chứng quickstart: test chuyên biệt, hiệu năng thực tế, lỗi dọn tệp, GTK trim và WPF trên Windows.

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
|---|---|---|
| Sửa `XDM.Core` (shared GTK + WPF), đụng `IApplicationCore`, `IVideoTracker`, `INew*DownloadDialog`, `BaseMediaProcessor` | Người dùng yêu cầu tính năng trên cả hai nền tảng với cùng hành vi. Downloader và hộp thoại controller đều nằm trong Core | Làm riêng trong từng UI không được: UI không truy cập được bước assemble của downloader. Hậu xử lý sau `DownloadFinished` thì mục tải đã "hoàn tất", không hiển thị được trạng thái chuyển đổi và phải tự sửa DB/UI (research D2) |
| Đổi định dạng nhị phân `.state` | FR-010 yêu cầu cờ còn hiệu lực qua tạm dừng/khởi động lại | Thêm cột SQLite thì cần migration schema. File phụ riêng thì thêm một nơi lưu nữa phải dọn. Field cuối + đọc tuỳ chọn là thay đổi nhỏ nhất và tương thích ngược |
