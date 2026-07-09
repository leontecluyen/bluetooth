# 05 — Công nghệ & quy tắc phát triển

## 5.1. Bảng công nghệ đầy đủ

### PC (LeontecSyncLogSystem/)

| Công nghệ | Phiên bản | Vai trò |
|-----------|-----------|---------|
| .NET | 10.0 (`net10.0-windows10.0.19041.0`) | Runtime; bản Win10 cần cho RFCOMM WinRT (đổi net8→net10 để Designer + run dùng chung runtime 10.0.9 đã cài) |
| WinForms | built-in | Dashboard desktop |
| ASP.NET Core (Kestrel) | 10.x | Host web trong tiến trình (giám sát, endpoint) |
| Entity Framework Core | 8.0.13 | ORM, bảng `CsvUploads` + bảng chuẩn hóa theo type + `Devices` |
| Pomelo.EntityFrameworkCore.MySql | 8.0.3 | Provider MySQL/MariaDB (duy nhất) |
| EFCore.NamingConventions | 8.0.3 | Đặt tên bảng/cột snake_case |
| MariaDB (đóng gói) | 11.4.4 winx64 | Server DB nhúng, chạy như tiến trình con của app |
| dotnet-ef / EF Core Design | 8.x | Tạo & áp migration |
| InTheHand.Net.Bluetooth (32feet.NET) | 4.2.1 | Server Bluetooth SPP (RFCOMM) |

### Android (`shipment_support/`, module `bluetooth_module`)

App quét mã vạch kho Nittsu (Java thuần, Android View). Module gửi log dùng đúng các API
Bluetooth Classic của nền tảng, không thêm thư viện đồng bộ nào (không Room/WorkManager/Retrofit).

| Công nghệ | Phiên bản | Vai trò |
|-----------|-----------|---------|
| Java | thuần (không Kotlin) | Ngôn ngữ |
| Android View + DataBinding | built-in | UI (không Jetpack Compose) |
| SQLite (`SqliteHelper`) | built-in | DB nghiệp vụ của app (`car_stock.db`) — **không** dùng cho việc gửi log |
| Bluetooth Classic SPP (RFCOMM) | platform API | Kênh gửi log (`BluetoothSyncManager`) |
| `androidx.documentfile` | — | Đọc/ghi file log qua SAF `content://` |
| Android SDK | compile/target 36, min 24 | — |

> Chi tiết công nghệ + quy ước của toàn app `shipment_support` (kể cả 3 màn nghiệp vụ) nằm trong
> `shipment_support/CLAUDE.md`. Tài liệu này chỉ quan tâm module `bluetooth_module`.

## 5.2. Mẫu thiết kế xuyên suốt

- **Idempotency:** PC dùng cơ chế **supersede** cho dữ liệu typed (`UploadIndex` mới của cùng
  `(TermId, Type)` đánh dấu bản cũ `Superseded`); Android dùng `OnConflictStrategy.REPLACE`.
  (Dedup theo `LogId`/bảng `SyncLogs` cũ đã bị gỡ.)
- **Tách phần thuần để test:** `FrameDecoder`, `CsvTypes` không phụ thuộc phần cứng.
- **Gửi có xác nhận (Android):** batch nhiều file/1 kết nối + `BATCH_END` → PC trả `RESULT`; chỉ
  file PC báo OK mới MOVE sang backup (không mất dữ liệu). Kênh Wi-Fi đã bỏ.
- **Tự hồi phục:** server BT retry 5s; Android `connectWithRetry` (jitter) tránh kẹt kết nối BR/EDR.
- **DB nhúng, tự chứa:** MariaDB đóng gói theo app, chạy như tiến trình con (port 3307 loopback);
  schema qua **EF Core migrations**. Không cần cài MySQL trên máy đích.
- **Singleton trạng thái sống (PC):** `ServiceStatus`, `CsvInbox`, ... đăng ký DI singleton.

## 5.3. Quy tắc phát triển bắt buộc (từ nay về sau)

> Các quy tắc này áp dụng cho **mọi thay đổi code** ở cả hai project. Đã đưa vào `CLAUDE.md`.

### A. Code chuẩn enterprise

- Đặt tên rõ ràng, hàm nhỏ, một trách nhiệm; tách phần thuần khỏi I/O để test được.
- Xử lý lỗi tường minh (không nuốt exception âm thầm); dùng `CancellationToken` cho tác vụ
  nền (C#) và coroutine có cấu trúc (Kotlin).
- Không phá idempotency: mọi đường ghi dữ liệu phải an toàn khi gửi lại.
- Đổi giao thức/DB → giữ tương thích hai phía; nếu phá vỡ phải có kế hoạch di trú.
- Tuân theo phong cách code xung quanh (đặt tên, mật độ comment, idiom).

### B. Log đầy đủ ở mỗi bước

- **Mỗi bước nghiệp vụ quan trọng phải có log** với mức phù hợp:
  - `Debug/Trace`: chi tiết khung, số byte, từng dòng parse.
  - `Information`: mốc vòng đời (server lắng nghe, client kết nối, đã ingest N bản ghi).
  - `Warning`: tình huống hồi phục được (lớp 1 fail chuyển lớp 2, radio tắt, retry).
  - `Error`: thất bại thật kèm ngữ cảnh (mã HTTP, exception, địa chỉ thiết bị).
- **PC:** dùng `ILogger<T>` (Microsoft.Extensions.Logging); mức cấu hình trong
  `appsettings.json` (`Logging`).
- **Android:** dùng `Log`/`Timber`-style với **tag nhất quán** (vd `SYNC_WORKER`,
  `BluetoothSyncManager`); log số lần thử, chuyển lớp, số byte, lỗi.
- Log phải đủ để **dựng lại luồng sự cố** từ log mà không cần debugger. Không log dữ liệu
  nhạy cảm thừa.

### C. Sửa code là sửa luôn docs

- **Mỗi lần thay đổi code phải cập nhật tài liệu tương ứng** trong `docs/`:
  - Đổi phía PC → sửa [02](02-ung-dung-pc-csharp.md).
  - Đổi phía Android → sửa [03](03-ung-dung-android.md).
  - Đổi giao thức/wire-format/DB → sửa [04](04-giao-thuc-va-luong-du-lieu.md) **và cả hai phía**.
  - Đổi kiến trúc tổng → sửa [01](01-tong-quan-kien-truc.md).
- Cập nhật `CLAUDE.md` nếu thay đổi ảnh hưởng quy ước/cạm bẫy.
- Ghi lại các điểm quan trọng/khó vào memory của trợ lý.

### D. Việc khó / nhiều thay đổi / nhiều case → HỎI TRƯỚC

Trước khi làm các loại việc sau, **phải hỏi xác nhận** (đừng tự quyết):

- Đổi **wire-format** hoặc **schema DB** (nhiều case: thời gian, quoting, tên field, di trú).
- Bật lại **kênh Wi-Fi** (parse JSON, đổi cổng 8080, map field).
- Đổi **provider DB** mặc định hoặc cách tạo schema.
- Thay đổi **chạm tới Bluetooth pairing/UUID/cách khớp tên**.
- Bất kỳ thay đổi nào **xóa/ghi đè dữ liệu** (không có git ở repo này — xóa là mất).
- Refactor lớn nhiều file hoặc đổi hành vi mặc định.

## 5.4. Build / chạy nhanh

```bash
# PC
dotnet build LeontecSyncLogSystem.slnx -c Release
dotnet run --project LeontecSyncLogSystem -c Release

# Android
# Mở shipment_support/ trong Android Studio, hoặc:
cd shipment_support && ./gradlew assembleDebug
```

## 5.5. Cạm bẫy đã biết (đừng vấp lại)

- PC là **server SPP**, không phải cổng COM — đừng đi tìm COM.
- Tên radio PC là `"LUYEN - Front"` ≠ `"LUYEN"` → app khớp lỏng + ưu tiên MAC.
- `SyncMethod` không có trong CSV — do kênh nhận set.
- App PC là GUI, **không chạy được như Windows Service** không màn hình.
- 32feet.NET cần radio Bluetooth; thiếu radio thì server chỉ retry, không crash.
- **Không có git repo** ở repo này — xóa file không hoàn tác được.
