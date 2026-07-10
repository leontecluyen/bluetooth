# 02 — Ứng dụng PC (C# / .NET 8 / WinForms)

Thư mục: `LeontecSyncLogSystem/`. Output: `LogManagement.exe` (đặt bởi `<AssemblyName>LogManagement</AssemblyName>`; không còn tên "Leontec" trong thư mục xuất bản — namespace nguồn vẫn giữ nguyên).

## 2.1. Bản chất: một desktop app "lai"

Đây là **một** project WinForms (.NET 8) duy nhất, nhưng bên trong nó **nhúng một
ASP.NET Core generic host**. Khi chạy:

1. Tạo generic `Host` (`Host.CreateDefaultBuilder`) với EF Core + HttpListener API + các service nền.
2. `Start()` host (không chặn luồng).
3. Chạy `Application.Run(MainForm)` — vòng lặp thông điệp WinForms trên luồng chính.
4. Khi đóng form → `Stop()` host.

Vì dashboard và host **ở chung tiến trình**, MainForm lấy `MonitorService` trực tiếp từ DI,
không gọi HTTP vào chính mình.

> Lưu ý cấu hình project: target **`net48`** (.NET Framework 4.8) — có sẵn trong mọi Windows 10/11
> nên app chạy trên máy trắng **không cần cài/nhúng runtime** (~7 MB). 32feet.NET chạy RFCOMM SPP
> **server** trên net48 qua **Win32 stack** (`Win32BluetoothListener`, lib `net462`), không phải WinRT.
> Các API/cú pháp net6+ được xử lý: `DateOnly`/`TimeOnly` → polyfill **Portable.System.DateTimeOnly**
> (+ EF value converters); records/`init`/`required`/range-index → **PolySharp** (polyfill compile-time);
> **ASP.NET Core/Kestrel không có** → HTTP API dùng **`System.Net.HttpListener`**
> (`Services/HttpApiService.cs`) và app dùng **generic `Host`** (`Microsoft.Extensions.Hosting` 3.1)
> thay `WebApplication`. (Trước đây target net10 + self-contained; đã chuyển net48 ngày 2026-07-10.)

## 2.2. Sơ đồ thư mục & vai trò file

```
LeontecSyncLogSystem/
  Program.cs            Bootstrap host (WebApplication) + đăng ký DI + endpoint HTTP + chạy WinForms
  MainForm.cs           Dashboard; mỗi 2s gọi MonitorService (trong tiến trình)
  Worker.cs             BackgroundService: chạy BluetoothSppServer
  appsettings.json      Cấu hình: Logging, Kestrel, Sync (tên service BT), Database
  Models/
    DeviceRecord.cs     Entity DB Devices — roster thiết bị BT (bền qua restart)
    CsvUpload.cs        Entity DB CsvUploads — 1 lần sync = 1 CSV (FK→Devices), giữ RawCsv + Type/TermId/UploadIndex/Superseded
    MonitorEntry.cs     Entity DB MonitorEntries — 1 dòng monitor (入出庫) chuẩn hóa
    PalletOp.cs         Entity DB PalletOps + PalletOpItems — thao tác pallet + item tách từ 品目明細
    DirectEntry.cs      Entity DB DirectEntries — 1 dòng direct (直送管理) chuẩn hóa
  Data/
    AppDbContext.cs     EF Core; devices(PK id, address UNIQUE) + csv_uploads(PK id, FK device_id) + bảng chuẩn hóa (snake_case)
  Services/
    SyncOptions.cs        SyncOptions (BluetoothServiceName + BackupFolder) + DatabaseOptions
    CsvBackupWriter.cs    Ghi bản sao thô của mỗi CSV nhận được ra đĩa (backup theo ngày, best-effort)
    MasterStore.cs        Nguồn chân lý 2 file master (customer/item) trên PC: load/save (UTF-8 no BOM) + seed từ master-seed/ + Version() (SHA-256) cho đồng bộ ngược

    FrameDecoder.cs       Đóng/gỡ khung byte STX/ETX (thuần, test được)
    BluetoothSppServer.cs  Server RFCOMM SPP (32feet.NET), accept nhiều client
    CsvTypes.cs           Định nghĩa type (monitor/pallet/direct): header hằng, DetectType(header), ParseFilename(term/index), parser ra MonitorEntry/PalletOp(+Item)/DirectEntry
    CsvStore.cs           Lưu CSV upload + chuẩn hóa vào bảng theo type + supersede bản cũ; query theo device; trả RawCsv để hiển thị
    DeviceStore.cs        Lưu/nạp/xoá roster thiết bị xuống bảng Devices (bền qua restart)
    ServiceStatus.cs      Trạng thái sống thread-safe (client BT + server), singleton
  Monitoring/
    MonitorService.cs     Dựng snapshot StatusDto từ ServiceStatus + CsvInbox + DB
    StatusModels.cs       Các DTO (vừa là nguồn bind cho lưới UI, vừa là shape JSON /api/status)
```

## 2.3. Bootstrap — `Program.cs`

- Đọc `appsettings.json` từ thư mục exe (chỉ còn `Sync`/`Kestrel`/`Logging`).
- Bind `SyncOptions` (mục `Sync`). **Không còn `DatabaseOptions`.**
- **Cấu trúc thư mục** (giải bởi `Services/AppPaths.cs`, neo trên `AppContext.BaseDirectory`) — giống
  nhau cho debug & release, **không dùng `%LOCALAPPDATA%`**: exe ở thư mục **`LogManagement/`** (csproj
  đặt `AppendTargetFrameworkToOutputPath=false` + `OutputPath=bin\<Config>\LogManagement\`, thay cho tên
  TFM); `_master`, `_backup`, `mysql.xml` nằm ở **thư mục cha** của `LogManagement/`; `configuration.xml`,
  `ui-language.txt`, `crash.log` nằm trong `LogManagement/`.
- **Seed master + shortcut mỗi lần Build/Debug:** 2 target MSBuild (AfterTargets=Build) —
  `CopyMasterSeedToRoot` copy `master-seed/*.csv` (customer + item) vào `<root>/_master` (ghi đè), và
  `CreateRootShortcut` (PowerShell + WScript.Shell) tạo lại `<root>/Log Management.lnk` trỏ tới exe
  (icon từ exe; có lưu đường dẫn tương đối nên copy cả `<root>` sang máy khác vẫn mở được). Đặt
  `DisableFastUpToDateCheck=true` để VS **không bỏ qua build khi F5** → 2 target luôn chạy lại cả khi
  Build lẫn Debug/Run.
- **DB = MySQL ngoài (cài/chạy riêng).** MariaDB nhúng đã **gỡ bỏ** — app không đóng gói/khởi động DB
  nữa. `Services/MySqlConfig.cs` đọc `<root>/mysql.xml` (`host`/`port`/`database`/`user`/`password`;
  thiếu file thì tạo mặc định `localhost:3306`, `log_management`, `root`, rỗng) → dựng connection string.
- Provider DB = **MySQL qua Pomelo** (net48 dùng **EF Core 3.1** + Pomelo 3.2):
  `UseMySql(connectionString).UseSnakeCaseNamingConvention()` — Pomelo 3.2 **không nhận tham số
  `ServerVersion`** và không mở kết nối lúc cấu hình (context dựng offline, MySQL tắt cũng không sao).
  Schema tạo lúc khởi động bằng **`db.Database.EnsureCreated()`** (KHÔNG dùng migrations — `Data/Migrations/`
  đã gỡ). `DateOnly`/`TimeOnly` map qua **EF value converter** trong `AppDbContext` (→ `time(6)`/`date`).
  **DB tắt lúc khởi động KHÔNG làm app crash**: lỗi được log (Error) rồi app vẫn mở, dashboard hiện
  "MySQL: disconnected"; schema/roster nạp ở lần chạy sau khi MySQL đã lên. Sau khi tạo schema OK thì
  **nạp roster thiết bị** và `ServiceStatus.SeedFromPersisted(...)`.
- Nạp `UiConfig` từ `app/configuration.xml` (7 công tắc ẩn/hiện, mặc định false) và đăng ký singleton;
  `MainForm.ApplyUiConfig()` áp lúc khởi động.
- Đăng ký DI (đa số là **singleton** vì là trạng thái sống của tiến trình):
  - `ServiceStatus` — trạng thái BT toàn cục.
  - `IDeviceStore`/`DeviceStore` — lưu/nạp roster thiết bị (bảng `Devices`).
  - `ICsvStore`/`CsvStore` — lưu/nạp CSV uploads (bảng `CsvUploads`).
  - `ICsvBackupWriter`/`CsvBackupWriter` — ghi bản sao thô mỗi CSV ra đĩa. Thư mục gốc lấy từ
    `Sync:BackupFolder`; rỗng ⇒ mặc định `<root>/_backup` (`AppPaths.BackupDir`). Đường dẫn
    đã giải quyết được **log ở mức Information lúc khởi động**.
  - `MonitorService` — dựng snapshot.
  - `Worker` (hosted service) — chạy server Bluetooth.

> **Backup ra đĩa.** Sau khi mỗi CSV nhận được đã ghi vào DB, `BluetoothSppServer` gọi
> `CsvBackupWriter.SaveAsync` để lưu thêm một bản thô vào
> `Sync:BackupFolder/<yyyyMMdd>/<filename>`. `<filename>` = tên file upload của điện thoại (dựng lại
> `{type}_{yyyyMMdd}_{termId}_{index}.txt` nếu khung không kèm tên). Ghi **nguyên tử** (file `.tmp`
> rồi `Move` đè), UTF-8 không BOM, trung thực với byte nhận được. **Best-effort:** lỗi backup được
> log mức Warning rồi nuốt — **không bao giờ làm hỏng quá trình ingest** (dòng đã nằm trong DB). Gửi
> lại cùng `index` sẽ ghi đè idempotent.
- Map endpoint HTTP: `POST /api/sync`, `GET /api/status`, `GET /health`.
- **Bắt lỗi toàn cục:** trước `Application.Run`, đăng ký `Application.ThreadException` +
  `AppDomain.UnhandledException`, và bọc `new MainForm(...)`/`Run` trong try/catch → `ReportFatal`
  ghi exception vào `crash.log` **trong thư mục app** và hiện `MessageBox`. Nhờ vậy
  lỗi lúc khởi động **không còn "run rồi tắt liền" im lặng** — luôn thấy nguyên nhân + có log.
- **⚠️ `SplitContainer` — đặt MỌI kích thước ở `MainForm.Load`, KHÔNG trong `InitializeComponent`.**
  Cụ thể `Panel1MinSize`/`Panel2MinSize`/`SplitterDistance` đều set trong `SetupSplitter()` gọi từ
  `Load`. Lý do: lúc `InitializeComponent` chạy, `_split` còn ở bề rộng mặc định **150px**, nên khi
  `EndInit()` áp `Panel2MinSize=420` nó tính `150 − 420 < 0` → ném
  `InvalidOperationException: "SplitterDistance must be between Panel1MinSize and Width − Panel2MinSize"`
  → **app crash ngay khi run** (đúng lỗi đã gặp 2026-06). Để min size ở mặc định (25) lúc init cho
  `EndInit` hợp lệ, rồi `SetupSplitter` mới nới rộng + đặt vị trí ~30% sau khi đã biết bề rộng thật
  (có clamp để không bao giờ vượt width). Đây cũng là lý do **KHÔNG mở `MainForm` bằng Designer trực
  quan**: nó sinh lại `InitializeComponent`, xoá các lời gọi `ConfigureGrid(...)` (lưới mất
  ReadOnly/AutoGenerateColumns=false → cột trùng) **và** nhúng cứng `Panel*MinSize`/`SplitterDistance`
  vào init → tái hiện đúng crash trên. **Đã chặn cứng bằng `[DesignerCategory("Code")]`
  trên class `MainForm`** → VS mở file bằng cửa sổ code, không bật khung Designer (double-click = mở
  code, "View Designer" bị vô hiệu) nên không thể bị sinh lại/phá. Code viết tay và Designer trực quan
  **không đồng bộ hai chiều được** — Designer độc chiếm và viết lại `InitializeComponent`, vứt mọi thứ
  không serialize được (lời gọi hàm, logic lúc chạy); đây là cách chuẩn để giữ form viết tay.
- Start host → `Application.Run` → Stop host khi thoát. **Thoát dứt khoát:** sau khi đóng form,
  `app.StopAsync(timeout 5s)` (chặn thời gian, không chờ vô hạn) rồi **`Environment.Exit(0)`** để
  process **chắc chắn kết thúc** — `BluetoothListener.AcceptBluetoothClient()` của 32feet là lệnh
  chặn, có thể giữ thread không hủy sạch được; nếu để process "mồ côi" sống tiếp nó sẽ **khoá file
  `LogManagement.exe`** và làm build kế tiếp lỗi *"the file is locked by LogManagement
  (PID)"* (copy `apphost.exe → .exe` thất bại). Gặp lỗi đó: kill tiến trình `LogManagement`
  còn sót (`Get-Process LogManagement | Stop-Process -Force`) rồi build lại.

## 2.4. Nhận Bluetooth — `Worker.cs` + `Services/BluetoothSppServer.cs`

`Worker` là `BackgroundService`, chỉ làm một việc: dựng `BluetoothSppServer` với các
dependency được tiêm (`ServiceStatus`, `ICsvStore`, `IDeviceStore`, `ICsvBackupWriter`, tên
service, logger) rồi `await server.RunAsync(stoppingToken)`.

`BluetoothSppServer` (file ~214 dòng) là trái tim kênh chính:

- **UUID SPP** chuẩn `00001101-0000-1000-8000-00805F9B34FB`; retry mỗi **5s** nếu radio
  tắt/không có.
- **Vòng ngoài (self-heal):** đọc tên radio Bluetooth của PC, đưa vào `ServiceStatus` để
  người vận hành biết phải khai báo tên gì cho app Android; tạo `BluetoothListener`; nếu lỗi
  (không radio) thì set `ServerListening=false`, ghi `LastError`, chờ 5s rồi thử lại.
- **Vòng accept:** mỗi client được chấp nhận → xử lý trên **task riêng** ⇒ nhiều máy đồng thời.
- **Xử lý 1 client (`HandleClientAsync`):** lấy/ tạo `BtClientStatus` theo địa chỉ BT của
  thiết bị (giữ qua các lần reconnect), tạo một `FrameDecoder` **riêng cho kết nối**, đọc
  stream theo từng chunk → `decoder.Push(buffer, read)` → mỗi khung trọn vẹn được **định
  tuyến**: khung bắt đầu bằng `PING` → `HandleHeartbeatAsync`; còn lại là dữ liệu → `AddFrame`
  + `IngestFrameAsync`. Mất kết nối/ lỗi → `MarkDisconnected`; chỉ đếm `AddDataSession` nếu
  kết nối có nhận **dữ liệu** (heartbeat-only không tính là session).
- **`IngestFrameAsync`:** tách dòng tên file (nếu có) → phát hiện type từ header row-1
  (`CsvTypes.DetectType`) → đếm số dòng theo parser của type → lưu upload qua `ICsvStore.SaveAsync`
  (chuẩn hóa vào bảng theo type + supersede bản cũ) → ghi bản backup ra đĩa → log tóm tắt.
- **`HandleHeartbeatAsync` (liveness):** nhận `PING,<deviceName>,<epochMillis>`, cập nhật
  `BtClientStatus.AddHeartbeat()` (đặt `LastSeenUtc`/`LastHeartbeatUtc`, đếm `Heartbeats`) và
  **trả** `PONG,<radioName>,<epochMillis>` (bọc STX/ETX) để Android xác nhận listener sống &
  phản hồi. Không ghi DB, không tính gói/session dữ liệu. Connect/disconnect log ở mức
  **Debug** (vì heartbeat 5s/lần); chỉ log **Information** khi thiết bị chuyển offline→online.
  Ngưỡng offline `HeartbeatTimeout` = **15s** (lỡ 3 nhịp). Chi tiết giao thức:
  [04 §4.1](04-giao-thuc-va-luong-du-lieu.md).

## 2.5. Gỡ khung — `Services/FrameDecoder.cs`

Bộ tách khung **có trạng thái**, biến luồng byte thành các khung text `STX...ETX`:

- `STX = 0x02` mở khung (reset phần dở dang), `ETX = 0x03` đóng khung → trả chuỗi UTF-8
  đã trim; `MaxFrameBytes = 64KB` chống khung "chạy loạn".
- Byte ngoài khung bị bỏ qua (nhiễu/keep-alive). Khung rỗng (STX ngay ETX) trả `null`.
- `Push(byte)` → `string?`; `Push(byte[], count)` → `IEnumerable<string>` (nhiều khung trong
  một chunk vẫn yield đúng thứ tự; một khung bị cắt qua nhiều chunk vẫn ghép lại được).

## 2.6. Phân tích CSV theo type — `Services/CsvTypes.cs`

Thuần, test được. Định nghĩa 3 type CSV (monitor/pallet/direct) qua header row-1.

- `DetectType(header)` xác định type từ token đặc trưng trong header (chịu được đổi thứ tự cột).
- `ParseFilename(name)` tách `{type}_{yyyyMMdd}_{termId}_{index}.txt` (neo vào 8 chữ số ngày,
  `index` = nhóm số cuối); vẫn nhận định dạng cũ `{type}__{index}__{termId}.csv`.
- `ParseMonitor` / `ParsePallet` / `ParseDirect` chuẩn hóa từng dòng ra
  `MonitorEntry` / `PalletOp`(+`PalletOpItem`) / `DirectEntry`.
- **Tách CSV** kiểu RFC-4180: xử lý field có dấu nháy, nháy lồng `""`→`"`, dấu phẩy.

> **Đã gỡ:** type `legacy` (header `id`/`LogId`) cùng `Services/CsvLogParser.cs`,
> `Services/LogIngestService.cs` và bảng `SyncLogs` đã bị bỏ. CSV không khớp 3 type → `Type="unknown"`
> (chỉ giữ `RawCsv`).

## 2.7. Idempotency — supersede

Không còn dedup theo `LogId`. Với dữ liệu typed hiện tại, `CsvStore` đánh dấu upload cũ hơn của
cùng `(TermId, Type)` là `Superseded=true` khi có `UploadIndex` mới → số liệu dashboard không nhân
đôi khi gửi lại. Bản backup trên đĩa (cùng index) được ghi đè idempotent.

## 2.8. Trạng thái sống & inbox

- **`Services/ServiceStatus.cs`** (singleton): `ConcurrentDictionary<address, BtClientStatus>`.
  Mỗi `BtClientStatus` giữ `Name`, `WorkerId`, `Connected`, `ConnectedAtUtc`, `LastFrameUtc`
  (data), `LastSeenUtc` (mọi liên lạc), `LastHeartbeatUtc`, và counters thread-safe
  `FramesReceived` / `RecordsIngested` / `Sessions` (data) / `Heartbeats` (dùng `Interlocked`).
  `IsOnline(now)` = đang nối **hoặc** `LastSeenUtc` trong vòng `HeartbeatTimeout` (**15s**,
  hằng số tĩnh trên `ServiceStatus`). Cấp server: `ServerListening`, `RadioName`,
  `ServiceName`, `LastError`. `Clients` trả danh sách đã sắp (**online trước**, rồi theo liên
  lạc gần nhất). Có `SeedFromPersisted(records)` để **nạp lại roster** lúc khởi động và
  `RestoreFrom(...)` trên `BtClientStatus` để khôi phục counters/timestamps (đặt offline).
- **`Services/DeviceStore.cs`** (`IDeviceStore`, singleton): mirror roster xuống bảng `Devices`.
  `LoadAllAsync` (nạp lúc start), `UpsertAsync(BtClientStatus)` (gọi khi connect/ingest/
  heartbeat/disconnect — best-effort, không làm hỏng luồng nhận nếu DB lỗi), `ClearAsync`.
- **`Services/CsvStore.cs`** (`ICsvStore`, singleton): lưu mỗi upload vào bảng `CsvUploads`
  (giữ nguyên `RawCsv`), `GetByDeviceAsync(address)` (metadata, mới nhất trước, cap 500),
  `GetRowsAsync(id)` (parse lại `RawCsv` → rows, **giữ cả dòng trùng trong file**), `ClearAsync`.
  Thay cho `CsvInbox` in-memory cũ ⇒ danh sách CSV **bền qua restart**.

## 2.9. Mô hình dữ liệu & DB — `Data/AppDbContext.cs`

> Bảng `SyncLogs` (và `Models/LogEntry.cs`) **đã bị gỡ bỏ**. Startup còn chạy
> `DROP TABLE IF EXISTS "SyncLogs"` để dọn DB cũ (bảng rỗng nên không mất dữ liệu).

Tên bảng/cột dùng **snake_case** (MySQL). Mọi bảng có PK số `id BIGINT AUTO_INCREMENT`; FK trỏ tới `id`.

Bảng `devices` (roster thiết bị BT, bền qua restart) — PK = `id`:

| Cột | Ghi chú |
|-----|---------|
| `id` (PK) | `bigint` auto-increment |
| `address` (UNIQUE) | Địa chỉ MAC Bluetooth — khóa tự nhiên ổn định (index UNIQUE) |
| `name` / `worker_id` | Tên máy + WorkerId gần nhất |
| `first_seen_utc` / `last_seen_utc` / `last_frame_utc` / `last_heartbeat_utc` | `datetime(6)` |
| `frames_received` / `records_ingested` / `sessions` / `heartbeats` | Counters tích luỹ |

Bảng `csv_uploads` (mỗi lần sync = 1 CSV) — PK = `id`, **FK `device_id`→devices.id** (ON DELETE
CASCADE), index trên `device_id`, `(term_id, type)`, `(type, log_date)`:

| Cột | Ghi chú |
|-----|---------|
| `id` (PK) | `bigint` auto-increment |
| `device_id` (FK) | Thuộc thiết bị nào (quan hệ device 1—* csv_uploads). Lấy tên/WorkerId qua JOIN `devices` |
| `received_at_utc` / `source` | `datetime(6)` / "Bluetooth"|"WiFi" |
| `type` / `term_id` / `upload_index` / `superseded` | Envelope từ tên file (xem docs/04 §4.1b) |
| `log_date` | **Ngày của log** (`DATE`, từ `yyyyMMdd` trong tên file) — dùng cho **bộ lọc ngày**. `null` cho bản cũ → fallback theo `received_at_utc`. |
| `row_count` | Số dòng |
| `raw_csv` | **Nội dung CSV nguyên văn** (`longtext`) — parse lại để dựng rows (csv 1—* rows) |

> Đã **bỏ** khỏi `csv_uploads` (2026-07-06): `Device`(tên)/`WorkerId` (trùng với `devices`) và
> `Inserted`/`Duplicates` (tàn dư dedup cũ). `CsvUpload.DeviceAddress` giờ `[NotMapped]` — biến tạm để
> `CsvStore.SaveAsync` resolve ra `device_id` rồi lưu 2 pha (insert upload → lấy id → insert rows chuẩn hóa).

Bảng chuẩn hóa (`monitor_entries`/`pallet_ops`(+`pallet_op_items`)/`direct_entries`): cột thời-gian
`開始/終了時刻` là `TIME` (`TimeOnly?`), `出荷日` là `DATE` (`DateOnly?`), số đếm `INT`, mã/`状態` `VARCHAR`.

**Quan hệ:** `devices` 1—* `csv_uploads` (theo `device_id`); mỗi `csv_uploads` 1—* rows
(parse `raw_csv`) và 1—* bản chuẩn hóa (cascade khi xoá upload).

DB = **MySQL ngoài** (cài/chạy riêng; kết nối theo `mysql.xml`). Schema tạo lúc khởi động bằng
**`db.Database.EnsureCreated()`** (EF Core 3.1 trên net48; không dùng migrations). Data do server
MySQL bên ngoài quản lý.

## 2.10. Giám sát & dashboard — `Monitoring/` + `MainForm.cs`

- **`MonitorService.GetSnapshotAsync()`** dựng `StatusDto`: thời gian server/uptime; trạng
  thái server BT; danh sách client (từ `ServiceStatus`); **tổng số dòng log + log "hôm nay"**. Số
  này đếm **`SUM(RowCount)` trên `CsvUploads` chưa bị supersede**; "hôm nay" lọc theo `LogDate`
  (ngày trong tên file) so với `DateTime.Today` (giờ địa phương).
  CSV **không** nằm trong snapshot — lấy riêng theo nhu cầu:
  `GetCsvsForDeviceAsync(address, day?)` (danh sách CSV của 1 thiết bị; khi truyền `day` thì **lọc
  theo ngày trong TÊN FILE** = `LogDate`, không phải ngày nhận — upload `LogDate==null` bị
  loại khi lọc) và `GetCsvRowsAsync(id)` (parse `RawCsv` → rows) qua `ICsvStore`.
- **`MonitorService.ClearAllAsync()`** — xoá **toàn bộ** `CsvUploads` (cascade sang bảng chuẩn hóa)
  + `Devices` trong DB (`ExecuteDeleteAsync`) **và** danh sách client sống (`ServiceStatus.ClearClients()`).
  Có ghi `LogWarning`. Mang tính phá huỷ → UI phải xác nhận trước. (Không huỷ ghép đôi Bluetooth
  ở Windows — chỉ reset dữ liệu/trạng thái trong app.)
- **`StatusModels.cs`**: `StatusDto` / `BtServerDto` / `ClientDto` / `ReceivedCsvDto` /
  `LogsDto` / `LogDto`. **Lưu ý:** các DTO này vừa là nguồn bind cho lưới WinForms vừa là
  shape JSON của `/api/status` — sửa một chỗ ảnh hưởng cả hai.
- **`MainForm.cs`**: Timer 2s gọi `GetSnapshotAsync()` rồi cập nhật:
  - **Trái-trên:** lưới client Bluetooth (Tên máy, WorkerId, **Hiện diện** [Online/Offline theo
    heartbeat], Gói, Bản ghi, **Nhịp cuối** [heartbeat], **Data cuối**). Danh sách này **bền qua
    restart** (nạp từ bảng `Devices`, hiện offline cho tới khi máy kết nối lại). **Chọn 1 thiết bị
    để lọc** danh sách CSV bên dưới; chưa chọn thì **mặc định chọn thiết bị đầu tiên**
    (`UpdateClients` giữ lựa chọn theo `Address`; `OnDeviceSelectionChanged` → `RefreshCsvListAsync`).
  - **Trái-dưới:** lưới CSV **của thiết bị đang chọn**, lấy từ DB qua
    `GetCsvsForDeviceAsync(address, day)` (mỗi lần sync = 1 dòng) — **bền qua restart**. Danh sách
    này **lọc theo cùng ngày của bộ lọc ngày** bên phải (theo **ngày trong tên file** = `LogDate`),
    nên chỉ hiện CSV của ngày đang chọn; đổi ngày → `OnDayFilterChanged` reset `_csvSig` và gọi lại
    cả `RefreshCsvListAsync` lẫn `RefreshDayLogAsync`. Danh sách **chỉ mang tính tham khảo**: chọn 1
    CSV **không** điều khiển bảng bên phải.
  - **Ranh giới trái/phải KÉO ĐƯỢC:** thân cửa sổ là một `SplitContainer` dọc (`_split`,
    `Orientation = Vertical`) — Panel1 = cột trái (client + CSV), Panel2 = bảng log bên phải.
    Người dùng kéo thanh chia để chỉnh độ rộng hai bên (`Panel1MinSize=260`, `Panel2MinSize=420`).
    Vị trí ban đầu (~30% bên trái) đặt ở `MainForm.Load` (clamp trong khoảng hợp lệ) — **không**
    đặt `SplitterDistance` trong Designer vì lúc đó bề rộng còn là 150px sẽ ném lỗi.
  - **Phải — LOG THEO NGÀY:** không hiển thị rows của 1 CSV đang chọn, mà hiện **toàn bộ log của 1
    ngày** cho 1 type. Có **bộ lọc ngày** + **radio chọn type** (`monitor` mặc định / `pallet` /
    **`direct`**). Bộ lọc ngày dạng **`データ日 ‹ [DateTimePicker] ›`**: `DateTimePicker` **mặc định
    hôm nay**, `MaxDate = DateTime.Today` nên **không chọn được ngày sau hôm nay**; hai nút **‹ / ›**
    (`StepDay(±1)`) lùi/tiến 1 ngày, nút **›** **bị vô hiệu khi đã ở hôm nay** (`UpdateDateNav` chạy
    mỗi `ValueChanged`). `RefreshDayLogAsync` → `MonitorService.GetDayLogAsync(typeKey, date)` → gộp
    **mọi** upload của type đó trong ngày (`LogDate`, fallback `ReceivedAtUtc`), rồi `ApplyDisplayFilter`
    lọc theo type (monitor ẩn 状態=9; direct hiện hết; pallet dedup key PLNo.+顧客+納入便, ẩn 9, giữ
    終了時刻 mới nhất). Cột **`#`** + **tô màu dòng trùng** (`DupPalette`); cột lưới **kéo dài tới mép
    phải** (`AutoSizeColumnsMode = Fill`) với **tỉ lệ % theo từng cột** (`ApplyLogColumnWeights` gán
    `FillWeight` theo header — 品目明細 rộng, #/状態 hẹp; chạy lại mỗi lần `DataBindingComplete` vì cột
    auto-generate). Né nhấp nháy bằng chữ ký `_dayLogSig`.
  - **Layout bộ lọc (`_filterRow`)** là **một `TableLayoutPanel` 1 hàng** (không còn `FlowLayoutPanel`
    + hack `Padding` top): mỗi control `AutoSize` + `Anchor` (nhóm lọc neo `Left`, nút xuất neo `Right`)
    nên **tự canh giữa theo chiều dọc và thẳng hàng** trên cùng một baseline — không còn offset top phải
    chỉnh tay. Cột: `[nhãn ngày][‹][picker][›] · [nhãn type][monitor][pallet][direct] · [cột co giãn][Xuất CSV]`;
    cột co giãn (`Percent 100`) đẩy nút Xuất sát mép phải. **Chiều cao hàng lọc = 34px, bằng đúng hàng 0
    của `_clientsLayout` (nhãn trạng thái `● Bluetooth SPP` bên trái)** nên hàng lọc phải và nhãn server
    trái nằm **cùng một baseline** giữa hai panel — không còn cảm giác panel phải lệch cao độ so với trái.
  - **Canh mép trên hai group box:** group box trái nằm trong `TableLayoutPanel` (`_leftLayout`) nên bị
    **Margin ô mặc định 3px** đẩy xuống; còn `_grpLogs` phải `Dock Fill` thẳng trong `Panel2` (Panel bỏ
    qua Margin) → trước đây cao hơn trái đúng 3px. Đã đặt **`_split.Panel2.Padding = new Padding(3)`** để
    thụt group box phải 3px đều bốn phía → mép trên hai bên trùng khít (đo pixel: cùng một `y`).
  - **Nút "Xuất CSV" (CSV出力)** nằm **bên phải bộ lọc** (`_btnExportDay`, neo phải — chỉ còn 1 nút
    này, nút Export trên thanh công cụ đã bỏ). **Xuất ĐÚNG bảng đang hiển thị**: serialize thẳng
    `DataTable` mà `_dgvLogs` đang bind (cùng type+ngày, cùng bộ lọc, **cùng cột `#`**, cùng thứ tự
    dòng) ra `.csv` (UTF-8 BOM) qua `SaveFileDialog`. Bảng được dựng **một chỗ** ở
    `RefreshDayLogAsync` → sửa cách dựng bảng thì cả lưới lẫn file xuất đổi theo, không lệch nhau.
  - Header hiện tên radio + trạng thái lắng nghe; thời gian đổi UTC→giờ địa phương; ô **Hiện
    diện** xanh `● Online` khi còn heartbeat trong 15s, xám `○ Offline` khi quá hạn.
  - **Đa ngôn ngữ:** mọi nhãn/cột/thông báo lấy qua `UI/Localization.cs` (`Loc.T(key)`) — **EN /
    JA** (tiếng Việt đã bỏ khỏi tool PC), đổi tại chỗ bằng combo ngôn ngữ trên thanh công cụ
    (`Loc.SetLanguage` → sự kiện `Loc.Changed` → `ApplyTexts` + làm mới). Mặc định lần đầu **theo
    locale Windows** (ja → tiếng Nhật, còn lại → English); lựa chọn được lưu ở
    `ui-language.txt` **trong thư mục app** (giá trị `Vi` cũ nếu còn sót sẽ không
    parse được và tự rơi về dò theo locale).
  - **Thanh công cụ:** nút **"Reset"** (đỏ) + **"Mở thư mục backup"** + nhãn trạng thái/uptime/tổng +
    **nhãn trạng thái MySQL** (`● connected` xanh / `× disconnected` đỏ, do `MonitorService.IsDbConnectedAsync`
    kiểm tra mỗi 2s khi được bật) + combo ngôn ngữ.
  - **Ngôn ngữ theo config:** `app/configuration.xml` có `<language>` (**mặc định `ja`**; `ja`/`en`) là
    **nguồn chân lý** — áp lúc khởi động qua `Loc.SetLanguage` (đè dò-OS / `ui-language.txt`); đổi bằng
    combo lúc chạy thì **ghi ngược lại config** để config luôn là nguồn duy nhất.
  - **Ẩn/hiện theo `app/configuration.xml` (`UI/UiConfig.cs`, mặc định ẩn HẾT):** 7 công tắc —
    `showResetButton`, `showOpenBackupButton`, `showLanguageButton`, `showMasterButtons` (2 nút master),
    `showBluetoothPanel` (panel1 trái-trên), `showCsvPanel` (panel2 trái-dưới), `showMysqlStatus`
    (nhãn MySQL). Khi tất cả = false thì **cả cột trái thu gọn**, chỉ còn bảng log bên phải.
    `MainForm.ApplyUiConfig()` áp một lần lúc khởi động.
  - **Icon** cửa sổ/taskbar = **logo NEX** (`app.ico` cạnh exe; `ApplicationIcon` trong csproj +
    `MainForm.TryLoadAppIcon` lúc chạy — không có file thì bỏ qua, không lỗi).
  - **Nút "Reset"** (`_btnClear`, đỏ; trước tên là "Xoá toàn bộ DB"): hỏi xác nhận (MessageBox Yes/No)
    → `ClearAllAsync()` (xoá toàn bộ log + CsvUploads + Devices) → làm mới lưới. Dùng để test lại từ đầu.

### Master (顧客 / 品目) — xem / sửa / lưu + đồng bộ ngược (Giai đoạn 1)

Mục tiêu: sửa master trên PC rồi đẩy ngược về app, **khỏi phải cài lại app** mỗi lần đổi master.

- **Lưu trữ (`Services/MasterStore.cs`, `IMasterStore`, singleton):** PC là **nguồn chân lý** của
  2 file `customer_master.csv` (`デポ納入先,顧客コード,納入先,納入便`) và `item_master.csv`
  (`品目コード,品目名称,箱種,品目名称_2`). File nằm ở `<root>/_master/` (`AppPaths.MasterDir`),
  lưu **UTF-8 không BOM** (khớp asset của app để stream nguyên byte). Lần đầu thiếu file thì **seed**
  từ bản đóng kèm cạnh exe (`master-seed/`, copy từ assets Android qua csproj). `LastModifiedUnixMillis(kind)`
  = mtime file (epoch ms) — mốc "authored" phía PC để so với `created_at` của app khi đồng bộ ngược.
- **UI (`MainForm`):** hàng nút **ngay trên panel1 (danh sách thiết bị)** — `_masterBar` gồm
  **[顧客マスタ] [品目マスタ]** (KHÔNG có nút đồng bộ — app tự kéo về, xem dưới). Bấm 顧客/品目 → panel
  **bên phải đổi sang chế độ master**: ẩn `_grpLogs`, hiện `_grpMaster` (grid `_dgvMaster` **sửa
  được**: thêm/xoá dòng, sửa ô) + thanh công cụ **[行追加][行削除][保存][閉じる]**. Đúng một trong
  `_grpLogs`/`_grpMaster` hiện tại một thời điểm.
- **Đọc/ghi round-trip:** `CsvToDataTable` (dùng lại `CsvTypes.SplitCsv`, dòng 1 = header, giữ
  `_masterHeaders` gốc để lưu đúng) ↔ `DataTableToCsv` (ghi header gốc + mọi dòng **không rỗng**, bỏ
  dòng trắng cuối lưới), escape CSV **giống hệt** export day-log → lưới và file không lệch nhau. **保存**
  ghi file (temp+move, UTF-8 no BOM) → cập nhật mtime → lần app 「マスタ受信」 kế tiếp sẽ kéo bản mới.
- **Đồng bộ ngược (PC phục vụ, app kéo):** không có nút đẩy trên PC. `BluetoothSppServer` xử lý frame
  `MASTER_REQ` do app gửi: so `LastModifiedUnixMillis` với `created_at` app gửi lên, file nào PC mới
  hơn thì gửi về, kết bằng `MASTER_END`. Chi tiết wire-format ở `docs/04 §4.1`. (Vì điện thoại đi quét
  ra/vào vùng BT, PC không tự mở kết nối được → luồng luôn do app khởi tạo.)
- **Không đụng day-log:** khi đang mở master, `RefreshDayLogAsync` return sớm (không rebind lưới ẩn,
  không đè lên ô người dùng đang sửa).

## 2.11. Endpoint HTTP (HttpListener, trong tiến trình)

| Method | Đường dẫn | Mục đích |
|--------|-----------|----------|
| `GET` | `/api/status` | Snapshot giám sát đầy đủ (chính là cái dashboard hiển thị) |
| `GET` | `/health` | `{"status":"ok"}` |
| `POST` | `/api/sync` | Trả **501 Not Implemented** — Wi-Fi chưa đấu, đường CSV legacy đã gỡ |

Phục vụ bởi `Services/HttpApiService.cs` (`System.Net.HttpListener`, một `IHostedService`) — net48
không có Kestrel. Thử bind `http://+:8090/` (mọi interface, có thể cần urlacl admin) rồi **fallback
`http://localhost:8090/`**; nếu vẫn lỗi thì tắt API kèm cảnh báo (dashboard đọc state in-process, không
phụ thuộc HTTP). Port lấy từ key cũ `Kestrel:Endpoints:Http:Url` trong `appsettings.json` (mặc định 8090).

## 2.12. Build / chạy

```bash
dotnet build LeontecSyncLogSystem.slnx -c Release
dotnet run --project LeontecSyncLogSystem -c Release   # khởi động host + mở dashboard
# exe: LeontecSyncLogSystem/bin/Release/LogManagement/LogManagement.exe
```

### 2.12.1. Triển khai sang máy khác (KHÔNG cần cài gì)

App target **.NET Framework 4.8**, vốn **có sẵn trong mọi Windows 10/11**. Nên triển khai chỉ là:
**copy thư mục output `LogManagement/` sang máy đích rồi chạy exe** — không cần cài runtime, không nhúng
runtime (~7 MB). Lần chạy đầu app tự tạo các thư mục anh em `_master` (đã seed)/`_backup`/`mysql.xml`.
(MySQL vẫn là **external** — cài/chạy riêng và trỏ `mysql.xml` vào nó.)

> Trước đây từng target .NET hiện đại (net10) + publish self-contained (~210 MB). Đã **chuyển sang
> net48 ngày 2026-07-10** để chạy trên Windows mặc định mà không cần cài/nhúng runtime. 32feet chạy
> RFCOMM SPP server trên net48 qua Win32 stack; EF Core 3.1 + `EnsureCreated`; HTTP API dùng
> `HttpListener` thay Kestrel; `DateOnly`/`TimeOnly` + cú pháp C# mới được polyfill
> (Portable.System.DateTimeOnly + PolySharp).

Để Bluetooth chạy: điện thoại phải **bonded** với PC này, và `pcBluetoothName` của app phải
khớp (lỏng) với tên radio Bluetooth của PC (hiện trực tiếp trên header dashboard).

## 2.13. Lưu ý / cạm bẫy

- App là **GUI desktop**, không chạy được như Windows Service không màn hình. Cần chạy nền
  thực sự thì phải tách host ra lại.
- 32feet.NET cần radio Bluetooth; server tự retry nếu radio tắt/không có.
- Không có git repo ở đây — **xóa file là không hoàn tác được**.
- Bất kỳ thay đổi code nào ở phần này phải cập nhật tài liệu này — xem
  [05 — Công nghệ & quy tắc phát triển](05-cong-nghe-va-quy-tac-phat-trien.md).
