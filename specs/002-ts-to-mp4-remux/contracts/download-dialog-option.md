# Contract: Tuỳ chọn "Convert to MP4" trong hộp thoại tải (GTK + WPF)

## Interface (XDM.Core/UI)

Thêm vào **cả** `INewDownloadDialog` và `INewVideoDownloadDialog`:

```csharp
bool ShowConvertToMp4Checkbox { get; set; }  // hiện/ẩn checkbox
bool IsConvertToMp4Checked { get; set; }     // trạng thái tick
event EventHandler FileNameChangedEvent;    // sửa tên trực tiếp hoặc bằng code
```

Có bốn nơi triển khai và phải sửa cả bốn:

| Nền tảng | Hộp thoại | File |
|---|---|---|
| GTK | Tải thường | `XDM.Gtk.UI/Dialogs/NewDownload/NewDownloadWindow.cs` + `glade/new-download-window.glade` |
| GTK | Tải video | `XDM.Gtk.UI/Dialogs/NewVideoDownload/NewVideoDownloadWindow.cs` + `glade/new-video-download-window.glade` |
| WPF | Tải thường | `XDM.Wpf.UI/Dialogs/NewDownload/NewDownloadWindow.xaml(.cs)` |
| WPF | Tải video | `XDM.Wpf.UI/Dialogs/NewVideoDownload/NewVideoDownloadWindow.xaml(.cs)` |

Widget: GTK `GtkCheckButton` (field `[UI]`), WPF `CheckBox`. Nhãn lấy từ khoá `LBL_CONVERT_TO_MP4`. Mặc định ẩn trong markup; controller quyết định hiện.

## Hành vi controller (XDM.Core/UI)

| Thời điểm | `NewDownloadDialogUIController` | `NewVideoDownloadDialogUIController` |
|---|---|---|
| Mở hộp thoại | `IsConvertToMp4Checked = true`; `ShowConvertToMp4Checkbox = IsTsVideo(SelectedFileName, contentType)` | như bên trái; `contentType` lấy từ tham số `ShowVideoDownloadDialog` |
| `UrlChangedEvent` (đổi URL → đổi tên) | tính lại `ShowConvertToMp4Checkbox` | — |
| `FileNameChangedEvent` | tính lại hiện/ẩn từ tên hiện tại, không reset trạng thái tick | như bên trái, gồm đổi tên do chọn MP3/video |
| Bấm "Download" / "Download later" | `StartDownload(..., convertTsToMp4: window.IsConvertToMp4Checked)` | `StartVideoDownload(..., convertTsToMp4: window.IsConvertToMp4Checked)` |

`IsTsVideo(name, contentType)` := đuôi `.ts` (không phân biệt hoa thường) **và** `contentType` rỗng hoặc không bắt đầu bằng `text/` và không chứa `javascript`/`typescript`. Chuẩn hoá so sánh MIME không phân biệt hoa thường. Khi chưa biết MIME thì chỉ suy ra từ tên; probe trong downloader xác nhận nội dung sau tải.

GTK nối trực tiếp `TxtFile.Changed`; WPF nối `TxtFile.TextChanged`, với handler có tên để tránh phụ thuộc reflection mới. Sự kiện chỉ thêm vào hai interface dialog; không đổi `IFileSelectable` chung. Controller tính lại trước cả Download và Download Later, nhưng không sửa tên hoặc tick trong handler đổi tên để tránh vòng lặp.

**Bất biến**

- Mỗi lần mở hộp thoại, checkbox luôn ở trạng thái tick, không nhớ lần trước (FR-002, US3-2).
- Bỏ tick → downloader nhận `false` → không có bước chuyển, hành vi giống trước khi có tính năng (FR-009).
- Checkbox ẩn không tự reset giá trị: ban đầu true; nếu người dùng đã bỏ tick thì giữ false khi đổi .ts → .mp4 → .ts trong cùng dialog. Khi mở dialog mới mới reset true. Downloader tự quyết theo tên cuối cùng và probe MPEG-TS ([ts-to-mp4-remuxer.md](./ts-to-mp4-remuxer.md)).
- Hộp thoại video có cả checkbox MP3 và MP4: chúng không đồng thời có nghĩa, vì tick MP3 đổi tên đích sang `.mp3` nên remux tự bỏ qua. Không cần ràng buộc UI giữa hai checkbox.

## Chuỗi (chỉ `app/XDM/Lang/English.txt`)

```text
LBL_CONVERT_TO_MP4=Convert to MP4
MSG_TS_TO_MP4_FAILED=The download finished, but XDM could not convert it to MP4. The original .ts file was kept.
MSG_TS_TO_MP4_CLEANUP_FAILED=XDM could not delete the following file.
```
