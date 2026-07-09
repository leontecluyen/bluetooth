# LeontecSyncLogSystem

Barcode/work-log sync system for Leontec. Android handheld devices push scan logs to a
central Windows PC over two channels; the PC stores them in a database with deduplicated
writes and shows live status in a desktop dashboard.

**Single application.** One WinForms (.NET 8) project, `LeontecSyncLogSystem`. On startup
it boots an in-process generic host that runs the background work — Bluetooth SPP serial
listeners, an embedded Kestrel web API, and the EF Core database — then runs the dashboard
UI on the main thread. The dashboard reads state directly from the host's services
(no HTTP round-trip to itself). Output assembly: `LeontecSyncLogSystem.exe`.

> Earlier this was split into a headless `PcBackgroundService` (Windows Service) + a
> separate GUI. It has since been **merged into one desktop app** — there is no separate
> service project or `install-service.ps1` anymore.

> **Full technical docs (Vietnamese) live in [`docs/`](docs/README.md)** — overview &
> architecture, the PC C# app, the Android app, the data protocol, and the technology +
> development-rules reference. Keep them in sync with the code (see "Mandatory dev rules" below).

## Project layout

```
LeontecSyncLogSystem/
  Program.cs            host bootstrap (WebApplication) + WinForms message loop
  MainForm.cs           dashboard logic; polls MonitorService every 2s (in-process)
  MainForm.Designer.cs  Designer-pattern layout skeleton (InitializeComponent) so the WinForms
                        Designer can render it; DI/data/localization stay in MainForm.cs
  Worker.cs             BackgroundService: spawns one serial listener per COM port
  appsettings.json      Sync (BluetoothServiceName + BackupFolder), Database (embedded MariaDB), Kestrel
  Data/AppDbContext.cs  EF Core context; typed tables (csv_uploads + normalized rows + devices)
  Data/DesignTimeDbContextFactory.cs  lets `dotnet ef` build the context without booting the app
  Data/Migrations/      EF Core migrations (schema source of truth; applied via Migrate() at startup)
  Services/
    SyncOptions.cs        SyncOptions (BluetoothServiceName + BackupFolder) + DatabaseOptions (embedded)
    CsvBackupWriter.cs    writes a raw copy of each received CSV to disk (backup, best-effort)
    MasterStore.cs        PC-owned source of truth for the 2 master CSVs (customer/item): load/save
                          (UTF-8 no BOM) + first-run seed from master-seed/ + SHA-256 Version()
    EmbeddedMariaDbServer.cs  runs the bundled MariaDB as a managed child process (self-contained DB)
    FrameDecoder.cs       STX/ETX byte framing (pure, testable)
    BluetoothSppServer.cs  32feet.NET RFCOMM SPP server, multi-client accept loop
    CsvInbox.cs           in-memory, capped list of received CSV uploads (+ their rows)
    ServiceStatus.cs      thread-safe live state (BT clients + server status), singleton
  Monitoring/
    MonitorService.cs     builds a StatusDto snapshot from ServiceStatus + CsvInbox + DB
    StatusModels.cs       StatusDto/BtServerDto/ClientDto/ReceivedCsvDto/LogsDto (UI + /api/status)
```

Dashboard layout: left-top = Bluetooth clients + server state; **left-bottom = list of received
CSV uploads** (one row per Bluetooth frame, informational only now); **right = the FULL LOG OF ONE
DAY** for one CSV type — driven by a **date picker (default today)** + a **type radio (default
monitor)**, NOT by the CSV selected on the left. `MonitorService.GetDayLogAsync(typeKey, date)`
aggregates the rows of **all** uploads of that type whose `LogDate` matches the day (rows from
multiple uploads concatenated; columns = the type's row-1 header; identical rows colour-coded). The
day comes from each upload's filename date (`CsvUpload.LogDate`; legacy uploads without a date fall
back to the received-local date).

**Localized UI (EN / JA).** All dashboard text goes through `UI/Localization.cs` (`Loc.T(key)`);
a language combo in the toolbar switches at runtime (`Loc.Changed` → `ApplyTexts`). First-run
language follows the OS UI culture (ja → Japanese, else English); the choice persists to
`%LOCALAPPDATA%/LeontecSyncLogSystem/ui-language.txt`. (Vietnamese was dropped from the PC tool;
if `ui-language.txt` still holds a stale `Vi`, it no longer parses and falls back to OS detection.)
The Android app still offers three languages via `values/` (English, default/fallback) +
`values-vi/` + `values-ja/` string resources and an in-app language picker (`LocaleHelper` +
`SyncConfig.appLanguage`, default "system" = follow device locale).

Targets **`net10.0-windows10.0.19041.0`** (the Win10 build is required by 32feet.NET's WinRT
RFCOMM server) with `<RollForward>LatestMajor</RollForward>`. **Why net10, not net8:** the box only
has `Microsoft.AspNetCore.App` as **10.x** (no 8.x), and the WinForms **Designer**
(`DesignToolsServer.exe`) loads the project's `FrameworkReference` and demands that exact major — a
net8 target made the designer keep prompting *"install AspNetCore.App 8.0"*, and installing it added
a stray .NET 8 runtime that then broke `dotnet run` (`NETCore.App 8.0.28` vs ASP.NET-10's `10.0.9` →
hostfxr 0x8000809C). All three shared frameworks are installed at **10.0.9** (NETCore / WindowsDesktop
/ AspNetCore), so net10 makes **build + run + designer** all use the installed runtime — no prompts,
no conflict. **Do NOT accept the designer's "install .NET 8" prompt.** Kestrel inside the WinForms
app comes from `<FrameworkReference Include="Microsoft.AspNetCore.App" />` plus a few `<Using>` items
that replace the implicit usings the Web SDK would otherwise provide.

## Data contract — typed CSV over Bluetooth

Each Bluetooth frame is `STX + (filename\r\n + CSV) + ETX`. The **first line is the upload
filename** `{type}_{yyyyMMdd}_{termId}_{index}.txt` (e.g. `monitor_log_20260622_GalaxyS10_3.txt`):
`yyyyMMdd` = **the log day** (Android defaults to today), then **`termId` (device name, spaces
stripped) BEFORE the numeric `index`** (per-type send counter). `CsvTypes.ParseFilename` anchors on
the 8-digit date and takes the **trailing `_<digits>` as the index** (regex
`^(type)_(\d{8})_(term)_(\d+)$`); the old `{type}__{index}__{termId}.csv` (double-underscore, no
date) is still parsed for backward compat. PC strips the filename line (`CsvTypes.IsFilenameLine`),
stores the date in `CsvUpload.LogDate`, then detects the **type from the CSV's row-1 header**
(authoritative) — see `Services/CsvTypes.cs`.

CSV log types (canonical headers — keep Android writer + PC parser in sync):
- **`monitor_log` (モニタリスト, 9 cols)** `開始時刻,終了時刻,入出庫伝票番号,顧客コード,品目コード,箱数,数量,積込箱数,状態`
  (`積込箱数` before the trailing `状態`; `状態` code 0=正常/9=削除) → `MonitorEntries` (`LoadedBoxes`).
  Old 8-col layout (no `積込箱数`) still parsed for backward compat.
- **`pallet_log` (パレット, 7 cols)** `開始時刻,終了時刻,PLNo.,顧客,納入便,品目明細 (品目コード:箱数x数量),状態`
  (`状態` 0=正常/1=移動/9=削除) → `PalletOps` + `PalletOpItems` (品目明細 = space-separated `code:boxesxqty`).
- **`direct_log` (直送管理, 11 cols)** `開始時刻,終了時刻,顧客,納入先,出荷日,品番,収容数,箱数,納入数,工場コード,ヨコオ品番`
  (1 row = 1 completed 照合; no 状態 column) → `DirectEntries`.

  (The old `legacy` scan format — header `id`/`LogId`, table `SyncLogs` — has been **removed**. A CSV
  whose header matches none of the three types is stored as `Type = "unknown"` with only its `RawCsv`.)

**Per-type display filter (right panel, `MonitorService.ApplyDisplayFilter`):**
- monitor: hide rows with `状態 == 9`; show the rest.
- direct: show all.
- pallet: key = (`PLNo.`, `顧客`, `納入便`); hide `状態 == 9`; among surviving rows (状態 0/1) of the
  same key show only the one with the **latest `終了時刻`**.

**Supersede:** a newer `index` for the same `(termId, type)` marks older `CsvUploads`
`Superseded=true`. The per-day right panel aggregates ALL uploads of the day (incl. superseded) then
applies the display filter above. Columns come from the CSV's own row 1 (dynamic per type). An
**Export CSV** button (right of the day-log filter) writes the currently-shown (filtered) day-log to a
file — it serializes the exact `DataTable` the grid is bound to, so grid and file never drift. The
left-bottom CSV list is also filtered by the date picker (by filename date `LogDate`). The toolbar is
a red **Reset** button (= `ClearAllAsync`), an **Open backup folder** button (`OpenBackupFolder` →
opens `ICsvBackupWriter.Root` in Explorer), status labels, and the language combo (the old
Refresh/Export toolbar buttons were removed; the grid auto-refreshes every 2s). Toolbar totals
("Logs today | Total") count `SUM(CsvUploads.RowCount)` of non-superseded uploads.

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
   - **Heartbeat (liveness) — PC-only, no longer used.** The PC still routes a frame starting with
     `PING` as a heartbeat and replies `PONG,<radioName>,<epochMillis>` (`ServiceStatus.HeartbeatTimeout`
     = 15 s), but the current `shipment_support` app **does not send PING** — the old 5 s ping /
     "Listener OK" feature was dropped. The PONG code is kept only for backward compatibility.
     Heartbeats never touch the DB and don't count as data frames/sessions. See `docs/04 §4.1`.

2. **Wi-Fi backup (secondary — parked).** The old `SyncLogs` app posted a **JSON array** of JobLog
   (Gson) to `http://<ip>:8080/api/sync`; the new `shipment_support` app **has no Wi-Fi channel**
   (Bluetooth only). The PC still exposes `POST /api/sync` but it returns **501 Not Implemented**
   (the legacy CSV parser/ingest was removed with the `SyncLogs` table). When re-enabling Wi-Fi:
   wire up typed-CSV ingest into `CsvUploads` (via `ICsvStore`) and bind Kestrel on 8080.

**On-disk backup.** After each received CSV is persisted to the DB, `CsvBackupWriter` also writes a
raw copy to `Sync:BackupFolder/<yyyyMMdd>/<filename>` (default
`%LOCALAPPDATA%/LeontecSyncLogSystem/backup`; resolved path logged at startup). `<filename>` = the
phone's upload name (rebuilt as `{type}_{yyyyMMdd}_{termId}_{index}.txt` if the wire frame had none);
written atomically (temp + move), UTF-8 no BOM, faithful to the bytes received. **Best-effort** — a
backup failure is logged (Warning) and swallowed so it never fails ingestion (the row is already in
the DB). A re-send of the same upload index overwrites idempotently.

## HTTP endpoints (Kestrel, in-process — used for monitoring; Wi-Fi ingest parked)

- `GET  /api/status` — full monitoring snapshot (also what the dashboard shows in-process)
- `GET  /health` — `{"status":"ok"}`
- `POST /api/sync` — returns 501 Not Implemented (Wi-Fi ingest parked; legacy CSV path removed)

## Build / run

```bash
# One-time (per checkout): fetch the bundled MariaDB (NOT committed to git):
pwsh scripts/fetch-mariadb.ps1                            # stages LeontecSyncLogSystem/mariadb/
dotnet build LeontecSyncLogSystem.slnx -c Release
dotnet run --project LeontecSyncLogSystem -c Release      # starts embedded DB + host + dashboard
# exe: LeontecSyncLogSystem/bin/Release/net10.0-windows10.0.19041.0/LeontecSyncLogSystem.exe
```

For Bluetooth to work the phone must be **bonded** to this PC and the app's `pcBluetoothName`
must match (leniently) the PC's Bluetooth radio name (shown live in the dashboard header).
Kestrel bind defaults to `http://0.0.0.0:8090` (`appsettings.json` → `Kestrel`); override for
a test with `Kestrel__Endpoints__Http__Url=http://127.0.0.1:8099 dotnet run ...`.
Schema is created/updated at startup via **EF Core migrations** (`db.Database.Migrate()`).

## Conventions / gotchas

- **Database = bundled MariaDB (MySQL-compatible), embedded.** SQLite/SqlServer/Postgres were
  removed — the app is MySQL-only via **Pomelo.EntityFrameworkCore.MySql** + snake_case naming
  (`UseSnakeCaseNamingConvention`). By default (`Database:Embedded=true`) the app runs its OWN MariaDB
  shipped under `<app>/mariadb/` as a child process (`Services/EmbeddedMariaDbServer.cs`) on a private
  loopback port (`Database:EmbeddedPort`, default **3307**) with data in `%LOCALAPPDATA%/
  LeontecSyncLogSystem/db-data` — so a target PC needs NO separate MySQL install. First run initializes
  the data dir (`mariadb-install-db --allow-remote-root-access`); the server is shut down gracefully on
  exit. Set `Database:Embedded=false` + `ConnectionString` to use an external MySQL/MariaDB instead.
  The `mariadb/` binaries are **~260 MB and NOT committed** (`.gitignore`) — run
  `scripts/fetch-mariadb.ps1` to stage them; the csproj copies them next to the exe (PreserveNewest).
- **Schema via EF Core migrations** (`Data/Migrations/`, applied by `db.Database.Migrate()` at
  startup). To change the schema: edit the entities/`AppDbContext`, then
  `dotnet ef migrations add <Name> --project LeontecSyncLogSystem --output-dir Data\Migrations`
  (a `Data/DesignTimeDbContextFactory.cs` lets the tools build the context without booting the app;
  it uses an explicit MySQL version so `migrations add` needs no live DB). Never hand-edit an applied
  migration — add a new one.
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
