[START OF ANDROID PROMPT]
Context: You are an expert Android Developer. Please generate the complete source code for an Enterprise-grade Offline-First Log Sync module using Kotlin, Jetpack Room, and WorkManager.

Requirements & Architecture:

1. Local Database (Room):
   - Create an Entity `JobLog` with fields: `id` (UUID String, Primary Key), `workerId` (String), `jobType` (String: 検品/出荷/直送), `barcodeData` (String), `startTime` (Long), `endTime` (Long), `syncStatus` (String: PENDING/SUCCESS), `syncMethod` (String: BLUETOOTH/WIFI).

2. Primary Synchronization Layer (Bluetooth SPP - RFCOMM):
   - Target Architecture: Bluetooth Serial Port Profile (SPP).
   - Logic: Query all rows from Room where `syncStatus = 'PENDING'`. Serialize the list into a **CSV file payload** (NOT JSON, NOT plain log text). The CSV has a header row followed by one row per log: `id,workerId,jobType,barcodeData,startTime,endTime`. Fields are RFC-4180 quoted/escaped so barcode data containing commas, quotes, or newlines cannot corrupt the columns.
   - Packet Framing: To prevent data corruption or merging over Serial COM ports, wrap the CSV string with standard delimiters: `[STX]` (Start of Text, 0x02) at the beginning and `[ETX]` (End of Text, 0x03) at the end. These are the real control bytes 0x02/0x03, not the literal text "[STX]"/"[ETX]".
   - Execution: Open an `BluetoothSocket` using the standard SerialPortServiceClass_UUID (`00001101-0000-1000-8000-00805F9B34FB`). Stream the framed CSV byte array to the pre-paired PC. If successful, update the local database rows to `SUCCESS` and `BLUETOOTH`.

3. Secondary Backup Layer (Wi-Fi REST API):
   - If the Bluetooth socket connection fails (catch IOException), fall back to sending the data via an HTTP POST request to the local PC's endpoint (`http://<PC_IP>:8080/api/sync`). The REST layer sends a JSON array body (standard for HTTP APIs); only the Bluetooth SPP layer uses the CSV payload.

4. Automation Triggers:
   - Geofencing Service: Implement a Google Play Services Geofencing component. Define a 50m radius around the office coordinates. Upon `GEOFENCE_TRANSITION_ENTER`, programmatically enable Bluetooth hardware (`BluetoothAdapter.enable()`) without user intervention.
   - WorkManager Background Worker: Wrap the entire synchronization logic inside a `CoroutineWorker`. Configure it with `BackoffPolicy.EXPONENTIAL` (initial delay 10 seconds) for automatic retries if both connection layers fail.

Please generate the Room Entity, the Room DAO, and the complete WorkManager Worker class implementation in Kotlin.
[END OF ANDROID PROMPT]
Khi code và sửa gì đó phải cập nhật docs và lưu cái quan trọng vào memory cũng như claude.md
Lưu ý: tât cả code phải viết logs để debug dễ dàng
