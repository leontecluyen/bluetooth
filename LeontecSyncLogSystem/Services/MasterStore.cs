using System.IO;
using System.Text;

namespace LeontecSyncLogSystem.Services
{
    /// <summary>The two master files the app owns and can push back to the phones.</summary>
    public enum MasterKind
    {
        /// <summary>顧客マスタ — customer_master.csv (デポ納入先,顧客コード,納入先,納入便).</summary>
        Customer,
        /// <summary>品目マスタ — item_master.csv (品目コード,品目名称,箱種,品目名称_2).</summary>
        Item,
    }

    /// <summary>
    /// One master file's on-disk state: its raw CSV text plus a content version (see
    /// <see cref="MasterStore.Version"/>).
    /// </summary>
    public sealed record MasterFile(MasterKind Kind, string FileName, string Csv);

    /// <summary>
    /// Owns the two editable master CSVs (customer + item) that the PC is the source of truth for.
    /// Files live under <c>&lt;root&gt;/{customer,item}_master.csv</c> (default
    /// <c>%LOCALAPPDATA%/LeontecSyncLogSystem/master</c>). On first run each missing file is seeded
    /// from the copy bundled next to the exe (<c>master-seed/</c>), which the build links straight
    /// from the Android app's assets (<c>shipment_support/.../assets</c>) — the single source of
    /// truth for both masters — so the operator starts from the exact data the phone ships with.
    ///
    /// Stored as UTF-8 (no BOM) to match the phone's asset files exactly, so a later reverse-sync
    /// (Giai đoạn 2) can stream the bytes unchanged. A <see cref="Version"/> (SHA-256 over both
    /// files) lets the phone skip re-import when nothing changed.
    /// </summary>
    public interface IMasterStore
    {
        /// <summary>The resolved master folder (for logging / "open folder").</summary>
        string Root { get; }

        /// <summary>Canonical file name for a kind (e.g. <c>customer_master.csv</c>).</summary>
        string FileName(MasterKind kind);

        /// <summary>The expected row-1 header for a kind (used to validate + label the grid).</summary>
        string Header(MasterKind kind);

        /// <summary>Load one master's raw CSV text (seeding from the bundled copy on first use).</summary>
        MasterFile Load(MasterKind kind);

        /// <summary>Overwrite one master's CSV text atomically (temp + move), UTF-8 no BOM.</summary>
        void Save(MasterKind kind, string csv);

        /// <summary>
        /// The master file's last-modified time as Unix epoch milliseconds (0 if it doesn't exist
        /// yet). The reverse-sync compares this against the phone's file last-modified time — a more
        /// recent PC time means the phone should pull the file.
        /// </summary>
        long LastModifiedUnixMillis(MasterKind kind);
    }

    /// <inheritdoc cref="IMasterStore"/>
    public sealed class MasterStore : IMasterStore
    {
        private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

        private readonly string _seedRoot;
        private readonly ILogger<MasterStore> _logger;
        private readonly object _gate = new();

        public MasterStore(string root, string seedRoot, ILogger<MasterStore> logger)
        {
            Root = root;
            _seedRoot = seedRoot;
            _logger = logger;
            Directory.CreateDirectory(Root);
            _logger.LogInformation(
                "Master folder: {Root} (seed source: {Seed}).", Root, _seedRoot);
        }

        public string Root { get; }

        public string FileName(MasterKind kind) => kind switch
        {
            MasterKind.Customer => "customer_master.csv",
            MasterKind.Item => "item_master.csv",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        public string Header(MasterKind kind) => kind switch
        {
            // Keep in sync with the Android asset headers (ItemMasterDBO validates its header prefix).
            MasterKind.Customer => "デポ納入先,顧客コード,納入先,納入便",
            MasterKind.Item => "品目コード,品目名称,箱種,品目名称_2",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        public MasterFile Load(MasterKind kind)
        {
            var name = FileName(kind);
            var path = Path.Combine(Root, name);
            lock (_gate)
            {
                if (!File.Exists(path))
                    SeedFromBundle(kind, path);

                var csv = File.Exists(path) ? File.ReadAllText(path, Utf8NoBom) : Header(kind) + "\r\n";
                _logger.LogDebug("Loaded master {Kind} from {Path} ({Bytes} bytes).", kind, path, csv.Length);
                return new MasterFile(kind, name, csv);
            }
        }

        public void Save(MasterKind kind, string csv)
        {
            var name = FileName(kind);
            var path = Path.Combine(Root, name);
            lock (_gate)
            {
                Directory.CreateDirectory(Root);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, csv, Utf8NoBom);
                // net48 has no File.Move(overwrite) — delete the destination first.
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            _logger.LogInformation("Saved master {Kind} to {Path} ({Bytes} bytes).", kind, path, csv.Length);
        }

        public long LastModifiedUnixMillis(MasterKind kind)
        {
            var path = Path.Combine(Root, FileName(kind));
            lock (_gate)
            {
                // Seed a missing file first so a fresh checkout still reports a real timestamp.
                if (!File.Exists(path))
                    SeedFromBundle(kind, path);
                if (!File.Exists(path))
                    return 0;
                var utc = File.GetLastWriteTimeUtc(path);
                return new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeMilliseconds();
            }
        }

        /// <summary>
        /// Copy the bundled seed CSV (next to the exe) into the master folder on first use. If the
        /// bundle is missing (e.g. a dev checkout without the copy step), write a header-only file so
        /// the grid still opens and the operator can enter rows by hand.
        /// </summary>
        private void SeedFromBundle(MasterKind kind, string destPath)
        {
            var name = FileName(kind);
            var seed = Path.Combine(_seedRoot, name);
            try
            {
                if (File.Exists(seed))
                {
                    File.Copy(seed, destPath, overwrite: false);
                    _logger.LogInformation("Seeded master {Kind} from bundle {Seed}.", kind, seed);
                }
                else
                {
                    File.WriteAllText(destPath, Header(kind) + "\r\n", Utf8NoBom);
                    _logger.LogWarning(
                        "Master seed {Seed} not found — created header-only {Kind} master.", seed, kind);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to seed master {Kind} into {Path}.", kind, destPath);
            }
        }
    }
}
