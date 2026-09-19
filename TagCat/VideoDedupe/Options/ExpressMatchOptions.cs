namespace VideoDedupe.Options;

/// <summary>
/// Controls for Express matching: files are grouped as duplicates by what's on their file
/// properties alone - name, timestamps, size - with no content read at all. This is what
/// makes it fast enough to be called "Express", and also why it can be wrong in either
/// direction: two unrelated files can share a name, and two real duplicates can have been
/// renamed or re-saved with a different timestamp.
/// </summary>
public sealed class ExpressMatchOptions
{
    public bool MatchFileName { get; init; } = true;
    public bool MatchCreatedDate { get; init; }
    public bool MatchModifiedDate { get; init; }

    /// <summary>Null means no bound on that side - matches TagCat's own size filter,
    /// where an empty box means no limit.</summary>
    public long? MinFileSizeBytes { get; init; }
    public long? MaxFileSizeBytes { get; init; }
}
