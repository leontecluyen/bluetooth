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
  appsettings.json      Sync (BluetoothServiceName), Database (provider+connstr), Kestrel
  Models/LogEntry.cs    DB entity (also the parse target)
  Data/AppDbContext.cs  EF Core context; table SyncLogs, PK = LogId
  Services/
    SyncOptions.cs        SyncOptions (BluetoothServiceName) + DatabaseOptions
    CsvLogParser.cs       CSV -> LogEntry (pure, testable; accepts 'id' or 'LogId' header)
    FrameDecoder.cs       STX/ETX byte framing (pure, testable)
    LogIngestService.cs   dedup-aware insert (ON CONFLICT DO NOTHING equivalent)
    BluetoothSppServer.cs  32feet.NET RFCOMM SPP server, multi-client accept loop
    CsvInbox.cs           in-memory, capped list of received CSV uploads (+ their rows)
    ServiceStatus.cs      thread-safe live state (BT clients + server status), singleton
  Monitoring/
    MonitorService.cs     builds a StatusDto snapshot from ServiceStatus + CsvInbox + DB
    StatusModels.cs       StatusDto/BtServerDto/ClientDto/ReceivedCsvDto/LogDto (UI + /api/status)
```

Dashboard layout: left-top = Bluetooth clients + server state; **left-bottom = list of received
CSV uploads** (one row per Bluetooth frame, informational only now); **right = the FULL LOG OF ONE
DAY** for one CSV type — driven by a **date picker (default today)** + a **type radio (default
monitor)**, NOT by the CSV selected on the left. `MonitorService.GetDayLogAsync(typeKey, date)`
aggregates the rows of **all** uploads of that type whose `LogDate` matches the day (rows from
multiple uploads concatenated; columns = the type's row-1 header; identical rows colour-coded). The
day comes from each upload's filename date (`CsvUpload.LogDate`; legacy uploads without a date fall
back to the received-local date).

**Localized UI (EN / VI / JA).** All dashboard text goes through `UI/Localization.cs` (`Loc.T(key)`);
a language combo in the toolbar switches at runtime (`Loc.Changed` → `ApplyTexts`). First-run
language follows the OS UI culture (vi/ja → that, else English); the choice persists to
`%LOCALAPPDATA%/LeontecSyncLogSystem/ui-language.txt`. The Android app mirrors this with `values/`
(English, default/fallback) + `values-vi/` + `values-ja/` string resources and an in-app language
picker (`LocaleHelper` + `SyncConfig.appLanguage`, default "system" = follow device locale).

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
- **`monitor_log` (モニタリスト, 8 cols)** `開始時刻,終了時刻,入出庫伝票番号,顧客コード,品目コード,箱数,数量,状態`
  (`状態` code 0=正常/9=削除) → `MonitorEntries`.
- **`pallet_log` (パレット, 7 cols)** `開始時刻,終了時刻,PLNo.,顧客,納入便,品目明細 (品目コード:箱数x数量),状態`
  (`状態` 0=正常/1=移動/9=削除) → `PalletOps` + `PalletOpItems` (品目明細 = space-separated `code:boxesxqty`).
- **`direct_log` (直送管理, 11 cols)** `開始時刻,終了時刻,顧客,納入先,出荷日,品番,収容数,箱数,納入数,工場コード,ヨコオ品番`
  (1 row = 1 completed 照合; no 状態 column) → `DirectEntries`.
- **`legacy`** header `id`/`LogId` → dedup into `SyncLogs` by LogId (old scan format; still supported).

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
just a red **Reset** button (= `ClearAllAsync`), status labels, and the language combo (the old
Refresh/Export toolbar buttons were removed; the grid auto-refreshes every 2s). Toolbar totals
("Logs today | Total") count `SUM(CsvUploads.RowCount)` of non-superseded uploads, not the empty
legacy `SyncLogs` table.

DB tables (relational, all survive restart):
- `SyncLogs` — canonical **dedup'd** legacy logs (`LogId Guid PK`, …).
- `Devices` (PK `Address`) — BT device roster + counters (`DeviceStore`, seeded offline on startup).
- `CsvUploads` (PK `Id`, FK `DeviceAddress`→Devices, + `Type`/`TermId`/`UploadIndex`/`Superseded`/
  `LogDate`, keeps `RawCsv`) — one row per sync (`CsvStore`). `LogDate` (date, from the filename's
  `yyyyMMdd`; null on legacy uploads) drives the dashboard's per-day filter; index on `(Type, LogDate)`.
- `MonitorEntries`, `PalletOps`, `PalletOpItems`, `DirectEntries` — normalized rows of typed CSVs
  (FK→CsvUploads / PalletOps, cascade). Relation: **CsvUpload 1—* entries/ops 1—* items**.
  (Display/export read `RawCsv`, not these — they're the queryable normalized copy.)

## Ingestion channels

> The protocol is defined by the Android app (`SyncLogs/`). Read its source before changing
> the wire format — `bluetooth/BluetoothSyncManager.kt`, `sync/LogPayloadSerializer.kt`,
> `network/SyncApiService.kt`, `config/SyncConfig.kt`, `data/JobLog.kt`.

1. **Bluetooth SPP (primary, the focus).** The PC is the **RFCOMM SPP _server_**; the phone
   is the client. Implemented with **32feet.NET** (`InTheHand.Net.Bluetooth`) in
   `Services/BluetoothSppServer.cs` — a `BluetoothListener` on UUID
   `00001101-0000-1000-8000-00805F9B34FB` that accepts clients in a loop, each serviced on
   its own task ⇒ **multiple devices concurrently**. NOT COM ports (the earlier COM approach
   was wrong — that's why no COM ever appeared).
   - The phone finds the PC by **Bluetooth radio name** = `SyncConfig.pcBluetoothName`
     (default `"LUYEN"`), then connects to the SPP UUID and writes `STX + CSV + ETX`.
   - **Name-match gotcha:** the dev PC's radio name is `"LUYEN - Front"`, not `"LUYEN"`. The
     app now matches leniently (case-insensitive, contains-either-way) and logs all bonded
     devices — see the edit in `BluetoothSyncManager.sync()`.
   - CSV per `LogPayloadSerializer`: header `id,workerId,jobType,barcodeData,startTime,endTime`,
     CRLF lines, RFC-4180 quoting, times = **epoch millis**, jobType is Japanese (検品/出荷/直送).
   - `FrameDecoder` strips STX/ETX (per-connection); `CsvLogParser.ParseDocument` parses the
     whole CSV (header `id` recognised); each client is tagged with its `WorkerId`.
   - **Heartbeat (liveness).** Same SPP channel + STX/ETX framing carries control frames: the
     phone sends `PING,<deviceName>,<epochMillis>` every **5 s** (only while the app is
     foregrounded, and **skipped while a sync is transmitting** — `BluetoothSyncManager.syncInProgress`)
     and the PC replies `PONG,<radioName>,<epochMillis>`. PC routes a frame as a heartbeat if it
     starts with `PING` (CSV always starts with `id`/`LogId`). PC marks a device **Offline**
     after **15 s** with no contact (`ServiceStatus.HeartbeatTimeout`, = 3 missed pings); the
     Android `ListenerCard` shows **Listener OK** when it gets a `PONG` within 3 s. Heartbeats
     never touch the DB and don't count as data frames/sessions. See `docs/04 §4.1`.

2. **Wi-Fi backup (secondary — currently parked).** The app posts a **JSON array** of JobLog
   (Gson) to `http://<ip>:8080/api/sync`. The PC still exposes `POST /api/sync` but it
   currently parses CSV, so JSON Wi-Fi ingest is NOT wired up yet. When re-enabling Wi-Fi:
   add JSON-array parsing (map `id`→LogId, epoch→DateTime) and bind Kestrel on 8080.

Dedup (`LogIngestService`) = provider-agnostic `ON CONFLICT (LogId) DO NOTHING`: collapse
in-batch dups → drop existing LogIds → row-by-row retry on a concurrent unique-key clash.

## HTTP endpoints (Kestrel, in-process — used for monitoring; Wi-Fi ingest parked)

- `GET  /api/status` — full monitoring snapshot (also what the dashboard shows in-process)
- `GET  /health` — `{"status":"ok"}`
- `POST /api/sync` — exists but parses CSV; Wi-Fi (JSON) ingest not wired yet

## Build / run

```bash
dotnet build LeontecSyncLogSystem.slnx -c Release
dotnet run --project LeontecSyncLogSystem -c Release      # starts host + opens dashboard
# exe: LeontecSyncLogSystem/bin/Release/net10.0-windows10.0.19041.0/LeontecSyncLogSystem.exe
```

For Bluetooth to work the phone must be **bonded** to this PC and the app's `pcBluetoothName`
must match (leniently) the PC's Bluetooth radio name (shown live in the dashboard header).
Kestrel bind defaults to `http://0.0.0.0:8090` (`appsettings.json` → `Kestrel`); override for
a test with `Kestrel__Endpoints__Http__Url=http://127.0.0.1:8099 dotnet run ...`.
Schema auto-created at startup (`EnsureCreated`).

## Conventions / gotchas

- DB provider switchable (`Database:Provider` = Sqlite | SqlServer | Postgres); default
  SQLite (`synclogs.db`, zero-config). `synclogs.db*` are local test artifacts — don't commit.
- **Schema is created via `EnsureCreated` (no migrations).** `EnsureCreated` does NOT alter an
  existing `synclogs.db` when the model gains a column. For SQLite, `Program.cs` startup runs
  idempotent `ALTER TABLE … ADD COLUMN` / `CREATE … IF NOT EXISTS` statements (e.g. `LogDate` was
  added this way) so existing dev DBs keep working without data loss — **add new columns there too**.
  A real migration story is still needed before any persistent SqlServer/Postgres deployment.
- **UI text is localized** via `UI/Localization.cs` (`Loc.T`) on PC and `values*/strings.xml` on
  Android — add new strings as keys, never hard-code user-facing text. EN is the fallback.
- `StatusModels.cs` DTOs are both the UI grid binding source and the `/api/status` JSON shape.
- `FrameDecoder` + `CsvLogParser` are pure and hardware-free testable (a scratch harness
  validated the exact Android frame: header `id`, epoch millis, 検品/出荷/直送, RFC-4180, split chunks).
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
