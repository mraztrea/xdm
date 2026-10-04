# Research: Chuyển video .ts sang .mp4 khi tải (remux)

**Feature**: [spec.md](./spec.md) | **Plan**: [plan.md](./plan.md) | **Ngày**: 2026-10-04

Các quyết định được đối chiếu với code hiện tại trong `app/XDM/XDM.Core`. Số đo 51 MB/0,12 s và bảng D1 là kết quả ghi lại từ lượt lập plan trước, không phải kiểm chứng ứng dụng sau triển khai. Lượt cập nhật này đã chạy thử độc lập FFmpeg 8.0.1 với TS chuẩn, prefix và file TypeScript; xem D12. Không build/test ứng dụng.

---

## D1 — Cách chuyển: remux bằng FFmpeg `-c copy`

- **Decision**: Sau khi xác nhận nội dung TS bằng D3/D12, gọi FFmpeg với tham số `-hide_banner -nostdin -f mpegts -i <in.ts> -map 0:v? -map 0:a? -c copy <out.mp4> -y`. Không mã hoá lại. Không dùng `-movflags +faststart`. Chỉ tạo output sau khi probe; `-f mpegts` không được dùng để coi mọi file đuôi .ts là video.
- **Rationale**:
  - XDM đã dùng FFmpeg qua `FFmpegMediaProcessor` để ghép luồng (`MergeAudioVideStream`, `MergeHLSAudioVideStream`) và đổi sang MP3. Có sẵn cả cơ chế tìm binary (`FindFFmpegBinary`: thư mục app → `FFMPEG_HOME` → `PATH`), huỷ (`CancelFlag` → `proc.Kill()`) và `LastError`.
  - `-c copy` chỉ đóng gói lại, nên tốn CPU rất ít. Đo thử: file TS H.264 720p + AAC dài 60 s (51 MB) remux trong **0,12 s**. Chất lượng giữ nguyên: codec, độ phân giải đều giống, thời lượng lệch 0,01 s.
  - `-map 0:v? -map 0:a?` chỉ giữ hình và tiếng; dấu `?` để không lỗi khi thiếu một trong hai. Các luồng phụ đề/teletext/dữ liệu (SCTE-35, `bin_data`) mà MP4 không chứa được thì bị bỏ qua thay vì làm hỏng cả lượt (đúng edge case trong spec).
  - Từ FFmpeg 4.x, muxer MP4 tự chèn bitstream filter `aac_adtstoasc` cho AAC từ TS, nên không cần thêm tham số.
  - Bỏ `+faststart` vì nó ghi lại toàn bộ file thêm một lượt (gấp đôi I/O, ngược với SC-002). File mở trên máy thì không cần.
- **Kết quả thử (scratch, ffmpeg 8.0.1)**:

  | Đầu vào TS | Kết quả |
  |---|---|
  | H.264 + AAC | OK, exit 0 |
  | MPEG-2 + AC-3 (giống DVD) | OK, exit 0, giữ nguyên `mpeg2video` + `ac3` |
  | MPEG-2 + MP2 | OK, exit 0 |
  | MPEG-2 + SMPTE 302M (âm thanh broadcast) | **Lỗi**, exit 234: "codec not currently supported in container"; **để lại file .mp4 0 byte** |
  | File văn bản TypeScript đặt tên `.ts` | **Lỗi**, exit 183: "Invalid data found" |

- **Alternatives considered**:
  - *Mã hoá lại (libx264/aac)*: bị loại vì spec cấm (FR-004) và chậm hơn hàng trăm lần.
  - *Tự viết bộ remux TS→MP4 bằng C#*: bị loại vì phải tự xử lý PES/PTS, AVC/HEVC/AC-3 → quá nhiều việc. XDM đã phụ thuộc FFmpeg cho video rồi.
  - *Cho HLS ghép segment thẳng ra .mp4 bằng `concat` demuxer*: bị loại vì nếu lỗi thì không còn file `.ts` để giữ lại (FR-008). Thêm nữa, mỗi loại downloader lại cần một nhánh riêng.

## D2 — Vị trí chèn bước chuyển đổi: cuối giai đoạn "assemble" của downloader

- **Decision**: Thêm một helper dùng chung `TsToMp4Remuxer`. Gọi nó ở cuối giai đoạn assemble, **trước** khi xoá file tạm và trước khi downloader phát sự kiện finished. Có hai chỗ gọi:
  1. `SingleSourceHTTPDownloader.AssemblePieces()`: link .ts trực tiếp, và video .ts bắt từ trình duyệt dạng HTTP thường.
  2. `MultiSourceDownloaderBase.Assemble()`, nhánh `!_state.Demuxed` (HLS muxed, ghép segment nhị phân ra `.ts`): video streaming bắt từ trình duyệt.
- **Rationale**:
  - `ApplicationCore.DownloadFinished` lấy `http.TargetFile` và `http.FileSize` để ghi DB (`MarkAsFinished` cập nhật `name`, `targetdir`, `size`), hiện hộp thoại hoàn tất và quét antivirus. Nếu downloader đổi `TargetFileName` sang `.mp4` và đặt lại kích thước *trước* khi finished, thì mọi thứ phía sau tự trỏ đúng file `.mp4` (FR-007) mà không phải sửa `ApplicationCore`.
  - Trong lúc chuyển, mục tải vẫn ở danh sách "đang tải" và chưa "hoàn tất". Đây đúng là tiền lệ của tính năng MP3 (`ConvertToMp3` trong `AssemblePieces`) → đáp ứng FR-006.
  - Hai downloader còn lại không bao giờ cho ra `.ts` nên không cần đụng: `DualSourceHTTPDownloader` (ghép mp4/mkv) và `MultiSourceDASHDownloader`.
- **Alternatives considered**:
  - *Hậu xử lý trong `ApplicationCore.DownloadFinished`*: bị loại vì lúc đó mục tải đã chuyển sang danh sách "hoàn tất", không còn hiển thị được trạng thái đang chuyển, và phải tự cập nhật lại DB/UI.
  - *Sửa cả 4 downloader*: không cần, xem trên.

## D3 — Điều kiện kích hoạt: cờ, tên cuối cùng và FFmpeg nhận diện video TS

- **Decision**: `ShouldRemux` chỉ kiểm tra cờ và đuôi `.ts` của tên cuối cùng, không phân biệt hoa thường. `LooksLikeMpegTs` tìm ít nhất 5 sync byte `0x47` cách nhau 188 byte tại bất kỳ offset trong buffer đầu tối đa 1 MiB; kết quả false KHÔNG cho phép bỏ qua chuyển đổi. Có hint → probe với `-f mpegts`; không có hint → auto-probe, rồi thử lại với `-f mpegts` nếu chưa xác nhận được video MPEGTS, kể cả khi auto-probe báo định dạng khác hoặc lỗi. Chỉ chấp nhận khi metadata có video với codec xác định và độ phân giải dương; header MPEGTS do ép demuxer không đủ. Giữ nguyên input, không cắt prefix.
- **Rationale**: FR-003/SC-001 bao gồm prefix mà FFmpeg đọc được, không đặt giới hạn 1 MiB. Thử nghiệm prefix 1.048.577 byte cho thấy auto-probe không có metadata nhưng forced probe đọc được cả video/audio. Fallback thứ hai xử lý trường hợp này mà không cần quét toàn file. Tên có thể đổi từ Content-Disposition hoặc HLS ProbeTarget, nên kiểm tra lại lúc assemble.
- **Phân loại**: định dạng khác/không có bằng chứng media → `Skipped`, giữ .ts và chỉ log. Có bằng chứng TS nhưng probe/remux không đọc được video → `Failed`, giữ .ts và báo lý do. Thiếu binary → `AppNotFound`; không suy diễn không-video từ việc không có FFmpeg. Đối với văn bản nhỏ không có hint TS, có thể loại trước probe nếu toàn bộ file kiểm tra được là UTF-8/ASCII văn bản không có byte nhị phân; không dùng heuristic này để bỏ qua file lớn chưa đọc hết. Mẫu TypeScript chuẩn phải hoàn tất không thông báo lỗi, kể cả khi thiếu FFmpeg.
- **Alternatives considered**: kiểm tra offset 0/188/376 bị loại vì bỏ sót prefix; quét toàn file thêm một lượt I/O; chỉ Content-Type không đáng tin; nhận diện chỉ từ header ép MPEGTS có thể nhầm dữ liệu rác; thêm ffprobe bắt buộc tăng dependency. Forced probe chỉ là phép thử, luôn phải xác nhận metadata video.

## D4 — Xử lý kết quả: thành công / thất bại / huỷ

- **Decision**:
  - **File đích**: `<tên>.mp4` trong cùng thư mục. Xử lý trùng tên giống `MultiSourceDownloaderBase.Assemble()`: nếu `Config.Instance.FileConflictResolution == AutoRename` thì dùng `FileHelper.GetUniqueFileName`; nếu là `Overwrite` thì ghi đè (đây là cấu hình người dùng chủ động chọn, nên không phải "ghi đè im lặng" theo FR-011).
  - **Thành công** (exit 0 và file `.mp4` có kích thước > 0): thực hiện xoá `.ts`, đặt `TargetFileName` = tên `.mp4`, đặt kích thước = kích thước `.mp4`. Lỗi xoá .ts không đổi `Converted`: giữ cả hai file, cảnh báo nêu lý do/đường dẫn và ghi log (FR-015). Nếu `Config.Instance.FetchServerTimeStamp` bật thì đặt lại `LastWriteTime` (giống nhánh MP3).
  - **Thất bại** (`MediaProcessingResult.Failed` hoặc `AppNotFound`): thực hiện xoá `.mp4` dở dang, giữ `.ts`, giữ `TargetFileName` cũ. Download **vẫn hoàn tất bình thường**, không ném `AssembleFailedException`. Nếu xoá lỗi, báo cả lỗi chuyển đổi và lỗi xoá, kèm đường dẫn; output dở dang không là kết quả tải (FR-008).
  - **Huỷ/dừng** (`CancelFlag` bật trong lúc chuyển): kill và chờ FFmpeg thoát, đóng writer/handle, thực hiện xoá `.mp4` dở và `.ts` vừa ghép; giữ piece/segment. Lỗi xoá giữ checkpoint đúng đường dẫn, cảnh báo và Stopped; Resume thử dọn checkpoint trước mọi probe/rename/assemble. Vẫn lỗi thì tiếp tục Stopped, không tạo output mới. Chỉ khi dọn xong mới ghép/chuyển lại với cờ đã lưu. Không chỉ `return` trong assemble: phải chặn các caller Finished/OnComplete như D16.
- **Rationale**: Thoả FR-005, FR-007, FR-008, FR-011, FR-015, SC-005 (không mất dữ liệu: khi lỗi thì còn `.ts`; khi huỷ thì còn dữ liệu đã tải để tiếp tục).
- **Ghi chú spec**: Spec bản đầu gộp "thất bại hoặc bị huỷ → giữ .ts, coi là đã tải xong". Lệnh dừng trong XDM có nghĩa là *dừng tải*, không phải *bỏ chuyển đổi*, nên plan tách riêng hai trường hợp. `spec.md` (FR-008, FR-016, Edge Cases) đã được cập nhật tương ứng.

## D5 — Cảnh báo bắt buộc: giải phóng file `.ts` trước khi gọi FFmpeg

- **Decision**: Trong `SingleSourceHTTPDownloader.AssemblePieces()`, `outfs` (`using var outfs = FileHelper.CreateTargetFile(...)`) phải được **Dispose/flush trước** khi gọi remux.
- **Rationale**: Hiện `outfs` khai báo bằng `using var` trong khối `try` ngoài, nên vẫn mở cho tới hết khối, kể cả lúc `DeleteFileParts()`. Nếu remux trong lúc nó còn mở thì: (a) FFmpeg có thể đọc thiếu phần buffer chưa flush; (b) trên Windows không xoá được `.ts` vì handle còn mở. Nhánh MP3 không gặp vấn đề này vì nó ghi ra file tạm *khác* rồi FFmpeg đọc file tạm, nhưng file tạm đó cũng chưa được dispose. Khi triển khai, phải bọc `outfs` trong một khối `using { }` riêng.
- Với HLS, `ConcatSegments()` đã tự dispose `fsout` khi kết thúc hàm, nên không có vấn đề.

## D6 — Báo lỗi chuyển đổi cho người dùng

- **Decision**: Helper remux tự báo qua `ApplicationContext.Application`, chạy trên UI thread bằng `RunOnUiThread`:
  - `AppNotFound` (không có FFmpeg): dùng lại khoá có sẵn `MSG_FFMPEG_MISSING` với `Confirm(...)`. Nếu người dùng đồng ý thì `PlatformHelper.OpenBrowser(Links.HelperToolsUrl)`. Đây chính là cơ chế hộp thoại tải video đang dùng.
  - `Failed`: `ShowMessageBox(null, TextResource.GetText("MSG_TS_TO_MP4_FAILED") + "\n" + LastError)`.
  - Lỗi dọn tệp: dùng `MSG_TS_TO_MP4_CLEANUP_FAILED`, kèm đường dẫn và lỗi IO; khi chuyển đổi cũng lỗi thì gộp vào thông báo thất bại. Khi Stop/Resume bị chặn dọn tệp, thông báo không nói download đã hoàn tất. Chốt Converted/Failed/Cancelled trước khi gửi cảnh báo lên UI thread.
  - Mọi kết quả (thành công, thất bại, bỏ qua, luồng bị bỏ) đều ghi bằng `Log.Debug` (FR-012).
  - Nếu `ApplicationContext.Application == null` (unit test) thì chỉ ghi log.
- **Rationale**: Không phải đổi interface `IBaseDownloader` hay `IApplication`. Thông báo hiện đúng lúc download hoàn tất. Không chặn hộp thoại tải khi thiếu FFmpeg, vì file `.ts` vẫn có ích (US4).
- **Alternatives considered**: *Thêm `PostProcessingWarning` vào `IBaseDownloader` để `ApplicationCore` hiển thị*: bị loại vì phải sửa interface và hai base class mà không thêm giá trị.

## D7 — Truyền cờ từ hộp thoại tới downloader

- **Decision**:
  - Thêm tham số tuỳ chọn `bool convertTsToMp4 = true` (đặt **cuối**) vào `IApplicationCore.StartDownload`/`ApplicationCore.StartDownload` và `IVideoTracker.StartVideoDownload` (cả `VideoTracker` lẫn `CapturedVideoTracker`).
  - Truyền tiếp vào constructor `SingleSourceHTTPDownloader(..., bool convertTsToMp4 = true)` và `MultiSourceHLSDownloader(..., bool convertTsToMp4 = true)`.
  - Chỉ hai hộp thoại truyền giá trị checkbox. Mọi nơi gọi khác (batch `DownloadSelectionUIController`, `VideoDownloaderUIController`, `RestartDownload`, tự tải không qua hộp thoại) nhận mặc định `true`, đúng Assumption "tải không qua hộp thoại → bật".
- **Rationale**: Theo đúng tiền lệ `convertToMp3` (tham số → constructor → state). Giá trị mặc định ở tham số nghĩa là không phải sửa các chỗ gọi không liên quan. Mặc định `true` an toàn nhờ D3: không phải `.ts` hoặc không phải MPEG-TS thì không làm gì.
- **Ràng buộc C#**: `XDM.Core` là shared project, được biên dịch cả trong `XDM.Wpf.UI` với `LangVersion 9.0` → không dùng cú pháp C# 10+ (file-scoped namespace, global using…).

## D8 — Lưu bền cờ qua tạm dừng/khởi động lại (FR-010)

- **Decision**:
  - Thêm field `bool ConvertTsToMp4` vào `SingleSourceHTTPDownloaderState` và `MultiSourceDownloadState` (lớp cha; DASH kế thừa nhưng không đọc/ghi).
  - `DownloadStateIO` append theo thứ tự: `ConvertTsToMp4`, `PendingRemuxTsPath`, `PendingRemuxTsStamp`, `PendingRemuxMp4Path`, `PendingRemuxMp4Stamp` vào SingleHttp/Hls. Checkpoint được giải thích ở D16.
  - Khi đọc thì dùng helper `ReadOptionalBoolean(r)`: `try { return r.ReadBoolean(); } catch (EndOfStreamException) { return false; }`.
  - EOF trước bool → false và checkpoint rỗng; EOF ngay sau bool → checkpoint rỗng cho bản ghi chỉ có cờ. Nếu đã có bytes checkpoint thì đọc đủ bốn string, không nuốt EOF giữa nhóm hoặc string hỏng. State hỏng giữ dữ liệu và báo lỗi, không tự rename/cleanup.
- **Rationale**:
  - File `.state` là nhị phân, không có số phiên bản. Nếu thêm field rồi đọc thẳng file `.state` cũ (tạo trước khi nâng cấp) thì sẽ bị `EndOfStreamException`, và download dở dang không resume được.
  - Đọc kiểu tuỳ chọn → bản ghi cũ ra `false`, nghĩa là hành vi cũ (không chuyển). Không bất ngờ cho download tạo trước tính năng.
- **Ghi chú**: File `.info` (`RequestDataIO`) **không** lưu cờ, giống `ConvertToMp3` hiện nay. `RestartDownload` tạo download mới với mặc định `true`, tức là hành vi của một lần tải mới.
- **Alternatives considered**: *Thêm cột vào SQLite `downloads`*: bị loại vì phải viết migration schema (`SchemaInitializer`) cho một giá trị chỉ downloader cần.

## D9 — Giao diện: checkbox trong hai hộp thoại, cả GTK và WPF

- **Decision**:
  - Thêm vào `INewDownloadDialog` và `INewVideoDownloadDialog` hai property: `bool ShowConvertToMp4Checkbox { get; set; }` và `bool IsConvertToMp4Checked { get; set; }`.
  - Controller (`NewDownloadDialogUIController`, `NewVideoDownloadDialogUIController`) đặt `IsConvertToMp4Checked = true` mỗi lần mở (FR-002). Checkbox hiện khi tên file đang chọn có đuôi `.ts` và Content-Type (nếu có) không phải `text/*` hoặc `application/(java|type)script`.
  - Thêm `FileNameChangedEvent` vào hai interface dialog, không sửa `IFileSelectable` dùng chung. GTK phát từ `TxtFile.Changed`, WPF từ `TxtFile.TextChanged` bằng handler trực tiếp. Controller tính lại khi mở, đổi tên, đổi URL hoặc lựa chọn video/MP3 đổi tên, và trước Download/Download Later.
  - Lúc bấm tải, controller truyền `IsConvertToMp4Checked`, kể cả khi checkbox ẩn. Giá trị khởi tạo là true, nhưng nếu người dùng đã bỏ tick thì giữ false trong cùng hộp thoại khi đổi .ts → .mp4 → .ts. Mở hộp thoại mới mới reset true. Downloader tự quyết theo tên cuối và D3.
  - Triển khai UI:
    - GTK: thêm `GtkCheckButton` vào `glade/new-download-window.glade` và `glade/new-video-download-window.glade`, cùng field `[UI]` trong `NewDownloadWindow.cs`/`NewVideoDownloadWindow.cs`.
    - WPF: thêm `CheckBox` vào `NewDownloadWindow.xaml` và `NewVideoDownloadWindow.xaml` (cạnh `ChkMp3`), cùng property trong `.xaml.cs`.
- **Rationale**: Checkbox MP3 đã là tiền lệ (`ShowMp3Checkbox`/`IsMp3CheckboxChecked`). Lưu ý bản GTK của checkbox MP3 hiện chỉ là stub, nên với GTK phải thêm widget thật.
- **Trimming (GTK)**: field `[UI]` được `Gtk.Builder` gán bằng reflection, nhưng các field `[UI]` hiện có vẫn chạy được trong bản trim. Field mới cùng lớp đi theo cùng cơ chế. Vẫn phải xác nhận bằng một lần `publish` rồi chạy thật (AGENTS.md §5).

## D10 — Chuỗi hiển thị

- **Decision**: Thêm vào **chỉ** `app/XDM/Lang/English.txt`:
  - `LBL_CONVERT_TO_MP4=Convert to MP4`
  - `MSG_TS_TO_MP4_FAILED=The download finished, but XDM could not convert it to MP4. The original .ts file was kept.`
  - `MSG_TS_TO_MP4_CLEANUP_FAILED=XDM could not delete the following file.`

  Các ngôn ngữ khác, kể cả `Vietnamese.txt`, kế thừa tiếng Anh (AGENTS.md §5). WPF đọc khoá qua `TranslationResourceDictionary` (`{StaticResource LBL_CONVERT_TO_MP4}`), GTK qua `TextResource.GetText`.
- **Trạng thái trong lúc chuyển**: dùng lại nhãn giai đoạn hậu xử lý có sẵn `STAT_ASSEMBLING`. `ApplicationCore.AssembleProgressChanged` đang hiển thị nhãn này cho mọi bước FFmpeg sau tải (MP3, ghép DASH). Nhãn này khác "đang tải", và mục tải chỉ "hoàn tất" khi `.mp4` đã sẵn sàng, nên đáp ứng FR-006 mà không phải thêm kênh trạng thái mới. Câu chữ acceptance scenario US1-3 trong spec đã được chỉnh theo.
- **Hạn chế đã biết**: `FFmpegMediaProcessor.ProcessMedia` chỉ phân tích tiến độ từ stdout, trong khi FFmpeg ghi tiến độ ra stderr → thanh tiến độ có thể đứng ở mức assemble trong vài giây remux. Không sửa trong phạm vi này.

## D11 — Kiểm thử

- **Decision**:
  - Unit test NUnit trong `app/XDM/XDM.Tests` (project này đã reference `XDM.Gtk.UI`, nên biên dịch được cả `XDM.Core`) cho các hàm thuần:
    - quyết định chuyển `ShouldRemux(flag, fileName)`
    - nhận diện chữ ký `LooksLikeMpegTs(path)`
    - prefix ở offset bất kỳ trong buffer và vượt buffer; hint false + auto-probe lỗi/nhầm định dạng vẫn gọi forced probe; parser không chấp nhận header ép MPEGTS thiếu codec/độ phân giải video
    - lỗi xoá .ts sau thành công và lỗi xoá .mp4 sau thất bại: đúng kết quả, cảnh báo có đường dẫn, bảo toàn nguồn
    - Stop cleanup lỗi từng file/cả hai; serialize/restore checkpoint; Resume liên tiếp vẫn Stopped, không Finished/rename/output mới; khôi phục quyền rồi Resume không trùng; fingerprint khác/chưa chốt thì không xoá file còn tồn tại
    - tham số FFmpeg `CreateRemuxArgs(in, out)`
    - đọc/ghi `.state` tương thích ngược (bản ghi cũ thiếu field → `false`; ghi rồi đọc lại → giữ giá trị).
  - Test tích hợp remux thật với TS chuẩn, prefix 187 byte, 65.537 byte và 1.048.577 byte; thêm TypeScript và dữ liệu rác có sync giả. Tự `Assert.Ignore` khi máy không có FFmpeg chỉ với test tích hợp. So sánh codec, độ phân giải, số packet video và thời lượng.
  - Còn lại kiểm thử thủ công theo [quickstart.md](./quickstart.md).
- **Rationale**: CI (`xdm-wpf-build.yml`) chỉ build/test WPF trên Windows và không chạy `XDM.Tests` của GTK, nên phần GTK phải verify cục bộ (AGENTS.md §3). Lỗi có sẵn `JsonParsingTest.cs:156` luôn fail trên Linux; không liên quan, nên chạy test với filter.

## D12 — Probe cùng binary FFmpeg và bằng chứng prefix

- **Decision**: `ProbeTs` chạy `-hide_banner -nostdin [-f mpegts] -i <infile>` không có output, đọc metadata stderr. Hint true → forced probe; hint false → auto-probe và forced fallback nếu chưa xác nhận video MPEGTS. Kiểm tra cancellation trước lượt fallback. Exit 1 do thiếu output không tự là lỗi: cần section `Input #0`, format MPEGTS, codec video khác `none`/`unknown` và width/height > 0. Không tạo hoặc xoá file khi probe. Metadata từ lượt forced thành công thay thế catalog của lượt auto nhầm định dạng.
- **Rationale**: Đã thử độc lập FFmpeg 8.0.1 trên fixture MPEG-2/AC-3 160×90, 2 giây. Chuẩn và prefix 187 byte: probe nhận mpegts, exit 1, remux exit 0, output 108.864 byte. Prefix 65.537 byte: auto-probe nhầm `mpeg` chỉ thấy audio, output 25.349 byte; khi có hint và chọn `-f mpegts`, probe thấy cả video/audio, remux exit 0, output 108.864 byte. TypeScript `const x: number = 1;` không có Input header, exit 183. Đây là thử nghiệm công cụ, không chứng minh XDM đã chạy được.
- **Bằng chứng bổ sung trong lượt plan này**: fixture MPEG-2/AC-3 160×90, 2 giây, prefix 1.048.577 byte: auto-probe exit 183, không metadata; forced probe exit 1, nhận cả video/audio; forced remux exit 0, output 121.824 byte. TypeScript và mẫu 10 packet sync giả đều không có metadata video khi forced probe (exit 187). Fixture bổ sung dùng sine 1.000 Hz nên không so byte count với fixture trước. Đây là thử nghiệm FFmpeg độc lập, chưa kiểm chứng parser/downloader XDM.
- **Alternatives considered**: chỉ dựa exit code probe nhận sai kết quả; chỉ auto-probe bỏ sót prefix vượt buffer; chỉ nhìn header ép MPEGTS có thể nhận sai văn bản/rác. Giữ default probesize/analyzeduration của FFmpeg, không giảm xuống để đạt benchmark giả.
- **Nguồn**: [FFmpeg streamcopy](https://ffmpeg.org/ffmpeg.html#Streamcopy), [format probing options](https://ffmpeg.org/ffmpeg-formats.html#Format-Options), [source MPEGTS](https://www.ffmpeg.org/doxygen/6.1/mpegts_8c_source.html). Các nguồn mô tả streamcopy/probing; cách kết hợp hint/probe là quyết định thiết kế từ thử nghiệm trên.

## D13 — Log luồng bị bỏ và số đo thực tế

- **Decision**: Bổ sung callback/parser riêng cho các lượt probe/remux trong runner FFmpeg hiện có; các lệnh merge/MP3 giữ semantics cũ. Parse Input stream `#0:index`, type và codec, dedup stream nếu in lại trong Program; parse source index trong `Stream mapping:`. Ghi từng stream không được map với `index`, `type`, `codec`, lý do bị bỏ, thay vì chỉ dump stderr. Với tập video/audio được map, mọi stream đều `copy`; không thử encode lại khi codec lỗi.
- **Rationale**: `FFmpegMediaProcessor.cs` đã đọc stderr nhưng chỉ giữ `lastLogLine`; đó chưa là log danh sách stream bị bỏ theo FR-012. Giữ raw log để chẩn đoán và thêm log có cấu trúc, không phụ thuộc ffprobe. Parser chỉ coi stream không được map là bị bỏ sau khi đã nhận được mapping; thiếu metadata phải log không xác định, không khẳng định danh sách rỗng.
- **Đo**: mỗi process sau WaitForExit ghi `TS->MP4 step=probe|remux input=... cpu_ms=...` từ `Process.TotalProcessorTime`, trước Dispose. Helper dùng Stopwatch ghi `TS->MP4 outcome=... input=... output=... elapsed_ms=... input_bytes=... output_bytes=...` từ trước nhận diện đến sau dọn tệp, trước gửi thông báo UI. Tổng CPU là tổng probe+remux của cùng input; không lấy `%CPU` ở top làm nghiệm thu SC-003.
- **Alternatives considered**: ffprobe bắt buộc thêm dependency; benchmark lệnh ffmpeg độc lập không chứng minh hiệu năng pipeline XDM; chỉ Log.Debug stderr không làm rõ stream bị bỏ.

## D14 — API tương thích hai framework và môi trường kiểm chứng

- **Decision**: Core dùng C# 9 + API .NET Framework 4.7.2; WPF build/kiểm chứng trên Windows, GTK trên Linux có native GTK3, sau một lần publish trim. Giữ ArgumentList trong nhánh NET5_0_OR_GREATER và quoting helper ở nhánh còn lại; dùng WaitForExit/Kill/byte[]/FileStream có sẵn, không thêm package hoặc nâng framework.
- **Rationale**: WPF csproj target net4.7.2; GTK/Tests net6.0. Trong môi trường của lượt plan này, `~/.dotnet/dotnet` không tồn tại và native GTK3 không có; FFmpeg 8.0.1 có sẵn. Quickstart ghi điều kiện cần cho lần kiểm chứng sau, không coi lịch sử toolchain trong AGENTS.md là bằng chứng hiện tại.
- **Alternatives considered**: chuyển WPF sang .NET 6 vượt phạm vi; build WPF trên Linux không thay được nghiệm thu giao diện Windows.

## D15 — Thiết kế nghiệm thu hiệu năng và lỗi dọn tệp

- **Decision**: Dùng mẫu TS khoảng 1 GB, ghi kích thước thực, ổ đĩa, thời lượng và trạng thái cache; lấy median 3 lượt xử lý XDM so với median 3 lượt copy flush trên cùng ổ. SC-002 áp dụng elapsed log toàn pipeline; SC-003 áp dụng tổng CPU log của mọi process probe/remux, kể cả fallback. Với hai mẫu có dung lượng chênh ≤5% và thời lượng khác ít nhất 2 lần, đặt `E = median(elapsed_ms)`, `B = input_bytes`, `C = median(copy_ms)`. Cả hai mẫu phải đạt `E <= 2*C`; đồng thời `R = (E_long/B_long)/(E_short/B_short) <= 1,25`. Đây là tiêu chí vận hành cho phần không tăng theo thời lượng trong SC-002; dung sai 25% dành cho nhiễu I/O/process startup, không là kết quả đã đo.
- **Độ ổn định phép đo**: Trong mỗi nhóm 3 lượt XDM/copy, `(max-min)/median <= 0,20`. Vượt ngưỡng → kết quả chưa kết luận, loại nhiễu và đo lại toàn bộ cặp mẫu theo cùng quy trình; không chọn riêng lượt nhanh để đánh dấu đạt. Nếu nhóm ổn định nhưng R > 1,25 thì không đạt, dù cả hai vẫn dưới 2× copy.
- **Rationale**: Mẫu 500 MB và `/usr/bin/time ffmpeg` riêng trong guide cũ không kiểm chứng SC-002/003 của XDM. Giữ byte rate khác nhau để tách kích thước khỏi thời lượng; dùng cùng chính sách cache cho hai phép đo.
- **Lỗi dọn tệp**: test đơn vị giả lập File.Delete ném IOException/UnauthorizedAccessException qua delegate internal của helper (production mặc định File.Delete; không thêm public service/setting). Kiểm chứng desktop trên dữ liệu QA riêng: giữ cả file khi xoá .ts lỗi, hoặc giữ .ts + .mp4 rác và cảnh báo hai lỗi; không thay trạng thái Completed theo hai câu trả lời đã chốt. Cancellation giữ Stopped/chunks và checkpoint lưu bền; retry dọn tệp trước rename, như D16.
- **Alternatives considered**: tạo framework filesystem abstraction toàn repo là thừa; chỉ kiểm tra exit FFmpeg bỏ sót lỗi cleanup; thao tác quyền trên dữ liệu người dùng thật không cần thiết.

## D16 — Checkpoint output và Resume khi Stop không dọn được tệp

- **Decision**: Hai downloader giữ path và stamp cho từng output đang xử lý. Stamp là chuỗi `length:creationTimeUtcTicks:lastWriteTimeUtcTicks`, lấy sau khi writer/process đóng; rỗng nghĩa là chưa chốt. AutoRename phải reserve bằng `FileMode.CreateNew`, retry khi tên vừa bị chiếm; Overwrite chỉ dùng chính sách người dùng đã chọn. Lưu path với stamp rỗng ngay sau reserve, trước ghi dữ liệu; save thất bại thì không ghi. Sau khi đóng writer/chờ process, cập nhật stamp và save trước cleanup. Dùng writer transaction hiện có, không thêm sidecar, DB hoặc filesystem service.
- **Rationale**: `FileHelper.GetUniqueFileName` chỉ kiểm tra tồn tại (`Util/FileHelper.cs:138–148`); `CreateTargetFile` dùng `FileMode.Create` (`:33`). Chọn tên lại trong HTTP `SortAndValidatePieces` (`SingleSourceHTTPDownloader.cs:313–317`) hoặc HLS `Assemble` (`MultiSourceDownloaderBase.cs:557–561`) sẽ tạo bản trùng khi output cũ không xoá được. Checkpoint giữ chính xác output của lượt này, không suy ra tên bằng glob hay xoá mọi `<tên>_*.mp4`.
- **Retry**: Ngay sau `RestoreState`, trước `OnStarted`, HLS `Init/ProbeTarget`, chọn tên hoặc assemble, kiểm tra checkpoint. Chỉ retry xoá khi chunks đủ để khôi phục và file còn khớp stamp; file không tồn tại thì clear entry. Path phải là đường dẫn tuyệt đối đã chọn trong TargetDir lưu cùng state, đúng extension và khác path chunks. Mismatch, stamp chưa chốt, thiếu chunks hoặc lỗi IO → giữ Stopped, cảnh báo đường dẫn/lý do, không xoá/ghi đè/tạo tên khác. Người dùng có thể xử lý file còn sót; Resume thấy file đã vắng sẽ clear entry và tiếp tục. Stamp metadata là kiểm tra thay đổi thông thường, không phải bảo đảm trước thay thế cố ý có cùng metadata.
- **Persist**: Mỗi entry chỉ clear và save sau khi file đã xoá/vắng; cleanup phần còn lại không làm mất checkpoint file còn sót. Khi Converted hoặc Failed đi tới Completed, clear checkpoint để không dọn file kết quả hợp lệ ở lượt sau; cảnh báo cleanup của Completed không biến thành công việc Resume. Khi crash giữa ghi, stamp chưa chốt → fail closed nếu file còn tồn tại, tránh xoá nhầm. Đây là giới hạn phục hồi tự động, không ảnh hưởng Stop thông thường đã đóng handle/chốt stamp.
- **Điểm gọi và completion**: HTTP retry ngay sau restore (`SingleSourceHTTPDownloader.cs:118`); HLS tại `MultiSourceDownloaderBase.cs:185`. Guard lần nữa trước rename trong assemble. HTTP `OnPieceCompleted` (`HTTPDownloaderBase.cs:268–269`) và Resume (`SingleSourceHTTPDownloader.cs:126–128`) đang gọi Finished vô điều kiện sau assemble. HLS Resume/Download (`MultiSourceDownloaderBase.cs:201–202,285–286`) cũng vậy. Thêm kiểm tra cancellation/cleanup-block trước Finished/OnComplete và DeleteFileParts. HTTP base dùng chung DualSource nên guard chỉ dựa cancellation hiện có, không đổi luồng thành công của DualSource. Cleanup-block đặt cancellation và phát OnCancelled một lần; Stop đã phát thì không phát lại. HLS không throw cancellation trước khi `_cancelRequestor` được tạo vì catch hiện dereference `_cancelRequestor.Error`.
- **API**: callback checkpoint từ Run về downloader để save path/stamp MP4; TS checkpoint do caller lưu trước/sau assemble. Helper retry cleanup nhận path/stamp và callback clear từng entry; không thêm interface filesystem. Các thao tác state trong assemble dùng writer dưới lock đang giữ, không gọi lại hàm tự lấy cùng lock.
- **Alternatives considered**: chỉ lưu path sau lỗi Stop mất thông tin khi crash trước save; luôn AutoRename tạo bản trùng; overwrite output chưa chứng minh thuộc lượt tải vi phạm FR-011. Staging/publish theo GUID cho toàn pipeline có thể phục hồi crash tốt hơn nhưng mở rộng thay đổi quá mức; metadata stamp và fail closed là lựa chọn nhỏ hơn.
