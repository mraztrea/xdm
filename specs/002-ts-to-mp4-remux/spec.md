# Feature Specification: Chuyển video .ts sang .mp4 khi tải (remux, không mã hoá lại)

**Feature Branch**: `002-ts-to-mp4-remux`
**Created**: 2026-10-04
**Status**: Draft
**Input**: User description: "Khi tải 1 file .ts (video dvd) hãy thêm tùy chọn \"Convert sang .mp4\". Mặc định là enable. Convert theo phương pháp mà không sử dụng CPU (không phải re-encode)."

## Clarifications

### Session 2026-10-04

- Q: Tính năng áp dụng cho những nguồn tải nào? → A: Cả hai — link file .ts trực tiếp (hộp thoại tải thường) và video streaming bắt từ trình duyệt được lưu thành .ts (hộp thoại tải video).
- Q: Tính năng có trên nền tảng nào? → A: Cả bản Linux (GTK) và Windows (WPF); logic chuyển đổi dùng chung, mỗi giao diện thêm tuỳ chọn trong hộp thoại tải.
- Q: Sau khi chuyển sang .mp4 thành công, xử lý file .ts gốc thế nào? → A: Xoá file .ts, chỉ giữ lại .mp4; nếu không xoá được .ts thì áp dụng ngoại lệ cảnh báo bên dưới.
- Q: Nếu file .mp4 đã chuyển đổi thành công nhưng XDM không xoá được file .ts gốc, mục tải nên kết thúc thế nào? → A: Đánh dấu hoàn tất, trỏ tới .mp4; giữ .ts, hiển thị cảnh báo không xoá được và ghi log.
- Q: XDM có cần chuyển cả video .ts có vài byte dư ở đầu file, nếu công cụ xử lý media vẫn đọc được video đó không? → A: Có; chuyển cả những file này khi hình/tiếng tương thích với .mp4, vẫn không mã hoá lại.
- Q: Nếu chuyển đổi thất bại và XDM cũng không xoá được file .mp4 dở dang, mục tải nên kết thúc thế nào? → A: Hoàn tất với .ts; báo lỗi chuyển đổi và lỗi xoá .mp4 dở dang, kèm đường dẫn; ghi log.

### Điều chỉnh khi lập plan (2026-10-04)

- Tách trường hợp *dừng khi đang chuyển đổi* khỏi FR-008, thêm FR-016. Lý do: lệnh dừng của XDM là dừng cả lượt tải (xem research D4).
- SC-003 đổi sang tổng thời gian CPU so với thời lượng video, vì "% CPU trung bình" của một lượt remux vài giây không đo được ổn định.
- US1-3 và FR-006 dùng nhãn giai đoạn hậu xử lý có sẵn ("Assembling") thay cho một nhãn "đang chuyển đổi" mới (research D10).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Tải video .ts và nhận về file .mp4 (Priority: P1)

Người dùng tải một video có định dạng .ts (MPEG transport stream — kiểu file thường gặp khi ghi/rip DVD, truyền hình hoặc video streaming). Hộp thoại tải xuống hiển thị tuỳ chọn "Convert sang .mp4" đã được tick sẵn. Người dùng giữ nguyên và bấm tải. Khi tải xong, XDM đóng gói lại nội dung sang .mp4 mà **không mã hoá lại** hình và tiếng; người dùng nhận được một file .mp4 mở được trên hầu hết trình phát, điện thoại, TV và trình duyệt.

**Why this priority**: Đây là toàn bộ giá trị của yêu cầu. File .ts khó dùng (nhiều thiết bị/trình phát không hỗ trợ, tua chậm, không có thời lượng chính xác), còn .mp4 thì dùng được ngay. Mặc định bật để người dùng không phải làm gì thêm.

**Independent Test**: Tải một file video .ts mẫu với tuỳ chọn để mặc định; khi trạng thái là hoàn tất, thư mục đích chứa file .mp4 cùng tên, phát được, có cùng thời lượng, độ phân giải và chất lượng như bản gốc.

**Acceptance Scenarios**:

1. **Given** người dùng mở hộp thoại tải một file video .ts, **When** hộp thoại hiện ra, **Then** có tuỳ chọn "Convert sang .mp4" và tuỳ chọn này đang được bật.
2. **Given** tuỳ chọn đang bật và việc xoá file .ts không gặp lỗi, **When** quá trình tải hoàn tất, **Then** XDM tự động chuyển file sang .mp4 và kết quả cuối cùng trong thư mục đích chỉ là file `<tên>.mp4` (không còn file .ts).
3. **Given** quá trình chuyển đổi đang chạy, **When** người dùng nhìn danh sách tải/cửa sổ tiến độ, **Then** mục tải hiển thị trạng thái xử lý sau tải (giai đoạn "Assembling" có sẵn của XDM, khác với "đang tải") và chỉ chuyển sang "hoàn tất" khi file .mp4 đã sẵn sàng.
4. **Given** chuyển đổi thành công, **When** người dùng mở file .mp4, **Then** hình và tiếng giống hệt bản gốc (không giảm chất lượng, cùng độ phân giải, cùng thời lượng).
5. **Given** chuyển đổi thành công, **When** người dùng dùng các thao tác "Mở file" / "Mở thư mục" của XDM trên mục tải đó, **Then** các thao tác này trỏ tới file .mp4.
6. **Given** người dùng bắt được một video streaming từ trình duyệt mà XDM sẽ lưu thành .ts, **When** hộp thoại tải video hiện ra, **Then** tuỳ chọn "Convert sang .mp4" cũng có mặt và đang bật, với cùng hành vi như link .ts trực tiếp.
7. **Given** tính năng trên bản Linux và bản Windows, **When** người dùng tải cùng một file .ts với tuỳ chọn mặc định, **Then** cả hai bản cho kết quả như nhau.
8. **Given** file .mp4 đã hoàn chỉnh, **When** XDM không xoá được file .ts gốc, **Then** mục tải vẫn được đánh dấu hoàn tất và trỏ tới .mp4; file .ts được giữ lại, người dùng nhận cảnh báo nêu lý do không xoá được và đường dẫn file .ts, đồng thời lỗi được ghi log.
9. **Given** video .ts có byte dư ở đầu file nhưng công cụ xử lý media đọc được và luồng hình/tiếng tương thích với .mp4, **When** tải xong với tuỳ chọn chuyển đổi bật, **Then** XDM vẫn chuyển sang .mp4 mà không mã hoá lại; việc xoá .ts tuân theo FR-015.

---

### User Story 2 - Chuyển đổi nhanh, gần như không tốn CPU (Priority: P1)

Người dùng tải video .ts dung lượng lớn (vài GB, ví dụ rip từ DVD). Bước chuyển sang .mp4 diễn ra nhanh, không làm máy nóng/chậm, vì XDM chỉ đóng gói lại (remux) các luồng hình/tiếng sẵn có chứ không mã hoá lại.

**Why this priority**: Đây là ràng buộc rõ ràng của người dùng ("không sử dụng CPU", "không phải re-encode"). Nếu chuyển đổi bằng cách mã hoá lại thì vừa chậm (có thể lâu hơn cả thời gian tải), vừa giảm chất lượng — vi phạm yêu cầu.

**Independent Test**: Chuyển một file .ts khoảng 1 GB; đo thời gian và mức sử dụng CPU trong lúc chuyển; so sánh luồng hình/tiếng của file kết quả với bản gốc.

**Acceptance Scenarios**:

1. **Given** một file .ts khoảng 1 GB vừa tải xong, **When** XDM chuyển sang .mp4, **Then** quá trình hoàn tất trong thời gian xấp xỉ thời gian sao chép file đó trên cùng ổ đĩa (không phụ thuộc vào thời lượng video).
2. **Given** chuyển đổi đang chạy, **When** người dùng theo dõi tải hệ thống, **Then** mức dùng CPU thấp (không chiếm trọn một nhân CPU kéo dài) và máy vẫn dùng bình thường.
3. **Given** chuyển đổi hoàn tất, **When** so sánh luồng hình và tiếng của .mp4 với .ts gốc, **Then** codec, độ phân giải, bitrate và số khung hình giữ nguyên.

---

### User Story 3 - Giữ nguyên .ts khi người dùng tắt tuỳ chọn (Priority: P2)

Người dùng cần giữ file .ts gốc (ví dụ để biên tập, hoặc vì thiết bị của họ đọc .ts tốt). Họ bỏ tick "Convert sang .mp4" trong hộp thoại tải; XDM tải và giữ file .ts như hành vi hiện tại.

**Why this priority**: Vì tuỳ chọn mặc định bật, người dùng phải có cách tắt cho từng lần tải. Đây là đường thoát, ít người dùng hơn luồng chính.

**Independent Test**: Tải một file .ts sau khi bỏ tick tuỳ chọn; kết quả là file .ts, không có file .mp4 nào được tạo, không có bước chuyển đổi.

**Acceptance Scenarios**:

1. **Given** người dùng bỏ tick "Convert sang .mp4", **When** tải xong, **Then** thư mục đích chỉ chứa file .ts, giống hệt hành vi trước khi có tính năng này.
2. **Given** người dùng đã bỏ tick ở một lần tải, **When** họ mở hộp thoại tải cho một file .ts khác, **Then** tuỳ chọn lại được bật mặc định.

---

### User Story 4 - Chuyển đổi thất bại không làm mất file đã tải (Priority: P2)

Một số file .ts chứa luồng mà định dạng .mp4 không chứa được nếu không mã hoá lại, hoặc máy thiếu công cụ xử lý media, hoặc ổ đĩa hết chỗ. Khi chuyển đổi thất bại, người dùng vẫn giữ được file .ts đã tải và được báo rõ lý do.

**Why this priority**: Tải lại một file vài GB rất tốn kém; tính năng tiện ích không được phép làm mất dữ liệu người dùng đã tải.

**Independent Test**: Chuyển một file .ts cố tình không tương thích (hoặc chạy khi thiếu công cụ xử lý media); mục tải kết thúc với file .ts nguyên vẹn trong thư mục đích và một thông báo nêu lý do chuyển đổi thất bại.

**Acceptance Scenarios**:

1. **Given** chuyển đổi thất bại và việc xoá file .mp4 dở dang không gặp lỗi, **When** quá trình kết thúc, **Then** file .ts gốc vẫn nằm ở thư mục đích, nguyên vẹn, và không còn file .mp4 dở dang nào.
2. **Given** chuyển đổi thất bại, **When** người dùng xem mục tải, **Then** mục tải được coi là đã tải xong (file .ts dùng được) kèm thông báo dễ hiểu rằng việc chuyển sang .mp4 không thành công và lý do.
3. **Given** máy không có công cụ xử lý media cần thiết, **When** tải xong một file .ts với tuỳ chọn bật, **Then** người dùng được báo thiếu công cụ (và cách khắc phục theo cơ chế XDM đang dùng cho các tính năng video khác), file .ts được giữ lại.
4. **Given** chuyển đổi thất bại và XDM không xoá được file .mp4 dở dang, **When** quá trình kết thúc, **Then** file .ts gốc vẫn nguyên vẹn, mục tải được đánh dấu hoàn tất và trỏ tới .ts; người dùng được báo cả lỗi chuyển đổi lẫn lý do không xoá được .mp4 dở dang, kèm đường dẫn file còn sót để tự xoá; cả hai lỗi được ghi log.

---

### Edge Cases

- **Phần mở rộng .ts nhưng không phải video** (ví dụ file mã nguồn TypeScript): tuỳ chọn không được hiển thị khi XDM biết chắc đó không phải video (loại nội dung máy chủ trả về là văn bản/mã nguồn). Nếu vẫn lọt vào bước chuyển đổi và thất bại, áp dụng User Story 4 (giữ nguyên file).
- **Video .ts có byte dư ở đầu file**: nếu công cụ xử lý media vẫn đọc được video và luồng hình/tiếng tương thích với .mp4, hệ thống vẫn chuyển đổi khi tuỳ chọn bật; không được bỏ qua chỉ vì video không bắt đầu ngay tại byte đầu tiên của file.
- **Đã có file `<tên>.mp4` trong thư mục đích**: áp dụng đúng quy tắc xử lý trùng tên mà XDM đang dùng cho file tải về; không được ghi đè im lặng lên file của người dùng.
- **Người dùng đổi tên file trong hộp thoại**: nếu đổi phần mở rộng khỏi .ts thì tuỳ chọn không còn áp dụng; nếu đổi lại thành .ts thì tuỳ chọn áp dụng như bình thường.
- **Người dùng dừng/huỷ khi đang chuyển đổi**: lệnh dừng của XDM là dừng cả lượt tải: dừng chuyển đổi, xoá file .mp4 dở dang, mục tải về trạng thái dừng; dữ liệu đã tải được giữ để tiếp tục, và khi tiếp tục thì XDM ghép + chuyển đổi lại (FR-016).
- **XDM bị tắt hoặc máy khởi động lại khi đang tải/chuyển đổi**: lựa chọn bật/tắt chuyển đổi của mục tải được giữ lại; khi tiếp tục tải, kết quả cuối cùng vẫn theo lựa chọn ban đầu.
- **Không đủ dung lượng đĩa cho file .mp4** (cần tạm thời chỗ trống xấp xỉ bằng kích thước file .ts): chuyển đổi thất bại theo User Story 4.
- **Không xoá được .ts sau khi chuyển đổi thành công**: giữ cả file .mp4 hoàn chỉnh và file .ts gốc; mục tải hoàn tất và trỏ tới .mp4, hiển thị cảnh báo nêu lý do và đường dẫn .ts để người dùng tự xoá, đồng thời ghi log. Đây là lỗi dọn tệp, không phải lỗi chuyển đổi ở FR-008.
- **Không xoá được .mp4 dở dang sau khi chuyển đổi thất bại**: giữ .ts nguyên vẹn, mục tải hoàn tất và trỏ tới .ts; thông báo cả lỗi chuyển đổi lẫn lý do không xoá được file .mp4 dở dang, kèm đường dẫn file còn sót để người dùng tự xoá; ghi log cả hai lỗi. File .mp4 dở dang KHÔNG ĐƯỢC dùng làm kết quả tải.
- **File .ts chứa phụ đề hoặc luồng dữ liệu mà .mp4 không chứa được**: giữ hình và tiếng; các luồng không chứa được thì bỏ qua (không mã hoá lại) và ghi log, thay vì làm hỏng cả quá trình.
- **Tải không qua hộp thoại** (ví dụ tải tự động/hàng đợi khởi chạy không hiện hộp thoại): áp dụng giá trị mặc định (bật).

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Khi tải một file mà tên file đích có phần mở rộng .ts và nội dung là video, hộp thoại tải PHẢI hiển thị tuỳ chọn có nhãn tương đương "Convert sang .mp4".
- **FR-002**: Tuỳ chọn ở FR-001 PHẢI được bật sẵn mỗi lần hộp thoại mở; lựa chọn của người dùng chỉ áp dụng cho mục tải đó.
- **FR-003**: Khi tuỳ chọn bật, sau khi tải xong XDM PHẢI tự động chuyển file sang .mp4 bằng cách **đóng gói lại (remux) các luồng hình và tiếng có sẵn, không mã hoá lại**. Phạm vi bao gồm video .ts có byte dư ở đầu file nếu công cụ xử lý media vẫn đọc được video và luồng hình/tiếng tương thích với .mp4; KHÔNG ĐƯỢC bỏ qua chỉ vì video không bắt đầu ngay tại byte đầu tiên của file.
- **FR-004**: Hệ thống KHÔNG ĐƯỢC mã hoá lại hình hoặc tiếng trong quá trình chuyển đổi; nếu không thể đưa luồng vào .mp4 mà không mã hoá lại thì chuyển đổi được coi là thất bại (FR-008), không được tự động chuyển sang mã hoá lại.
- **FR-005**: File .mp4 kết quả PHẢI có cùng tên gốc (chỉ đổi phần mở rộng) và nằm trong cùng thư mục đích mà người dùng đã chọn.
- **FR-006**: Trong lúc chuyển đổi, mục tải PHẢI hiển thị trạng thái xử lý sau tải (khác với "đang tải"); mục tải chỉ được đánh dấu hoàn tất khi file .mp4 đã sẵn sàng.
- **FR-007**: Sau khi chuyển đổi thành công, mục tải trong danh sách (tên, kích thước, "Mở file", "Mở thư mục") PHẢI trỏ tới file .mp4.
- **FR-008**: Khi chuyển đổi thất bại (luồng không tương thích, thiếu công cụ xử lý media, hết dung lượng…), hệ thống PHẢI giữ nguyên file .ts trong thư mục đích, thực hiện xoá mọi file .mp4 dở dang, coi mục tải là đã tải xong và trỏ tới .ts, đồng thời thông báo cho người dùng lý do chuyển đổi không thành công. Nếu không xoá được .mp4 dở dang, mục tải vẫn hoàn tất với .ts; hệ thống PHẢI thông báo thêm lý do không xoá được và đường dẫn file còn sót để người dùng tự xoá, đồng thời ghi log cả lỗi chuyển đổi lẫn lỗi dọn tệp. File .mp4 dở dang KHÔNG ĐƯỢC dùng làm kết quả tải.
- **FR-009**: Khi tuỳ chọn tắt, hành vi tải PHẢI giống hệt hành vi hiện tại (chỉ có file .ts, không có bước chuyển đổi).
- **FR-010**: Lựa chọn bật/tắt chuyển đổi PHẢI được lưu cùng mục tải để còn hiệu lực sau khi tạm dừng/tiếp tục hoặc khởi động lại XDM.
- **FR-011**: Hệ thống KHÔNG ĐƯỢC ghi đè im lặng lên file đã tồn tại trùng tên với file .mp4 kết quả; xử lý trùng tên theo quy tắc hiện có của XDM.
- **FR-012**: Hệ thống PHẢI ghi log kết quả của mỗi lần chuyển đổi (thành công/thất bại, lý do, các luồng bị bỏ qua) để hỗ trợ chẩn đoán.
- **FR-013**: Tính năng PHẢI áp dụng cho cả hai nguồn tải: (a) link file .ts trực tiếp qua hộp thoại tải thường, và (b) video streaming bắt từ trình duyệt mà XDM lưu thành .ts, qua hộp thoại tải video.
- **FR-014**: Tính năng PHẢI có trên cả bản Linux (GTK) và bản Windows (WPF), với cùng hành vi chuyển đổi; mỗi bản hiển thị tuỳ chọn trong hộp thoại tải tương ứng.
- **FR-015**: Sau khi chuyển đổi thành công, hệ thống PHẢI thực hiện xoá file .ts gốc, chỉ sau khi file .mp4 đã hoàn chỉnh. Nếu xoá thành công, thư mục đích chỉ còn file .mp4. Nếu không xoá được .ts, hệ thống PHẢI giữ file .mp4 hoàn chỉnh và file .ts gốc, đánh dấu mục tải hoàn tất và trỏ tới .mp4, hiển thị cảnh báo nêu lý do và đường dẫn .ts để người dùng tự xoá, đồng thời ghi log; KHÔNG ĐƯỢC coi lỗi dọn tệp này là lỗi chuyển đổi ở FR-008.
- **FR-016**: Khi người dùng dừng mục tải trong lúc đang chuyển đổi, hệ thống PHẢI dừng chuyển đổi, xoá file .mp4 dở dang và đưa mục tải về trạng thái dừng như mọi lần dừng khác; dữ liệu đã tải PHẢI được giữ để khi tiếp tục, kết quả cuối cùng vẫn theo lựa chọn chuyển đổi ban đầu và không sinh file trùng.

### Key Entities *(include if feature involves data)*

- **Mục tải (download item)**: Một lần tải trong danh sách của XDM. Bổ sung thuộc tính "có chuyển sang .mp4 hay không" (bật/tắt), được lưu bền cùng các thông tin khác của mục tải; tên/đường dẫn file kết quả thay đổi từ .ts sang .mp4 khi chuyển đổi thành công.
- **Kết quả chuyển đổi**: Thành công (file .mp4 sẵn sàng) hoặc thất bại kèm lý do (luồng không tương thích, thiếu công cụ xử lý media, hết dung lượng, bị huỷ); dùng để hiển thị thông báo và ghi log.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 100% các lần tải file video .ts với tuỳ chọn mặc định cho ra file .mp4 phát được trên trình phát phổ biến, khi công cụ xử lý media đọc được video và luồng hình/tiếng tương thích với .mp4 (ví dụ H.264/H.265 + AAC/MP3/AC-3, MPEG-2 video); bộ mẫu kiểm chứng PHẢI bao gồm cả TS chuẩn và video .ts có byte dư ở đầu file.
- **SC-002**: Thời gian chuyển đổi một file .ts 1 GB không vượt quá 2 lần thời gian sao chép file đó trên cùng ổ đĩa, và không tăng theo thời lượng video.
- **SC-003**: Tổng thời gian CPU dùng cho một lần chuyển đổi không vượt quá 5% thời lượng video (ví dụ video 60 phút tốn dưới 3 phút CPU), đặc trưng của việc chỉ đóng gói lại; mã hoá lại thường tốn xấp xỉ hoặc hơn thời lượng video.
- **SC-004**: File .mp4 kết quả có thời lượng chênh lệch không quá 1 giây so với bản gốc, cùng độ phân giải và cùng codec hình/tiếng.
- **SC-005**: 0 trường hợp mất dữ liệu đã tải: mọi lần chuyển đổi thất bại đều để lại file .ts nguyên vẹn trong thư mục đích; mọi lần dừng trong lúc chuyển đổi đều giữ dữ liệu đã tải và tiếp tục được tới kết quả cuối.
- **SC-006**: Người dùng không phải thao tác thêm bước nào so với hiện tại để nhận được file .mp4 (0 cú click bổ sung với hành vi mặc định).

## Assumptions

- "Không sử dụng CPU" được hiểu là **không mã hoá lại** (chỉ đóng gói lại/remux, sao chép nguyên luồng); việc đọc/ghi file vẫn tốn một lượng CPU rất nhỏ là chấp nhận được.
- "Video DVD" được hiểu là video ở dạng MPEG transport stream (.ts); file DVD gốc dạng .VOB/thư mục VIDEO_TS nằm ngoài phạm vi.
- Tuỳ chọn là **theo từng lần tải** trong hộp thoại; không thêm cài đặt chung trong Settings và không ghi nhớ lựa chọn lần trước.
- Việc chuyển đổi dùng lại công cụ xử lý media mà XDM đã dùng cho các tính năng video hiện có (ghép luồng hình/tiếng); khi thiếu công cụ, áp dụng cơ chế thông báo/khắc phục XDM đang có.
- Phụ đề và luồng dữ liệu không chứa được trong .mp4 có thể bị bỏ qua; người dùng ưu tiên có file .mp4 dùng được hơn là giữ đủ mọi luồng.
- Tải hàng loạt (batch) và tải tự động không hiện hộp thoại dùng giá trị mặc định (bật).
- Không đụng tới bản macOS.
