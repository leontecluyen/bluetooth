# 03 — Ứng dụng Android (Kotlin / Jetpack Compose)

Thư mục: `SyncLogs/`. Package: `leontec.jp.synclogs`. Mã nguồn chính ở
`app/src/main/java/leontec/jp/synclogs/`.

## 3.1. Bản chất

Ứng dụng máy cầm tay: thu thập bản ghi công việc (quét mã vạch), lưu cục bộ bằng **Room**,
rồi đồng bộ sang PC qua **hai lớp dự phòng**: Bluetooth SPP (chính) → Wi-Fi REST (phụ). Việc
đồng bộ chạy nền bằng **WorkManager** (thủ công, định kỳ, hoặc tự động khi vào geofence văn
phòng). UI viết hoàn toàn bằng **Jetpack Compose**.

Cấu hình SDK (`app/build.gradle.kts`): `compileSdk=34`, `minSdk=26`, `targetSdk=34`,
Java 17, Compose compiler 1.5.11, dùng **KSP** cho Room.

## 3.2. Sơ đồ thư mục `app/src/main`

```
java/leontec/jp/synclogs/
  MainActivity.kt              MENU: 1 nút thẻ "④ ログ送信" → mở LogSendActivity
  LogSendActivity.kt           Màn hình "ログ送信": chọn ngày + file 未送信/送信済 + 送信 (5 ca kết quả)
  SyncLogsApplication.kt       onCreate(): đảm bảo lịch đồng bộ định kỳ
  bluetooth/
    BluetoothSyncManager.kt    Quản lý SPP: bonding, auto-enable, gửi STX+CSV+ETX
    BluetoothScanner.kt        Quét thiết bị lân cận (Flow)
  config/
    SyncConfig.kt              Cấu hình bền (SharedPreferences), singleton — nguồn sự thật
  data/
    JobLog.kt                  Entity (id UUID = khóa idempotent), enum trạng thái/phương thức
    JobLogDao.kt               Room DAO (Flow + suspend)
    AppDatabase.kt             Room database singleton
    Converters.kt              TypeConverter cho enum
    CsvFileStore.kt            Kho FILE: outbox/ (chưa gửi) + backup/{yyyyMMdd}/ (đã gửi) — màn hình ログ送信
    PendingSampleStore.kt      (LEGACY) kho mẫu JSON cho SyncWorker/auto-sync — KHÔNG dùng ở màn hình mới
  geofence/
    GeofenceManager.kt         Đăng ký/gỡ geofence (Play Services)
    GeofenceBroadcastReceiver.kt  Nhận sự kiện ENTER → kích hoạt đồng bộ
  network/
    SyncApiService.kt          Retrofit interface POST /api/sync
    RetrofitFactory.kt         Dựng Retrofit theo baseUrl động
  notification/
    BluetoothEnableNotifier.kt Thông báo "chạm để bật Bluetooth" (Android 13+)
  sync/
    LogPayloadSerializer.kt    JobLog[] → CSV (đúng wire-format với PC)
  ui/
    MainViewModel.kt           MVVM: state quan sát + hành động người dùng
  worker/
    SyncScheduler.kt           Lập lịch WorkManager (one-time + định kỳ 15')
    SyncWorker.kt              CoroutineWorker: pipeline đồng bộ 2 lớp
AndroidManifest.xml            Quyền + khai báo Activity/Receiver
LocaleHelper.kt                Bọc Context theo ngôn ngữ đã chọn (en/vi/ja/system)
res/values/strings.xml         Chuỗi UI — MẶC ĐỊNH/FALLBACK = tiếng Anh
res/values-vi/strings.xml      Chuỗi UI tiếng Việt
res/values-ja/strings.xml      Chuỗi UI tiếng Nhật
```

## 3.3. Mô hình dữ liệu — `data/`

`JobLog` (entity Room, bảng `job_logs`):

| Trường | Ghi chú |
|--------|---------|
| `id` | `UUID.randomUUID()` mặc định — **khóa chính, đảm bảo idempotent** |
| `workerId` | Mã công nhân/thiết bị |
| `jobType` | `検品` (KENPIN) / `出荷` (SHUKKA) / `直送` (CHOKUSO) |
| `barcodeData` | Mã vạch đã quét |
| `startTime` / `endTime` | Mốc thời gian (millisecond) |
| `syncStatus` | enum `PENDING` / `SUCCESS` |
| `syncMethod` | enum `BLUETOOTH` / `WIFI` (kênh nào thành công) |
| `batchId` / `batchName` | **Gom nhóm "CSV"** — các log tạo cùng lúc thuộc 1 batch, hiện thành 1 hàng trong danh sách app |
| `logId` | **Id gửi trong cột CSV** (PC dùng dedup), TÁCH khỏi khóa chính `id`. Để trống → serialize dùng `id`. Cho phép TRÙNG (test dedup) trong khi Room vẫn lưu đủ dòng |

- `workerId` = **tên máy/thiết bị** (lấy từ Settings `device_name`, fallback `Build.MODEL`).
- **`AppDatabase`**: `@Database(version=4)` (v4 thêm `logId`; v3 thêm `batchId/batchName`),
  `@TypeConverters(Converters)`, singleton, `fallbackToDestructiveMigration()` (nâng cấp xoá
  dữ liệu cũ 1 lần), file `synclogs.db`.
- **`JobLogDao`**: `observeAllLogs()`, `observeBatches()` (Flow<List<BatchSummary>>),
  `observePendingCount()`, `getUnsyncedLogs()`, `recentLogIds(limit)` (lấy vài `logId` đã có để
  tạo trùng-file-khác), `insert()` / `insertAll()` (REPLACE → idempotent), `markAsSynced(...)`,
  `deleteAll()` (cho Reset), `deleteSyncedOlderThan(threshold)`.
- **`BatchSummary`**: POJO `{ batchId, batchName, rowCount, syncedCount, createdAt }`.
- **`Converters`**: enum ↔ String cho Room.

## 3.4. Cấu hình bền — `config/SyncConfig.kt`

Singleton bọc SharedPreferences (`synclogs_config`), nguồn sự thật cho thiết lập triển khai:

- `pcBluetoothName` — tên thiết bị PC đã chọn (để hiển thị).
- `pcBluetoothAddress` — **địa chỉ MAC** (ưu tiên, khớp chính xác hơn tên).
- `hasPcTarget` — đã chọn PC chưa; `clearPcTarget()` — xoá PC đã lưu (dùng cho Reset).
- `geofenceEnabled` — trạng thái bật/tắt geofence (bền, để switch khôi phục sau khi mở lại app).
- `pcIpAddress` (mặc định `192.168.0.100`), `pcPort` (mặc định `8080`),
  `restBaseUrl` = `http://{IP}:{port}/` — cho kênh Wi-Fi (đang tạm gác).
- `geofenceLatitude/Longitude/RadiusMeters` — tâm + bán kính geofence (mặc định ga Tokyo
  35.681236, 139.767125, 50m); `GEOFENCE_REQUEST_ID = "office_geofence"`.

## 3.5. Pipeline đồng bộ — `worker/`

**`SyncWorker`** (CoroutineWorker) — `doWork()`:

1. Lấy **mẫu chờ gửi** (`PendingSampleStore.getAll()`) + log `PENDING` (`getUnsyncedLogs()`).
   Cả hai rỗng → `Result.success()`.
2. **Mẫu CSV typed (monitor/pallet):** gửi từng mẫu **chưa gửi** (`!sent`) qua
   `BluetoothSyncManager.sendCsvFile(type, csv, term, index, …, date)` (dùng đúng `index`/`date` đã
   gán lúc tạo); gửi xong mẫu nào thì `PendingSampleStore.markSent(id)` (giữ lại trong list để UI
   hiện trạng thái "đã gửi", KHÔNG xoá). Đây là nơi **"Đồng bộ ngay" / Auto thực sự gửi mẫu**.
   Toàn bộ `doWork()` được bọc trong `BluetoothSyncManager.beginSync()/endSync()` (+ `pausePing(30s)`)
   để **chặn heartbeat ping suốt cả đợt sync** — tránh PING chen vào giữa các file gây tranh chấp
   kết nối BR/EDR khiến PC tưởng mất liên lạc (→ Offline). Xem [04 §4.1 Heartbeat].
3. **Log legacy (Room) — Lớp 1 Bluetooth SPP:** gọi `BluetoothSyncManager.sync(logs)`. Thành công
   → đánh dấu `SUCCESS/BLUETOOTH`. Thất bại → sang lớp 2.
4. **Lớp 2 — Wi-Fi REST:** dựng Retrofit, gọi `syncLogs()` POST `/api/sync`. HTTP 2xx →
   `SUCCESS/WIFI`. Thất bại → log lỗi kèm mã HTTP.
5. Còn mục nào chưa gửi được → `Result.retry()` (backoff mũ); hết → `Result.success()`.

Log chi tiết theo tag `SYNC_WORKER` (số lần thử, chuyển lớp, số byte).

**`SyncScheduler`**: `enqueueOnce()` (one-time, backoff mũ 10s đầu, `ExistingWorkPolicy.REPLACE`
chống xếp trùng); `ensurePeriodicSync()` (định kỳ 15', `ExistingPeriodicWorkPolicy.KEEP`).
`SyncLogsApplication.onCreate()` gọi `ensurePeriodicSync()` để luôn có lưới an toàn.

## 3.6. Bluetooth — `bluetooth/`

**`BluetoothSyncManager`** (UUID SPP `00001101-...-34FB`):

- `isSupported/isEnabled/isDisabled`, `enableAdapter()` (bật im lặng chỉ trên API ≤32;
  Android 13+ cần người dùng tương tác), `pairedDevices()`, `bond()`,
  `unpairAll()` (huỷ pair toàn bộ thiết bị bonded qua reflection `removeBond` — dùng cho Reset).
- **`sync(logs, targetName?, targetAddress?)`** — lõi truyền:
  - **Ưu tiên khớp theo địa chỉ MAC**, dự phòng khớp theo **tên lỏng** (xử lý hậu tố tên PC
    Windows kiểu "LUYEN - Front" vs "LUYEN": không phân biệt hoa thường, contains hai chiều)
    và log toàn bộ thiết bị bonded để debug.
  - **Chưa lưu PC nào → tự chọn** (`autoPickBondedPc`): nếu name+addr đều trống (vd vừa Reset rồi
    pair lại bằng Cài đặt hệ thống), `resolveTarget` lấy thiết bị bonded **lớp Máy tính**
    (`BluetoothClass` major = COMPUTER), nếu không có thì lấy thiết bị bonded **duy nhất** → bấm
    gửi là chạy luôn, không bắt chọn PC thủ công. `detectPcCandidate()` cho UI tự nhận PC khi
    `ON_RESUME` (xem 3.9 `autoAdoptPc`).
  - **`sendCsvFile(type, csvText, termId, index, …, date)`** — gửi 1 file CSV thật (monitor/pallet)
    với **dòng đầu = tên file** `{type}_{yyyyMMdd}_{index}_{termId}.txt` rồi tới CSV. `date` =
    **ngày của log** (mặc định `todayYmd()` = hôm nay); `index` lấy từ
    `SyncConfig.nextUploadIndex(type)` (đếm theo từng type); `termId` = tên máy. PC dùng tên file
    để biết type / **ngày** / index / term + supersede bản cũ, và lọc **log theo ngày** trên
    dashboard. Xem [04 §4.1b/4.2](04-giao-thuc-va-luong-du-lieu.md).
  - Thử socket SPP **secure trước, fallback insecure** — qua `connectWithRetry` (thử lại 4 lần,
    backoff **ngẫu nhiên có jitter** 250ms→~1.4s). Lý do: Bluetooth Classic chỉ thiết lập được
    **một kết nối tại một thời điểm** ở tầng radio, nên khi 2 máy bấm đồng bộ cùng lúc, máy
    "thua" sẽ bị từ chối/timeout; retry nhanh + jitter giúp 2 máy lệch pha và kết nối được
    trong vài giây thay vì chờ WorkManager retry (10s). (Đây là giới hạn của BR/EDR, không phải
    SPP/PC server — PC accept nhiều client song song bình thường.)
  - **Gửi MỖI batch = MỘT khung CSV riêng**: `logs.groupBy { batchId }` rồi mỗi nhóm →
    `LogPayloadSerializer.toCsv(rows)` → đóng khung `STX(0x02) + CSV + ETX(0x03)` → ghi +
    flush. Nhờ vậy nếu có 4 CSV thì PC nhận 4 frame ⇒ hiện **4 dòng** (không gộp thành 1).
  - Thành công thì lưu PC target (tên + địa chỉ) vào `SyncConfig`.
  - Chạy trên `Dispatchers.IO` để tránh ANR.
  - Việc tìm thiết bị (`resolveTarget`) và mở socket (`connect`, secure→insecure) được **tách
    hàm dùng chung** với heartbeat.
- **`sendOneCsv(type, csvText, termId, index, date, targetName?, targetAddress?) : SendOutcome`** —
  gửi **MỘT** file CSV trên một kết nối SPP riêng (để biết file nào thành công) và phân loại kết
  quả thành enum **`SendOutcome`** đúng 5 ca cho màn hình "ログ送信": `BLUETOOTH_OFF` (adapter tắt) /
  `NOT_PAIRED` (chưa chọn/không tìm thấy PC bonded) / `CONNECT_FAILED` (mở socket thất bại sau
  `connectWithRetry`) / `SEND_FAILED` (ghi/flush lỗi) / `SUCCESS`. Cùng khung
  `STX + filename\r\n + csv + ETX`, filename `{type}_{date}_{term}_{index}.txt` như `sendCsvFile`.
- **`heartbeat(deviceName, targetName?, targetAddress?) : HeartbeatResult`** — kiểm tra
  **listener PC còn sống & phản hồi** (khác với "đã ghép đôi"):
  - Mở socket SPP ngắn → gửi khung `STX + "PING,<deviceName>,<epochMillis>" + ETX` → **chờ
    `PONG`** (đọc 1 khung STX/ETX bằng `readFrame`, timeout `HEARTBEAT_REPLY_TIMEOUT_MS` = 3s) →
    đóng socket.
  - Trả `HeartbeatResult(connected, acknowledged, pcRadioName, error)?`; `ok = connected &&
    acknowledged`. Mở được socket nhưng không có PONG ⇒ "không phản hồi"; không mở được ⇒
    "mất kết nối". Không gửi dữ liệu log, không đụng DB.
  - **Bỏ qua khi đang sync:** nếu cờ tiến trình `syncInProgress` (companion, do `sync()` bật
    trong cửa sổ truyền) đang true → trả **`null`** ngay, không ping (tránh tranh chấp kết nối).
  - Giao thức PING/PONG: [04 §4.1](04-giao-thuc-va-luong-du-lieu.md).

**`BluetoothScanner.discover()`**: `callbackFlow` đăng ký `BroadcastReceiver`
(`ACTION_FOUND`/`DISCOVERY_STARTED`/`DISCOVERY_FINISHED`), quét ~12s, emit thiết bị
chưa bonded, dọn receiver khi đóng.

## 3.7. Wi-Fi — `network/`

- **`SyncApiService`** (Retrofit): `@POST /api/sync` body `List<JobLog>` (Gson), trả
  `Response<Unit>` (suspend).
- **`RetrofitFactory.create()`**: OkHttp (connect 10s, read/write 20s),
  `HttpLoggingInterceptor` (BODY ở debug, NONE ở release), `GsonConverterFactory`, baseUrl
  **động** từ `SyncConfig` (IP + port đổi theo nơi triển khai).

## 3.8. Geofence — `geofence/`

- **`GeofenceManager`**: `registerOfficeGeofence()` (suspend, cần `ACCESS_FINE_LOCATION`):
  dựng `Geofence.Builder` (request id `office_geofence`, vùng tròn từ `SyncConfig`,
  `NEVER_EXPIRE`, chỉ `TRANSITION_ENTER`), `GeofencingRequest` với `INITIAL_TRIGGER_ENTER`,
  bắc cầu Task của Play Services qua `suspendCancellableCoroutine`. `pendingIntent` lazy,
  `FLAG_MUTABLE` trên API 31+. Có `removeOfficeGeofence()`.
- **`GeofenceBroadcastReceiver.onReceive()`**: khi `TRANSITION_ENTER` → gọi
  `enableAdapter()` (best-effort) → nếu vẫn tắt thì post `BluetoothEnableNotifier` (chạm để
  bật) → `SyncScheduler.enqueueOnce()`.
- **Lưu ý "bật mà không tự sync":** geofence CHỈ bắn khi *đi vào* vùng 50m (`TRANSITION_ENTER`);
  nếu đã ở trong vùng thì không có sự kiện mới. Vì vậy khi BẬT switch, ViewModel
  `setGeofenceEnabled(true)` vừa đăng ký geofence (cho lần ENTER sau) vừa **gọi `triggerSync()`
  ngay**. Để geofence bắn lúc app ở nền cần thêm `ACCESS_BACKGROUND_LOCATION` + GPS bật.

## 3.9. UI & ViewModel

> **Tái cấu trúc 2026-06-25.** Màn hình chính cũ (máy trạng thái 1-nút + Auto/Geofence + heartbeat
> + danh sách mẫu) đã được **thay** bằng: **MainActivity = MENU 1 nút** → **LogSendActivity = màn
> hình gửi log**. Dữ liệu màn hình mới dựa trên **FILE thật** (`CsvFileStore`), KHÔNG dùng
> `PendingSampleStore`. Auto-sync nền (`SyncWorker`/geofence, mục 3.5/3.8) vẫn còn nhưng **đã tách
> rời** (không còn UI bật/tắt và không còn nguồn dữ liệu vì mẫu giờ là file outbox).

### Kho file — `data/CsvFileStore.kt`

Trong `filesDir` của app:
- **未送信 (chưa gửi) = `outbox/{type}_{yyyyMMdd}.txt`** — mỗi loại 1 file/ngày (tạo lại sẽ ghi đè).
- **送信済 (đã gửi) = `backup/{yyyyMMdd}/{type}_{yyyyMMdd}_{term}_{index}.txt`** (đúng tên đã gửi lên PC).
- `unsentForDay(date)` / `sentForDay(date)` liệt kê theo ngày; `writeOutbox(type,date,csv)`;
  **`nextIndex(type,date)` = `1 + max(index của type đó trong backup/{date}/)`** (thư mục tạo mới
  nếu chưa có → bắt đầu từ 1); `moveToBackup(entry, sentName)` (rename, fallback copy+delete);
  `readCsv`, `clearAll()` (cho Reset).

### `MainViewModel` (AndroidViewModel)

`hasPcTarget`, `selectedPcName/Address`, `geofenceEnabled`, `deviceName` (Settings `device_name` /
`Build.MODEL`); hành động cho màn hình mới:
- **`unsentFiles(date)` / `sentFiles(date)`** — đọc từ `CsvFileStore`.
- **`createSampleFiles(date, onDone)`** — sinh 3 file CSV mẫu (monitor + pallet + direct) cho NGÀY
  đã chọn rồi **ghi vào outbox** (chưa gửi). KHÔNG gán index lúc này (index tính lúc gửi). Logic
  sinh dữ liệu xem mục **3.9b**.
- **`sendDay(entries, onDone)`** — gửi **từng** file đã chọn, mỗi file 1 kết nối qua
  `BluetoothSyncManager.sendOneCsv(...)`. Với mỗi file: `index = CsvFileStore.nextIndex(type,date)`;
  **gửi thành công → `moveToBackup` (outbox → backup/{date}/ với đúng tên đã gửi)**. `onDone` trả
  `SendOutcome` **nghiêm trọng nhất** gặp phải (BLUETOOTH_OFF > NOT_PAIRED > CONNECT_FAILED >
  SEND_FAILED > SUCCESS) để hiện đúng 1 trong 5 thông báo; file nào OK vẫn được chuyển.
- `selectPcDevice(name, addr)`, `checkListener()`, `setGeofenceEnabled(...)` (legacy, vẫn còn).
- `resetAll(onDone)` — huỷ pair + `clearPcTarget()` + tắt geofence + `dao.deleteAll()` +
  `resetUploadIndexes()` + `PendingSampleStore.clear()` + **`CsvFileStore.clearAll()`**.

### `MainActivity` (MENU) + `LogSendActivity` (Compose Material3)

- **`MainActivity`** = màn hình menu chỉ có **1 nút thẻ** "④ ログ送信 / 作業ログのPCへの送信"
  (`menu_log_send_*`) → mở `LogSendActivity`. Vẫn nhận intent geofence `EXTRA_PROMPT_BT_ENABLE` và
  **chuyển tiếp** sang `LogSendActivity` (nơi bật Bluetooth).
- **`LogSendActivity`** = màn hình "ログ送信":
  - **送信日** (ngày gửi): date picker ◀/▶, mặc định **hôm nay**, **chặn ngày tương lai** (▶ disable
    khi đang ở hôm nay).
  - **対象ファイル**: radio **未送信 / 送信済** (lọc outbox vs backup theo ngày).
  - **全選択** + **選択 N 件**; danh sách file (mỗi loại direct/monitor/pallet 1 dòng cho ngày đã chọn),
    checkbox chọn (mặc định chọn hết). Nút **サンプルログを作成** (`createSampleFiles` cho ngày đang chọn).
  - **送信** (`sendDay`) — **chỉ bật ở tab 未送信**. Kết quả hiện qua Toast đúng 5 ca (key
    `send_result_*`): `Bluetooth を ON にしてください。` / `PC とペアリングしてください。` /
    `PC への接続に失敗しました。` / `ファイル送信に失敗しました。` / `送信が完了しました。`. Gửi xong refresh
    → file thành công rời 未送信 sang 送信済.
  - 2 dòng ghi chú: `※ 1回の送信で 選択日 1日分のみ` / `※ 送信成功で backup/ へ自動移動`.
  - Menu **☰** (góc phải): **PC選択** (dialog bonded + discover 15s), **すべてリセット** (`resetAll`),
    và **đổi ngôn ngữ** (system/en/vi/ja). Xin quyền BT/Location khi vào màn hình nếu thiếu.
- **Đa ngôn ngữ (EN / VI / JA):** mọi chuỗi UI lấy từ `strings.xml` (`values/` = **tiếng Anh,
  fallback mặc định**, `values-vi/`, `values-ja/`). `attachBaseContext` bọc Context bằng
  `LocaleHelper.wrap`; đổi ngôn ngữ → lưu `SyncConfig.appLanguage` rồi `recreate()`. Mặc định
  `"system"` ⇒ theo locale máy.

## 3.9b. Sinh dữ liệu CSV mẫu (đúng logic nghiệp vụ kho)

`MainViewModel.generateSampleCsvSet(now)` sinh ĐỒNG THỜI 3 CSV nhất quán theo luồng kho thật:

- **monitor = NHẬN hàng** (モニタリスト): mỗi dòng = 1 品目 nhận theo `箱数 × 数量/箱`. Sinh ~30–100
  dòng, gom theo phiếu (3–6 dòng/phiếu), ~8% dòng `状態 9` (削除), lặp 2–3 dòng `状態 0` để test tô
  màu trùng ở PC. `数量 = 箱数 × 数量/箱` (収容数 của mã đó).
- **pallet = ĐÓNG PALLET từ hàng đã nhận** (パレット): chỉ dùng các 品目 đã nhận ở monitor (`状態 0`).
  Lấy số thùng đã nhận (đôi khi **75–100%** → có thể ít hơn monitor), **xếp vào pallet sức chứa có
  hạn** (`cap` thùng/pallet, suy ra để số pallet ~30–100) nên **1 品目 có thể trải qua NHIỀU pallet**.
  Vẫn giữ case test bộ lọc PC: PL1 có `状態 0` rồi `状態 1` (終了時刻 mới hơn) cùng key → PC chỉ hiện
  `状態 1`; thêm 1 dòng `状態 9`. Tổng số thùng đóng pallet của mỗi mã **≤ số đã nhận**.
- **direct = 直送管理** (độc lập): ~30–100 dòng, 1 dòng = 1 照合 hoàn tất, không có cột `状態`.
  `納入数 = 箱数 × 収容数`.

## 3.10. Quyền — `AndroidManifest.xml`

| Quyền | API | Mục đích |
|-------|-----|----------|
| `INTERNET`, `ACCESS_NETWORK_STATE` | mọi | Kênh Wi-Fi |
| `BLUETOOTH`, `BLUETOOTH_ADMIN` | ≤30 | Bluetooth cũ |
| `BLUETOOTH_CONNECT`, `BLUETOOTH_SCAN` | 31+ | Kết nối / quét |
| `ACCESS_FINE/COARSE_LOCATION` | mọi | Geofence |
| `ACCESS_BACKGROUND_LOCATION` | 29+ | Geofence chạy nền |
| `POST_NOTIFICATIONS` | 33+ | Thông báo bật BT |

## 3.11. Bảng thư viện chính

| Thư viện | Phiên bản | Dùng để |
|----------|-----------|---------|
| Kotlin | 1.9.23 | Ngôn ngữ |
| Jetpack Compose (BOM) | 2024.04.00 | UI |
| Material3 | 1.11.0 | Component |
| Room | 2.6.1 | DB cục bộ (Flow) |
| WorkManager | 2.9.0 | Đồng bộ nền + backoff |
| Retrofit / Gson | 2.11.0 / 2.10.1 | REST + JSON |
| OkHttp | 4.12.0 | HTTP transport, logging |
| Play Services Location | 21.2.0 | Geofencing |
| Coroutines Play Services | 1.8.0 | Cầu Coroutine ↔ Task |
| KSP | 1.9.23-1.0.19 | Annotation processor (Room) |

## 3.12. Mẫu thiết kế áp dụng

Singleton (`SyncConfig`, `AppDatabase`), Factory (`RetrofitFactory`), DAO (`JobLogDao`),
MVVM (`MainViewModel`+`MainActivity`), **đồng bộ 2 lớp fallback**, **idempotency** (UUID +
`OnConflictStrategy.REPLACE`), BroadcastReceiver (geofence nền), TypeConverter (enum Room).

> Bất kỳ thay đổi nào ở wire-format (CSV/khung/field) **phải đồng bộ với phía PC** và cập
> nhật cả tài liệu này lẫn [04 — Giao thức](04-giao-thuc-va-luong-du-lieu.md).
