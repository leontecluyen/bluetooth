# 03 — Ứng dụng Android (`shipment_support`, module gửi log)

> **Đổi app nguồn (2026-07-09).** App Android cũ `SyncLogs/` (Kotlin, Jetpack Compose, Room,
> WorkManager, geofence, Wi-Fi) **đã bị gỡ khỏi repo**. Bên gửi log giờ là app
> **`shipment_support/`** — app quét mã vạch cho kho Nittsu (Java thuần, Android View, không
> Kotlin/Compose). Tài liệu này chỉ mô tả **phần liên quan tới LeontecSyncLogSystem**: tính năng
> **「ログ送信」** (gửi log lên PC qua Bluetooth SPP), đóng gói trọn trong package
> `bluetooth_module`. Spec nghiệp vụ đầy đủ của 3 màn hình kho (検品 / 出荷 / 直送管理) nằm trong
> `shipment_support/CLAUDE.md` + `shipment_support/docs/` — **không** thuộc phạm vi tài liệu này.

Thư mục: `shipment_support/`. Package gốc: `jp.leontec.nittsu.carstock`. Module gửi log:
`app/src/main/java/jp/leontec/nittsu/carstock/bluetooth_module/`.

## 3.1. Bản chất

`shipment_support` là app máy cầm tay quét mã vạch, tự **ghi log nghiệp vụ ra FILE theo ngày**
(qua `common/FileLogHelper`). Module `bluetooth_module` thêm màn hình **「ログ送信」** để người
vận hành **chọn ngày → chọn file log → gửi lên PC** qua **Bluetooth SPP (RFCOMM)**. Không có DB
cục bộ cho việc gửi, không WorkManager, không geofence, không Wi-Fi — chỉ đọc file log thật của
app rồi đẩy sang PC theo yêu cầu thủ công.

Cấu hình SDK (`shipment_support/app/build.gradle`): `namespace`/`applicationId` =
`jp.leontec.nittsu.carstock`, `minSdk 24`, `targetSdk 36`, **Java thuần** (Android View +
DataBinding + MVVM thủ công). App **chưa release**.

## 3.2. Sơ đồ package `bluetooth_module`

```
jp/leontec/nittsu/carstock/bluetooth_module/
  BluetoothModule.java       Điểm vào DUY NHẤT: attachSendLogsButton(activity) — nối nút
                             「④ ログ送信」 (id btnSendLogs trong layout include) → mở LogSendActivity;
                             + ẩn/hiện nút: isSendLogsVisible/setSendLogsVisible (mặc định ẩn)
  LogSendActivity.java       Màn hình 「ログ送信」 (View thuần, AppCompatActivity): chọn ngày +
                             未送信/送信済 + chọn file + 送信 (5 ca kết quả) + ☰ (PC選択 / リセット)
  BluetoothSyncManager.java  Lõi SPP: bonding, connect (secure→insecure) + retry, sendOneCsv,
                             sendBatch (giao thức batch + RESULT), buildUploadFilename, unpairAll
  BackupStore.java           Kho 送信済 trên đĩa (filesDir/backup/{yyyyMMdd}/) — nguồn của index
  DayLogRepository.java       Đọc file log THẬT của app theo ngày (File hoặc SAF) + tạo file mẫu
  BtSyncConfig.java          SharedPreferences riêng (bt_module_config): CHỈ nhớ PC (tên + MAC)
```

Layout kèm theo: `res/layout/activity_log_send.xml` + `res/layout/include_send_logs_button.xml`.
Chuỗi UI dùng `res/values*/strings.xml` của shipment_support (nhóm key `logsend_*`; **日本語 mặc
định + English**, xem [3.9](#39-đa-ngôn-ngữ)).

**Tích hợp vào app (đúng 3 điểm chạm tối thiểu, cố ý gói gọn để không phải sửa nhiều về sau):**
- `AndroidManifest.xml` — khai báo quyền Bluetooth + đăng ký `LogSendActivity`.
- `res/layout/activity_main.xml` — đúng **một** `<include layout="@layout/include_send_logs_button"/>`
  (đổi giao diện nút thì sửa file include, KHÔNG sửa lại `activity_main.xml`).
- `MainActivity.java` — đúng **một** dòng `BluetoothModule.attachSendLogsButton(this);` sau
  `setContentView(...)`.

**Ẩn/hiện menu ④ ログ送信 (công tắc trong 設定).** Nút ④ mặc định **ẩn**; operator bật ở màn
設定 (card "ログ送信メニュー" + `SwitchCompat` `swShowLogSend`). Lựa chọn lưu trong cache bool
(`Utils.getCacheBool`/`saveCacheBool`, key `BluetoothModule.CACHE_SHOW_SEND_LOGS =
"show_send_logs_button"`, **mặc định `false` = ẩn**). Toàn bộ logic gói trong `BluetoothModule`:
`isSendLogsVisible(ctx)` / `setSendLogsVisible(ctx, visible)`; `attachSendLogsButton` đặt
`btnSendLogs.setVisibility(...)` theo giá trị này ⇒ đổi công tắc có hiệu lực ở lần mở màn hình
chính kế tiếp. `SettingActivity` chỉ khởi tạo trạng thái công tắc + bấm cả hàng để bật/tắt rồi
gọi `setSendLogsVisible`; MainActivity không cần sửa thêm.

## 3.3. Nguồn dữ liệu gửi — `DayLogRepository`

Module KHÔNG có kho riêng: nó gửi chính **các file log mà `shipment_support` tự ghi** qua
`FileLogHelper`, tên đặt theo ngày (`YYYYMMDD` = `yyyyMMdd`):

| Type gửi PC  | Tên file log của app                | Ý nghĩa    |
|--------------|-------------------------------------|------------|
| `monitor_log`| `monitor_log_YYYYMMDD.txt`          | モニタリスト (nhận hàng) |
| `pallet_log` | `pallet_log_YYYYMMDD.txt`           | パレット (đóng pallet)  |
| `direct_log` | `direct_log_YYYYMMDD.txt`           | 直送管理 (giao thẳng)   |

- **Nơi lưu file** = folder do người dùng chọn trong app, phân giải qua
  `Utils.getCache(SHARE_KEY_FOLDER_PATH)`. Hỗ trợ **cả 2 dạng**: đường dẫn File thường
  (`/storage/emulated/0/…`) **và** SAF `content://…` (dùng `DocumentFile`).
- **`unsentForDay(date)`** = mọi file log thật của ngày (LUÔN hiện — cho phép gửi nhiều lần, mỗi
  lần gửi sinh 1 bản backup với index +1 để PC supersede bản cũ).
- **`sentForDay(date)`** = liệt kê `BackupStore` (các file đã gửi của ngày, tên kèm term + index).
- **`nextIndex(type,date)`** = ủy quyền cho `BackupStore` (đếm từ thư mục backup, xem 3.5).
- **`moveToBackup(entry, sentName, csv)`** = ghi bản sao vào backup với đúng tên đã gửi PC **rồi
  XOÁ file log gốc** (giống outbox→backup của app cũ).
- **`createSampleFiles(date)`** = ghi (đè) 3 file log MẪU cho ngày (`monitor`/`pallet`/`direct`)
  với header **giống hệt file thật** để PC nhận dạng như log thường. Sinh dữ liệu nghiệp-vụ-hợp-lý
  (monitor 8 cột, có dòng `状態 9` để test lọc; pallet 7 cột, có `状態 0/1/9`; direct 11 cột, không
  cột 状態, `納入数` chỉ >0 với khách トヨタ). KHÔNG đụng backup → index vẫn tăng tiếp.

## 3.4. Cấu hình bền — `BtSyncConfig`

SharedPreferences **riêng** của module (`bt_module_config`, không đụng prefs sẵn có của
shipment_support). Chỉ nhớ **PC mục tiêu**:

- `pcBluetoothName` — tên Bluetooth PC đã chọn (để khớp lỏng khi không có MAC).
- `pcBluetoothAddress` — **địa chỉ MAC** PC (ưu tiên, khớp chính xác).
- `hasPcTarget()` — đã chọn PC chưa; `resetAll()` — quên PC đã lưu (dùng cho リセット).

> index upload và trạng thái "đã gửi" **KHÔNG** lưu ở prefs — chúng được **suy ra từ thư mục
> backup trên đĩa** (`BackupStore`). Đây là khác biệt cố ý so với app cũ (từng lưu index vào prefs).

## 3.5. Kho 送信済 & index — `BackupStore`

Kho "đã gửi" đặt **ngay trong folder log người dùng chọn** (chỗ chứa các file CSV theo ngày), thư
mục con `backup/{yyyyMMdd}/`: tức `<folder người dùng chọn>/backup/{yyyyMMdd}/`. Mỗi lần gửi
**THÀNH CÔNG** ghi 1 bản sao CSV vào đây với **đúng tên đã gửi lên PC**
`{type}_{yyyyMMdd}_{term}_{index}.txt`. `BackupStore` phân giải folder giống `DayLogRepository`/
`FileLogHelper` — hỗ trợ **cả** đường dẫn File thường (`/storage/emulated/0/…`) **và** SAF
`content://…` (dùng `DocumentFile`); folder mặc định (chưa chọn) = `…/ShipmentSupport/backup/`.
(Trước đây backup nằm ở bộ nhớ nội bộ app `filesDir/backup/{date}/` — đã chuyển ra folder log ngày
để backup nằm cạnh file CSV gốc.)

> **Danh sách 未送信 KHÔNG hiện thư mục `backup/`.** `DayLogRepository.unsentForDay` chỉ dò đúng 3
> tên file cố định (`monitor_log_/pallet_log_/direct_log_<date>.txt`), không liệt kê toàn bộ folder,
> nên thư mục con `backup/` không bao giờ xuất hiện trong danh sách CSV cần gửi.

- **`nextIndex(type,date)` = `1 + max(index của type trong backup/{date}/)`** — liệt kê file của
  ngày, tách cụm số index ở cuối tên (regex `^([a-zA-Z]+_log)_(\d{8})_.+_(\d+)$`), lấy max rồi +1.
  Thư mục trống/chưa có ⇒ trả **1**.
- `save(date, sentName, csv)`, `read(date, fileName)` (đọc lại để gửi lại), `listForDay(date)`
  (sắp theo type rồi index), `hasType`, `clearType` (xoá khi tạo lại file mẫu), `clearAll` (リセット).

## 3.6. Lõi Bluetooth SPP — `BluetoothSyncManager`

UUID SPP `00001101-0000-1000-8000-00805F9B34FB`; đóng khung `STX(0x02) + body + ETX(0x03)`.
**Tất cả phương thức mạng là ĐỒNG BỘ (blocking)** — caller phải chạy trên thread nền
(`LogSendActivity` dùng `ExecutorService` một luồng). Không tự xin quyền runtime; nếu thiếu
`BLUETOOTH_CONNECT` (Android 12+) thì bắt `SecurityException` → trả kết quả lỗi tương ứng.

- **`buildUploadFilename(type, date, termId, index)`** → `{type}_{date}_{termId}_{index}.txt`
  (term **TRƯỚC** index; term rỗng → `"unknown"`). Dùng chung để tên gửi đi và tên lưu backup khớp
  nhau. Xem [04 §4.1b](04-giao-thuc-va-luong-du-lieu.md).
- **`localTerminalId()`** = **địa chỉ MAC Bluetooth của máy** làm `termId` (bỏ `:`, viết HOA — vd
  `A1B2C3D4E5F6`) để mã máy **cố định + duy nhất** (tên Bluetooth có thể trùng/bị đổi). Lấy MAC theo
  thứ tự: `Settings.Secure "bluetooth_address"` → `adapter.getAddress()` → **fallback
  `localTerminalName()`** khi ROM không cho đọc MAC (từ Android 6 `getAddress()` bị ẩn thành
  `02:00:00:00:00:00`). `LogSendActivity` vẫn **bỏ mọi khoảng trắng** khỏi kết quả trước khi dựng
  filename (phòng khi rơi vào fallback tên có dấu cách).
- **`localTerminalName()`** = tên Bluetooth của máy, fallback `Build.MODEL` (nay chỉ dùng làm fallback
  cho `localTerminalId()`).
- **`pairedDevices()`** = **chỉ** thiết bị đã bonded (KHÔNG discovery → không cần
  `BLUETOOTH_SCAN`). `unpairAll()` = huỷ pair mọi thiết bị bonded qua reflection `removeBond`
  (cho リセット).
- **`resolveTarget(name, addr)`** — tìm PC: (1) **MAC** đã lưu (chính xác) → (2) **tên** khớp
  lỏng (bằng nhau, hoặc contains hai chiều, không phân biệt hoa thường — xử lý hậu tố tên PC
  Windows kiểu `"LUYEN - Front"` vs `"LUYEN"`) → (3) chưa lưu gì thì **tự đoán** (`autoPickBondedPc`):
  thiết bị bonded lớp **COMPUTER**, nếu không có thì thiết bị bonded **duy nhất**. ⇒ pair PC bằng
  Cài đặt hệ thống rồi bấm gửi là chạy, không bắt chọn PC thủ công.
- **`connect()`** thử socket SPP **secure trước, fallback insecure**; **`connectWithRetry()`** thử
  lại 4 lần với backoff **có jitter** (250ms→~1.4s). Lý do: Bluetooth Classic (BR/EDR) chỉ 1 kết
  nối/lúc ở tầng radio — nhiều máy bấm gửi cùng lúc thì máy "thua" bị từ chối; retry + jitter giúp
  lệch pha và kết nối trong vài giây. (Đây là giới hạn BR/EDR, không phải SPP/PC server — PC accept
  nhiều client song song.)

### Hai cách gửi

- **`sendOneCsv(...) : SendOutcome`** — gửi **MỘT** file trên một kết nối SPP riêng, phân loại kết
  quả thành enum **`SendOutcome`** đúng 5 ca cho màn hình: `BLUETOOTH_OFF` (adapter tắt/thiếu
  quyền) / `NOT_PAIRED` (chưa chọn/không thấy PC bonded) / `CONNECT_FAILED` (mở socket thất bại sau
  retry) / `SEND_FAILED` (ghi/flush lỗi) / `SUCCESS`. (Không dùng trong luồng gửi chính hiện tại,
  giữ làm tiện ích.)
- **`sendBatch(files, name, addr) : BatchResult`** — **cách gửi chính** của màn hình. Gửi **NHIỀU
  file trên MỘT kết nối**: ghi lần lượt các frame `STX + filename\r\n + csv + ETX`, rồi một frame
  điều khiển **`BATCH_END`**. PC ingest hết, trả **MỘT** frame tổng hợp
  `STX + "RESULT\n<filename>=OK|ERR\n…" + ETX` (chờ tối đa 15s). `BatchResult.okFiles` = tập file
  PC **xác nhận OK** → caller chỉ MOVE sang backup các file đó (không mất dữ liệu nếu gửi lỗi).
  Chi tiết giao thức: [04 §4.1](04-giao-thuc-va-luong-du-lieu.md).

> **Không còn heartbeat.** App cũ ping `PING/PONG` mỗi 5s để dò "listener PC còn sống". Module
> mới **bỏ hoàn toàn** heartbeat (không có hàm nào gửi PING). PC vẫn còn code trả `PONG` để tương
> thích ngược, nhưng không thiết bị nào gọi nữa — xem [04 §4.1](04-giao-thuc-va-luong-du-lieu.md).

### Nhận master (PC → 端末) — `requestMaster`

- **`requestMaster(masterDir, knownTimestamps, name, addr) : MasterResult`** — kéo master mới từ PC.
  Mở MỘT kết nối, gửi `MASTER_REQ\ncustomer_master.csv=<pcMtime>\nitem_master.csv=<pcMtime>` với
  `<pcMtime>` = **mốc do PC phát mà app đã lưu** (`knownTimestamps`, từ `BtSyncConfig.getMasterTimestamp`,
  0 nếu chưa có). Đọc các frame PC trả (file `<tên>\t<pcMtime>\r\n<csv>`) tới khi gặp
  **`MASTER_END\n<tên>=UPDATED|UPTODATE…`** (timeout 20s), **ghi đè** `<logDir>/master/<tên>.csv` (UTF-8),
  trả `MasterResult{outcome, received, status, receivedTimestamps, completed}`.
- **Chống lệch đồng hồ:** app KHÔNG dùng `File.lastModified` của chính nó (máy handheld có thể chạy
  nhanh/chậm hơn PC → so sai). Thay vào đó lưu **mốc do PC cấp** (`receivedTimestamps` → lưu qua
  `BtSyncConfig.setMasterTimestamp` chỉ khi import OK) rồi gửi ngược lên; PC so trong cùng hệ đồng hồ
  của nó. Xem [04 §4.1](04-giao-thuc-va-luong-du-lieu.md).
- **Thông báo** (`receiveMaster`): `outcome != SUCCESS` → thông báo chuẩn (`messageFor`); `!completed`
  (không có `MASTER_END`) → *PC không phản hồi*; import lỗi/thiếu → *lỗi*; `updatedCount()==0` → *đã
  mới nhất*; ngược lại → *đã cập nhật N file*.

## 3.7. Màn hình 「ログ送信」 — `LogSendActivity`

`AppCompatActivity` (View thuần, KHÔNG Compose), fullscreen. `attachBaseContext` áp ngôn ngữ đã chọn
qua `LocaleHelper.buildOverrideConfig` + `applyOverrideConfiguration` (KHÔNG dùng
`createConfigurationContext` — nếu không sẽ vỡ `PrintManager.print`, xem shipment_support/CLAUDE.md §i18n).

- **送信日** (ngày gửi): 2 nút ◀/▶ đổi ngày, mặc định **hôm nay**, **chặn ngày tương lai** (▶
  disable khi đang ở hôm nay). So sánh ngày = so sánh chuỗi `yyyyMMdd`.
- **対象ファイル**: radio **未送信 / 送信済** (lọc log thật vs backup theo ngày).
- **全選択** + **選択 N 件**; danh sách file dựng động (mỗi type có log trong ngày = 1 dòng
  checkbox, bấm cả dòng = toggle). Đổi tab thì RESET lựa chọn: **未送信 chọn sẵn tất cả**;
  **送信済 bỏ chọn** (chọn tay để gửi lại).
- **サンプルログを作成** — `createSampleFiles` cho ngày đang chọn, rồi chuyển về tab 未送信.
- **送信** (`sendSelected`) — **bật ở CẢ 2 tab** (`n > 0`): 未送信 = gửi MỚI (tính index từ backup
  +1, tránh trùng trong cùng batch); 送信済 = **gửi LẠI** file backup, **giữ nguyên** tên (term +
  index) → PC nhận cùng filename và **replace** bản cũ. Gọi `sendBatch`; file PC báo OK: 未送信 →
  `moveToBackup` (xoá gốc), 送信済 → đã ở backup nên khỏi move. Refresh → file thành công rời 未送信
  sang 送信済.
- **Kết quả** hiện qua Toast, đúng **5 ca** (khóa `logsend_*`, tiếng Nhật): `Bluetooth を ON にし…`
  / `PC とペアリング…` / `PC への接続に失敗…` / `ファイル送信に失敗…` / `送信が完了しました。`.
  Outcome hiển thị = **`BatchResult.outcome`** (SUCCESS nếu PC xác nhận OK đủ số file, ngược lại
  SEND_FAILED / lỗi kết nối / chưa pair / BT off).
- **マスタ受信** (`btnReceiveMaster` → `receiveMaster`): kéo master mới từ PC (`requestMaster`) rồi
  re-import từng file nhận được vào SQLite (`importCustomerCsvFromDisk`/`importItemCsvFromDisk`).
  Toast: cập nhật N file / đã mới nhất / lỗi. Chạy trên thread nền (`io`).
- **Menu ☰** (`PopupMenu`): **PC選択** (dialog danh sách bonded, đánh dấu ✓ PC hiện tại),
  **リセット** (`unpairAll` + `BtSyncConfig.resetAll` + `clearAllBackup`, có hộp xác nhận).
- **Quyền:** xin `BLUETOOTH_CONNECT` ngay khi vào màn (chỉ API ≥ 31, qua
  `registerForActivityResult(RequestPermission)`); `sendSelected()`/`choosePc()` cũng chặn xin
  quyền nếu thiếu. Máy cũ (< API 31) no-op.

## 3.8. Quyền — `AndroidManifest.xml`

| Quyền | API | Mục đích |
|-------|-----|----------|
| `BLUETOOTH`, `BLUETOOTH_ADMIN` | ≤30 (maxSdk) | Bluetooth cũ |
| `BLUETOOTH_CONNECT` | 31+ | Kết nối SPP + đọc thiết bị bonded (**bắt buộc** ở Android 12+) |
| `READ/WRITE/MANAGE_EXTERNAL_STORAGE` | — | Đọc/ghi file log của app (dùng chung với 3 màn nghiệp vụ) |

Không xin `BLUETOOTH_SCAN` (không discovery), không quyền vị trí/geofence, không dùng
`INTERNET` cho việc gửi log (kênh Wi-Fi đã bỏ).

## 3.9. Đa ngôn ngữ

Kế thừa cơ chế i18n của shipment_support: **日本語 (mặc định/fallback) + English**, chuyển trong
màn 設定 của app. Chuỗi UI của module nằm trong `res/values/strings.xml` (JA) + `values-en/strings.xml`
(EN), nhóm khóa `logsend_*` (+ `common_*`). Các chuỗi **cố ý giữ tiếng Nhật, KHÔNG externalize**:
header/nội dung CSV log (hợp đồng dữ liệu với PC — xem [04](04-giao-thuc-va-luong-du-lieu.md)).
Xem thêm [i18n](05-cong-nghe-va-quy-tac-phat-trien.md) và `shipment_support/CLAUDE.md`.

## 3.10. Khác biệt so với app cũ (`SyncLogs`)

| Khía cạnh | App cũ `SyncLogs` (đã gỡ) | Module mới `bluetooth_module` |
|-----------|---------------------------|-------------------------------|
| Ngôn ngữ / UI | Kotlin + Jetpack Compose | **Java + Android View** |
| Nguồn dữ liệu gửi | Room DB (`JobLog`) + kho file `CsvFileStore` | **File log thật của app** (`FileLogHelper`) đọc qua `DayLogRepository` |
| index upload | Lưu trong prefs (`SyncConfig.nextUploadIndex`) | **Đếm từ thư mục backup** (`BackupStore.nextIndex`) |
| Đồng bộ nền | WorkManager (định kỳ 15' + geofence) | **Không** (chỉ gửi thủ công) |
| Kênh Wi-Fi | Retrofit POST `/api/sync` (lớp 2 dự phòng) | **Không** (đã bỏ) |
| Heartbeat | `PING/PONG` mỗi 5s ("Listener OK") | **Không** (bỏ hoàn toàn) |
| Giao thức gửi | mỗi batch = 1 frame, không xác nhận từng file | **Batch + `BATCH_END` + `RESULT`** (PC xác nhận OK từng file) |
| Cấu hình | `SyncConfig` (PC + IP + geofence…) | `BtSyncConfig` (**chỉ** PC name/MAC) |

> Bất kỳ thay đổi nào ở wire-format (CSV/khung/field/giao thức batch) **phải đồng bộ với phía
> PC** (`Services/BluetoothSppServer.cs`, `Services/CsvTypes.cs`) và cập nhật cả tài liệu này lẫn
> [04 — Giao thức](04-giao-thuc-va-luong-du-lieu.md).
