using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;

namespace MediaTagger.Services
{
    /// <summary>
    /// Persists generated thumbnails to disk so reopening a folder doesn't mean regenerating
    /// every one from scratch. Mirrors the Duplicate Finder's fingerprint cache: same SQLite
    /// dependency (already in the project, MIT licensed, no commercial restriction), same
    /// "safe to delete, the next thumbnail just takes longer" design.
    ///
    /// Freshness is checked against the source file's size and last-write time - if either has
    /// changed since caching, the entry is treated as a miss and silently overwritten once a
    /// fresh thumbnail is made, so an edited file is never shown a stale one.
    /// </summary>
    public sealed class ThumbnailCacheStore
    {
        private readonly string _connectionString;

        public ThumbnailCacheStore(string databasePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

            var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = true
            }.ToString();
        }

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

            var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;

                CREATE TABLE IF NOT EXISTS Thumbnails (
                    FilePath        TEXT    NOT NULL PRIMARY KEY,
                    FileSizeBytes   INTEGER NOT NULL,
                    LastModifiedUtc INTEGER NOT NULL,
                    ImageBytes      BLOB    NOT NULL,
                    GeneratedUtc    INTEGER NOT NULL
                );
                """;

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Returns the cached thumbnail if present and still fresh, else null. Never
        /// throws - a cache problem should look exactly like a cache miss to the caller.</summary>
        public async Task<BitmapSource?> TryGetAsync(string filePath, CancellationToken cancellationToken = default)
        {
            try
            {
                var info = new FileInfo(filePath);
                if (!info.Exists) return null;

                await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
                var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT ImageBytes, FileSizeBytes, LastModifiedUtc
                    FROM Thumbnails WHERE FilePath = $path;
                    """;
                command.Parameters.AddWithValue("$path", filePath);

                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;

                bool stillFresh = reader.GetInt64(1) == info.Length
                                   && reader.GetInt64(2) == info.LastWriteTimeUtc.Ticks;
                if (!stillFresh) return null; // StoreAsync overwrites this once a fresh one exists

                var bytes = (byte[])reader[0];
                return DecodeBitmap(bytes);
            }
            catch
            {
                // Missing table, a locked file, a corrupt row - any of these should just fall
                // through to generating the thumbnail fresh, not surface as an error.
                return null;
            }
        }

        public async Task StoreAsync(string filePath, BitmapSource thumbnail, CancellationToken cancellationToken = default)
        {
            try
            {
                var info = new FileInfo(filePath);
                if (!info.Exists) return;

                var bytes = EncodeBitmap(thumbnail);

                await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
                var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO Thumbnails (FilePath, FileSizeBytes, LastModifiedUtc, ImageBytes, GeneratedUtc)
                    VALUES ($path, $size, $modified, $bytes, $generated)
                    ON CONFLICT(FilePath) DO UPDATE SET
                        FileSizeBytes = excluded.FileSizeBytes,
                        LastModifiedUtc = excluded.LastModifiedUtc,
                        ImageBytes = excluded.ImageBytes,
                        GeneratedUtc = excluded.GeneratedUtc;
                    """;
                command.Parameters.AddWithValue("$path", filePath);
                command.Parameters.AddWithValue("$size", info.Length);
                command.Parameters.AddWithValue("$modified", info.LastWriteTimeUtc.Ticks);
                command.Parameters.AddWithValue("$bytes", bytes);
                command.Parameters.AddWithValue("$generated", DateTime.UtcNow.Ticks);

                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // The thumbnail is already showing on screen either way; a failed cache write
                // just means it gets regenerated next time, which is a cost, not a failure.
            }
        }

        private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
        {
            var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }

        /// <summary>JPEG rather than PNG: these are small preview images, mostly photographic,
        /// where JPEG's size advantage matters far more than PNG's losslessness.</summary>
        private static byte[] EncodeBitmap(BitmapSource bitmap)
        {
            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }

        private static BitmapSource DecodeBitmap(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes);
            var decoder = new JpegBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze(); // safe to hand back to a UI-thread caller from any thread
            return frame;
        }
    }
}
