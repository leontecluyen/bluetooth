using System.IO;

namespace LeontecSyncLogSystem.Services
{
    /// <summary>
    /// Writes a faithful copy of every received CSV upload to a backup folder on disk, so the raw
    /// files survive independently of the database. Files are grouped per log day:
    /// <c>&lt;root&gt;/&lt;yyyyMMdd&gt;/&lt;filename&gt;</c> (the filename is the phone's upload name, e.g.
    /// <c>monitor_log_20260622_GalaxyS10_3.txt</c>). Best-effort: a backup failure is logged and
    /// swallowed so it can never fail ingestion (the row is already persisted in the DB).
    /// </summary>
    public interface ICsvBackupWriter
    {
        /// <summary>The resolved backup root folder (for logging at startup).</summary>
        string Root { get; }

        /// <summary>
        /// Persist one upload's CSV body to the backup folder. <paramref name="filename"/> is the
        /// upload filename from the wire envelope; when empty it is rebuilt from the other fields.
        /// Returns the path written, or <c>null</c> if the backup was skipped/failed.
        /// </summary>
        Task<string?> SaveAsync(
            string filename, string csv, CsvType type, string termId, int index, DateOnly? logDate,
            CancellationToken token);
    }

    /// <inheritdoc cref="ICsvBackupWriter"/>
    public sealed class CsvBackupWriter : ICsvBackupWriter
    {
        private readonly ILogger<CsvBackupWriter> _logger;

        public CsvBackupWriter(string root, ILogger<CsvBackupWriter> logger)
        {
            Root = root;
            _logger = logger;
        }

        public string Root { get; }

        public async Task<string?> SaveAsync(
            string filename, string csv, CsvType type, string termId, int index, DateOnly? logDate,
            CancellationToken token)
        {
            try
            {
                var day = logDate ?? DateOnly.FromDateTime(DateTime.Now);
                var name = Sanitize(string.IsNullOrWhiteSpace(filename)
                    ? $"{CsvTypes.TypeKey(type)}_{day:yyyyMMdd}_{termId}_{index}.txt"
                    : filename);

                var dir = Path.Combine(Root, day.ToString("yyyyMMdd"));
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, name);

                // A re-send of the same upload (same filename) carries identical content, so an
                // overwrite stays idempotent. Write to a temp file then move so a crash mid-write
                // never leaves a half-written backup.
                var tmp = path + ".tmp";
                await File.WriteAllTextAsync(tmp, csv, new System.Text.UTF8Encoding(false), token);
                File.Move(tmp, path, overwrite: true);

                _logger.LogDebug("Backed up upload to {Path} ({Bytes} bytes).", path, csv.Length);
                return path;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                // Backup is a convenience; the upload is already in the DB. Never fail ingestion.
                _logger.LogWarning(ex, "Failed to back up upload '{File}' to {Root}.", filename, Root);
                return null;
            }
        }

        /// <summary>Strip characters that are illegal in a Windows filename.</summary>
        private static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }
    }
}
