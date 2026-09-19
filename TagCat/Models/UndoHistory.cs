using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MediaTagger.Models
{
    /// <summary>One file that moved, and where it moved from.</summary>
    public record FileRename(string FromPath, string ToPath);

    /// <summary>
    /// What kind of action an undo entry reverses. Each needs genuinely different handling:
    /// a rename or move goes back where it came from, a copy has its copy removed, and a
    /// delete has to be pulled out of the Recycle Bin.
    /// </summary>
    public enum UndoKind
    {
        Rename,
        Move,
        Copy,
        Delete
    }

    /// <summary>
    /// A whole user action ("Added tag beach to 43 files"), undone as a unit. Grouping
    /// matters: a bulk tag is one decision the user made, so it should take one undo to
    /// reverse, not forty-three.
    /// </summary>
    public class UndoOperation
    {
        public string Description { get; }
        public DateTime PerformedAt { get; } = DateTime.Now;
        public IReadOnlyList<FileRename> Renames { get; }
        public UndoKind Kind { get; }

        public UndoOperation(string description, IReadOnlyList<FileRename> renames,
            UndoKind kind = UndoKind.Rename)
        {
            Description = description;
            Renames = renames;
            Kind = kind;
        }
    }

    /// <summary>
    /// Undo history for tag changes, which are really file renames.
    ///
    /// Covers tag changes, moves, copies and deletes. Deliberately session-only: a stored
    /// history would go stale the moment files were touched outside the app, and undoing
    /// against stale state is worse than having no undo at all.
    /// </summary>
    public class UndoHistory
    {
        /// <summary>Bounded so a long session cannot grow this without limit.</summary>
        private const int MaxOperations = 50;

        private readonly List<UndoOperation> _operations = new();

        public bool CanUndo => _operations.Count > 0;

        public string? NextDescription =>
            _operations.Count > 0 ? _operations[^1].Description : null;

        public void Record(string description, IReadOnlyList<FileRename> renames,
            UndoKind kind = UndoKind.Rename)
        {
            if (renames.Count == 0) return;

            _operations.Add(new UndoOperation(description, renames, kind));
            while (_operations.Count > MaxOperations) _operations.RemoveAt(0);
        }

        public void Clear() => _operations.Clear();

        /// <summary>
        /// Reverses the most recent operation. Renames are undone in reverse order, so a
        /// batch that produced a "(2)" suffix to dodge a collision frees that name up again
        /// before the file that originally held it is moved back.
        ///
        /// A file that has since been moved or deleted outside the app is skipped rather
        /// than treated as a failure, and one whose original name has been taken in the
        /// meantime is left alone rather than overwriting whatever is now there.
        /// </summary>
        public UndoOutcome UndoLast()
        {
            if (_operations.Count == 0) return new UndoOutcome("Nothing to undo.", 0, 0, 0);

            var operation = _operations[^1];
            _operations.RemoveAt(_operations.Count - 1);

            int restored = 0, missing = 0, blocked = 0;
            var failures = new List<string>();

            foreach (var rename in operation.Renames.Reverse())
            {
                try
                {
                    switch (operation.Kind)
                    {
                        case UndoKind.Copy:
                            // Undoing a copy means removing the copy it created. The original
                            // was never touched, so only ToPath is involved. Recycled rather
                            // than hard-deleted: undo should not destroy anything outright,
                            // in case the copy has been edited since.
                            if (!File.Exists(rename.ToPath)) { missing++; break; }

                            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                                rename.ToPath,
                                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                            restored++;
                            break;

                        case UndoKind.Delete:
                            // The file is in the Recycle Bin, which has no supported API for
                            // restoring a specific file by path - the shell verb for it is
                            // locale-dependent and unreliable. Counted as blocked, and the
                            // caller explains where the files actually are.
                            blocked++;
                            break;

                        default:
                            if (!File.Exists(rename.ToPath)) { missing++; break; }

                            if (File.Exists(rename.FromPath) &&
                                !string.Equals(rename.FromPath, rename.ToPath, StringComparison.OrdinalIgnoreCase))
                            {
                                blocked++;
                                break;
                            }

                            // Recreates the original folder if it was emptied and cleaned up
                            // after a move - otherwise moving back fails on a missing parent.
                            var parent = Path.GetDirectoryName(rename.FromPath);
                            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                            File.Move(rename.ToPath, rename.FromPath);
                            restored++;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    blocked++;
                    failures.Add($"{Path.GetFileName(rename.ToPath)}: {ex.Message}");
                }
            }

            if (operation.Kind == UndoKind.Delete)
            {
                return new UndoOutcome(
                    $"\"{operation.Description}\" can't be undone from here - the files are in the " +
                    "Recycle Bin. Open it, select them and choose Restore.",
                    0, 0, blocked)
                { Failures = failures };
            }

            var verb = operation.Kind == UndoKind.Copy ? "copies removed" : "file(s) restored";
            var summary = $"Undid \"{operation.Description}\" — {restored} {verb}";
            if (missing > 0) summary += $", {missing} no longer found";
            if (blocked > 0) summary += $", {blocked} could not be undone";

            return new UndoOutcome(summary + ".", restored, missing, blocked) { Failures = failures };
        }
    }

    public class UndoOutcome
    {
        public string Summary { get; }
        public int Restored { get; }
        public int Missing { get; }
        public int Blocked { get; }
        public List<string> Failures { get; init; } = new();

        public UndoOutcome(string summary, int restored, int missing, int blocked)
        {
            Summary = summary;
            Restored = restored;
            Missing = missing;
            Blocked = blocked;
        }
    }
}
