# Leontec Sync Log System

Một **ứng dụng desktop duy nhất** (.NET 10 WinForms) vừa chạy nền nhận log từ máy Android
qua **Bluetooth SPP**, vừa hiển thị bảng giám sát. Khi mở app, nó tự khởi động một *generic
host* trong tiến trình (Kestrel + EF Core + các background service) rồi chạy WinForms trên
luồng chính (dashboard đọc trạng thái trực tiếp từ DI, không gọi HTTP vào chính mình):

- **MySQL ngoài** — MySQL/MariaDB được **cài & chạy riêng** (bộ cài / Windows service riêng); app
  **không còn đóng gói/khởi động DB**. App đọc `mysql.xml` (thư mục cha của `app/`) để lấy
  host/port/db/user/password rồi kết nối; DB sập lúc khởi động **không làm app crash** (dashboard vẫn
  mở, hiện "MySQL: disconnected"). (MariaDB nhúng đã gỡ bỏ 2026-07-09.)
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
# MySQL cài/chạy riêng. Đảm bảo MySQL đang chạy và mysql.xml trỏ đúng (mặc định localhost:3306,
# db leontec_sync, user root, password rỗng). App tự tạo DB + schema qua EF migrations lúc khởi động.
dotnet build LeontecSyncLogSystem.slnx -c Release
dotnet run --project LeontecSyncLogSystem -c Release
# exe: LeontecSyncLogSystem\bin\Release\net10.0-windows10.0.19041.0\LeontecSyncLogSystem.exe
```

### Cấu trúc thư mục (giống nhau cho debug & release, KHÔNG dùng %LOCALAPPDATA%)

```
<root>/
  _master/            master CSV (khách hàng / mặt hàng) — sửa được; tự copy mỗi lần build
  _backup/            bản sao thô của CSV nhận được, theo ngày
  mysql.xml           cấu hình kết nối MySQL ngoài
  Log Management.lnk  shortcut mở tool (khỏi vào thư mục LogManagement)
  LogManagement/      toàn bộ tool (exe + file)   ← trước là tên TFM (net10.0-...)
    configuration.xml ngôn ngữ + 7 công tắc ẩn/hiện (mặc định false = ẩn hết)
```
Bản debug: exe ở `bin/<Config>/LogManagement/`, nên `_master`/`_backup`/`mysql.xml` nằm ở `bin/<Config>/`.
File thiếu sẽ được tạo với giá trị mặc định lúc chạy lần đầu. **Mỗi lần Build hoặc Debug/Run**: target
`CopyMasterSeedToRoot` copy `master-seed/*.csv` vào `_master` (ghi đè) + `CreateRootShortcut` tạo lại
`Log Management.lnk` (`DisableFastUpToDateCheck=true` để F5 không bị bỏ qua build).

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

## Cấu hình

**`app/configuration.xml`** — `<language>` (**mặc định `ja`**; `ja`/`en`) là **ngôn ngữ UI** (áp lúc
khởi động; đổi bằng combo thì ghi ngược lại đây) + 7 công tắc ẩn/hiện (mặc định **false = ẩn**):
`showResetButton`, `showOpenBackupButton`, `showLanguageButton`, `showMasterButtons`,
`showBluetoothPanel` (panel trái trên), `showCsvPanel` (panel trái dưới), `showMysqlStatus` (label
trạng thái MySQL). Ẩn hết ⇒ cột trái thu gọn, chỉ còn bảng log bên phải.

**`mysql.xml`** (thư mục cha của `app/`) — `<host>`/`<port>`/`<database>`/`<user>`/`<password>`
(mặc định `localhost:3306`, `leontec_sync`, `root`, rỗng).

**[appsettings.json](appsettings.json)** — chỉ còn Bluetooth/Kestrel/Logging:
```jsonc
"Kestrel":  { "Endpoints": { "Http": { "Url": "http://0.0.0.0:8090" } } },
"Sync":     { "BluetoothServiceName": "SyncLogServer", "BackupFolder": "" }
```
- `Sync:BackupFolder` rỗng ⇒ `<root>/_backup`; mỗi CSV nhận được lưu bản sao thô vào
  `<BackupFolder>/<yyyyMMdd>/<filename>` (best-effort).
- **Icon**: đặt file `app.ico` (logo NEX) trong thư mục project → dùng làm icon cửa sổ/taskbar
  (không có file thì bỏ qua, không lỗi build).

## Lưu ý
- Cần có **adapter Bluetooth** trên PC. Nếu tắt/không có, server tự thử lại; dashboard báo lỗi.
- Là app desktop có giao diện ⇒ không chạy dạng Windows Service nền. Cần nền 24/7 thì tách host.
- **Wi-Fi (JSON) tạm gác**: endpoint `POST /api/sync` hiện trả **501 Not Implemented** (đường
  ingest cũ đã gỡ); app Android mới cũng không còn kênh Wi-Fi.
- MySQL cài/chạy **riêng** — app chỉ kết nối theo `mysql.xml` và hiện trạng thái. Xem thêm
  [`CLAUDE.md`](../CLAUDE.md) + [`docs/`](../docs/README.md).
