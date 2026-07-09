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
- **Gửi có kiểm chứng**: khi gửi, PC xác nhận từng file đã nhận OK để máy cầm tay biết chắc
  (giao thức batch `BATCH_END` → `RESULT`, xem [04 §4.1](04-giao-thuc-va-luong-du-lieu.md)).

## 1.2. Hai thành phần

| Thành phần | Thư mục | Vai trò | Công nghệ chính |
|-----------|---------|---------|-----------------|
| **Ứng dụng Android** | `shipment_support/` | Quét mã vạch, ghi log ra file, gửi sang PC (module `bluetooth_module`) | Java, Android View, SQLite, Bluetooth Classic SPP |
| **Ứng dụng PC** | `LeontecSyncLogSystem/` | Nhận log, lưu DB, hiển thị dashboard | C# .NET 10, WinForms, ASP.NET Core (Kestrel), EF Core, MariaDB nhúng, 32feet.NET |

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
   │                                               │  CsvTypes phát hiện type + parse
   │                                               │  CsvStore chuẩn hóa + ghi DB
```

- Đây là kênh **trọng tâm**, hoạt động trong cùng mạng nội bộ/khoảng cách Bluetooth.
- Mỗi thiết bị Android = một kết nối SPP riêng, PC phục vụ **nhiều máy đồng thời**.

### Kênh dự phòng — Wi-Fi REST (đang tạm gác)

```
Android  ──► POST JSON (mảng JobLog, Gson) ──► http://<ip>:8080/api/sync  (PC)
```

- Hiện **chưa đấu nối hoàn chỉnh**: endpoint `POST /api/sync` trả **501 Not Implemented**
  (đường parse CSV/ingest cũ đã bị gỡ cùng bảng legacy `SyncLogs`). Khi bật lại Wi-Fi cần: đấu
  ingest CSV typed vào `CsvUploads` (qua `ICsvStore`) và bind Kestrel ở cổng 8080.
- Trên Android, app mới `shipment_support` **không còn** kênh Wi-Fi (đã bỏ Retrofit/`SyncWorker`);
  chỉ gửi qua Bluetooth SPP. Đây chỉ là phần dành sẵn phía PC cho tương lai.

## 1.4. Sơ đồ luồng dữ liệu đầu-cuối

```
[Android shipment_support] Quét mã  →  ghi log ra FILE theo ngày (FileLogHelper)
                          │              monitor_log / pallet_log / direct_log _YYYYMMDD.txt
                          ▼  Màn 「ログ送信」: chọn ngày + file → 送信 (thủ công)
                   LogSendActivity.sendSelected()  →  BluetoothSyncManager.sendBatch()
                          │
                          ▼  Bluetooth SPP (1 kết nối / cả batch)
  STX + filename\r\n + CSV + ETX  (mỗi file 1 frame)  +  BATCH_END
                                 │            ▲
                                 │            └── RESULT\n<file>=OK|ERR  (PC xác nhận)
                                 ▼
══════════════════════════════ PC ══════════════════════════════
  BluetoothSppServer (accept loop, 1 task/client)
        │
        ▼
  FrameDecoder  (gỡ STX/ETX, ghép chunk → 1 khung CSV trọn vẹn)
        │
        ▼
  CsvTypes  (DetectType từ header row-1 → parse ra entity theo type)
        │
        ▼
  CsvStore  (lưu CsvUpload giữ RawCsv + chuẩn hóa vào bảng theo type +
             supersede bản cũ của cùng (TermId, Type))
        │
        ├──► AppDbContext.SaveChanges()  →  CsvUploads + bảng chuẩn hóa (EF Core)
        └──► ServiceStatus               →  cập nhật counters của client
                  │
                  ▼
  MonitorService.GetSnapshotAsync()  (mỗi 2s, gộp ServiceStatus + CsvInbox + DB → StatusDto)
                  │
                  ▼
  MainForm (WinForms)  →  3 lưới: client Bluetooth | CSV đã nhận | log cả ngày theo type
```

## 1.5. Triết lý thiết kế (vì sao làm như vậy)

1. **Một tiến trình, không gọi HTTP vào chính mình.** PC khởi động một *generic host* trong
   tiến trình (Kestrel + EF Core + các background service), rồi chạy WinForms trên luồng
   chính. Dashboard đọc trạng thái **trực tiếp** từ DI container — không có vòng HTTP nội bộ,
   độ trễ thấp. Endpoint HTTP chỉ để giám sát từ xa.

2. **Idempotent (chống trùng).** Với dữ liệu typed, cơ chế **supersede** đảm bảo idempotent:
   `UploadIndex` mới hơn của cùng `(TermId, Type)` đánh dấu upload cũ `Superseded=true` → số liệu
   dashboard không nhân đôi khi gửi lại. (Bảng `SyncLogs` dedup theo `LogId` cũ đã bị gỡ.)

3. **Tách phần thuần (pure) để test được.** `FrameDecoder` và `CsvTypes` không phụ thuộc
   phần cứng, kiểm thử được độc lập với đúng khung byte mà Android gửi.

4. **Tự hồi phục.** Server Bluetooth retry mỗi 5s nếu radio tắt/không có. Android mở socket SPP
   có **thử lại + jitter** (`connectWithRetry`) để nhiều máy gửi cùng lúc không kẹt kết nối
   BR/EDR; file gửi lỗi được giữ lại (không MOVE sang backup) để gửi lại sau.

5. **DB nhúng, tự chứa (embedded).** App đóng gói sẵn **MariaDB** (`<app>/mariadb/`) và chạy nó như
   tiến trình con (port 3307 loopback, data ở `%LOCALAPPDATA%`), nên copy app sang máy khác là chạy
   được ngay — không cần cài MySQL. Chỉ hỗ trợ MySQL/MariaDB (qua Pomelo); schema qua EF Core migrations.

## 1.6. Đọc tiếp

- Chi tiết phía PC: [02 — Ứng dụng PC](02-ung-dung-pc-csharp.md)
- Chi tiết phía Android: [03 — Ứng dụng Android](03-ung-dung-android.md)
- Hợp đồng dữ liệu & giao thức: [04 — Giao thức & luồng dữ liệu](04-giao-thuc-va-luong-du-lieu.md)
