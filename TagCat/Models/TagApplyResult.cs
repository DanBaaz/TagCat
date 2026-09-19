namespace MediaTagger.Models
{
    /// <summary>
    /// What happened when tags were applied. ApplyTags used to return only the new path,
    /// which meant a rename that quietly appended "(2)" to dodge a name clash, or one that
    /// was refused outright, looked identical to a clean success.
    /// </summary>
    public class TagApplyResult
    {
        public enum ResultKind
        {
            /// <summary>The name was already correct; nothing was touched.</summary>
            Unchanged,

            /// <summary>Renamed exactly as asked.</summary>
            Renamed,

            /// <summary>Renamed, but the wanted name was taken so a suffix was added.</summary>
            Collided,

            /// <summary>Nothing was renamed; the file is untouched.</summary>
            Failed
        }

        public ResultKind Kind { get; private init; }
        public string PreviousPath { get; private init; } = string.Empty;
        public string CurrentPath { get; private init; } = string.Empty;

        /// <summary>Populated for Failed, phrased to slot into "Couldn't rename X: {Reason}".</summary>
        public string Reason { get; private init; } = string.Empty;

        public bool Succeeded => Kind != ResultKind.Failed;

        /// <summary>True when the file actually moved, so callers know an undo entry is warranted.</summary>
        public bool Moved => Kind is ResultKind.Renamed or ResultKind.Collided;

        public static TagApplyResult Unchanged(string path) => new()
        {
            Kind = ResultKind.Unchanged,
            PreviousPath = path,
            CurrentPath = path
        };

        public static TagApplyResult Renamed(string previous, string current) => new()
        {
            Kind = ResultKind.Renamed,
            PreviousPath = previous,
            CurrentPath = current
        };

        public static TagApplyResult Collided(string previous, string current) => new()
        {
            Kind = ResultKind.Collided,
            PreviousPath = previous,
            CurrentPath = current
        };

        public static TagApplyResult Failed(string path, string reason) => new()
        {
            Kind = ResultKind.Failed,
            PreviousPath = path,
            CurrentPath = path,
            Reason = reason
        };
    }
}
