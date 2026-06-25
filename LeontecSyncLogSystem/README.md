# Leontec Sync Log System

Một **ứng dụng desktop duy nhất** (.NET 8 WinForms) vừa chạy nền nhận log từ máy Android
qua **Bluetooth SPP**, vừa hiển thị bảng giám sát. Khi mở app, nó tự khởi động:

- **Bluetooth SPP server** (32feet.NET) — PC là *server*, điện thoại là *client*. Nhận
  được **nhiều máy kết nối cùng lúc**, mỗi khung `STX + CSV + ETX` → parse → lưu DB.
- **CSDL EF Core** (SQLite) — lưu log, chống trùng theo `id` (LogId).
- **Dashboard** — trái-trên: server Bluetooth + máy đang kết nối; **trái-dưới: danh sách CSV đã
  nhận** (mỗi lần sync = 1 dòng); **phải: bấm vào 1 CSV để xem các log của CSV đó**.
- (Kestrel `/api/status` chạy kèm để giám sát từ xa. **Wi-Fi tạm gác lại.**)

```
   Android (client)            LeontecSyncLogSystem.exe  (1 tiến trình)
   ┌────────┐  ═BT SPP═▶  [BluetoothSppServer]  ┐  (UUID 00001101-…, nhiều client)
   │ JobLog │             accept loop, 1 task/máy ├─▶ [LogIngestService] ─▶ DB (SyncLogs)
   │  → CSV │                                     ┘            │
   └────────┘                          [ServiceStatus] ─▶ [MonitorService] ─▶ Dashboard
```

## Chạy

```powershell
dotnet run --project LeontecSyncLogSystem -c Release
# hoặc: LeontecSyncLogSystem\bin\Release\net8.0-windows10.0.19041.0\LeontecSyncLogSystem.exe
```

Mở app → header dashboard hiện **tên Bluetooth của PC** (ví dụ `"LUYEN - Front"`) và trạng
thái "ĐANG LẮNG NGHE".

## Để điện thoại kết nối được (quan trọng)

1. **Ghép đôi (pair)** điện thoại với PC này trong Windows Bluetooth settings.
2. Trong app Android, đặt `pcBluetoothName` **khớp với tên Bluetooth của PC** hiển thị trên
   dashboard. App đã khớp linh hoạt (không phân biệt hoa thường, chứa nhau), nhưng tên càng
   đúng càng chắc. Mặc định app là `"LUYEN"` — nếu tên PC là `"LUYEN - Front"` vẫn khớp được.
3. PC không cần tạo cổng COM nào cả — đó là lý do trước đây "không thấy COM": app kết nối
   *tới* PC qua SPP, PC chỉ cần chạy server (app này) là đủ.

## Định dạng dữ liệu Bluetooth (CSV)

Khung gửi qua SPP: `STX(0x02) + CSV(UTF-8) + ETX(0x03)`. CSV có dòng tiêu đề, cột:

```
id,workerId,jobType,barcodeData,startTime,endTime
3f2504e0-4f89-41d3-9a0c-0305e82c3301,W-100,検品,4901234567894,1750000000000,1750000005000
```

- `id` (UUID) là khóa chính + chống trùng. `startTime`/`endTime` là epoch-millis.
- `workerId` = **tên máy/thiết bị Android** (app tự lấy tên thiết bị). Tạo log mẫu cũng dùng tên này.
- `jobType`: 検品 / 出荷 / 直送. Trường có dấu phẩy được bọc `"..."` (RFC-4180).
- `SyncMethod` PC tự gán = `"Bluetooth"` (thiết bị không gửi).

## Cấu hình — [appsettings.json](appsettings.json)

```jsonc
"Sync":     { "BluetoothServiceName": "SyncLogServer" },
"Database": { "Provider": "Sqlite", "ConnectionString": "Data Source=synclogs.db" },
"Kestrel":  { "Endpoints": { "Http": { "Url": "http://0.0.0.0:8090" } } }
```

`Database:Provider`: `Sqlite` (mặc định), `SqlServer`, hoặc `Postgres`. Bảng tạo tự động.

## Lưu ý
- Cần có **adapter Bluetooth** trên PC. Nếu tắt/không có, server tự thử lại; dashboard báo lỗi.
- Là app desktop có giao diện ⇒ không chạy dạng Windows Service nền. Cần nền 24/7 thì tách host.
- **Wi-Fi (JSON) tạm gác**: app gửi JSON array qua `http://<ip>:8080/api/sync`; endpoint hiện
  parse CSV nên chưa dùng được cho Wi-Fi — sẽ bổ sung parse JSON khi cần.
