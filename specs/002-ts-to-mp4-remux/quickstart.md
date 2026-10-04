# Quickstart: Kiểm chứng chuyển .ts → .mp4 khi tải

**Feature**: [spec.md](./spec.md) | **Plan**: [plan.md](./plan.md) | **Contracts**: [contracts/](./contracts/)

Hướng dẫn kiểm chứng thủ công trên desktop thật (AGENTS.md §4). Bản GTK phải tự kiểm chứng ở máy này, vì CI chỉ build WPF. Bản WPF kiểm chứng trên Windows bằng các kịch bản tương tự.

Điều kiện chạy: SDK .NET 6 ở `~/.dotnet/dotnet`, native GTK3, FFmpeg và ffprobe (ffprobe chỉ phục vụ QA, ứng dụng không yêu cầu). Trong lượt cập nhật plan, SDK tại đường dẫn đó và GTK3 chưa có; các bước dưới đây là hướng dẫn nghiệm thu sau triển khai, chưa phải kết quả đã chạy. Máy Windows cần toolchain/targeting pack .NET Framework 4.7.2 và FFmpeg.

## 0. Chuẩn bị

```bash
# Chạy từ repo root; lưu đường dẫn trước khi tạo fixture
QA_REPO="$(pwd -P)"
QA_ROOT="$HOME/xdm-qa"
QA_HOME="$QA_ROOT/home"
QA_DEST="$QA_ROOT/downloads"
~/.dotnet/dotnet --version
pkg-config --modversion gtk+-3.0
ffmpeg -version | head -1
ffprobe -version | head -1

# Dữ liệu mẫu (thư mục tạm của bạn, ví dụ ~/xdm-qa)
mkdir -p "$QA_ROOT/www" "$QA_HOME" "$QA_DEST"
cd "$QA_ROOT/www"
ffmpeg -v error -y -f lavfi -i testsrc2=size=1280x720:rate=25 -f lavfi -i sine=f=440:sample_rate=48000 \
  -t 360 -c:v mpeg2video -b:v 20M -minrate 20M -maxrate 20M -bufsize 2M \
  -c:a ac3 -muxrate 22000000 -f mpegts big.ts                               # khoảng 1 GB
ffmpeg -v error -y -f lavfi -i testsrc2=size=1280x720:rate=25 -f lavfi -i sine=f=440:sample_rate=48000 \
  -t 720 -c:v mpeg2video -b:v 10M -minrate 10M -maxrate 10M -bufsize 2M \
  -c:a ac3 -muxrate 11000000 -f mpegts long.ts                              # dung lượng tương đương, thời lượng gấp 2
ffmpeg -v error -y -f lavfi -i testsrc2=size=720x576:rate=25 -f lavfi -i sine=f=440:sample_rate=48000 \
  -t 30 -c:v mpeg2video -c:a ac3 -f mpegts dvd.ts                              # giống DVD
ffmpeg -v error -y -f lavfi -i testsrc2=size=720x576:rate=25 \
  -f lavfi -i "sine=f=440:sample_rate=48000,pan=stereo|c0=c0|c1=c0" \
  -t 5 -c:v mpeg2video -c:a s302m -strict -2 -f mpegts bad.ts                 # MP4 không chứa được s302m
echo 'const x: number = 1;' > code.ts                                          # TypeScript, không phải video
python3 - <<'PY'
from pathlib import Path
source = Path('dvd.ts').read_bytes()
Path('prefix.ts').write_bytes(b'X' * 187 + source)
Path('prefix-long.ts').write_bytes(b'X' * 65537 + source)
Path('prefix-over-buffer.ts').write_bytes(b'X' * 1048577 + source)
Path('fake-sync.ts').write_bytes((b'G' + b'X' * 187) * 10)
PY
mkdir -p hls && ffmpeg -v error -y -i dvd.ts -c copy -f hls -hls_time 4 -hls_playlist_type vod hls/index.m3u8
python3 -m http.server 8765 --directory "$QA_ROOT/www" &

# Build + chạy XDM với HOME riêng để không đụng dữ liệu/instance thật (AGENTS.md §5)
cd "$QA_REPO"
~/.dotnet/dotnet publish "$QA_REPO/app/XDM/XDM.Gtk.UI/XDM.Gtk.UI.csproj" -c Release -r linux-x64 --self-contained
QA_APP="$QA_REPO/app/XDM/XDM.Gtk.UI/bin/Release/net6.0/linux-x64/publish/xdm-app"
HOME="$QA_HOME" XDM_DEBUG_MODE=1 "$QA_APP"
# Log: $QA_HOME/.xdm-app-data/log.txt
```

Dùng bản **publish** (đã trim) để phát hiện luôn lỗi trimming với widget `[UI]` mới (research D9).

Nếu instance QA đã chạy, thoát instance đó trước khi đổi binary hoặc môi trường FFmpeg; giữ nguyên instance/dữ liệu thật của người dùng. Các biến QA vẫn ở shell đã chạy khối chuẩn bị; với shell mới, đặt lại đường dẫn QA.

## 1. Unit test (một lần sau khi code xong)

```bash
~/.dotnet/dotnet test app/XDM/XDM.Tests/XDM.Tests.csproj --filter "FullyQualifiedName~TsToMp4"
```

Kỳ vọng: tất cả pass. Test tích hợp remux thật tự `Ignore` nếu không có FFmpeg. Không chạy cả suite: `JsonParsingTest` có lỗi sẵn trên Linux.

## 2. US1: link .ts trực tiếp, mặc định → nhận .mp4

1. Trong XDM: **New download** → URL `http://127.0.0.1:8765/dvd.ts`.
2. ✅ Checkbox **Convert to MP4** hiện và **đã tick**.
3. Bấm **Download now**, chờ hoàn tất.
4. ✅ Thư mục đích chỉ có `dvd.mp4`, không còn `dvd.ts`.
5. ✅ Chạy `ffprobe -v error -show_entries stream=codec_name,width,height:format=duration -of compact` cho file đầu ra trong `$QA_DEST` và file nguồn `$QA_ROOT/www/dvd.ts` → `mpeg2video` 720×576 + `ac3`, thời lượng lệch ≤ 1 s (SC-004). So sánh nguồn fixture, vì .ts trong thư mục đích đã bị xoá.
6. ✅ Trong danh sách hoàn tất: tên `dvd.mp4`, kích thước bằng kích thước file `.mp4`. **Open file** và **Open folder** trỏ tới `.mp4` (FR-007).
7. ✅ Log có dòng kết quả remux (FR-012).

## 3. US2: tốc độ và CPU (SC-002, SC-003)

1. Ghi kích thước `big.ts`/`long.ts` bằng `stat -c '%n %s' "$QA_ROOT/www/big.ts" "$QA_ROOT/www/long.ts"`, thời lượng bằng ffprobe và filesystem bằng `df -T "$QA_ROOT/www" "$QA_DEST"`. Hai mẫu phải chênh kích thước ≤5%, thời lượng khác ít nhất 2 lần; nếu chưa đạt, điều chỉnh fixture trước đo. Mẫu khoảng 1 GB nghĩa là 0,95–1,05 GB thập phân, ghi số byte thực tế.
2. Tải `big.ts` tới `$QA_DEST` với mặc định, giữ máy không có lượt tải/remux khác. Sau một lượt làm nóng cache không tính điểm, thực hiện 3 lượt đo, dùng tên mới theo AutoRename. Lấy `elapsed_ms` trong log `TS->MP4 outcome=Converted` của từng input: gồm detector, probe, remux và cleanup; không đo bằng việc nhìn thanh tiến độ. Ghi CPU từng process `step=probe|remux` có cùng input và cộng lại.
3. Đo 3 lượt copy trên **cùng ổ và cùng chính sách cache nóng**, không reflink, có flush. Chạy `cat "$QA_ROOT/www/big.ts" > /dev/null` trước mỗi lượt copy; lượt XDM đọc chính file .ts vừa ghép nên cũng ở cache nóng. Dùng lệnh dưới; ghi từng số `copy_s` và median. Xoá chỉ file `copy-benchmark.ts` của QA giữa các lượt. ✅ Median elapsed XDM ≤2× median copy (SC-002).
4. ✅ Với mỗi lượt, tổng `cpu_ms` probe+remux ≤5% thời lượng video ×1000 (SC-003); với 360 s thì ≤18.000 ms. Không dùng `top` hay chạy FFmpeg riêng để thay số đo XDM. Có thể xem top để quan sát, nhưng thiếu CPU log không được coi là pass.
5. Lặp lại với `long.ts`, cùng máy/ổ/cache. Đặt `E = median(elapsed_ms)`, `C = median(copy_ms)`, `B = input_bytes`. ✅ Cả hai mẫu đạt `E <= 2*C` và `R = (E_long/B_long)/(E_big/B_big) <= 1,25`: thời gian trên mỗi byte không tăng quá 25% khi thời lượng ít nhất gấp đôi. Trong mỗi nhóm 3 lượt XDM/copy phải có `(max-min)/median <= 0,20`; vượt ngưỡng là **chưa kết luận**, xử lý nhiễu rồi đo lại toàn bộ cặp mẫu. Nhóm ổn định mà R > 1,25 là **không đạt**. Dùng `ffprobe -count_packets -show_entries stream=codec_name,width,height,nb_read_packets:format=duration -of json` cho nguồn/kết quả: codec, độ phân giải và số packet video giữ nguyên, thời lượng lệch ≤1 s. Giữ log, số byte, duration, 3 số đo mỗi nhóm, median và R làm bằng chứng; packet count không đồng nghĩa frame count cho mọi codec.

```bash
/usr/bin/time -f 'copy_s=%e' sh -c \
  'cp --reflink=never -- "$1" "$2" && sync -f "$2"' \
  sh "$QA_ROOT/www/big.ts" "$QA_DEST/copy-benchmark.ts"
```

Không so bitrate toàn container TS với MP4: overhead container khác nhau. Xác nhận không mã hoá lại bằng codec + Stream mapping `(copy)` và tiêu chí SC-004; nếu cần đếm khung hình cho US2-3, dùng `ffprobe -count_frames` như một bước QA ngoài khoảng thời gian benchmark.

## 4. US3: bỏ tick → giữ .ts

1. New download → `http://127.0.0.1:8765/dvd.ts`, **bỏ tick** Convert to MP4, tải.
2. ✅ Chỉ có `dvd.ts` (hoặc `dvd_1.ts` nếu trùng), không có `.mp4`, log không có remux (FR-009).
3. Mở lại New download với một URL `.ts` bất kỳ → ✅ checkbox **đã tick lại** (FR-002).

## 5. US4: chuyển đổi thất bại không mất file

1. Tải `http://127.0.0.1:8765/bad.ts` với tuỳ chọn mặc định.
2. ✅ Hiện thông báo `MSG_TS_TO_MP4_FAILED` kèm lý do ("codec not currently supported in container").
3. ✅ Mục tải ở danh sách **hoàn tất**, tên `bad.ts`. Thư mục đích có `bad.ts` nguyên vẹn (cùng kích thước nguồn) và **không** có `bad.mp4`, kể cả file 0 byte (FR-008, SC-005).

## 6. US4: thiếu FFmpeg

1. Thoát instance QA. Chạy binary tuyệt đối với PATH QA không có FFmpeg, không có FFmpeg trong thư mục app và bỏ `FFMPEG_HOME`: `env -u FFMPEG_HOME HOME="$QA_HOME" PATH=/usr/local/sbin:/sbin XDM_DEBUG_MODE=1 "$QA_APP"`. Không đổi binary FFmpeg của máy thật.
2. Tải `dvd.ts`.
3. ✅ Hộp thoại hỏi về FFmpeg (`MSG_FFMPEG_MISSING`); chọn **Yes** thì mở trang hướng dẫn.
4. ✅ Mục tải hoàn tất với `dvd.ts` nguyên vẹn.

## 7. Edge cases

| # | Thao tác | Kỳ vọng |
|---|---|---|
| E1 | Tải `http://127.0.0.1:8765/code.ts` (mặc định), cả khi có/thiếu FFmpeg | Hoàn tất với `code.ts`, **không** thông báo lỗi; log lý do skipped không-media |
| E2 | Tạo sẵn `dvd.mp4` trong thư mục đích, cấu hình *Auto rename*, tải `dvd.ts` | Có `dvd_1.mp4` (hoặc tên duy nhất theo XDM); `dvd.mp4` cũ không bị ghi đè (FR-011) |
| E3 | New download với URL không phải `.ts` (`.../hls/index.m3u8` hoặc `.mp4`) | Checkbox **ẩn** |
| E4 | Tải `big.ts`, bấm **Stop** trong lúc *Assembling/remux*, quyền xoá bình thường | Mục tải về **Stopped**; process đã thoát, không còn output lượt này, chunks giữ nguyên; Resume → hoàn tất .mp4, không sinh thêm .ts/.mp4 do output của lượt bị huỷ (FR-016). Nhánh lỗi quyền xem §10 |
| E5 | Bắt đầu tải `big.ts`, **Stop** khi đang tải, thoát XDM, mở lại, **Resume** | Kết quả cuối vẫn là `.mp4` (cờ được lưu, FR-010) |
| E6 | Bỏ tick, **Stop** khi đang tải, khởi động lại, **Resume** | Kết quả cuối là `.ts` |
| E7 | Có download dở dang tạo bằng bản XDM **cũ** (trước tính năng), nâng cấp rồi Resume | Resume bình thường, không lỗi đọc state; kết quả `.ts` (bản ghi cũ → cờ `false`) |
| E8 | Trong mỗi loại dialog, sửa tên .ts → .mp4 → .ts, không đổi URL | Checkbox ẩn → hiện; mặc định tick nếu chưa sửa lựa chọn; nếu đã bỏ tick thì vẫn bỏ tick trong cùng dialog |
| E9 | Tải `prefix.ts`, `prefix-long.ts` và `prefix-over-buffer.ts` với mặc định | Cả ba thành .mp4 có video/audio như dvd.ts; mẫu prefix 1.048.577 byte phải qua forced fallback dù hint false; không chỉ có audio; thời lượng lệch ≤1 s; .ts xoá được thì bị xoá |
| E10 | Dùng fixture MPEGTS QA có Subtitle/Data không được map | .mp4 giữ video/audio; log liệt kê từng stream bị bỏ với index/type/codec/lý do |
| E11 | Tải `fake-sync.ts`; kiểm thử parser bằng header MPEGTS nhưng thiếu codec/độ phân giải video | Không tạo .mp4, không nhận diện thành video chỉ do sync/header giả; giữ .ts và log lý do |

## 8. Nguồn video từ trình duyệt (FR-013b)

Cần extension XDM trong trình duyệt, đã kết nối với instance QA.

1. Mở `http://127.0.0.1:8765/hls/index.m3u8` (hoặc trang có video HLS thật) để XDM bắt được stream.
2. Chọn video trong danh sách bắt được → hộp thoại **tải video** hiện tên `….ts` → ✅ checkbox **Convert to MP4** hiện và đã tick.
3. Tải → ✅ kết quả là `.mp4`, không còn `.ts`. Bỏ tick ở lần khác → ✅ kết quả là `.ts`.

## 9. Windows (WPF)

Build trên Windows từ repo root: `dotnet build app/XDM/XDM.Wpf.UI/XDM.Wpf.UI.csproj -c Release` (net4.7.2; không chạy lệnh WPF này trên máy Linux để coi là nghiệm thu Windows). Lặp lại §2, §4–§8 và §10 với bản WPF, gồm cả dialog tải thường/video, prefix, sửa tên, thiếu FFmpeg, Stop/Resume và restart. Có thể dùng server fixture chạy trên Linux, truy cập bằng IP thay cho localhost.

✅ File .ts thực sự bị xoá sau remux bình thường (không còn handle mở), checkbox mặc định bật mỗi lần mở, kết quả và cảnh báo giống GTK. Ghi OS, phiên bản FFmpeg, commit/build và kết quả mỗi kịch bản; CI build WPF không thay được kiểm chứng các hành vi này.

## 10. Lỗi dọn tệp sau khi chuyển đổi

Unit test lỗi File.Delete qua delegate internal trong bộ TsToMp4 là kiểm chứng xác định được trên mọi hệ điều hành. Kiểm chứng desktop dùng **thư mục QA riêng**. Trên Linux, có thể tạo `$QA_DEST/cleanup-test` với sticky bit (`chmod 1777`) và nhờ tài khoản QA thứ hai tạo file .ts đích: XDM có quyền mở/ghi nhưng không được unlink file không thuộc sở hữu nó; chọn Overwrite chỉ trong profile QA. Không dùng root để chạy XDM. Trên Windows dùng ACL của thư mục QA cho phép ghi nhưng từ chối Delete/Delete child đối với file mục tiêu. Chuẩn bị quyền trước tải để tránh phải bấm đúng thời điểm remux.

| Kịch bản | Nguồn và điều kiện | Kết quả bắt buộc |
|---|---|---|
| Xoá .ts lỗi | `dvd.ts`, file .ts đích không được xoá nhưng .mp4 tạo được | Completed, tên/kích thước và Open file trỏ .mp4; giữ .ts; cảnh báo nêu lý do/đường dẫn .ts; log cleanup error |
| Xoá .mp4 dở lỗi | `bad.ts`, tạo sẵn file bad.mp4 của tài khoản QA thứ hai có quyền ghi nhưng không unlink; profile QA chọn Overwrite | Completed với .ts nguyên vẹn; .mp4 dở vẫn còn; thông báo chứa lỗi codec lẫn lỗi xoá/đường dẫn .mp4; không mở file dở làm kết quả |
| Stop, xoá .ts lỗi | `big.ts`, deny Delete cho .ts theo cấu hình QA nêu trên; bấm Stop lúc remux | Stopped, process thoát/chunks giữ; file còn sót có checkpoint path/stamp; cảnh báo nêu path/lý do, không nói Completed |
| Stop, xoá .mp4 dở lỗi | `big.ts`, deny Delete cho .mp4 đã cho phép Overwrite trong profile QA; Stop lúc remux | Stopped và checkpoint .mp4 còn sót; không Finished/Open file dở; giữ chunks |
| Resume còn bị chặn, rồi restart | Giữ nguyên quyền gây lỗi; bấm Resume hai lần, thoát/mở lại XDM QA và Resume | Mỗi lần vẫn Stopped; checkpoint còn; không ghép, không remux, không tạo tên AutoRename mới, không xoá chunks |
| Resume sau khôi phục quyền | Khôi phục quyền xoá của file QA còn sót, không đổi lựa chọn Convert to MP4 | Dọn đúng path checkpoint, clear/save từng entry; ghép/remux lại; hoàn tất .mp4 theo cờ đã lưu, không có output trùng của lượt bị huỷ |
| File checkpoint đã thay đổi hoặc stamp chưa chốt | Sau Stop sửa file QA còn sót; hoặc unit test mô phỏng crash giữa ghi trước stamp | File còn tồn tại không bị xoá/ghi đè; Stopped và cảnh báo. Tự xử lý file QA rồi Resume → clear checkpoint vắng, tiếp tục bình thường |

Thực hiện các kịch bản Stop/Resume với cả HTTP trực tiếp và HLS muxed, gồm lỗi xoá từng file và cả hai file trong unit test. Kiểm tra log không có Finished sau Cancelled, state checkpoint sống qua restart và chỉ clear entry đã dọn. Unit test còn phải phủ state cũ không có extension, state chỉ có bool và checkpoint bị cắt hỏng (không tự coi là rỗng).

Nếu máy không có tài khoản QA thứ hai hoặc quyền ACL cần thiết, ghi kịch bản desktop chưa chạy và dùng kết quả unit test để kiểm chứng nhánh lỗi; không đánh dấu desktop pass. Khôi phục quyền và cấu hình QA sau thử nghiệm. Stamp metadata chỉ phát hiện thay đổi thông thường; crash giữa ghi có stamp chưa chốt sẽ cần xử lý file còn sót trước Resume tự động. Không đánh dấu trường hợp này đã phục hồi tự động.
