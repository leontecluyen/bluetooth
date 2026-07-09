# Tài liệu kỹ thuật — LeontecSyncLogSystem

Bộ tài liệu này mô tả **nguyên lý hoạt động** và **công nghệ sử dụng** cho toàn bộ hệ thống
đồng bộ log quét mã vạch của Leontec, gồm cả **ứng dụng Android** (máy cầm tay) và
**ứng dụng PC C#** (máy chủ trung tâm trên Windows).

> Tài liệu viết bằng tiếng Việt để cả nhóm dễ đọc. Đây là nguồn tham chiếu chính thức về
> kiến trúc. **Quy tắc bắt buộc:** mỗi khi sửa code, phải cập nhật tài liệu tương ứng trong
> thư mục này (xem [05 — Công nghệ & quy tắc phát triển](05-cong-nghe-va-quy-tac-phat-trien.md)).

## Hệ thống làm gì? (1 câu)

Máy Android cầm tay (`shipment_support`) quét mã vạch → ghi log nghiệp vụ ra file CSV theo ngày →
gửi sang PC trung tâm qua **Bluetooth SPP** (kênh Wi-Fi đã gỡ) → PC lưu vào cơ sở dữ liệu có
**chống trùng (supersede)** và hiển thị trạng thái trực tiếp trên **dashboard desktop**.

## Mục lục

| # | Tài liệu | Nội dung |
|---|----------|----------|
| 01 | [Tổng quan & kiến trúc](01-tong-quan-kien-truc.md) | Bức tranh toàn cảnh, sơ đồ luồng dữ liệu, hai kênh đồng bộ, triết lý thiết kế |
| 02 | [Ứng dụng PC (C# / .NET / WinForms)](02-ung-dung-pc-csharp.md) | Máy chủ: host trong tiến trình, server Bluetooth SPP, parser, dedup, dashboard |
| 03 | [Ứng dụng Android (`shipment_support`, Java)](03-ung-dung-android.md) | Máy cầm tay: module 「ログ送信」 gửi file log lên PC qua Bluetooth SPP |
| 04 | [Giao thức & luồng dữ liệu](04-giao-thuc-va-luong-du-lieu.md) | Hợp đồng dữ liệu CSV, đóng khung STX/ETX, giao thức batch, các endpoint HTTP |
| 05 | [Công nghệ & quy tắc phát triển](05-cong-nghe-va-quy-tac-phat-trien.md) | Bảng công nghệ đầy đủ, chuẩn code enterprise, quy tắc log, quy trình sửa đổi |

## Hai thành phần, một giao thức

```
┌─────────────────────────────┐         Bluetooth SPP (chính)         ┌──────────────────────────────┐
│  ANDROID (shipment_support/) │  ── STX + filename\r\n + CSV + ETX ─► │   PC  (LeontecSyncLogSystem/)  │
│  Java + Android View         │  ── BATCH_END ──────────────────────► │   C# .NET + WinForms           │
│  module bluetooth_module     │  ◄─ RESULT\n<file>=OK|ERR ──────────  │   Kestrel + EF Core            │
│  máy CLIENT Bluetooth        │                                       │   máy SERVER Bluetooth SPP     │
└─────────────────────────────┘                                       └──────────────────────────────┘
```

- **Android là client Bluetooth**, **PC là server Bluetooth SPP**. (Không phải cổng COM.)
- Dữ liệu gửi là **file CSV log theo ngày** (`monitor_log` / `pallet_log` / `direct_log`); PC nhận
  diện type từ header row-1, chống trùng bằng cơ chế **supersede** theo `(termId, type, index)`.
- Kênh Wi-Fi (`POST /api/sync`) đã bị gỡ khỏi app Android; PC vẫn để endpoint nhưng trả **501**.
