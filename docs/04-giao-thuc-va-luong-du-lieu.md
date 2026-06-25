# 04 — Giao thức & luồng dữ liệu

Tài liệu này là **hợp đồng dữ liệu** giữa Android và PC. Đổi bất kỳ điều gì ở đây phải sửa
**cả hai phía** đồng thời, nếu không dữ liệu sẽ hỏng hoặc không parse được.

## 4.1. Kênh chính — Bluetooth SPP

### Vai trò

- **PC = server SPP (RFCOMM).** Mở `BluetoothListener` trên UUID
  `00001101-0000-1000-8000-00805F9B34FB`, accept nhiều client.
- **Android = client.** Tìm PC theo **tên radio Bluetooth**, mở socket RFCOMM, ghi dữ liệu.

### Tìm PC theo tên (cạm bẫy khớp tên)

App tìm PC bằng `SyncConfig.pcBluetoothName` (mặc định `"LUYEN"`). Nhưng tên radio PC máy dev
thực tế là `"LUYEN - Front"`, không phải `"LUYEN"`. Do đó app **khớp lỏng**: không phân biệt
hoa thường, contains hai chiều, và **ưu tiên khớp địa chỉ MAC** (`pcBluetoothAddress`) khi đã
lưu. Dashboard PC hiện tên radio thật trên header để khai báo cho đúng.

### Đóng khung (framing)

Mỗi lần truyền, payload CSV được bọc:

```
STX (0x02)  +  <toàn bộ CSV>  +  ETX (0x03)
```

- Android: `BluetoothSyncManager.sync()` gửi **mỗi batch CSV = một khung riêng**
  (`byteArrayOf(0x02) + csvBytes + byteArrayOf(0x03)` cho từng nhóm `batchId`). Một lần đồng bộ
  có thể gửi nhiều khung liên tiếp trên cùng một kết nối.
- PC: `FrameDecoder` gom byte, gặp STX mở khung, gặp ETX trả khung; **mỗi khung = 1 mục CSV**
  trong danh sách (vd 4 batch ⇒ 4 dòng). Byte ngoài khung bỏ qua; một khung có thể bị cắt qua
  nhiều lần đọc TCP/Bluetooth và vẫn được ghép lại. Giới hạn an toàn 64KB/khung.

### Heartbeat (liveness) — khung điều khiển PING/PONG

Ngoài khung dữ liệu CSV, kênh SPP còn mang **khung điều khiển heartbeat** để Android biết
listener PC còn sống & phản hồi (không chỉ "PC đã ghép đôi"). Dùng **chung kết nối SPP, chung
đóng khung STX/ETX**, chỉ khác nội dung:

```
Android → PC :  STX  PING,<deviceName>,<epochMillis>  ETX
PC → Android :  STX  PONG,<radioName>,<epochMillis>   ETX
```

- **Phân biệt với CSV:** PC coi khung là heartbeat nếu nội dung **bắt đầu bằng `PING`**
  (không phân biệt hoa thường). CSV luôn bắt đầu bằng header `id`/`LogId` nên không nhầm.
- **Nhịp & ngưỡng:** Android ping **mỗi 5s** (chỉ khi app đang mở/hiển thị). PC coi thiết bị
  **offline** nếu quá **15s** (lỡ 3 nhịp) không có liên lạc nào — hằng số
  `ServiceStatus.HeartbeatTimeout`, khớp với `HEARTBEAT_INTERVAL_MS` bên Android.
- **Ping là PHỤ, không bao giờ tranh chấp với sync** (`BluetoothSyncManager`, các cờ ở cấp
  companion/tiến trình vì UI và `SyncWorker` dùng instance khác nhau):
  1. **Đang sync** (`syncInProgress`) → bỏ nhịp ping (không mở kết nối).
  2. **Vừa yêu cầu sync** → `triggerSync()` gọi `pausePing(10s)` để bịt khoảng trễ giữa lúc
     enqueue WorkManager và lúc `sync()` thực sự chạy.
  3. **Vừa sync THÀNH CÔNG** (trong `SYNC_OK_TTL_MS` = 12s) → coi luôn là "listener OK",
     **khỏi** mở kết nối ping (`markSyncOk` đặt mốc; heartbeat trả OK ngay). Một lần sync OK đã
     chứng minh listener sống & nhận được dữ liệu.
  ⇒ Trong các đợt đồng bộ, điện thoại **không ping** nữa; ping chỉ chạy khi rảnh. Việc này giảm
  hẳn va chạm kết nối khi nhiều máy sync. (Không thể tắt ping của *máy khác* trong lúc máy này
  sync — đó là lý do còn cần `connectWithRetry` jitter.)
- **Ngữ nghĩa "Listener OK" (Android):** mở được socket SPP **và** nhận `PONG` hợp lệ trong 3s
  (`HEARTBEAT_REPLY_TIMEOUT_MS`). Mở được socket nhưng không có PONG ⇒ "không phản hồi"; không
  mở được ⇒ "mất kết nối".
- **Ngữ nghĩa "Online" (PC):** thiết bị đang kết nối **hoặc** có liên lạc (data **hoặc**
  heartbeat) trong vòng `HeartbeatTimeout`. Heartbeat **không** tính vào số gói/bản ghi/session
  dữ liệu; chỉ cập nhật `LastSeenUtc` + `LastHeartbeatUtc` + đếm `Heartbeats`.
- **Không đụng dedup/DB:** heartbeat không ghi gì vào DB.

> Code: PC `Services/BluetoothSppServer.cs` (`IsHeartbeat`/`HandleHeartbeatAsync`),
> `Services/ServiceStatus.cs` (`AddHeartbeat`/`IsOnline`/`HeartbeatTimeout`). Android
> `bluetooth/BluetoothSyncManager.kt` (`heartbeat`/`readFrame`), vòng lặp ở
> `MainActivity.kt` (`repeatOnLifecycle` + `HEARTBEAT_INTERVAL_MS`).

## 4.1b. Tên file (envelope: type / NGÀY / term_id / index)

Dòng **ĐẦU TIÊN** trong khung (trước CSV) là **tên file**, mang metadata của lần gửi —
**bao gồm NGÀY LOG** (`yyyyMMdd`), rồi **termID (TRƯỚC) và index (SAU)**:

```
{type}_{yyyyMMdd}_{termId}_{index}.txt   vd: monitor_log_20260622_GalaxyS10_3.txt
<row1 = header CSV thật>
<các dòng dữ liệu...>
```

- `type` = `monitor_log` | `pallet_log` | `direct_log` (PC vẫn ưu tiên nhận diện type từ
  **header row-1**; tên file là phụ trợ). `yyyyMMdd` = **ngày của log** (mặc định hôm nay).
  `termId` = **tên máy Android** (`deviceName`, đã **bỏ mọi khoảng trắng**), đặt **TRƯỚC** index.
  `index` = lần gửi thứ mấy của (type) đó, là **cụm số ở CUỐI**.
- **Tách không nhập nhằng:** PC neo theo cụm 8 chữ số ngày và lấy **`_<số>` ở cuối làm index**
  bằng regex `^(type)_(\d{8})_(term)_(\d+)$` (`CsvTypes.ParseFilename`). PC tách dòng đầu nếu nó
  **không** phải header CSV (`CsvTypes.IsFilenameLine`), phần còn lại là CSV. App tăng `index` mỗi
  lần gửi (`SyncConfig.nextUploadIndex(type)`), gửi qua `BluetoothSyncManager.sendCsvFile`.
- **Ngày lưu vào DB:** PC ghi ngày này vào cột `CsvUploads.LogDate` (kiểu date). Dùng cho bộ lọc
  **"log theo ngày"** ở bảng bên phải dashboard. Bản cũ **không có ngày** (format cũ
  `{type}__{index}__{termId}.csv`, double-underscore — vẫn được parse để tương thích ngược) ⇒
  `LogDate = null`, dashboard fallback theo **ngày nhận** (`ReceivedAtUtc`, giờ địa phương).
- **Supersede:** PC nhận `index` MỚI HƠN của cùng `(termId, type)` ⇒ đánh dấu các bản cũ
  `Superseded=true` (giữ lịch sử; danh sách CSV bên trái tô xám bản cũ). *Lưu ý:* bảng **log
  theo ngày** bên phải **gộp TẤT CẢ** upload trong ngày của type đó (kể cả bản superseded).

## 4.2. Các loại CSV (type) — header lấy theo dòng 1

PC nhận diện type từ **token đặc trưng trong row-1**; mỗi type có header hằng (thứ tự cột cố định):

### `monitor_log` (モニタリスト単位 — 8 cột)
```
開始時刻,終了時刻,入出庫伝票番号,顧客コード,品目コード,箱数,数量,状態
```
- Cột cuối **`状態` = code**: `0` = 正常, `9` = 削除.
→ chuẩn hóa thành **MonitorEntries** (`StatusCode` = code).
- **Hiển thị PC:** dòng `状態=0` → hiện; `状態=9` → ẩn.

### `pallet_log` (パレット単位 — 7 cột, KHÔNG còn 操作)
```
開始時刻,終了時刻,PLNo.,顧客,納入便,品目明細 (品目コード:箱数x数量),状態
```
- Cột cuối **`状態` = code**: `0` = 正常, `1` = 移動, `9` = 削除.
- `品目明細` = `品目コード:箱数x数量` cách nhau bằng dấu cách (vd `74841:30x1 77958:20x1`),
  bọc trong `"..."`.
→ chuẩn hóa thành **PalletOps** (`StatusCode`=状態 code) + **PalletOpItems** (tách 品目明細).
- **Hiển thị PC:** key = (`PLNo.`, `顧客`, `納入便`). `状態=9` → ẩn; trong các dòng còn lại
  (状態 0/1) cùng key, chỉ hiện dòng có **`終了時刻` mới nhất**.

### `direct_log` (直送管理単位 — 11 cột, type MỚI)
```
開始時刻,終了時刻,顧客,納入先,出荷日,品番,収容数,箱数,納入数,工場コード,ヨコオ品番
```
- 1 dòng = 1 lần 照合 hoàn tất; **không có cột `状態`**.
→ chuẩn hóa thành **DirectEntries**.
- **Hiển thị PC:** hiện **tất cả** các dòng.

### `legacy` (định dạng quét cũ — đang giữ tương thích)
Header `id`/`LogId` → đi vào `SyncLogs` (dedup theo LogId) như trước.

```
id,workerId,jobType,barcodeData,startTime,endTime
```

Quy ước dòng/định dạng (do `LogPayloadSerializer` bên Android định nghĩa):

- Kết thúc dòng **CRLF**.
- Trích dẫn **RFC-4180**: field chứa `,` `"` hoặc xuống dòng được bọc trong nháy kép, nháy
  bên trong nhân đôi (`"` → `""`).
- Thời gian = **epoch millis** (app dùng kiểu này). PC cũng chấp nhận ISO-8601.
- jobType là tiếng Nhật: `検品` / `出荷` / `直送`.

### Ý nghĩa từng cột

| Cột | Nguồn | Ghi chú |
|-----|-------|---------|
| `id` | Android (`logId`, fallback PK `id`) | **Khóa dedup của PC.** Android gửi `JobLog.logId` (tách khỏi khóa chính Room để có thể tạo TRÙNG khi test); nếu `logId` trống thì gửi PK. Nếu cột này trống, PC suy ra Guid ổn định (SHA-1 kiểu v5) từ nội dung ⇒ gửi lại vẫn dedup |
| `workerId` | Android | **Tên máy/thiết bị Android** (app lấy từ Settings `device_name`, fallback `Build.MODEL`); nút tạo log mẫu cũng dùng tên cố định này, không phải `W-####` ngẫu nhiên |
| `jobType` | Android | 検品 / 出荷 / 直送 |
| `barcodeData` | Android | Nội dung mã vạch |
| `startTime` | Android | epoch-millis; PC chuẩn hóa về **UTC** |
| `endTime` | Android | epoch-millis; PC chuẩn hóa về **UTC** |

### `SyncMethod` KHÔNG nằm trong CSV

`SyncMethod` (`"Bluetooth"` / `"WiFi"`) **do kênh nhận của PC tự gán**, không truyền trong
CSV. Đây là điểm hay nhầm.

## 4.3. Bảng đích trong DB — `SyncLogs`

```
LogId (Guid, PK) | WorkerId | JobType | BarcodeData | StartTime | EndTime | SyncMethod
```

- `LogId` map từ `id`; `StartTime`/`EndTime` là UTC; `SyncMethod` do PC set.
- Index trên `WorkerId` (truy vấn theo máy) và `StartTime` (thống kê "hôm nay").

## 4.4. Dedup (chống trùng) — quy tắc

`LogIngestService` thực hiện tương đương `ON CONFLICT (LogId) DO NOTHING`, độc lập provider:

1. Gộp các bản trùng `LogId` ngay trong batch (giữ bản đầu).
2. Query DB, bỏ các `LogId` đã tồn tại.
3. Chèn một lần; nếu đụng khóa do tranh chấp đồng thời (`DbUpdateException`) → retry **từng
   dòng**, dòng nào đụng thì đếm là trùng.

Hệ quả: gửi lại cùng dữ liệu nhiều lần là **an toàn** — số `Inserted` chỉ tính bản mới.
`Duplicates = Received − Inserted`, gộp CẢ **trùng trong file** (cùng `LogId` trong 1 CSV) lẫn
**trùng file khác** (LogId đã có trong DB) → con số trên dashboard trực quan.

## 4.5. Kênh dự phòng — Wi-Fi REST (đang tạm gác)

- **Android:** `SyncApiService` POST một **mảng JSON `JobLog`** (Gson) tới
  `http://<ip>:8080/api/sync`. Đây là lớp 2 trong `SyncWorker` khi Bluetooth fail.
- **PC:** có endpoint `POST /api/sync` nhưng **hiện đang parse CSV**, nên ingest JSON Wi-Fi
  **chưa được đấu nối**.
- **Khi bật lại Wi-Fi cần:** (1) thêm parse JSON-array ở PC (map `id`→`LogId`,
  epoch→`DateTime`, set `SyncMethod="WiFi"`); (2) bind Kestrel ở cổng **8080** (mặc định hiện
  tại là 8090). → Đây là thay đổi có nhiều case (định dạng thời gian, tên field JSON
  camelCase, lỗi mạng) — **xác nhận trước khi làm**.

## 4.6. Endpoint HTTP của PC (Kestrel, trong tiến trình)

| Method | Đường dẫn | Mục đích | Trạng thái |
|--------|-----------|----------|------------|
| `GET` | `/api/status` | Snapshot giám sát đầy đủ (`StatusDto`) | Dùng được |
| `GET` | `/health` | `{"status":"ok"}` | Dùng được |
| `POST` | `/api/sync` | Nhận dữ liệu đồng bộ | Đang parse CSV; **JSON Wi-Fi chưa đấu** |

Mặc định bind `http://0.0.0.0:8090`.

## 4.7. Checklist khi đổi wire-format

Khi sửa bất kỳ phần nào của giao thức (cột CSV, ký tự khung, định dạng thời gian, tên field
JSON), **làm đủ các bước**:

1. Sửa Android: `sync/LogPayloadSerializer.kt` (CSV) và/hoặc `network/SyncApiService.kt` (JSON).
2. Sửa PC: `Services/CsvLogParser.cs`, `Services/FrameDecoder.cs`, và/hoặc endpoint
   `POST /api/sync` trong `Program.cs`.
3. Kiểm tra dedup vẫn đúng (`LogId` ổn định).
4. Cập nhật **tài liệu này** + [02](02-ung-dung-pc-csharp.md) + [03](03-ung-dung-android.md).
5. Đây là loại thay đổi **rủi ro cao, nhiều case** → theo quy tắc, **hỏi xác nhận trước**.
