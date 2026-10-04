# Contract: `TsToMp4Remuxer` (XDM.Core)

**Vị trí**: `app/XDM/XDM.Core/Downloader/MediaProcessor/TsToMp4Remuxer.cs`, namespace `XDM.Core.MediaProcessor`. Phải thêm `<Compile Include>` vào `XDM.Core.projitems`.
**Ràng buộc**: C# 9, API tương thích WPF net4.7.2 và GTK net6.0. Không reflection hoặc ffprobe bắt buộc. Không đổi public API có sẵn ngoài các mục được liệt kê ở đây.

## API

```csharp
public enum RemuxOutcome { Skipped, Converted, Failed, Cancelled }

public static class TsToMp4Remuxer
{
    // Pure: flag && extension ".ts" (OrdinalIgnoreCase).
    public static bool ShouldRemux(bool convertTsToMp4, string targetFileName);

    // Hint: tìm 5 sync 0x47 cách nhau 188 byte trong buffer đầu tối đa 1 MiB.
    // false KHÔNG có nghĩa file không phải MPEG-TS; vẫn cần FFmpeg probe.
    public static bool LooksLikeMpegTs(string path);

    // Chạy toàn bộ bước: kiểm tra → chọn tên .mp4 → gọi media processor → dọn dẹp → thông báo.
    // Không ném exception khi FFmpeg lỗi; chỉ ném khi lỗi lập trình (đối số null).
    public static RemuxOutcome Run(BaseMediaProcessor mediaProcessor, string tsFile,
        CancelFlag cancel, Action<string, string> checkpointMp4,
        out string finalFile, out long finalSize);

    // Chỉ cho entry đã reserve/ghi bởi lượt này, đã kiểm tra TargetDir/chunks ở caller.
    // clearPending(true) cho TS, false cho MP4; callback clear và save từng entry.
    internal static bool TryCleanupPendingOutputs(string tsPath, string tsStamp,
        string mp4Path, string mp4Stamp, Action<bool> clearPending,
        out string warning);
}
```

`checkpointMp4(path, stamp)` cập nhật state và save bằng writer transaction hiện có: lần đầu sau reserve, trước ghi với stamp rỗng; lần sau khi process đã thoát/handle đã đóng với stamp `length:creationTimeUtcTicks:lastWriteTimeUtcTicks`. TS dùng cùng quy trình ở caller assemble. Save lỗi thì dừng pipeline, giữ chunks và Stopped, không báo Completed; lỗi callback được xử lý thành Cancelled/cleanup-block, không gom vào Failed hoàn tất .ts. Callback bên trong lock phải dùng writer dưới lock đang giữ, không lấy lock lặp lại. Các chữ ký mới chỉ áp dụng helper của feature, không thêm filesystem interface.

`BaseMediaProcessor` có thêm:

```csharp
public abstract MediaProcessingResult RemuxTsToMp4(string infile, string outfile,
    CancelFlag cancellationToken, out long outFileSize);

public abstract MediaProcessingResult ProbeTs(string infile, bool hasTsSignature,
    CancelFlag cancellationToken, out bool isTsVideo);
```

`ProbeTs` dùng cùng cơ chế binary, quoting và cancellation với runner hiện tại; args `-hide_banner -nostdin [-f mpegts] -i <infile>`. `hasTsSignature=true` → forced probe; false → auto-probe rồi forced fallback khi chưa xác nhận được video TS, kể cả auto-probe lỗi/nhận định dạng khác. Kiểm tra cancellation trước fallback. Parser yêu cầu Input MPEGTS, codec video khác `none`/`unknown` và width/height > 0; header MPEGTS do ép demuxer không đủ. Không coi exit 1 do thiếu output là thất bại nếu metadata hợp lệ; catalog của lượt xác nhận được dùng cho remux/log. `Success + isTsVideo=false` chỉ khi hết các lượt probe vẫn không có bằng chứng video TS; có hint hoặc metadata TS tự nhận diện nhưng video lỗi → `Failed`. Header từ forced probe đơn thuần không được tính là bằng chứng TS. `AppNotFound` và `Cancelled` giữ nghĩa hiện có. Không tạo output trong probe.

`FFmpegMediaProcessor.RemuxTsToMp4` chỉ được gọi khi probe đã xác nhận TS. Args cố định, có unit test:

```text
-hide_banner -nostdin -f mpegts -i <infile> -map 0:v? -map 0:a? -c copy <outfile> -y
```

## Hành vi của `Run`

| Điều kiện | Hành động | Kết quả |
|---|---|---|
| Văn bản nhỏ được kiểm tra toàn bộ, không có hint TS | Không gọi FFmpeg; log lý do không-media | `Skipped`, `finalFile = tsFile` |
| Hint false | Auto-probe FFmpeg; chưa xác nhận video TS thì thử forced MPEGTS probe, không giới hạn prefix theo buffer detector | Chưa chốt kết quả |
| Probe xác nhận không phải video TS, không có bằng chứng TS | Log `"TS->MP4 skipped: not MPEG-TS video"` | `Skipped`, `finalFile = tsFile` |
| Chọn tên | `<dir>/<name>.mp4`; AutoRename → GetUniqueFileName + reserve CreateNew, retry nếu vừa bị chiếm; Overwrite → giữ tên theo chính sách; save checkpoint trước FFmpeg ghi | — |
| `RemuxTsToMp4` → `Success` và `.mp4` > 0 byte | Xoá `tsFile`; nếu `FetchServerTimeStamp` thì do downloader đặt lại timestamp | `Converted`, `finalFile = .mp4`, `finalSize = len(.mp4)` |
| Nhánh thành công nhưng xoá .ts lỗi | Giữ cả hai; UI cảnh báo `MSG_TS_TO_MP4_CLEANUP_FAILED`, đường dẫn .ts và lý do; log | Vẫn `Converted`, trỏ .mp4 |
| Cancellation | Kill/chờ process, đóng handle/chốt stamp, thực hiện xoá `.mp4` dở và `tsFile`; mỗi file xoá/vắng clear entry và save; giữ chunks | `Cancelled`, không finalFile cho Completed |
| Cancellation và cleanup lỗi | Giữ checkpoint path/stamp file còn sót; cảnh báo đường dẫn/lý do; không chuyển sang nhánh Failed hoàn tất .ts | Vẫn `Cancelled`/Stopped |
| `AppNotFound` | Xoá `.mp4` dở dang nếu có; UI: `Confirm(MSG_FFMPEG_MISSING)` → `OpenBrowser(Links.HelperToolsUrl)` | `Failed`, `finalFile = tsFile` |
| Probe/Remux `Failed` hoặc `.mp4` 0 byte | Thực hiện xoá `.mp4` dở dang; UI: `ShowMessageBox(MSG_TS_TO_MP4_FAILED + "\n" + LastError)` | `Failed`, `finalFile = tsFile`, `finalSize = len(tsFile)` |
| Nhánh lỗi nhưng xoá .mp4 dở dang lỗi | Giữ .ts và file còn sót; gộp cảnh báo cleanup + đường dẫn + lỗi xoá vào thông báo lỗi chuyển đổi | Vẫn `Failed`; downloader hoàn tất với .ts |

- Kiểm tra cancellation trước probe, giữa probe/remux và sau khi process kết thúc; ưu tiên `Cancelled` trước xử lý success để không báo finished khi người dùng đã dừng.
- Không xoá file chưa reserve thuộc lượt tải. Retry cleanup chỉ xoá entry có stamp khớp, path hợp lệ trong TargetDir và chunks đủ khôi phục. Không còn file → clear entry; mismatch/stamp rỗng/IO lỗi → return false, giữ entry, cảnh báo. Không tìm/xoá file bằng glob. Nếu một entry đã dọn, save trước khi thử entry tiếp theo. Callback save lỗi phải chặn pipeline để không dùng checkpoint mất đồng bộ.
- Converted/Failed đi tới Completed thì caller clear checkpoint; file kết quả hợp lệ và file còn sót được cảnh báo ở Completed không là mục tiêu retry Resume. Không clear entry Cancelled còn tồn tại chỉ để đổi tên/tải tiếp.
- Mọi nhánh đều `Log.Debug` kết quả và lý do (FR-012). Từ catalog input dedup theo index và `Stream mapping`, ghi từng stream không được map với index/type/codec và lý do bỏ. Không dùng danh sách mapping thiếu làm bằng chứng không có stream bị bỏ.
- Runner probe/remux ghi `cpu_ms` từ TotalProcessorTime trước Dispose; helper ghi `elapsed_ms` toàn pipeline, input/output path và byte count. Nhánh merge/MP3 cũ không thay semantics. Xem research D13.
- Thông báo UI đi qua `ApplicationContext.Application?.RunOnUiThread(...)`. Nếu `Application == null` thì bỏ qua.
- Xoá file bọc `try/catch`; lỗi phải được log và cảnh báo như bảng, không đổi file kết quả. Test lỗi xoá qua delegate internal mặc định File.Delete; không thêm filesystem service công khai. Các lỗi IO đọc/chọn tên/output đi về Failed và giữ .ts, không ném ra ngoài như lỗi lập trình.

## Chỗ gọi (downloader)

| Downloader | Vị trí | Điều kiện gọi | Sau `Converted` |
|---|---|---|---|
| `SingleSourceHTTPDownloader.AssemblePieces` | sau khi ghi xong và **đã dispose `outfs`**, trước `DeleteFileParts()` | `!state.ConvertToMp3 && ShouldRemux(state.ConvertTsToMp4, TargetFileName)` | `TargetFileName = Path.GetFileName(finalFile)`, `totalSize = finalSize` |
| `MultiSourceDownloaderBase.Assemble`, nhánh `!_state.Demuxed` | sau `ConcatSegments(...)`, trước `DeleteFileParts()` | `ShouldRemux(_state.ConvertTsToMp4, TargetFileName)` | `TargetFileName = Path.GetFileName(finalFile)`, `_state.FileSize = finalSize` |

`Cancelled` → downloader `return` ngay, theo mẫu `if (cancel.IsCancellationRequested) return;` có sẵn.

**Guard caller bắt buộc**: Return trong assemble chưa đủ. HTTP `HTTPDownloaderBase.OnPieceCompleted` và `SingleSourceHTTPDownloader.Resume` chỉ gọi Finished khi không cancellation/cleanup-block; HLS hai caller Assemble và OnComplete có cùng guard. Không gọi DeleteFileParts khi bị dừng/chặn cleanup. Đặt cancellation và phát OnCancelled một lần; tránh double event nếu Stop đã phát. HLS cleanup bị chặn trước Init phải return có kiểm soát, không throw vào catch đang dùng `_cancelRequestor` chưa khởi tạo.

**Resume/restore**: Gọi `TryCleanupPendingOutputs` ngay sau RestoreState trước OnStarted, HLS Init/ProbeTarget, AutoRename hoặc assemble; guard lần nữa trước chọn tên. False → Stopped và cảnh báo; không probe, rename hay tạo output mới. True → tiếp tục bằng cờ đã lưu. Khởi động lại dùng cùng checkpoint. Crash giữa ghi có stamp rỗng thì file còn tồn tại cần xử lý thủ công; Resume sau khi file đã vắng clear entry và tiếp tục. Metadata stamp phát hiện thay đổi thông thường, không bảo đảm trước thay thế cố ý cùng metadata (research D16).
