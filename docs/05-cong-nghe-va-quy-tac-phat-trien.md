# 05 — Công nghệ & quy tắc phát triển

## 5.1. Bảng công nghệ đầy đủ

### PC (LeontecSyncLogSystem/)

| Công nghệ | Phiên bản | Vai trò |
|-----------|-----------|---------|
| .NET | 10.0 (`net10.0-windows10.0.19041.0`) | Runtime; bản Win10 cần cho RFCOMM WinRT (đổi net8→net10 để Designer + run dùng chung runtime 10.0.9 đã cài) |
| WinForms | built-in | Dashboard desktop |
| ASP.NET Core (Kestrel) | 10.x | Host web trong tiến trình (giám sát, endpoint) |
| Entity Framework Core | 8.0.13 | ORM, bảng `SyncLogs` |
| EF Core SQLite | 8.0.13 | DB mặc định (zero-config) |
| EF Core SQL Server | 8.0.13 | Provider tùy chọn |
| Npgsql EF Core (PostgreSQL) | 8.0.11 | Provider tùy chọn |
| InTheHand.Net.Bluetooth (32feet.NET) | 4.2.1 | Server Bluetooth SPP (RFCOMM) |

### Android (SyncLogs/)

| Công nghệ | Phiên bản | Vai trò |
|-----------|-----------|---------|
| Kotlin | 1.9.23 | Ngôn ngữ |
| Jetpack Compose (BOM) / Material3 | 2024.04.00 / 1.11.0 | UI |
| Room | 2.6.1 | DB cục bộ |
| WorkManager | 2.9.0 | Đồng bộ nền + backoff |
| Retrofit / Gson / OkHttp | 2.11.0 / 2.10.1 / 4.12.0 | REST + JSON (Wi-Fi) |
| Play Services Location | 21.2.0 | Geofencing |
| Coroutines Play Services | 1.8.0 | Cầu Coroutine ↔ Task |
| KSP | 1.9.23-1.0.19 | Annotation processor (Room) |
| Android SDK | compile/target 34, min 26 | — |
| Java | 17 | Biên dịch |

## 5.2. Mẫu thiết kế xuyên suốt

- **Idempotency:** khóa `LogId`/`id` (Guid/UUID) là khóa dedup ở cả hai phía. PC suy `LogId`
  ổn định bằng SHA-1 khi thiếu; Android dùng `OnConflictStrategy.REPLACE`.
- **Tách phần thuần để test:** `FrameDecoder`, `CsvLogParser` không phụ thuộc phần cứng.
- **Đồng bộ 2 lớp dự phòng (Android):** Bluetooth → Wi-Fi.
- **Tự hồi phục:** server BT retry 5s; WorkManager backoff mũ.
- **DB cắm-rút:** đổi provider runtime.
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
# Mở SyncLogs/ trong Android Studio, hoặc:
cd SyncLogs && ./gradlew assembleDebug
```

## 5.5. Cạm bẫy đã biết (đừng vấp lại)

- PC là **server SPP**, không phải cổng COM — đừng đi tìm COM.
- Tên radio PC là `"LUYEN - Front"` ≠ `"LUYEN"` → app khớp lỏng + ưu tiên MAC.
- `SyncMethod` không có trong CSV — do kênh nhận set.
- App PC là GUI, **không chạy được như Windows Service** không màn hình.
- 32feet.NET cần radio Bluetooth; thiếu radio thì server chỉ retry, không crash.
- **Không có git repo** ở repo này — xóa file không hoàn tác được.
