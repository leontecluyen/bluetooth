# 02 — Ứng dụng PC (C# / .NET 8 / WinForms)

Thư mục: `LeontecSyncLogSystem/`. Output: `LeontecSyncLogSystem.exe`.

## 2.1. Bản chất: một desktop app "lai"

Đây là **một** project WinForms (.NET 8) duy nhất, nhưng bên trong nó **nhúng một
ASP.NET Core generic host**. Khi chạy:

1. Tạo `WebApplication` (host) với Kestrel + EF Core + các service nền.
2. `Start()` host (không chặn luồng).
3. Chạy `Application.Run(MainForm)` — vòng lặp thông điệp WinForms trên luồng chính.
4. Khi đóng form → `Stop()` host.

Vì dashboard và host **ở chung tiến trình**, MainForm lấy `MonitorService` trực tiếp từ DI,
không gọi HTTP vào chính mình.

> Lưu ý cấu hình project: target **`net10.0-windows10.0.19041.0`** (bản Win10 này bắt buộc cho
> RFCOMM server WinRT của 32feet.NET), `<RollForward>LatestMajor</RollForward>`. **Vì sao net10
> chứ không net8:** máy dev chỉ có `Microsoft.AspNetCore.App` bản **10.x** (không có 8.x), mà
> WinForms **Designer** (`DesignToolsServer.exe`) nạp `FrameworkReference` của project và đòi đúng
> major — để net8 thì Designer cứ hiện popup *"cài AspNetCore.App 8.0"*; bấm cài sẽ thêm runtime
> .NET 8 lạc rồi làm `dotnet run` crash (NETCore 8.0.28 vs ASP.NET-10 cần 10.0.9). Cả 3 shared
> framework đều có 10.0.9 nên net10 giúp **build + run + designer** dùng chung runtime đã cài —
> **đừng bấm "Yes" cài .NET 8** ở popup Designer. Kestrel trong app WinForms đến từ
> `<FrameworkReference Include="Microsoft.AspNetCore.App" />` cộng vài `<Using>` thay cho
> implicit usings mà Web SDK lẽ ra cung cấp.

## 2.2. Sơ đồ thư mục & vai trò file

```
LeontecSyncLogSystem/
  Program.cs            Bootstrap host (WebApplication) + đăng ký DI + endpoint HTTP + chạy WinForms
  MainForm.cs           Dashboard; mỗi 2s gọi MonitorService (trong tiến trình)
  Worker.cs             BackgroundService: chạy BluetoothSppServer
  appsettings.json      Cấu hình: Logging, Kestrel, Sync (tên service BT), Database
  Models/
    LogEntry.cs         Entity DB SyncLogs (canonical legacy, đã dedup)
    DeviceRecord.cs     Entity DB Devices — roster thiết bị BT (bền qua restart)
    CsvUpload.cs        Entity DB CsvUploads — 1 lần sync = 1 CSV (FK→Devices), giữ RawCsv + Type/TermId/UploadIndex/Superseded
    MonitorEntry.cs     Entity DB MonitorEntries — 1 dòng monitor (入出庫) chuẩn hóa
    PalletOp.cs         Entity DB PalletOps + PalletOpItems — thao tác pallet + item tách từ 品目明細
  Data/
    AppDbContext.cs     EF Core; SyncLogs(PK=LogId) + Devices(PK=Address) + CsvUploads(PK=Id, FK Device)
  Services/
    SyncOptions.cs        SyncOptions (BluetoothServiceName) + DatabaseOptions
    CsvLogParser.cs       CSV → LogEntry (thuần, test được; nhận header 'id' hoặc 'LogId')
    FrameDecoder.cs       Đóng/gỡ khung byte STX/ETX (thuần, test được)
    LogIngestService.cs   Ghi DB có dedup (tương đương ON CONFLICT DO NOTHING)
    BluetoothSppServer.cs  Server RFCOMM SPP (32feet.NET), accept nhiều client
    CsvTypes.cs           Định nghĩa type (monitor/pallet/legacy): header hằng, DetectType(header), ParseFilename(term/index), parser ra MonitorEntry/PalletOp(+Item)
    CsvStore.cs           Lưu CSV upload + chuẩn hóa vào bảng theo type + supersede bản cũ; query theo device; trả RawCsv để hiển thị
    DeviceStore.cs        Lưu/nạp/xoá roster thiết bị xuống bảng Devices (bền qua restart)
    ServiceStatus.cs      Trạng thái sống thread-safe (client BT + server), singleton
  Monitoring/
    MonitorService.cs     Dựng snapshot StatusDto từ ServiceStatus + CsvInbox + DB
    StatusModels.cs       Các DTO (vừa là nguồn bind cho lưới UI, vừa là shape JSON /api/status)
```

## 2.3. Bootstrap — `Program.cs`

- Đọc `appsettings.json` từ thư mục exe.
- Bind `SyncOptions` (mục `Sync`) và `DatabaseOptions` (mục `Database`).
- Chọn provider DB theo `Database:Provider`: `UseSqlite` / `UseSqlServer` / `UseNpgsql`.
  Schema tạo tự động lúc khởi động bằng `EnsureCreated`. Vì `EnsureCreated` **không** thêm bảng
  mới vào DB đã tồn tại, với SQLite có thêm `CREATE TABLE IF NOT EXISTS` cho `Devices` **và**
  `CsvUploads` (có FK→Devices, không phá dữ liệu cũ). Sau đó **nạp roster thiết bị** từ DB và
  `ServiceStatus.SeedFromPersisted(...)`
  để các máy đã thấy hiện lại (trạng thái offline) thay vì biến mất khi mở lại app.
- Đăng ký DI (đa số là **singleton** vì là trạng thái sống của tiến trình):
  - `ServiceStatus` — trạng thái BT toàn cục.
  - `LogIngestService` — dedup + ghi DB.
  - `IDeviceStore`/`DeviceStore` — lưu/nạp roster thiết bị (bảng `Devices`).
  - `ICsvStore`/`CsvStore` — lưu/nạp CSV uploads (bảng `CsvUploads`).
  - `MonitorService` — dựng snapshot.
  - `Worker` (hosted service) — chạy server Bluetooth.
- Map endpoint HTTP: `POST /api/sync`, `GET /api/status`, `GET /health`.
- **Bắt lỗi toàn cục:** trước `Application.Run`, đăng ký `Application.ThreadException` +
  `AppDomain.UnhandledException`, và bọc `new MainForm(...)`/`Run` trong try/catch → `ReportFatal`
  ghi exception vào `%LOCALAPPDATA%/LeontecSyncLogSystem/crash.log` và hiện `MessageBox`. Nhờ vậy
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
  `LeontecSyncLogSystem.exe`** và làm build kế tiếp lỗi *"the file is locked by LeontecSyncLogSystem
  (PID)"* (copy `apphost.exe → .exe` thất bại). Gặp lỗi đó: kill tiến trình `LeontecSyncLogSystem`
  còn sót (`Get-Process LeontecSyncLogSystem | Stop-Process -Force`) rồi build lại.

## 2.4. Nhận Bluetooth — `Worker.cs` + `Services/BluetoothSppServer.cs`

`Worker` là `BackgroundService`, chỉ làm một việc: dựng `BluetoothSppServer` với các
dependency được tiêm (`ILogIngestService`, `ServiceStatus`, `CsvInbox`, tên service, logger)
rồi `await server.RunAsync(stoppingToken)`.

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
- **`IngestFrameAsync`:** `CsvLogParser.ParseDocument` → lấy `WorkerId` từ dòng đầu cập nhật
  client → `LogIngestService.IngestAsync` → thêm `ReceivedCsv` vào `CsvInbox` → ghi log
  tóm tắt (số khung, số bản ghi chèn/trùng).
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

## 2.6. Phân tích CSV — `Services/CsvLogParser.cs`

Thuần, test được. CSV cột: `id,workerId,jobType,barcodeData,startTime,endTime`.

- `ParseDocument(csv, syncMethod)` cho CSV nhiều dòng (Wi-Fi); `TryParseLine(...)` cho 1 dòng.
- Bỏ dòng header nếu field đầu là `id`/`LogId` (không phân biệt hoa thường).
- **Tách CSV** kiểu RFC-4180: xử lý field có dấu nháy, nháy lồng `""`→`"`, dấu phẩy.
- **Thời gian linh hoạt:** nhận **epoch-millis** (≥10 chữ số) của Android, hoặc ISO-8601
  (nhiều biến thể có/không timezone, DD/MM và MM/DD). Chuẩn hóa về **UTC**.
- **Suy `LogId` ổn định:** nếu `id` trống/sai → SHA-1 từ
  `{WorkerId}|{JobType}|{BarcodeData}|{StartTime:O}|{EndTime:O}`, set version 5 + variant
  (RFC 4122). Cùng nội dung ⇒ cùng Guid ⇒ dedup khi gửi lại.

## 2.7. Ghi DB có dedup — `Services/LogIngestService.cs`

Tương đương `ON CONFLICT (LogId) DO NOTHING`, độc lập provider. Trả
`IngestResult(Received, Inserted, Duplicates)`:

1. **Gộp trùng trong batch:** group theo `LogId`, giữ bản đầu.
2. **Bỏ LogId đã tồn tại:** query DB lấy các LogId đang có, loại ra.
3. **Chèn 1 lần** `SaveChangesAsync()`. Nếu dính `DbUpdateException` (luồng/tiến trình khác
   chèn xen vào giữa lúc check và write) → clear change tracker → **retry từng dòng**: dòng
   nào đụng khóa thì detach và đếm là trùng.

Thiết kế này an toàn với hai kênh chạy song song và với việc gửi lại — chèn cùng batch hai
lần cho cùng kết quả `Inserted`. `Duplicates = Received − Inserted` (gộp cả trùng-trong-file
do gộp ở bước 1 lẫn trùng-với-DB) để con số hiển thị trực quan.

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

## 2.9. Mô hình dữ liệu & DB — `Models/LogEntry.cs`, `Data/AppDbContext.cs`

Bảng `SyncLogs`:

| Cột | Kiểu | Ghi chú |
|-----|------|---------|
| `LogId` | Guid (PK) | `ValueGeneratedNever` — do thiết bị/parser cấp, DB không tự sinh |
| `WorkerId` | string | Tên máy/thiết bị Android; có index để truy vấn theo máy |
| `JobType` | string | 検品 / 出荷 / 直送 |
| `BarcodeData` | string | Nội dung mã vạch |
| `StartTime` | DateTime | UTC; có index cho thống kê "hôm nay" |
| `EndTime` | DateTime | UTC |
| `SyncMethod` | string | "Bluetooth" / "WiFi" — **do kênh nhận set**, không có trong CSV |

Bảng `Devices` (roster thiết bị BT, bền qua restart) — PK = `Address`:

| Cột | Ghi chú |
|-----|---------|
| `Address` (PK) | Địa chỉ MAC Bluetooth của thiết bị |
| `Name` / `WorkerId` | Tên máy + WorkerId gần nhất |
| `FirstSeenUtc` / `LastSeenUtc` / `LastFrameUtc` / `LastHeartbeatUtc` | Mốc thời gian |
| `FramesReceived` / `RecordsIngested` / `Sessions` / `Heartbeats` | Counters tích luỹ |

Bảng `CsvUploads` (mỗi lần sync = 1 CSV) — PK = `Id`, **FK `DeviceAddress`→Devices** (ON DELETE
CASCADE), có index trên `DeviceAddress`:

| Cột | Ghi chú |
|-----|---------|
| `Id` (PK) | Guid |
| `DeviceAddress` (FK) | Thuộc thiết bị nào (quan hệ Device 1—* CsvUploads) |
| `ReceivedAtUtc` / `Source` / `Device` / `WorkerId` | Metadata |
| `Type` / `TermId` / `UploadIndex` / `Superseded` | Envelope từ tên file (xem docs/04 §4.1b) |
| `LogDate` | **Ngày của log** (date, lấy từ `yyyyMMdd` trong tên file) — dùng cho **bộ lọc ngày** ở bảng bên phải. `null` cho bản cũ không có ngày → fallback theo `ReceivedAtUtc`. Có index `(Type, LogDate)`. |
| `RowCount` / `Inserted` / `Duplicates` | Số dòng / mới / trùng |
| `RawCsv` | **Nội dung CSV nguyên văn** — parse lại để dựng rows (Csv 1—* rows) |

**Quan hệ:** `Devices` 1—* `CsvUploads` (theo `DeviceAddress`); mỗi `CsvUploads` 1—* rows
(suy ra bằng cách parse `RawCsv`). `SyncLogs` là bản canonical đã dedup (tách khỏi audit per-CSV).

Provider đổi qua `Database:Provider` (`Sqlite`|`SqlServer`|`Postgres`); mặc định SQLite
(`synclogs.db`, zero-config). File `synclogs.db*` là artifact test cục bộ — đừng commit.

## 2.10. Giám sát & dashboard — `Monitoring/` + `MainForm.cs`

- **`MonitorService.GetSnapshotAsync()`** dựng `StatusDto`: thời gian server/uptime; trạng
  thái server BT; danh sách client (từ `ServiceStatus`); **tổng số dòng log + log "hôm nay"**. Số
  này đếm **`SUM(RowCount)` trên `CsvUploads` chưa bị supersede** (KHÔNG đếm `SyncLogs` — bảng legacy
  này rỗng nên trước đây totals luôn = 0); "hôm nay" lọc theo `LogDate` (ngày trong tên file) so với
  `DateTime.Today` (giờ địa phương).
  CSV **không** nằm trong snapshot — lấy riêng theo nhu cầu:
  `GetCsvsForDeviceAsync(address, day?)` (danh sách CSV của 1 thiết bị; khi truyền `day` thì **lọc
  theo ngày trong TÊN FILE** = `LogDate`, không phải ngày nhận — upload legacy `LogDate==null` bị
  loại khi lọc) và `GetCsvRowsAsync(id)` (parse `RawCsv` → rows) qua `ICsvStore`.
- **`MonitorService.ClearAllAsync()`** — xoá **toàn bộ** log + `CsvUploads` + `Devices` trong DB
  (`ExecuteDeleteAsync`) **và** danh sách client sống (`ServiceStatus.ClearClients()`).
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
  - **Nút "Xuất CSV" (CSV出力)** nằm **bên phải bộ lọc** (`_btnExportDay`, dock phải — chỉ còn 1 nút
    này, nút Export trên thanh công cụ đã bỏ). **Xuất ĐÚNG bảng đang hiển thị**: serialize thẳng
    `DataTable` mà `_dgvLogs` đang bind (cùng type+ngày, cùng bộ lọc, **cùng cột `#`**, cùng thứ tự
    dòng) ra `.csv` (UTF-8 BOM) qua `SaveFileDialog`. Bảng được dựng **một chỗ** ở
    `RefreshDayLogAsync` → sửa cách dựng bảng thì cả lưới lẫn file xuất đổi theo, không lệch nhau.
  - Header hiện tên radio + trạng thái lắng nghe; thời gian đổi UTC→giờ địa phương; ô **Hiện
    diện** xanh `● Online` khi còn heartbeat trong 15s, xám `○ Offline` khi quá hạn.
  - **Đa ngôn ngữ:** mọi nhãn/cột/thông báo lấy qua `UI/Localization.cs` (`Loc.T(key)`) — **EN /
    VI / JA**, đổi tại chỗ bằng combo ngôn ngữ trên thanh công cụ (`Loc.SetLanguage` → sự kiện
    `Loc.Changed` → `ApplyTexts` + làm mới). Mặc định lần đầu **theo locale Windows** (vi/ja →
    đúng ngôn ngữ đó, còn lại → English); lựa chọn được lưu ở
    `%LOCALAPPDATA%/LeontecSyncLogSystem/ui-language.txt`.
  - **Thanh công cụ** giờ chỉ còn: nút **"Reset"** (đỏ) + nhãn trạng thái/uptime/tổng + combo ngôn
    ngữ. Nút **Refresh** và **Export** đã bỏ (lưới tự làm mới mỗi 2s qua timer; Export đã có cạnh bảng).
  - **Nút "Reset"** (`_btnClear`, đỏ; trước tên là "Xoá toàn bộ DB"): hỏi xác nhận (MessageBox Yes/No)
    → `ClearAllAsync()` (xoá toàn bộ log + CsvUploads + Devices) → làm mới lưới. Dùng để test lại từ đầu.

## 2.11. Endpoint HTTP (Kestrel, trong tiến trình)

| Method | Đường dẫn | Mục đích |
|--------|-----------|----------|
| `GET` | `/api/status` | Snapshot giám sát đầy đủ (chính là cái dashboard hiển thị) |
| `GET` | `/health` | `{"status":"ok"}` |
| `POST` | `/api/sync` | Tồn tại nhưng đang parse CSV; **Wi-Fi (JSON) chưa đấu nối** |

Mặc định bind `http://0.0.0.0:8090` (mục `Kestrel`). Test có thể override:
`Kestrel__Endpoints__Http__Url=http://127.0.0.1:8099 dotnet run ...`.

## 2.12. Build / chạy

```bash
dotnet build LeontecSyncLogSystem.slnx -c Release
dotnet run --project LeontecSyncLogSystem -c Release   # khởi động host + mở dashboard
# exe: LeontecSyncLogSystem/bin/Release/net10.0-windows10.0.19041.0/LeontecSyncLogSystem.exe
```

Để Bluetooth chạy: điện thoại phải **bonded** với PC này, và `pcBluetoothName` của app phải
khớp (lỏng) với tên radio Bluetooth của PC (hiện trực tiếp trên header dashboard).

## 2.13. Lưu ý / cạm bẫy

- App là **GUI desktop**, không chạy được như Windows Service không màn hình. Cần chạy nền
  thực sự thì phải tách host ra lại.
- 32feet.NET cần radio Bluetooth; server tự retry nếu radio tắt/không có.
- Không có git repo ở đây — **xóa file là không hoàn tác được**.
- Bất kỳ thay đổi code nào ở phần này phải cập nhật tài liệu này — xem
  [05 — Công nghệ & quy tắc phát triển](05-cong-nghe-va-quy-tac-phat-trien.md).
