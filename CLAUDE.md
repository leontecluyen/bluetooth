# LeontecSyncLogSystem

Barcode/work-log sync system for Leontec. Android handheld devices push scan logs to a
central Windows PC over two channels; the PC stores them in a database with deduplicated
writes and shows live status in a desktop dashboard.

**Single application.** One WinForms (.NET 8) project, `LeontecSyncLogSystem`. On startup
it boots an in-process generic host that runs the background work — Bluetooth SPP serial
listeners, an in-process HttpListener web API, and the EF Core database — then runs the dashboard
UI on the main thread. The dashboard reads state directly from the host's services
(no HTTP round-trip to itself). Output assembly: `LogManagement.exe` (set by `<AssemblyName>LogManagement</AssemblyName>`; the shipped folder has no "Leontec" in any filename — the source `RootNamespace` stays `LeontecSyncLogSystem`).

> Earlier this was split into a headless `PcBackgroundService` (Windows Service) + a
> separate GUI. It has since been **merged into one desktop app** — there is no separate
> service project or `install-service.ps1` anymore.

> **Full technical docs (Vietnamese) live in [`docs/`](docs/README.md)** — overview &
> architecture, the PC C# app, the Android app, the data protocol, and the technology +
> development-rules reference. Keep them in sync with the code (see "Mandatory dev rules" below).

## Project layout

```
LeontecSyncLogSystem/
  Program.cs            host bootstrap (generic Host) + WinForms message loop
  MainForm.cs           dashboard logic; polls MonitorService every 2s (in-process)
  MainForm.Designer.cs  Designer-pattern layout skeleton (InitializeComponent) so the WinForms
                        Designer can render it; DI/data/localization stay in MainForm.cs
  Worker.cs             BackgroundService (IHostedService): runs the Bluetooth SPP server accept loop
  appsettings.json      Sync (BluetoothServiceName + BackupFolder), HTTP port (legacy "Kestrel" key),
                        Logging (DB settings are NOT here — external MySQL, see mysql.xml below)
  Data/AppDbContext.cs  EF Core 3.1 context (net48); typed tables (csv_uploads + normalized rows +
                        devices) + DateOnly/TimeOnly value converters; schema via EnsureCreated()
  Services/
    AppPaths.cs           resolves the on-disk layout (app dir + <root>/_master,_backup,mysql.xml)
    SyncOptions.cs        SyncOptions (BluetoothServiceName + BackupFolder); no DB options anymore
    MySqlConfig.cs        reads <root>/mysql.xml (host/port/db/user/pass) → connection string + status
    CsvBackupWriter.cs    writes a raw copy of each received CSV to disk (backup, best-effort)
    MasterStore.cs        PC-owned source of truth for the 2 master CSVs (customer/item): load/save
                          (UTF-8 no BOM) + first-run seed from the exe-side master-seed/ bundle
                          (linked from the Android assets — see below) + SHA-256 Version()
    FrameDecoder.cs       STX/ETX byte framing (pure, testable)
    BluetoothSppServer.cs  32feet.NET RFCOMM SPP server, multi-client accept loop
    HttpApiService.cs     IHostedService HTTP monitoring API via System.Net.HttpListener (net48; no Kestrel)
    CsvInbox.cs           in-memory, capped list of received CSV uploads (+ their rows)
    ServiceStatus.cs      thread-safe live state (BT clients + server status), singleton
  Monitoring/
    MonitorService.cs     builds a StatusDto snapshot from ServiceStatus + CsvInbox + DB;
                          IsDbConnectedAsync() = external-MySQL liveness probe for the status label
    StatusModels.cs       StatusDto/BtServerDto/ClientDto/ReceivedCsvDto/LogsDto (UI + /api/status)
  UI/
    Localization.cs       Loc.T(key) EN/JA + language persistence (ui-language.txt in the app dir)
    UiConfig.cs           reads app/configuration.xml (7 show/hide toggles, all default false)
```

**On-disk layout (same for debug & Release; NOT under `%LOCALAPPDATA%`).** The exe lives in an
`app/` folder; the data folders + config files are its siblings/children (resolved by
`Services/AppPaths.cs`, anchored on `AppContext.BaseDirectory`):

```
<root>/
  _master/            editable master CSVs (customer/item)        (AppPaths.MasterDir)
  _backup/            per-day raw copies of received CSVs          (AppPaths.BackupDir)
  mysql.xml           external MySQL connection settings           (AppPaths.MySqlConfigPath)
  Log Management.lnk  shortcut → LogManagement/…exe (launch here)  (CreateRootShortcut target)
  LogManagement/      the whole PC tool (exe + files) = "app/"     (AppPaths.AppDir)
    configuration.xml UI config (language + show/hide toggles)     (AppPaths.AppConfigPath)
    ui-language.txt   persisted UI language;  crash.log
```

The exe folder is named **`LogManagement`** (not the raw TFM): the csproj sets
`AppendTargetFrameworkToOutputPath=false` + `OutputPath=bin\<Config>\LogManagement\`. So in a dev build
the exe sits in `bin/<Config>/LogManagement/` and `_master`/`_backup`/`mysql.xml` land in
`bin/<Config>/` — the same relative shape as a deployed layout. Missing config files/data folders are
created with defaults on first run. **The two master CSVs are copied into `<root>/_master`** by the
`CopyMasterSeedToRoot` MSBuild target — **source = the Android app's assets folder**
(`../shipment_support/app/src/main/assets/{customer,item}_master.csv`, via the `$(MasterAssetsDir)`
property; overwrite so every Build/Debug re-pulls the latest master from assets). **The Android assets
are the single source of truth for both master CSVs** — there is no separate `master-seed/` copy in
the PC project anymore; the exe-side `master-seed/` bundle (runtime `MasterStore` seed root) is
`<None Include>`-linked straight from those same asset files, so PC and phone can't drift. Edit the
masters in `shipment_support/.../assets` only. A **`Log Management.lnk`
shortcut** to the exe is (re)created in `<root>` by the `CreateRootShortcut` target (PowerShell +
WScript.Shell) so the tool launches without entering `LogManagement/` (it stores a relative path too,
so it survives moving the whole `<root>` folder). Both targets are `AfterTargets="Build"` and run on
**every Build AND every Debug/Run** — `DisableFastUpToDateCheck=true` stops VS from skipping the build
(and thus these targets) when nothing changed.

**UI config (`app/configuration.xml`, `UI/UiConfig.cs`).** A `<language>` element (**default `ja`**;
`ja`/`en`) is the **authoritative UI language** — applied at startup via `Loc.SetLanguage` (overrides
OS detection / `ui-language.txt`); a runtime change from the language combo is written back here so
the config stays the single source. Plus eight show/hide toggles, **all default `false` (hidden)
EXCEPT `showOpenBackupButton` which defaults `true`** (missing element ⇒ shown):
`showResetButton`, `showOpenBackupButton`, `showLanguageButton`, `showMasterButtons` (the 2 master
buttons), `showBluetoothPanel` (top-left panel), `showCsvPanel` (bottom-left panel), `showMysqlStatus`
(toolbar MySQL-status label), `showRefreshButton` (the day-log Refresh button, hidden by default —
the grid auto-reloads every 2 s so it's redundant). With every toggle false the left column collapses entirely and only the
right day-log table shows. Applied once at startup by `MainForm.ApplyUiConfig()`.

Dashboard layout: left-top = Bluetooth clients + server state; **left-bottom = list of received
CSV uploads** (one row per Bluetooth frame, informational only now); **right = the FULL LOG OF ONE
DAY** for one CSV type — driven by a **date picker (default today)** + a **type radio (default
monitor)**, NOT by the CSV selected on the left. `MonitorService.GetDayLogAsync(typeKey, date)`
aggregates the rows of **all** uploads of that type whose `LogDate` matches the day (rows from
multiple uploads concatenated; identical rows colour-coded). The
day comes from each upload's filename date (`CsvUpload.LogDate`; legacy uploads without a date fall
back to the received-local date). **All 3 day-logs read their NORMALIZED DB table, not RawCsv
(2026-07-14):** `GetDayLogAsync` switches on type → `BuildMonitorDtoAsync` (`monitor_entries`) /
`BuildPalletDtoAsync` (`pallet_ops`) / `BuildDirectDtoAsync` (`direct_entries`); an **unknown** type
falls back to `BuildUnknownDtoFromRawAsync` (RawCsv). The `ICsvStore.Get{Monitor,PalletOps,Direct}…
ForDayAsync` methods join the typed table → `csv_uploads` for the same `LogDate` filter and order by
`ReceivedAtUtc` then `Id` (= creation order, so the display filter's 削除/latest-state rules still hold).
So rows deleted directly from those tables drop out of the grid **and** Export. `補給データ出力` is
unaffected — it still re-reads `RawCsv` for `収容数`/`ヨコオ品番`, so it is NOT shrunk by trimming
`direct_entries`. **Columns are PINNED to the type's canonical DISPLAY header**
(`MonitorService.CanonicalHeaders`: monitor 8-col, pallet 7-col, **direct 10-col**), **NOT derived
from the uploaded CSV** nor from "whichever upload arrived first". The display header may
**intentionally differ from the phone's wire layout**: direct is **uploaded 11-col** (old order, with
`ヨコオ品番`) but **shown/exported 10-col** per the display spec — `出荷日` pulled to the **front** and
`ヨコオ品番` dropped: `出荷日,開始時刻,終了時刻,顧客,納入先,工場コード,品番,収容数,箱数,納入数`. Each upload's rows are
re-projected onto those canonical columns **by header name** (`AppendCsvProjected`), so a canonical
column absent from the source is `""`, extra source columns (`ヨコオ品番`) are dropped, `出荷日` lands first
regardless of its source position, and older/mismatched monitor/pallet layouts (an extra `積込箱数`, a
duplicated `状態` = text `〇 完了` + numeric code, or a stray `操作` col on pallet) can't shift the columns
or make them "jump" between renders — which also keeps **Export** stable (Export serializes the exact
bound `DataTable`, minus the leading `#` ordinal column). The `状態` column resolves to the **last**
`状態` in the source header (always the numeric 0/9 code).

**`状態` is filter-only, NOT shown (monitor & pallet, 2026-07-14).** The canonical headers keep `状態`
so `AppendCsvProjected` + `ApplyDisplayFilter` can read the numeric code, but `GetDayLogAsync` calls
`RemoveColumn(dto, "状態")` **after** filtering — so `状態` appears in **neither the grid nor Export**.
Net shown/exported columns: **monitor 7-col, pallet 6-col** (`状態` dropped), direct 10-col (no `状態`).
The grid still prepends the display-only `#` ordinal; Export drops only `#` (matching the grid exactly).

**Localized UI (EN / JA).** All dashboard text goes through `UI/Localization.cs` (`Loc.T(key)`);
a language combo in the toolbar switches at runtime (`Loc.Changed` → `ApplyTexts`). First-run
language follows the OS UI culture (ja → Japanese, else English); the choice persists to
`ui-language.txt` **in the app folder** (`AppPaths.AppDataFile`). (Vietnamese was dropped from the PC tool;
if `ui-language.txt` still holds a stale `Vi`, it no longer parses and falls back to OS detection.)
The Android app still offers three languages via `values/` (English, default/fallback) +
`values-vi/` + `values-ja/` string resources and an in-app language picker (`LocaleHelper` +
`SyncConfig.appLanguage`, default "system" = follow device locale).

Targets **`net48`** (.NET Framework 4.8) — chosen because it ships **built into every Windows 10/11**,
so the app runs on a clean PC with **nothing installed and no bundled runtime** (a ~7 MB output
folder). This replaced an earlier modern-.NET (net10) + self-contained approach (~210 MB). **32feet.NET
runs the RFCOMM SPP server on net48 via the Win32 stack** (`Win32BluetoothListener`), not WinRT — the
`net462` lib of `InTheHand.Net.Bluetooth 4.2.1` exposes `BluetoothListener.Start/AcceptBluetoothClient`
(verified before the port). net48 lacks a few modern APIs, handled as follows:
- **`DateOnly`/`TimeOnly`** (used throughout) → backported by the **`Portable.System.DateTimeOnly`**
  NuGet; EF value converters map them to MySQL `DATE`/`TIME` (see `Data/AppDbContext.cs`).
- **records / `init` / `required` / range-index (`[..]`, `[^1]`)** → compile-time polyfills from
  **`PolySharp`** (generates `IsExternalInit`, `RequiredMemberAttribute`, `System.Index/Range`, …).
- **ASP.NET Core / Kestrel** is unavailable → the HTTP monitoring API uses **`System.Net.HttpListener`**
  (`Services/HttpApiService.cs`); the app uses the **generic `Host`** (`Microsoft.Extensions.Hosting`
  3.1) instead of `WebApplication`.
- **`ApplicationConfiguration.Initialize()`** (net5+ WinForms) → classic
  `Application.EnableVisualStyles()` + `SetCompatibleTextRenderingDefault(false)`.
- A handful of net-core-only BCL overloads (`File.WriteAllTextAsync`, `File.Move(overwrite)`,
  `string.Split(char, options)`, `Math.Clamp`, `Stream.ReadAsync(Memory<>)`) were swapped for their
  net48 equivalents.

## Data contract — typed CSV over Bluetooth

Each Bluetooth frame is `STX + (filename\r\n + CSV) + ETX`. The **first line is the upload
filename** `{type}_{yyyyMMdd}_{termId}_{index}.txt` (e.g. `monitor_log_20260622_A1B2C3D4E5F6_3.txt`):
`yyyyMMdd` = **the log day** (Android defaults to today), then **`termId` (the phone's **Bluetooth
MAC**, colons stripped + upper-cased, e.g. `A1B2C3D4E5F6`; falls back to the device name when the ROM
won't expose the MAC — Android 6+ hides `getAddress()`) BEFORE the numeric `index`** (per-type send
counter). `CsvTypes.ParseFilename` anchors on
the 8-digit date and takes the **trailing `_<digits>` as the index** (regex
`^(type)_(\d{8})_(term)_(\d+)$`); the old `{type}__{index}__{termId}.csv` (double-underscore, no
date) is still parsed for backward compat. PC strips the filename line (`CsvTypes.IsFilenameLine`),
stores the date in `CsvUpload.LogDate`, then detects the **type from the CSV's row-1 header**
(authoritative) — see `Services/CsvTypes.cs`.

CSV log types (canonical headers — keep Android writer + PC parser in sync):
- **`monitor_log` (モニタリスト)** `開始時刻,終了時刻,入出庫伝票番号,顧客コード,品目コード,箱数,数量,状態`
  (trailing `状態` code 0=正常/9=削除) → `MonitorEntries`. **The current Android writer
  (`FileLogHelper.writeLog`) emits 8 cols** (no `積込箱数`) — this 8-col layout is the day-log's pinned
  canonical (`MonitorService.MonitorHeaders`). Older builds emitted a 9-col variant with `積込箱数`
  before `状態`, and even a 10-col one with a duplicated `状態` (text `〇 完了` + numeric code); those are
  still ingested and the day-log projects them onto the 8 canonical cols by header name (`状態` = the
  **last** `状態`). `MonitorEntries.LoadedBoxes` maps `積込箱数` when present.
- **`pallet_log` (パレット, 7 cols)** `開始時刻,終了時刻,PLNo.,顧客,納入便,品目明細 (品目コード:箱数x数量),状態`
  (`状態` 0=正常/1=移動/9=削除) → `PalletOps` + `PalletOpItems` (品目明細 = space-separated `code:boxesxqty`).
- **`direct_log` (直送管理, 11 cols)** `開始時刻,終了時刻,顧客,納入先,工場コード,出荷日,品番,収容数,箱数,納入数,ヨコオ品番`
  (1 row = 1 completed 照合; no 状態 column; `工場コード` right after `納入先` — Android extracts it from the
  トヨタ QR ticket, chars 23–30 e.g. `1000L324`, blank for other customers) → `DirectEntries`.

  (The old `legacy` scan format — header `id`/`LogId`, table `SyncLogs` — has been **removed**. A CSV
  whose header matches none of the three types is stored as `Type = "unknown"` with only its `RawCsv`.)

**Display sort (all 3 types).** After the per-type filter below (and after `状態` is dropped), the day
log rows are sorted by **`終了時刻` (completion time) DESCENDING** — newest-completed first —
(`MonitorService.SortByEndTimeDesc`, stable; blank/unparseable times sort last). The grid binds these
rows in order and Export serialises the same table, so **grid + Export CSV + 補給データ出力 all share this
ordering** (the supply export sorts its トヨタ rows by `終了時刻` desc before projecting to its 5 columns).

**Per-type display filter (right panel, `MonitorService.ApplyDisplayFilter`).** Rows are walked in
**log-stream order** (upload-received order → in-file row order = **creation order**); a delete/move
only ever supersedes something created **before** it (never after):
- monitor: a `状態 == 9` (削除) row **cancels** the nearest `状態 == 0` (正常) row with the same
  `入出庫伝票番号` created **before** it (Android writes the delete row keeping every field, only `状態`
  flips — see `StockViewModel.deleteSelectedStock`). Hide **both** the delete row **and** the original
  it cancels (stack per invoiceNo: a `0` pushes, a `9` pops the most-recent prior `0`). An **orphan**
  `9` (no prior `0` in the day — e.g. its original is in another day/upload) cancels **nothing** and
  must NOT eat a `0` created after it. Applies to the grid **and** Export CSV.
- direct: show all.
- pallet: key = (`PLNo.`, `顧客`, `納入便`). Keep **exactly one row per key = its latest state**:
  `状態 0` (積込) / `1` (移動) set the current row (a later `移動` **supersedes** the earlier `積込`, so a
  moved pallet is not shown twice); `状態 9` (削除, whole pallet removed by
  `ShippingActivity.performDeletePallet`) clears it — removing only what was created **before** it, so
  a key **re-created after** a `削除` shows again. **A pallet emptied by a `移動`** (its `品目明細` is blank —
  Android's `FileLogHelper.buildProductDetails` returns `""` once the source pallet has no invoices
  left) is **treated as cleared too and NOT shown** (a blank latest state ⇒ no items ⇒ hide, rather
  than a blank row); a key `積込`-ed again with items afterwards shows again. (Old rule was "hide every row of any key that ever
  had a `9` + keep latest `終了時刻`"; that nuked re-created pallets and could show `積込`+`移動` twice.)

**Supersede:** a newer `index` for the same `(termId, type)` marks older `CsvUploads`
`Superseded=true`. The per-day right panel aggregates ALL uploads of the day (incl. superseded) then
applies the display filter above. Columns are the type's **pinned canonical header** (see the
GetDayLogAsync note above) — not the CSV's own row 1 — so they don't drift with mismatched uploads. An
**Export CSV** button (right of the day-log filter) writes the currently-shown (filtered) day-log to a
file — it serializes the exact `DataTable` the grid is bound to (minus the display-only `#` ordinal),
so grid and file never drift. A **補給データ出力** (supply-export) button (`btn_supply_export`,
`_btnSupplyExport`, immediately left of Export) is **shown ONLY on the direct radio** (its `Visible`
tracks `_rbDirect.Checked` — no `configuration.xml` toggle). It is a **different** export from the grid:
`MonitorService.GetDirectSupplyExportAsync(date)` re-reads the day's **raw** direct uploads (so it still
sees `収容数`/`ヨコオ品番`, which the 10-col display drops), keeps **only トヨタ rows** (`顧客 == "トヨタ"`), and
emits exactly **5 columns** `出荷日,品番,収容数(数量),工場コード,ヨコオ品番` (SJIS, no `#`). The `工場コード` value is
remapped by `MapFactoryCode` — its **5th–6th chars** (0-based index 4–5): `…T3…`→`A6`, `…L3…`→`A9`, else
`""` (e.g. `1000T322`→`A6`, `1000L324`→`A9`). A
**Refresh** button (`btn_refresh`, immediately left of the supply button) force-reloads the day-log now (resets
`_dayLogSig` then calls `RefreshAsync`) instead of waiting for the 2s timer; it is **hidden by default**
and shown only when `showRefreshButton` is `true` (the grid already auto-reloads every 2 s, so it's
redundant for normal use). The left-bottom CSV list is also filtered by the date picker (by filename date
`LogDate`). The toolbar is a red **Reset** button (= `ClearAllAsync`), status labels (incl. a **MySQL
connection-status** label driven by `MonitorService.IsDbConnectedAsync`), the language combo, and — on
the far right — an **Open backup folder** button (`OpenBackupFolder` → opens `ICsvBackupWriter.Root` in
Explorer; shown by default). **Every one of these toolbar
buttons/labels + both left panels + the master buttons is individually shown/hidden by
`configuration.xml` (all default hidden)** — see the on-disk-layout section above. The grid
auto-refreshes every 2s. Toolbar totals ("Logs today | Total") count `SUM(CsvUploads.RowCount)` of
non-superseded uploads. The window/taskbar **icon is the NEX logo** (`app.ico` next to the exe;
`ApplicationIcon` in the csproj + `MainForm.TryLoadAppIcon` at runtime — both no-op if the file is absent).

DB tables (relational, MySQL/MariaDB, snake_case names, all survive restart). Every table has a
numeric surrogate PK (`id BIGINT AUTO_INCREMENT`); FKs reference `id`:
- `devices` (PK `id`; `address` = BT MAC as a UNIQUE natural key) — BT device roster + counters
  (`DeviceStore`, seeded offline on startup).
- `csv_uploads` (PK `id`, FK `device_id`→devices, + `type`/`term_id`/`upload_index`/`superseded`/
  `log_date`/`source`, keeps `raw_csv`) — one row per sync (`CsvStore`). Slimmed on 2026-07-06: the
  duplicated device fields (`Device` name, `WorkerId`) and the vestigial dedup counters
  (`Inserted`/`Duplicates`) were dropped — join `devices` for device info. `log_date` (DATE, from the
  filename's `yyyyMMdd`; null on old uploads) drives the per-day filter; indexes on `(type, log_date)`
  and `(term_id, type)`. `CsvStore.SaveAsync` resolves the transient `DeviceAddress` → `device_id`,
  then two-phase saves (insert upload → get its id → insert normalized rows).
- `monitor_entries`, `pallet_ops`, `pallet_op_items`, `direct_entries` — normalized rows of typed
  CSVs (FK→csv_uploads / pallet_ops, cascade). Relation: **csv_upload 1—* entries/ops 1—* items**.
  Time-of-day cells (`開始時刻`/`終了時刻`) are `TIME` (`TimeOnly?`); `出荷日` is `DATE` (`DateOnly?`).
  (Display/export read `RawCsv`, not these — they're the queryable normalized copy.)

## Ingestion channels

> The protocol is defined by the Android app (`shipment_support/`, Java) — the old Kotlin
> `SyncLogs/` app was removed from the repo (2026-07-09). The sender lives entirely in the
> `bluetooth_module` package; read its source before changing the wire format —
> `bluetooth_module/BluetoothSyncManager.java` (SPP + batch protocol),
> `bluetooth_module/DayLogRepository.java` (reads the app's real log files),
> `bluetooth_module/BackupStore.java` (index), `bluetooth_module/BtSyncConfig.java` (PC target).
> See `docs/03` and `shipment_support/CLAUDE.md`.

1. **Bluetooth SPP (primary, the focus).** The PC is the **RFCOMM SPP _server_**; the phone
   is the client. Implemented with **32feet.NET** (`InTheHand.Net.Bluetooth`) in
   `Services/BluetoothSppServer.cs` — a `BluetoothListener` on UUID
   `00001101-0000-1000-8000-00805F9B34FB` that accepts clients in a loop, each serviced on
   its own task ⇒ **multiple devices concurrently**. NOT COM ports (the earlier COM approach
   was wrong — that's why no COM ever appeared).
   - The phone finds the PC by **Bluetooth radio name** = `BtSyncConfig.pcBluetoothName`, then
     connects to the SPP UUID and writes framed data (see the batch protocol below).
   - **Name-match gotcha:** the dev PC's radio name is `"LUYEN - Front"`, not `"LUYEN"`. The
     app matches leniently (case-insensitive, contains-either-way) and prefers the saved MAC —
     see `BluetoothSyncManager.resolveTarget()`.
   - **Batch protocol.** The phone sends **all files on ONE connection** — one frame per file
     (`STX + {filename}\r\n + CSV + ETX`), then a single `BATCH_END` control frame. The PC ingests
     each file, then replies with ONE `STX + "RESULT\n<filename>=OK|ERR\n…" + ETX` frame; the phone
     moves to backup only the files the PC confirmed OK (`BluetoothSyncManager.sendBatch` ↔
     `BluetoothSppServer.SendBatchResultAsync`). Old clients that never send `BATCH_END` just get no
     reply (backward-compatible). See `docs/04 §4.1`.
   - `FrameDecoder` strips STX/ETX (per-connection); the CSV type is detected from row 1
     (`CsvTypes.DetectType`) and stored/normalized by `CsvStore`; each client is tagged with its `WorkerId`.
   - **Master reverse-sync (PC → phone, phone-initiated pull).** The operator edits + saves the 2
     master CSVs in the dashboard (`MasterStore`, source of truth on PC). The phone tap "receive
     master" (`btnReceiveMaster`) opens a connection and sends `STX + "MASTER_REQ\n<name>=<pcMtime>\n…" + ETX`
     where `<pcMtime>` is the **PC-origin** timestamp the phone stored last time (0 if none — a
     PC-clock value, so phone/PC clock skew can't break the compare); the PC streams back each master
     whose file mtime now exceeds that stored value (`STX + <name>\t<pcMtime>\r\n + <csv> + ETX`, the
     phone stores that `<pcMtime>`), then a closing `MASTER_END\n<name>=UPDATED|UPTODATE\n…` frame
     reporting each file's status; the phone overwrites `<logDir>/master/*.csv`
     and re-imports into SQLite. The PC is the SPP server and can't dial a roaming phone, so there is
     **no PC push button** — delivery is always phone-initiated. `BluetoothSppServer.HandleMasterRequestAsync`
     ↔ `BluetoothSyncManager.requestMaster`. See `docs/04 §4.1`.
   - **Heartbeat (liveness) — PC-only, no longer used.** The PC still routes a frame starting with
     `PING` as a heartbeat and replies `PONG,<radioName>,<epochMillis>` (`ServiceStatus.HeartbeatTimeout`
     = 15 s), but the current `shipment_support` app **does not send PING** — the old 5 s ping /
     "Listener OK" feature was dropped. The PONG code is kept only for backward compatibility.
     Heartbeats never touch the DB and don't count as data frames/sessions. See `docs/04 §4.1`.

2. **Wi-Fi backup (secondary — parked).** The old `SyncLogs` app posted a **JSON array** of JobLog
   (Gson) to `http://<ip>:8080/api/sync`; the new `shipment_support` app **has no Wi-Fi channel**
   (Bluetooth only). The PC still exposes `POST /api/sync` but it returns **501 Not Implemented**
   (the legacy CSV parser/ingest was removed with the `SyncLogs` table). When re-enabling Wi-Fi:
   wire up typed-CSV ingest into `CsvUploads` (via `ICsvStore`) and add a POST handler for the CSV
   body in `Services/HttpApiService.cs` (HttpListener; no Kestrel on net48).

**On-disk backup.** After each received CSV is persisted to the DB, `CsvBackupWriter` also writes a
raw copy to `Sync:BackupFolder/<yyyyMMdd>/<filename>` (default `<root>/_backup`, i.e.
`AppPaths.BackupDir`; resolved path logged at startup). `<filename>` = the
phone's upload name (rebuilt as `{type}_{yyyyMMdd}_{termId}_{index}.txt` if the wire frame had none);
written atomically (temp + move), UTF-8 no BOM, faithful to the bytes received. **Best-effort** — a
backup failure is logged (Warning) and swallowed so it never fails ingestion (the row is already in
the DB). A re-send of the same upload index overwrites idempotently.

## HTTP endpoints (HttpListener, in-process — used for monitoring; Wi-Fi ingest parked)

- `GET  /api/status` — full monitoring snapshot (also what the dashboard shows in-process)
- `GET  /health` — `{"status":"ok"}`
- `POST /api/sync` — returns 501 Not Implemented (Wi-Fi ingest parked; legacy CSV path removed)

Served by `Services/HttpApiService.cs` (`System.Net.HttpListener`, an `IHostedService`), NOT Kestrel.
It tries to bind `http://+:8090/` (all interfaces, may need an admin urlacl) and **falls back to
`http://localhost:8090/`**, and if even that fails it disables the API with a warning — the dashboard
reads state in-process and never depends on this HTTP server. Port comes from the legacy
`Kestrel:Endpoints:Http:Url` setting in `appsettings.json` (default 8090).

## Build / run

```bash
# MySQL is installed/run SEPARATELY (its own installer / Windows service) — the app does NOT bundle
# or start a DB. Ensure MySQL is up and mysql.xml points at it (defaults: localhost:3306, db
# log_management, user root, empty password). The app CREATES the DB + schema via EF Core
# db.Database.EnsureCreated() at startup.
dotnet build LeontecSyncLogSystem.slnx -c Release
dotnet run --project LeontecSyncLogSystem -c Release      # starts host + dashboard, connects to MySQL
# exe: LeontecSyncLogSystem/bin/Release/LogManagement/LogManagement.exe (~7 MB folder)
```

### Deploy to a clean PC (no install needed)

The app targets **.NET Framework 4.8**, which is **built into every Windows 10/11**. So deployment is
just: **copy the `LogManagement/` output folder to the target PC and run the exe** — no runtime
install, no bundled runtime (~7 MB). On first run the app creates its siblings `_master` (seeded)/
`_backup`/`mysql.xml` per the on-disk layout. (MySQL is still external — install/run it separately
and point `mysql.xml` at it.)

For Bluetooth to work the phone must be **bonded** to this PC and the app's `pcBluetoothName`
must match (leniently) the PC's Bluetooth radio name (shown live in the dashboard header).
Schema is created at startup via **`db.Database.EnsureCreated()`** — but a DB that is **down at
startup no longer crashes the app**: the failure is logged and the dashboard opens showing
"MySQL: disconnected"; schema/roster load on a later restart once MySQL is up.

## Conventions / gotchas

- **Database = EXTERNAL MySQL/MariaDB (installed & run separately).** SQLite/SqlServer/Postgres were
  removed — the app is MySQL-only via **Pomelo.EntityFrameworkCore.MySql** + snake_case naming
  (`UseSnakeCaseNamingConvention`). The **embedded/bundled MariaDB was removed** (2026-07-09): the app
  no longer ships or starts a DB. It reads connection settings from **`<root>/mysql.xml`**
  (`Services/MySqlConfig.cs` → host/port/database/user/password; a missing file is created with
  defaults `localhost:3306`, db `log_management`, user `root`, empty password) and connects. On net48
  the DB is **EF Core 3.1** (last EF Core supporting .NET Framework) via **Pomelo 3.2**; `UseMySql`
  takes only the connection string (no `ServerVersion` arg — Pomelo 3.2 doesn't open a connection at
  config time, so the context builds offline). Connection health shows in the toolbar
  (`MonitorService.IsDbConnectedAsync`). A DB that is down at startup does NOT crash the app.
- **Schema via `db.Database.EnsureCreated()`** at startup (NOT migrations — EF Core 3.1 migration
  tooling isn't wired up on net48; `Data/Migrations/` + the design-time factory were removed). To
  change the schema: edit the entities/`AppDbContext`; EnsureCreated builds the tables from the model
  on a fresh DB. **`DateOnly`/`TimeOnly` need EF value converters** (already in `AppDbContext`:
  `TimeOnly↔TimeSpan`→`time(6)`, `DateOnly↔DateTime`→`date`) since the polyfilled types aren't mapped
  automatically. EnsureCreated does NOT alter an existing DB — for a schema change on an already-created
  DB, drop it (or the affected tables) and let EnsureCreated recreate. **Bulk ops:** EF Core 3.1 has no
  `ExecuteDelete`/`ExecuteUpdate` — clears use `db.Database.ExecuteSqlRawAsync("DELETE FROM …")`
  (cascades via FK) and supersede/replace load rows then `RemoveRange`/set-flag + `SaveChanges`.
- **UI text is localized** via `UI/Localization.cs` (`Loc.T`) on PC and `values*/strings.xml` on
  Android — add new strings as keys, never hard-code user-facing text. EN is the fallback.
- `StatusModels.cs` DTOs are both the UI grid binding source and the `/api/status` JSON shape.
- `FrameDecoder` + `CsvTypes` (header detection + per-type row parsers) are pure and
  hardware-free testable (RFC-4180 splitting, filename parsing, split chunks).
- 32feet.NET requires a Bluetooth radio; the SPP server self-heals/retries if it's off/absent.
  The dashboard header shows the radio name + listening state.
- It's a desktop GUI app now, so it can't run as a headless Windows Service (services have
  no desktop). If true unattended/service operation is needed later, split the host back out.
- Android CLAUDE.md rule (followed here too): when you change code, update docs + memory, and
  write debug logs liberally.
- No git repo here — deletions are irreversible.

## Mandatory dev rules (apply to EVERY code change, both projects)

These are user-mandated and binding. Full version in
[`docs/05-cong-nghe-va-quy-tac-phat-trien.md`](docs/05-cong-nghe-va-quy-tac-phat-trien.md).

1. **Enterprise-grade code.** Clear naming, small single-responsibility functions, explicit
   error handling (no silently-swallowed exceptions), `CancellationToken`/structured
   coroutines for background work, never break idempotency (re-sends must stay safe).
2. **Full logging at every step.** Log each meaningful business step at the right level —
   `Debug` (frames, bytes, per-row parse), `Information` (lifecycle: listening, client
   connected, ingested N), `Warning` (recoverable: layer-1→2 fallback, radio off, retry),
   `Error` (real failures with context: HTTP code, exception, device address). PC uses
   `ILogger<T>`; Android uses consistent tags (e.g. `SYNC_WORKER`). Logs must let you
   reconstruct an incident without a debugger.
3. **Change code ⇒ change docs in the same pass.** PC change → update `docs/02`; Android →
   `docs/03`; protocol/wire-format/DB → `docs/04` **and both sides**; architecture → `docs/01`.
   Update this `claude.md` if a convention/gotcha changes, and record key/non-obvious points
   to memory.
4. **Hard / multi-change / many-cases work ⇒ ASK FIRST.** Confirm before: changing the
   wire-format or DB schema, re-enabling Wi-Fi (JSON parse, port 8080, field mapping),
   switching the default DB provider or schema creation, touching Bluetooth pairing/UUID/
   name-matching, anything that deletes or overwrites data (no git here), or large
   multi-file refactors / default-behavior changes.
