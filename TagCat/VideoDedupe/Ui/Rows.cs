using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Imaging;
using VideoDedupe.Models;
// ScanFailure/ScanResult live in the root VideoDedupe namespace, one level up from
// VideoDedupe.Ui - not picked up automatically the way VideoDedupe.Models is.
using VideoDedupe;

namespace VideoDedupe.Ui;

public abstract class NotifyBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Change notifications are marshalled to the UI thread. WPF's binding engine
    /// updates controls the instant this fires, so a notification raised from a
    /// worker thread is what produces "the calling thread cannot access this object".
    /// </summary>
    protected void Raise([CallerMemberName] string? name = null)
    {
        var handler = PropertyChanged;
        if (handler is null) return;

        Dispatch.OnUi(() => handler(this, new PropertyChangedEventArgs(name)));
    }
}

/// <summary>One row in the left-hand list of duplicate groups.</summary>
public sealed class ClusterRow
{
    public required DuplicateCluster Cluster { get; init; }

    public string Title =>
        $"{Cluster.Count} files · {Format.Bytes(Cluster.ReclaimableBytes)} to reclaim";

    public string Subtitle => Cluster.IsExpressMatch
        ? $"Identical file content · {Path.GetFileName(Cluster.SuggestedKeep.FilePath)}"
        : $"{Cluster.AverageSimilarity:P0} match · {Path.GetFileName(Cluster.SuggestedKeep.FilePath)}";

    public string Warning => "Contains partial or trimmed versions — review carefully";

    public Visibility WarningVisibility =>
        Cluster.ContainsPartialMatches ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>One file that could not be fingerprinted, shown in the could-not-read list.</summary>
public sealed class FailureRow
{
    public required ScanFailure Failure { get; init; }

    public string FilePath => Failure.FilePath;

    public string FileName => Path.GetFileName(Failure.FilePath);

    public string FolderPath => Path.GetDirectoryName(Failure.FilePath) ?? string.Empty;

    public string Reason => Failure.Reason;
}

/// <summary>One video inside the selected group, shown in the review panel.</summary>
public sealed class MemberRow : NotifyBase
{
    private bool _isTicked;
    private BitmapSource? _thumbnail;

    public required VideoFingerprint Fingerprint { get; init; }

    public required bool IsKeeper { get; init; }

    public string FilePath => Fingerprint.FilePath;

    public string FileName => Path.GetFileName(Fingerprint.FilePath);

    public string FolderPath => Path.GetDirectoryName(Fingerprint.FilePath) ?? string.Empty;

    public string Details => Fingerprint.IsExpressMatch
        ? $"Modified {Fingerprint.LastModifiedUtc.ToLocalTime():g} · " +
          $"Created {Fingerprint.CreatedUtc.ToLocalTime():g} · " +
          $"{Format.Bytes(Fingerprint.FileSizeBytes)}"
        : $"{Fingerprint.Width}×{Fingerprint.Height} · " +
          $"{Fingerprint.Duration:hh\\:mm\\:ss} · " +
          $"{Format.Bytes(Fingerprint.FileSizeBytes)}";

    public string KeeperNote => "Suggested keeper — highest quality in this group";

    public Visibility KeeperVisibility => IsKeeper ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// True when this file lives inside a recycle bin or trash folder. Worth calling out
    /// in the results: a match against something already deleted is usually the copy you
    /// want to ignore, not evidence that the file you kept is redundant. Only ever true
    /// when the "include trash" advanced option was ticked, since otherwise such files
    /// are never discovered.
    /// </summary>
    public bool IsInTrash => TrashPaths.Contains(Fingerprint.FilePath);

    public string TrashNote => "In trash / recycle bin";

    public Visibility TrashVisibility => IsInTrash ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Ticked rows are the ones the delete button acts on.</summary>
    public bool IsTicked
    {
        get => _isTicked;
        set
        {
            if (_isTicked == value) return;
            _isTicked = value;
            Raise();
        }
    }

    /// <summary>
    /// Must always hold a frozen bitmap. A frozen Freezable is immutable and therefore
    /// safe to create on a worker thread and hand to the UI; an unfrozen one is not.
    /// </summary>
    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            Raise();
        }
    }
}

public static class Format
{
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }
}
