using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using VideoDedupe.Models;
namespace VideoDedupe.Storage;

/// <summary>
/// SQLite-backed fingerprint cache. Microsoft.Data.Sqlite is MIT licensed and
/// published by Microsoft, so it carries no commercial restriction.
///
/// Frame hashes are stored as a single BLOB per video rather than one row per
/// frame. A thorough scan can produce 600 frames per file, and a row-per-frame
/// schema turns a 20,000 file library into 12 million rows for no benefit: the
/// frames are only ever read as a complete ordered sequence.
/// </summary>
public sealed class SqliteFingerprintStore : IFingerprintStore
{
    private readonly string _connectionString;

    public SqliteFingerprintStore(string databasePath)
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

            CREATE TABLE IF NOT EXISTS VideoFingerprints (
                FilePath        TEXT    NOT NULL,
                ProfileKey      TEXT    NOT NULL,
                LibraryFileId   INTEGER NULL,
                FileSizeBytes   INTEGER NOT NULL,
                LastModifiedUtc INTEGER NOT NULL,
                DurationTicks   INTEGER NOT NULL,
                Width           INTEGER NOT NULL,
                Height          INTEGER NOT NULL,
                FrameCount      INTEGER NOT NULL,
                Timestamps      BLOB    NOT NULL,
                Hashes          BLOB    NOT NULL,
                GeneratedUtc    INTEGER NOT NULL,
                PRIMARY KEY (FilePath, ProfileKey)
            );

            CREATE INDEX IF NOT EXISTS IX_Fingerprints_Profile
                ON VideoFingerprints (ProfileKey);
            """;

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<VideoFingerprint?> GetAsync(
        string filePath,
        long fileSizeBytes,
        DateTime lastModifiedUtc,
        string profileKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT LibraryFileId, FileSizeBytes, LastModifiedUtc, DurationTicks,
                   Width, Height, Timestamps, Hashes, GeneratedUtc
            FROM VideoFingerprints
            WHERE FilePath = $path AND ProfileKey = $profile
              AND FileSizeBytes = $size AND LastModifiedUtc = $modified;
            """;
        command.Parameters.AddWithValue("$path", filePath);
        command.Parameters.AddWithValue("$profile", profileKey);
        command.Parameters.AddWithValue("$size", fileSizeBytes);
        command.Parameters.AddWithValue("$modified", lastModifiedUtc.Ticks);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;

        return Read(reader, filePath, profileKey);
    }

    public async Task SaveAsync(VideoFingerprint fingerprint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO VideoFingerprints
                (FilePath, ProfileKey, LibraryFileId, FileSizeBytes, LastModifiedUtc,
                 DurationTicks, Width, Height, FrameCount, Timestamps, Hashes, GeneratedUtc)
            VALUES
                ($path, $profile, $libraryId, $size, $modified,
                 $duration, $width, $height, $frameCount, $timestamps, $hashes, $generated)
            ON CONFLICT (FilePath, ProfileKey) DO UPDATE SET
                LibraryFileId   = excluded.LibraryFileId,
                FileSizeBytes   = excluded.FileSizeBytes,
                LastModifiedUtc = excluded.LastModifiedUtc,
                DurationTicks   = excluded.DurationTicks,
                Width           = excluded.Width,
                Height          = excluded.Height,
                FrameCount      = excluded.FrameCount,
                Timestamps      = excluded.Timestamps,
                Hashes          = excluded.Hashes,
                GeneratedUtc    = excluded.GeneratedUtc;
            """;

        var (timestamps, hashes) = Pack(fingerprint.Frames);

        command.Parameters.AddWithValue("$path", fingerprint.FilePath);
        command.Parameters.AddWithValue("$profile", fingerprint.ProfileKey);
        command.Parameters.AddWithValue("$libraryId", (object?)fingerprint.LibraryFileId ?? DBNull.Value);
        command.Parameters.AddWithValue("$size", fingerprint.FileSizeBytes);
        command.Parameters.AddWithValue("$modified", fingerprint.LastModifiedUtc.Ticks);
        command.Parameters.AddWithValue("$duration", fingerprint.Duration.Ticks);
        command.Parameters.AddWithValue("$width", fingerprint.Width);
        command.Parameters.AddWithValue("$height", fingerprint.Height);
        command.Parameters.AddWithValue("$frameCount", fingerprint.Frames.Count);
        command.Parameters.AddWithValue("$timestamps", timestamps);
        command.Parameters.AddWithValue("$hashes", hashes);
        command.Parameters.AddWithValue("$generated", fingerprint.GeneratedUtc.Ticks);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VideoFingerprint>> GetAllAsync(
        string profileKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT FilePath, LibraryFileId, FileSizeBytes, LastModifiedUtc, DurationTicks,
                   Width, Height, Timestamps, Hashes, GeneratedUtc
            FROM VideoFingerprints
            WHERE ProfileKey = $profile;
            """;
        command.Parameters.AddWithValue("$profile", profileKey);

        var results = new List<VideoFingerprint>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(Read(reader, reader.GetString(0), profileKey, pathIsFirstColumn: true));
        }

        return results;
    }

    public async Task RemoveAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM VideoFingerprints WHERE FilePath = $path;";
        command.Parameters.AddWithValue("$path", filePath);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> PruneMissingFilesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var select = connection.CreateCommand();
        select.CommandText = "SELECT DISTINCT FilePath FROM VideoFingerprints;";

        var missing = new List<string>();
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var path = reader.GetString(0);
                if (!File.Exists(path)) missing.Add(path);
            }
        }

        if (missing.Count == 0) return 0;

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var delete = connection.CreateCommand();
        delete.Transaction = (SqliteTransaction)transaction;
        delete.CommandText = "DELETE FROM VideoFingerprints WHERE FilePath = $path;";
        var parameter = delete.CreateParameter();
        parameter.ParameterName = "$path";
        delete.Parameters.Add(parameter);

        foreach (var path in missing)
        {
            parameter.Value = path;
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return missing.Count;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static VideoFingerprint Read(
        SqliteDataReader reader,
        string filePath,
        string profileKey,
        bool pathIsFirstColumn = false)
    {
        var offset = pathIsFirstColumn ? 1 : 0;

        var libraryId = reader.IsDBNull(offset) ? (long?)null : reader.GetInt64(offset);
        var size = reader.GetInt64(offset + 1);
        var modified = new DateTime(reader.GetInt64(offset + 2), DateTimeKind.Utc);
        var duration = TimeSpan.FromTicks(reader.GetInt64(offset + 3));
        var width = reader.GetInt32(offset + 4);
        var height = reader.GetInt32(offset + 5);
        var timestamps = (byte[])reader.GetValue(offset + 6);
        var hashes = (byte[])reader.GetValue(offset + 7);
        var generated = new DateTime(reader.GetInt64(offset + 8), DateTimeKind.Utc);

        return new VideoFingerprint
        {
            FilePath = filePath,
            LibraryFileId = libraryId,
            FileSizeBytes = size,
            LastModifiedUtc = modified,
            Duration = duration,
            Width = width,
            Height = height,
            Frames = Unpack(timestamps, hashes),
            ProfileKey = profileKey,
            GeneratedUtc = generated
        };
    }

    private static (byte[] Timestamps, byte[] Hashes) Pack(IReadOnlyList<FrameHash> frames)
    {
        var timestamps = new byte[frames.Count * sizeof(long)];
        var hashes = new byte[frames.Count * sizeof(ulong)];

        for (var i = 0; i < frames.Count; i++)
        {
            BitConverter.TryWriteBytes(timestamps.AsSpan(i * sizeof(long)), frames[i].Timestamp.Ticks);
            BitConverter.TryWriteBytes(hashes.AsSpan(i * sizeof(ulong)), frames[i].Hash);
        }

        return (timestamps, hashes);
    }

    private static List<FrameHash> Unpack(byte[] timestamps, byte[] hashes)
    {
        var count = Math.Min(timestamps.Length / sizeof(long), hashes.Length / sizeof(ulong));
        var frames = new List<FrameHash>(count);

        for (var i = 0; i < count; i++)
        {
            var ticks = BitConverter.ToInt64(timestamps, i * sizeof(long));
            var hash = BitConverter.ToUInt64(hashes, i * sizeof(ulong));
            frames.Add(new FrameHash(TimeSpan.FromTicks(ticks), hash));
        }

        return frames;
    }
}
