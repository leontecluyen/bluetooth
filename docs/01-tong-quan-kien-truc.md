# 01 — Tổng quan & kiến trúc

## 1.1. Bối cảnh nghiệp vụ

Công nhân kho/xưởng dùng **máy Android cầm tay** để quét mã vạch khi thực hiện công việc.
Mỗi lần quét sinh ra một **bản ghi công việc** (`JobLog`) gồm: loại công việc, dữ liệu mã
vạch, thời gian bắt đầu/kết thúc, mã công nhân/thiết bị. Các bản ghi này cần được **gom về
một PC trung tâm** trong văn phòng để lưu trữ và theo dõi.

Yêu cầu cốt lõi:

- **Không mất dữ liệu**: máy cầm tay có thể offline; log phải được giữ lại và gửi lại sau.
- **Không trùng lặp**: gửi lại nhiều lần (do mất kết nối, retry) không được tạo bản ghi đôi.
- **Thấy trạng thái tức thời**: người vận hành PC nhìn dashboard biết máy nào đang gửi, đã
  nhận bao nhiêu bản ghi.
- **Tự động**: khi công nhân về văn phòng (vào vùng geofence), việc đồng bộ tự kích hoạt.

## 1.2. Hai thành phần

| Thành phần | Thư mục | Vai trò | Công nghệ chính |
|-----------|---------|---------|-----------------|
| **Ứng dụng Android** | `SyncLogs/` | Thu thập log, đồng bộ sang PC | Kotlin, Jetpack Compose, Room, WorkManager, Bluetooth Classic SPP, Retrofit |
| **Ứng dụng PC** | `LeontecSyncLogSystem/` | Nhận log, lưu DB, hiển thị dashboard | C# .NET 8, WinForms, ASP.NET Core (Kestrel), EF Core, 32feet.NET |

Android đóng vai **client Bluetooth**; PC đóng vai **server Bluetooth SPP**. Đây là điểm
mấu chốt từng gây nhầm lẫn: trước đây tưởng dùng cổng COM nên không bao giờ thấy COM hiện ra.
Thực tế PC mở một **RFCOMM SPP listener** và điện thoại chủ động kết nối tới.

## 1.3. Hai kênh đồng bộ

### Kênh chính — Bluetooth SPP (RFCOMM)

```
Android (client)                              PC (server SPP)
   │  tìm PC theo TÊN RADIO Bluetooth             │  BluetoothListener trên UUID
   │  (vd "LUYEN"), khớp lỏng                      │  00001101-0000-1000-8000-00805F9B34FB
   │  ──► mở socket RFCOMM (secure→insecure) ────► │  accept → 1 task / 1 client
   │  ──► ghi STX + CSV + ETX  ──────────────────► │  FrameDecoder gỡ khung
   │                                               │  CsvLogParser phân tích CSV
   │                                               │  LogIngestService dedup + ghi DB
```

- Đây là kênh **trọng tâm**, hoạt động trong cùng mạng nội bộ/khoảng cách Bluetooth.
- Mỗi thiết bị Android = một kết nối SPP riêng, PC phục vụ **nhiều máy đồng thời**.

### Kênh dự phòng — Wi-Fi REST (đang tạm gác)

```
Android  ──► POST JSON (mảng JobLog, Gson) ──► http://<ip>:8080/api/sync  (PC)
```

- Hiện **chưa đấu nối hoàn chỉnh**: phía PC có endpoint `POST /api/sync` nhưng đang phân
  tích CSV, chưa nhận JSON. Khi bật lại Wi-Fi cần: thêm parse JSON-array (map `id`→`LogId`,
  epoch→`DateTime`) và bind Kestrel ở cổng 8080.
- Trên Android, kênh Wi-Fi vẫn được code đầy đủ (Retrofit) như **lớp dự phòng** trong
  `SyncWorker`: Bluetooth thất bại → tự thử Wi-Fi.

## 1.4. Sơ đồ luồng dữ liệu đầu-cuối

```
[Android] Quét mã  →  JobLog (Room, syncStatus=PENDING)
                          │
                          ▼  WorkManager (thủ công / định kỳ 15' / geofence ENTER)
                   SyncWorker.doWork()
                          │
        ┌─────────────────┴───────────────────┐
        ▼ Lớp 1: Bluetooth                      ▼ Lớp 2: Wi-Fi (nếu lớp 1 fail)
  BluetoothSyncManager.sync()             Retrofit POST /api/sync (JSON)
  LogPayloadSerializer.toCsv()                  │
  STX + CSV + ETX  ──────────────┐              └────────► (PC: hiện chưa nhận JSON)
                                 │
                                 ▼
══════════════════════════════ PC ══════════════════════════════
  BluetoothSppServer (accept loop, 1 task/client)
        │
        ▼
  FrameDecoder  (gỡ STX/ETX, ghép chunk → 1 khung CSV trọn vẹn)
        │
        ▼
  CsvLogParser  (CSV → List<LogEntry>; tự sinh LogId nếu trống)
        │
        ▼
  LogIngestService  (dedup: gộp trùng trong batch → bỏ LogId đã có →
                     ghi 1 lần, đụng khóa thì retry từng dòng)
        │
        ├──► AppDbContext.SaveChanges()  →  bảng SyncLogs (EF Core)
        ├──► CsvInbox.Add()              →  danh sách upload gần đây (cho dashboard)
        └──► ServiceStatus               →  cập nhật counters của client
                  │
                  ▼
  MonitorService.GetSnapshotAsync()  (mỗi 2s, gộp ServiceStatus + CsvInbox + DB → StatusDto)
                  │
                  ▼
  MainForm (WinForms)  →  3 lưới: client Bluetooth | CSV đã nhận | bản ghi của CSV được chọn
```

## 1.5. Triết lý thiết kế (vì sao làm như vậy)

1. **Một tiến trình, không gọi HTTP vào chính mình.** PC khởi động một *generic host* trong
   tiến trình (Kestrel + EF Core + các background service), rồi chạy WinForms trên luồng
   chính. Dashboard đọc trạng thái **trực tiếp** từ DI container — không có vòng HTTP nội bộ,
   độ trễ thấp. Endpoint HTTP chỉ để giám sát từ xa.

2. **Idempotent (chống trùng tuyệt đối).** `LogId` (Guid) là khóa dedup. Nếu máy không cấp
   `id`, PC suy ra Guid ổn định bằng SHA-1 (kiểu v5) từ nội dung bản ghi → gửi lại bao nhiêu
   lần vẫn cùng một khóa → DB không nhân đôi.

3. **Tách phần thuần (pure) để test được.** `FrameDecoder` và `CsvLogParser` không phụ thuộc
   phần cứng, kiểm thử được độc lập với đúng khung byte mà Android gửi.

4. **Tự hồi phục.** Server Bluetooth retry mỗi 5s nếu radio tắt/không có. Android có
   WorkManager retry exponential-backoff khi cả hai kênh thất bại.

5. **DB cắm-rút (pluggable).** Đổi provider runtime: SQLite (mặc định, zero-config) /
   SQL Server / PostgreSQL, qua `Database:Provider`.

## 1.6. Đọc tiếp

- Chi tiết phía PC: [02 — Ứng dụng PC](02-ung-dung-pc-csharp.md)
- Chi tiết phía Android: [03 — Ứng dụng Android](03-ung-dung-android.md)
- Hợp đồng dữ liệu & giao thức: [04 — Giao thức & luồng dữ liệu](04-giao-thuc-va-luong-du-lieu.md)
