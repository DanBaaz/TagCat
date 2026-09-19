using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MediaTagger.Models
{
    /// <summary>One file that moved, and where it moved from.</summary>
    public record FileRename(string FromPath, string ToPath);

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

        public UndoOperation(string description, IReadOnlyList<FileRename> renames)
        {
            Description = description;
            Renames = renames;
        }
    }

    /// <summary>
    /// Undo history for tag changes, which are really file renames.
    ///
    /// Deliberately session-only and rename-only. It is not persisted, because a stored
    /// history would go stale the moment files were touched outside the app and undoing
    /// against stale state is worse than having no undo. It does not cover delete, move or
    /// copy: those leave the original location, and pretending to reverse them would be a
    /// promise this cannot keep.
    /// </summary>
    public class UndoHistory
    {
        /// <summary>Bounded so a long session cannot grow this without limit.</summary>
        private const int MaxOperations = 50;

        private readonly List<UndoOperation> _operations = new();

        public bool CanUndo => _operations.Count > 0;

        public string? NextDescription =>
            _operations.Count > 0 ? _operations[^1].Description : null;

        public void Record(string description, IReadOnlyList<FileRename> renames)
        {
            if (renames.Count == 0) return;

            _operations.Add(new UndoOperation(description, renames));
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
                    if (!File.Exists(rename.ToPath)) { missing++; continue; }

                    if (File.Exists(rename.FromPath) &&
                        !string.Equals(rename.FromPath, rename.ToPath, StringComparison.OrdinalIgnoreCase))
                    {
                        blocked++;
                        continue;
                    }

                    File.Move(rename.ToPath, rename.FromPath);
                    restored++;
                }
                catch (Exception ex)
                {
                    blocked++;
                    failures.Add($"{Path.GetFileName(rename.ToPath)}: {ex.Message}");
                }
            }

            var summary = $"Undid \"{operation.Description}\" — {restored} file(s) restored";
            if (missing > 0) summary += $", {missing} no longer found";
            if (blocked > 0) summary += $", {blocked} could not be restored";

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
