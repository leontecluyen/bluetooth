# 04 — Giao thức & luồng dữ liệu

Tài liệu này là **hợp đồng dữ liệu** giữa Android và PC. Đổi bất kỳ điều gì ở đây phải sửa
**cả hai phía** đồng thời, nếu không dữ liệu sẽ hỏng hoặc không parse được.

## 4.1. Kênh chính — Bluetooth SPP

### Vai trò

- **PC = server SPP (RFCOMM).** Mở `BluetoothListener` trên UUID
  `00001101-0000-1000-8000-00805F9B34FB`, accept nhiều client.
- **Android = client.** Tìm PC theo **tên radio Bluetooth**, mở socket RFCOMM, ghi dữ liệu.

### Tìm PC theo tên (cạm bẫy khớp tên)

App tìm PC bằng `BtSyncConfig.pcBluetoothName`. Nhưng tên radio PC máy dev thực tế là
`"LUYEN - Front"`, không phải `"LUYEN"`. Do đó app **khớp lỏng** (`resolveTarget`): không phân
biệt hoa thường, contains hai chiều, và **ưu tiên khớp địa chỉ MAC** (`pcBluetoothAddress`) khi
đã lưu. Chưa lưu PC nào thì tự đoán thiết bị bonded lớp COMPUTER (hoặc thiết bị bonded duy nhất).
Dashboard PC hiện tên radio thật trên header để khai báo cho đúng.

### Đóng khung (framing)

Mỗi khung được bọc:

```
STX (0x02)  +  <payload>  +  ETX (0x03)
```

Payload có thể là **khung dữ liệu** (`filename\r\n + CSV` — xem [§4.1b](#41b-tên-file-envelope-type--ngày--term_id--index))
hoặc **khung điều khiển** (`BATCH_END`, hoặc frame `RESULT` PC trả về).

- Android: `BluetoothSyncManager` ghi **mỗi file = một khung** (`STX + filename\r\n + csv + ETX`)
  trên **cùng một kết nối**; sau tất cả file là một khung điều khiển `BATCH_END`. Xem giao thức
  batch bên dưới.
- PC: `FrameDecoder` gom byte, gặp STX mở khung, gặp ETX trả khung; byte ngoài khung bỏ qua; một
  khung có thể bị cắt qua nhiều lần đọc Bluetooth và vẫn được ghép lại. Giới hạn an toàn 64KB/khung.

### Giao thức batch + xác nhận (`BATCH_END` → `RESULT`)

Đây là cách gửi của app hiện tại (`shipment_support`, `BluetoothSyncManager.sendBatch`) — gửi cả
lô file trên **một** kết nối rồi để PC **xác nhận từng file** đã nhận OK:

```
Android → PC :  STX  monitor_log_20260709_GalaxyS10_1.txt\r\n<CSV…>  ETX
Android → PC :  STX  pallet_log_20260709_GalaxyS10_1.txt\r\n<CSV…>   ETX
Android → PC :  STX  BATCH_END                                       ETX
PC → Android :  STX  RESULT\n<filename>=OK\n<filename>=ERR\n…        ETX
```

- PC (`BluetoothSppServer`) ingest từng khung dữ liệu, nhớ kết quả `(filename, ok)`; khi gặp khung
  **`BATCH_END`** thì trả **MỘT** khung `RESULT` liệt kê `<filename>=OK|ERR` cho từng file
  (`SendBatchResultAsync`).
- Android chờ khung `RESULT` (timeout **15s**), parse ra tập file **OK**. Chỉ file OK mới được
  **MOVE sang backup** (未送信) hoặc coi là gửi lại thành công (送信済); file `ERR`/thiếu được giữ
  lại để gửi lại → **không mất dữ liệu**. Nếu không nhận được `RESULT` (timeout / stream đóng) →
  coi cả batch là `SEND_FAILED`, giữ toàn bộ file.
- **Phân loại kết quả** (enum `SendOutcome`, 1 trong 5): `BLUETOOTH_OFF` / `NOT_PAIRED` /
  `CONNECT_FAILED` / `SEND_FAILED` / `SUCCESS` (SUCCESS = PC xác nhận OK **đủ** số file).
- **Tương thích ngược:** client cũ không gửi `BATCH_END` thì PC không trả `RESULT` (chỉ đóng
  socket) — vô hại.

> Code: PC `Services/BluetoothSppServer.cs` (`IsBatchEnd`/`IngestFrameAsync`/`SendBatchResultAsync`).
> Android `bluetooth_module/BluetoothSyncManager.java` (`sendBatch`/`readFrame`/`waitResult`),
> `bluetooth_module/LogSendActivity.java` (`sendSelected`).

### Heartbeat (liveness) — PING/PONG: **PC-only, app hiện KHÔNG dùng**

Kênh SPP có mang **khung điều khiển heartbeat** để một client biết listener PC còn sống & phản hồi:

```
client → PC :  STX  PING,<deviceName>,<epochMillis>  ETX
PC → client :  STX  PONG,<radioName>,<epochMillis>   ETX
```

- **PC vẫn hỗ trợ** (`BluetoothSppServer.IsHeartbeat`/`HandleHeartbeatAsync`): coi khung là
  heartbeat nếu nội dung **bắt đầu bằng `PING`**, trả `PONG`, cập nhật `LastSeenUtc`/
  `LastHeartbeatUtc`/đếm `Heartbeats` — **không** tính là gói/bản ghi/session dữ liệu, không đụng DB.
  Thiết bị bị coi **offline** nếu quá **15s** không có liên lạc (`ServiceStatus.HeartbeatTimeout`).
- **Nhưng app `shipment_support` (module `bluetooth_module`) KHÔNG gửi PING** — heartbeat/"Listener
  OK" đã **bị bỏ hoàn toàn** so với app cũ `SyncLogs`. Code PONG giữ lại chỉ để tương thích ngược.
  Do không còn liên lạc heartbeat, trạng thái Online của PC chỉ dựa vào lúc thực sự có kết nối
  gửi file.

### Đồng bộ ngược master (`MASTER_REQ` → file(s) → `MASTER_END`)

Chiều **PC → điện thoại**: đẩy 2 file master (`customer_master.csv`, `item_master.csv`) từ PC về
app để **sửa master trên PC là đồng bộ được, khỏi cài lại app**. PC là **source of truth**; sửa +
lưu master trong dashboard PC → app kéo về khi người dùng bấm **「マスタ受信」**.

Vì PC là SPP **server** còn điện thoại là **client** (điện thoại đi quét ra/vào vùng BT liên tục),
**PC không tự mở kết nối** — luồng luôn do **điện thoại khởi tạo** (pull theo yêu cầu). Trên kết nối
điện thoại mở:

```
Android → PC :  STX  MASTER_REQ\ncustomer_master.csv=<pcMtime>\nitem_master.csv=<pcMtime>   ETX
PC → Android :  STX  customer_master.csv\t<pcMtime>\r\n<csv>                     ETX   (chỉ khi PC mới hơn)
PC → Android :  STX  item_master.csv\t<pcMtime>\r\n<csv>                         ETX   (chỉ khi PC mới hơn)
PC → Android :  STX  MASTER_END\ncustomer_master.csv=UPDATED\nitem_master.csv=UPTODATE   ETX
```

- **Mốc do PC phát (chống lệch đồng hồ 2 máy).** `<pcMtime>` trong `MASTER_REQ` = mốc app **đã lưu**
  lần trước cho từng file (giá trị này DO PC cấp, lưu trong SharedPreferences `bt_module_config`, key
  `master_ts_<tên>`; `0` nếu chưa từng nhận). PC so **mtime hiện tại của file trên PC** với mốc app
  gửi lên — **cả 2 vế đều là mốc theo đồng hồ PC** nên không bị lệch giờ điện thoại/PC.
  (Trước đây so trực tiếp `File.lastModified` của máy app → sai khi máy app chạy nhanh hơn PC.)
- Nếu **mtime PC > `<pcMtime>` app gửi** (hoặc app = 0) → PC gửi file, **kèm mtime hiện tại của PC ở
  dòng 1** (`<tên>\t<pcMtime>`). App ghi file rồi **lưu `<pcMtime>` đó** để gửi ngược lần sau.
- **Khung kết `MASTER_END` mang trạng thái TỪNG file** — `UPDATED` (đã gửi) / `UPTODATE` (app đã mới
  nhất) — để app hiển thị thông báo chính xác: *cập nhật N file* / *đã mới nhất* / *lỗi* (PC báo
  UPDATED nhưng app ghi/nhập DB thất bại) / *PC không phản hồi* (không nhận được `MASTER_END`).
- App nhận từng frame tới khi gặp `MASTER_END` (timeout **20s**), ghi đè `<logDir>/master/<tên>.csv`
  (UTF-8), **re-import vào SQLite** (`importCustomerCsvFromDisk`/`importItemCsvFromDisk` — dùng chung
  với 設定 import từ đĩa), rồi **lưu `<pcMtime>`** (chỉ khi import OK; import lỗi thì giữ mốc cũ để lần
  sau thử lại).
- **Header phải giữ nguyên**: `item_master` phía Android validate header **bắt đầu bằng**
  `品目コード,品目名称,箱種` → PC phải giữ đúng header (`MasterStore.Header`).
- Encoding: cả 2 phía dùng **UTF-8 không BOM** (khớp asset app) → stream nguyên byte.

> Code: PC `Services/MasterStore.cs` (`Load`/`Save`/`LastModifiedUnixMillis`),
> `Services/BluetoothSppServer.cs` (`IsMasterRequest`/`HandleMasterRequestAsync`/`ParseMasterRequest`/
> `SendTextFrameAsync`). Android `bluetooth_module/BluetoothSyncManager.java`
> (`requestMaster`/`writeMasterFile`/`fileMillis`), `bluetooth_module/LogSendActivity.java`
> (`receiveMaster`, nút `btnReceiveMaster`).

## 4.1b. Tên file (envelope: type / NGÀY / term_id / index)

Dòng **ĐẦU TIÊN** trong khung (trước CSV) là **tên file**, mang metadata của lần gửi —
**bao gồm NGÀY LOG** (`yyyyMMdd`), rồi **termID (TRƯỚC) và index (SAU)**:

```
{type}_{yyyyMMdd}_{termId}_{index}.txt   vd: monitor_log_20260622_A1B2C3D4E5F6_3.txt
<row1 = header CSV thật>
<các dòng dữ liệu...>
```

- `type` = `monitor_log` | `pallet_log` | `direct_log` (PC vẫn ưu tiên nhận diện type từ
  **header row-1**; tên file là phụ trợ). `yyyyMMdd` = **ngày của log** (mặc định hôm nay).
  `termId` = **ĐỊA CHỈ MAC Bluetooth của máy Android** (đã **bỏ dấu `:`, viết HOA** — vd
  `A1B2C3D4E5F6`), đặt **TRƯỚC** index. Dùng MAC (thay cho tên Bluetooth trước đây) để mỗi máy có
  mã **cố định + duy nhất** — tên Bluetooth có thể trùng hoặc bị đổi. `BluetoothSyncManager.localTerminalId()`
  lấy MAC theo thứ tự `Settings.Secure "bluetooth_address"` → `adapter.getAddress()` →
  **fallback tên/model máy** (khi ROM không cho đọc MAC; từ Android 6 `getAddress()` bị ẩn thành
  `02:00:00:00:00:00`). `index` = lần gửi thứ mấy của (type) đó, là **cụm số ở CUỐI**.
- **Tách không nhập nhằng:** PC neo theo cụm 8 chữ số ngày và lấy **`_<số>` ở cuối làm index**
  bằng regex `^(type)_(\d{8})_(term)_(\d+)$` (`CsvTypes.ParseFilename`). PC tách dòng đầu nếu nó
  **không** phải header CSV (`CsvTypes.IsFilenameLine`), phần còn lại là CSV. Android dựng tên qua
  `BluetoothSyncManager.buildUploadFilename(type, date, term, index)`; **`index` được đếm từ thư
  mục backup** (`BackupStore.nextIndex` = 1 + max index đã gửi của ngày) chứ không lưu ở prefs —
  gửi trong cùng batch thì tăng dồn để không trùng. Gửi lại một file 送信済 thì **giữ nguyên** tên
  (term + index) → PC replace bản cũ.
- **Ngày lưu vào DB:** PC ghi ngày này vào cột `CsvUploads.LogDate` (kiểu date). Dùng cho bộ lọc
  **"log theo ngày"** ở bảng bên phải dashboard. Bản cũ **không có ngày** (format cũ
  `{type}__{index}__{termId}.csv`, double-underscore — vẫn được parse để tương thích ngược) ⇒
  `LogDate = null`, dashboard fallback theo **ngày nhận** (`ReceivedAtUtc`, giờ địa phương).
- **Supersede:** PC nhận `index` MỚI HƠN của cùng `(termId, type)` ⇒ đánh dấu các bản cũ
  `Superseded=true` (giữ lịch sử; danh sách CSV bên trái tô xám bản cũ). *Lưu ý:* bảng **log
  theo ngày** bên phải **gộp TẤT CẢ** upload trong ngày của type đó (kể cả bản superseded).

## 4.2. Các loại CSV (type) — header lấy theo dòng 1

PC nhận diện type từ **token đặc trưng trong row-1**; mỗi type có header hằng (thứ tự cột cố định):

### `monitor_log` (モニタリスト単位 — 9 cột)
```
開始時刻,終了時刻,入出庫伝票番号,顧客コード,品目コード,箱数,数量,積込箱数,状態
```
- `積込箱数` (loaded boxes) nằm **trước** cột cuối.
- Cột cuối **`状態` = code**: `0` = 正常, `9` = 削除.
→ chuẩn hóa thành **MonitorEntries** (`LoadedBoxes` = 積込箱数, `StatusCode` = code).
- Layout 8 cột cũ (không có `積込箱数`) vẫn được PC parse để tương thích ngược.
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

### `legacy` (định dạng quét cũ — **ĐÃ GỠ BỎ**)

Trước đây có type `legacy` (header `id`/`LogId`) đi vào bảng `SyncLogs` (dedup theo `LogId`).
Toàn bộ đường này — bảng `SyncLogs`, `Models/LogEntry.cs`, `Services/CsvLogParser.cs`,
`Services/LogIngestService.cs` — **đã bị gỡ bỏ**. Startup còn chạy `DROP TABLE IF EXISTS "SyncLogs"`
(bảng rỗng nên không mất dữ liệu). CSV có header không khớp 3 type sẽ được lưu với `Type="unknown"`
(chỉ giữ `RawCsv`, không chuẩn hóa).

### `SyncMethod` KHÔNG nằm trong CSV

`Source` (`"Bluetooth"` / `"WiFi"`) trên `CsvUpload` **do kênh nhận của PC tự gán**, không truyền
trong CSV. Đây là điểm hay nhầm.

## 4.3. Bảng đích trong DB

Không còn bảng `SyncLogs`. Dữ liệu nhận được lưu ở `CsvUploads` (mỗi frame = 1 dòng, giữ `RawCsv`)
và chuẩn hóa ra `MonitorEntries` / `PalletOps`(+`PalletOpItems`) / `DirectEntries`. Xem chi tiết
schema ở [02 §2.9](02-ung-dung-pc-csharp.md).

## 4.4. Chống trùng (dedup)

Dedup theo `LogId` (bảng `SyncLogs`) đã bị gỡ cùng type `legacy`. Với dữ liệu typed hiện tại,
tính idempotent do cơ chế **supersede**: index mới hơn của cùng `(TermId, Type)` đánh dấu upload
cũ `Superseded=true` (không nhân đôi số liệu dashboard). Gửi lại cùng index ghi đè file backup
trên đĩa một cách idempotent.

## 4.5. Kênh dự phòng — Wi-Fi REST (đã gỡ phía Android, tạm gác phía PC)

- **Android:** app cũ `SyncLogs` từng POST một **mảng JSON `JobLog`** (Gson) tới
  `http://<ip>:8080/api/sync` làm lớp 2 khi Bluetooth fail. App mới `shipment_support` **không còn
  kênh Wi-Fi** (đã bỏ Retrofit/`SyncApiService`/`SyncWorker`) — chỉ gửi qua Bluetooth SPP.
- **PC:** endpoint `POST /api/sync` nay trả **501 Not Implemented** (đường parse CSV/ingest cũ
  đã bị gỡ cùng bảng legacy `SyncLogs`), nên ingest Wi-Fi **chưa được đấu nối**.
- **Khi bật lại Wi-Fi cần:** (1) làm lại writer Wi-Fi phía Android; (2) đấu ingest CSV typed ở PC
  (map vào `CsvUploads` qua `ICsvStore`); (3) bind Kestrel ở cổng **8080** (mặc định hiện tại là
  8090). → Thay đổi nhiều case → **xác nhận trước khi làm**.

## 4.6. Endpoint HTTP của PC (Kestrel, trong tiến trình)

| Method | Đường dẫn | Mục đích | Trạng thái |
|--------|-----------|----------|------------|
| `GET` | `/api/status` | Snapshot giám sát đầy đủ (`StatusDto`) | Dùng được |
| `GET` | `/health` | `{"status":"ok"}` | Dùng được |
| `POST` | `/api/sync` | Nhận dữ liệu đồng bộ | **501** — Wi-Fi chưa đấu, đường CSV legacy đã gỡ |

Mặc định bind `http://0.0.0.0:8090`.

## 4.7. Checklist khi đổi wire-format

Khi sửa bất kỳ phần nào của giao thức (cột CSV, ký tự khung, định dạng thời gian, tên field
JSON), **làm đủ các bước**:

1. Sửa Android (`shipment_support`): writer log (`common/FileLogHelper`) và/hoặc module gửi
   (`bluetooth_module/BluetoothSyncManager.java`, `DayLogRepository.java`).
2. Sửa PC: `Services/CsvTypes.cs` (header/parser typed), `Services/FrameDecoder.cs`, và/hoặc endpoint
   `POST /api/sync` trong `Program.cs`.
3. Kiểm tra tính idempotent (supersede theo `(TermId, Type, UploadIndex)`).
4. Cập nhật **tài liệu này** + [02](02-ung-dung-pc-csharp.md) + [03](03-ung-dung-android.md).
5. Đây là loại thay đổi **rủi ro cao, nhiều case** → theo quy tắc, **hỏi xác nhận trước**.
