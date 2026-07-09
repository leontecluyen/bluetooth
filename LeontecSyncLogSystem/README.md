# Leontec Sync Log System

Một **ứng dụng desktop duy nhất** (.NET 10 WinForms) vừa chạy nền nhận log từ máy Android
qua **Bluetooth SPP**, vừa hiển thị bảng giám sát. Khi mở app, nó tự khởi động một *generic
host* trong tiến trình (Kestrel + EF Core + các background service) rồi chạy WinForms trên
luồng chính (dashboard đọc trạng thái trực tiếp từ DI, không gọi HTTP vào chính mình):

- **MariaDB nhúng** (`EmbeddedMariaDbServer`) — mặc định app tự chạy MariaDB đóng gói dưới
  `<app>/mariadb/` như tiến trình con (port loopback 3307, data ở `%LOCALAPPDATA%`), nên máy đích
  **không cần cài MySQL**. Đặt `Database:Embedded=false` để dùng MySQL/MariaDB ngoài.
- **Bluetooth SPP server** (32feet.NET) — PC là *server*, điện thoại là *client*. Nhận
  được **nhiều máy kết nối cùng lúc**, mỗi khung `STX + (filename\r\n + CSV) + ETX` → phát hiện
  type từ header → chuẩn hóa → lưu DB.
- **CSDL EF Core** (Pomelo, MySQL/MariaDB, snake_case) — bảng `csv_uploads` (giữ `raw_csv`) +
  các bảng chuẩn hóa theo type + `devices`; schema qua **EF Core migrations** (`Migrate()` lúc
  khởi động). Chống trùng bằng cơ chế **supersede** theo `(term_id, type, upload_index)`.
- **Dashboard** — trái-trên: server Bluetooth + máy đang kết nối; trái-dưới: **danh sách CSV đã
  nhận** của ngày; **phải: log CẢ NGÀY của một type** theo **date-picker + radio type** (không
  phụ thuộc CSV chọn bên trái), có nút **Export CSV**.
- (Kestrel `/api/status` + `/health` chạy kèm để giám sát từ xa. **Wi-Fi tạm gác lại.**)

```
   Android (client)                LeontecSyncLogSystem.exe  (1 tiến trình)
   ┌──────────────┐  ═BT SPP═▶  [BluetoothSppServer]  ┐  (UUID 00001101-…, nhiều client)
   │ shipment_    │   STX+file+CSV+ETX,   accept loop  ├─▶ [CsvStore] ─▶ MariaDB (csv_uploads
   │ support →    │   BATCH_END ─▶ RESULT  1 task/máy  ┘   + bảng chuẩn hóa + devices)
   │ file CSV     │                                              │
   └──────────────┘   [ServiceStatus] ─▶ [MonitorService] ─▶ Dashboard (WinForms)
```

## Chạy

```powershell
# Một lần / mỗi checkout: tải MariaDB đóng gói (KHÔNG commit vào git):
pwsh scripts/fetch-mariadb.ps1
dotnet build LeontecSyncLogSystem.slnx -c Release
dotnet run --project LeontecSyncLogSystem -c Release
# exe: LeontecSyncLogSystem\bin\Release\net10.0-windows10.0.19041.0\LeontecSyncLogSystem.exe
```

Mở app → header dashboard hiện **tên Bluetooth của PC** (ví dụ `"LUYEN - Front"`) và trạng
thái "ĐANG LẮNG NGHE".

## Để điện thoại kết nối được (quan trọng)

1. **Ghép đôi (pair)** điện thoại với PC này trong Windows Bluetooth settings.
2. Trong app Android (`shipment_support`, màn 「ログ送信」 → ☰ → **PC選択**), chọn PC theo tên/MAC
   khớp với tên Bluetooth hiển thị trên dashboard. App khớp linh hoạt (không phân biệt hoa
   thường, chứa nhau) và ưu tiên MAC; chưa chọn PC thì tự đoán thiết bị bonded lớp COMPUTER.
3. PC không cần tạo cổng COM nào cả — đó là lý do trước đây "không thấy COM": app kết nối
   *tới* PC qua SPP, PC chỉ cần chạy server (app này) là đủ.

## Định dạng dữ liệu Bluetooth (CSV có type)

Khung gửi qua SPP: `STX(0x02) + (filename\r\n + CSV UTF-8) + ETX(0x03)`. **Dòng đầu = tên file**
`{type}_{yyyyMMdd}_{termId}_{index}.txt` (mang type / **ngày log** / máy / lần gửi); phần còn
lại là CSV, PC nhận diện **type từ header row-1** (authoritative). Ba type:

- **`monitor_log`** (モニタリスト, 9 cột) → `monitor_entries`
- **`pallet_log`** (パレット, 7 cột) → `pallet_ops` + `pallet_op_items`
- **`direct_log`** (直送管理, 11 cột) → `direct_entries`

CSV không khớp 3 type → lưu `type = "unknown"` (chỉ giữ `raw_csv`). Chi tiết cột + bộ lọc hiển
thị: [`docs/04`](../docs/04-giao-thuc-va-luong-du-lieu.md).

- **Gửi theo lô có xác nhận:** điện thoại gửi tất cả file trên **một** kết nối, mỗi file 1 khung,
  rồi khung điều khiển `BATCH_END`; PC trả **một** khung `RESULT\n<filename>=OK|ERR\n…` để máy cầm
  tay biết file nào đã nhận OK.
- `SyncMethod`/`Source` PC tự gán = `"Bluetooth"` (thiết bị không gửi).

## Cấu hình — [appsettings.json](appsettings.json)

```jsonc
"Kestrel":  { "Endpoints": { "Http": { "Url": "http://0.0.0.0:8090" } } },
"Sync":     { "BluetoothServiceName": "SyncLogServer", "BackupFolder": "" },
"Database": { "Embedded": true, "EmbeddedPort": 3307, "DataDir": "",
              "ConnectionString": "Server=localhost;Port=3306;Database=leontec_sync;User=root;Password=;" }
```

- `Sync:BackupFolder` rỗng ⇒ `%LOCALAPPDATA%/LeontecSyncLogSystem/backup`; mỗi CSV nhận được lưu
  bản sao thô vào `<BackupFolder>/<yyyyMMdd>/<filename>` (best-effort).
- `Database:Embedded=true` ⇒ chạy MariaDB đóng gói (port `EmbeddedPort`); `false` ⇒ dùng
  `ConnectionString` tới MySQL/MariaDB ngoài.

## Lưu ý
- Cần có **adapter Bluetooth** trên PC. Nếu tắt/không có, server tự thử lại; dashboard báo lỗi.
- Là app desktop có giao diện ⇒ không chạy dạng Windows Service nền. Cần nền 24/7 thì tách host.
- **Wi-Fi (JSON) tạm gác**: endpoint `POST /api/sync` hiện trả **501 Not Implemented** (đường
  ingest cũ đã gỡ); app Android mới cũng không còn kênh Wi-Fi.
- `mariadb/` (~260 MB) **không commit** — chạy `scripts/fetch-mariadb.ps1` để tải; csproj copy
  cạnh exe. Xem thêm [`CLAUDE.md`](../CLAUDE.md) + [`docs/`](../docs/README.md).
