# Tài liệu kỹ thuật — LeontecSyncLogSystem

Bộ tài liệu này mô tả **nguyên lý hoạt động** và **công nghệ sử dụng** cho toàn bộ hệ thống
đồng bộ log quét mã vạch của Leontec, gồm cả **ứng dụng Android** (máy cầm tay) và
**ứng dụng PC C#** (máy chủ trung tâm trên Windows).

> Tài liệu viết bằng tiếng Việt để cả nhóm dễ đọc. Đây là nguồn tham chiếu chính thức về
> kiến trúc. **Quy tắc bắt buộc:** mỗi khi sửa code, phải cập nhật tài liệu tương ứng trong
> thư mục này (xem [05 — Công nghệ & quy tắc phát triển](05-cong-nghe-va-quy-tac-phat-trien.md)).

## Hệ thống làm gì? (1 câu)

Máy Android cầm tay quét mã vạch → gom thành các bản ghi công việc (JobLog) → gửi sang PC
trung tâm qua **Bluetooth (chính)** hoặc **Wi-Fi (dự phòng)** → PC lưu vào cơ sở dữ liệu
có **chống trùng (dedup)** và hiển thị trạng thái trực tiếp trên **dashboard desktop**.

## Mục lục

| # | Tài liệu | Nội dung |
|---|----------|----------|
| 01 | [Tổng quan & kiến trúc](01-tong-quan-kien-truc.md) | Bức tranh toàn cảnh, sơ đồ luồng dữ liệu, hai kênh đồng bộ, triết lý thiết kế |
| 02 | [Ứng dụng PC (C# / .NET 8 / WinForms)](02-ung-dung-pc-csharp.md) | Máy chủ: host trong tiến trình, server Bluetooth SPP, parser, dedup, dashboard |
| 03 | [Ứng dụng Android (Kotlin / Compose)](03-ung-dung-android.md) | Máy cầm tay: thu thập log, đồng bộ 2 lớp, WorkManager, geofence |
| 04 | [Giao thức & luồng dữ liệu](04-giao-thuc-va-luong-du-lieu.md) | Hợp đồng dữ liệu CSV, đóng khung STX/ETX, JSON Wi-Fi, các endpoint HTTP |
| 05 | [Công nghệ & quy tắc phát triển](05-cong-nghe-va-quy-tac-phat-trien.md) | Bảng công nghệ đầy đủ, chuẩn code enterprise, quy tắc log, quy trình sửa đổi |

## Hai thành phần, một giao thức

```
┌─────────────────────────────┐         Bluetooth SPP (chính)         ┌──────────────────────────────┐
│   ANDROID  (SyncLogs/)       │  ───────  STX + CSV + ETX  ────────►  │   PC  (LeontecSyncLogSystem/)  │
│   Kotlin + Jetpack Compose   │                                       │   C# .NET 8 + WinForms         │
│   Room + WorkManager         │  ───────  POST JSON /api/sync  ─────► │   Kestrel + EF Core            │
│   máy CLIENT Bluetooth       │            Wi-Fi (dự phòng)           │   máy SERVER Bluetooth SPP     │
└─────────────────────────────┘                                       └──────────────────────────────┘
```

- **Android là client Bluetooth**, **PC là server Bluetooth SPP**. (Không phải cổng COM.)
- Khóa chống trùng là `id` (Guid) — sinh trên máy hoặc suy ra ổn định từ nội dung bản ghi.
- jobType là tiếng Nhật: `検品` (kiểm phẩm), `出荷` (xuất hàng), `直送` (giao thẳng).
