# SyncLogs — Offline-First Log Sync (Android)

Enterprise offline-first module for handheld warehouse devices. Operators capture
job logs locally (Room); a background `WorkManager` job pushes pending rows to a PC
over **Bluetooth SPP (primary)** and falls back to **Wi-Fi REST (secondary)**.

## Architecture

| Layer | Component | Notes |
|-------|-----------|-------|
| Storage | `data/DeviceJobLog`, `DeviceJobLogDao`, `AppDatabase`, `Converters` | Room. Enums persisted via `@TypeConverters`. |
| Payload | `sync/LogPayloadSerializer` | **CSV** (RFC-4180) for Bluetooth; JSON for Wi-Fi. |
| Bluetooth (primary) | `bluetooth/BluetoothSyncManager` | RFCOMM/SPP. Frame = `STX(0x02) + CSV + ETX(0x03)`. |
| Wi-Fi (fallback) | `network/SyncApiService`, `RetrofitFactory` | `POST http://<PC_IP>:8080/api/sync` (JSON). |
| Orchestration | `worker/SyncWorker`, `SyncScheduler` | `CoroutineWorker`; `BackoffPolicy.EXPONENTIAL`, 10 s initial. |
| Automation | `geofence/GeofenceManager`, `GeofenceBroadcastReceiver` | 50 m office geofence; ENTER → enable BT + sync. |
| Config | `config/SyncConfig` | SharedPreferences: PC name / IP / port / geofence coords. |
| UI | `MainActivity`, `ui/MainViewModel` | Compose. Permissions + manual triggers. |

## Sync flow

1. `SyncWorker` reads rows where `syncStatus = PENDING` (ordered, stable).
2. **Bluetooth SPP** first: serialize → CSV → `STX…ETX` frame → stream to bonded PC.
3. If Bluetooth fails (not bonded / disabled / `IOException`) → **Wi-Fi REST** JSON POST.
4. On success: batch `UPDATE` rows to `SUCCESS` + record `syncMethod`; prune synced rows > 7 days.
5. If both fail: `Result.retry()` → exponential backoff (10s, 20s, 40s, …).

## Important platform constraints

- **Silent Bluetooth enable** (`BluetoothAdapter.enable()`) on geofence ENTER only works on
  **Android ≤ 12 (API 32)**. On Android 13+ it is a deprecated no-op; the OS requires a user
  prompt (`ACTION_REQUEST_ENABLE`). Implemented: geofence ENTER tries the silent enable, and if
  it can't, `BluetoothEnableNotifier` posts a tap-to-enable notification → opens `MainActivity` →
  shows the `ACTION_REQUEST_ENABLE` system popup (the dialog can only be shown from an Activity).
- **Geofencing while backgrounded** needs `ACCESS_BACKGROUND_LOCATION` (API 29+), which the user
  must grant separately ("Allow all the time"). Foreground location alone won't deliver ENTER events.
- **Cleartext HTTP**: permitted via `res/xml/network_security_config.xml` because this is a
  LAN-only device. Move the PC endpoint to HTTPS for production hardening.

## Configuration

Defaults live in `SyncConfig` (PC name `SyncLog-Server`, IP `192.168.1.100:8080`, geofence at a
placeholder coordinate). Override at runtime via the `SyncConfig` setters (SharedPreferences-backed).

## Build / test

```sh
./gradlew :app:assembleDebug          # build
./gradlew :app:testDebugUnitTest      # unit tests (CSV serializer)
```
