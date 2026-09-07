using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace IkeaEeg.Data
{
    /// <summary>
    /// Decides what RESTART is allowed to do to a run's files, and does it.
    ///
    /// THE RULE THIS ENFORCES: RESTART discards the CURRENT ACTIVE RUN. It may never touch any
    /// other run, any earlier session, any project asset, or anything outside the data root.
    ///
    /// DEFAULT BEHAVIOUR IS RETENTION, NOT DELETION. A discarded run keeps its files and gains
    /// a SESSION_DISCARDED_BY_RESTART marker, and no summary CSV or researcher summary is
    /// generated for it — so it does not appear in the normal analysis output, but the raw
    /// record still exists if it turns out to have been discarded by mistake. Deleting a
    /// participant's data because a button was pressed is not a recoverable error; keeping a
    /// clearly-marked folder is.
    ///
    /// Deletion is available (<see cref="TryDeleteRunDirectory"/>) but is OFF by default and
    /// refuses anything it cannot prove is the current run's own folder inside the data root.
    /// </summary>
    public static class SessionDiscardGuard
    {
        /// <summary>File written into a discarded run's folder. Its presence IS the status.</summary>
        public const string MarkerFileName = "SESSION_DISCARDED_BY_RESTART.txt";

        /// <summary>Status string used in events and in the marker file.</summary>
        public const string DiscardedStatus = "SESSION_DISCARDED_BY_RESTART";

        /// <summary>
        /// The only directory tree RESTART may ever write to or delete inside:
        /// &lt;persistentDataPath&gt;/&lt;root&gt;. Anything outside is refused.
        /// </summary>
        public static string DataRoot(string rootFolderName)
        {
            return Path.Combine(Application.persistentDataPath, rootFolderName);
        }

        /// <summary>
        /// True only when <paramref name="candidate"/> is a DIRECT child of the data root and
        /// its folder name matches the id of the run being discarded.
        ///
        /// Every clause matters:
        ///   * non-empty and rooted        — a blank or relative path could resolve anywhere;
        ///   * inside the data root        — never a project asset, never the user's documents;
        ///   * a DIRECT child of the root  — never the root itself, never a nested path;
        ///   * name equals the run id      — never a different run, however similar its name.
        /// </summary>
        public static bool IsDiscardableRunDirectory(string candidate, string runId,
            string rootFolderName, out string refusal)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                refusal = "the run directory is empty";
                return false;
            }

            if (string.IsNullOrWhiteSpace(runId))
            {
                refusal = "the run id is empty";
                return false;
            }

            string fullCandidate;
            string fullRoot;

            try
            {
                fullCandidate = Path.GetFullPath(candidate).TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                fullRoot = Path.GetFullPath(DataRoot(rootFolderName)).TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception e)
            {
                refusal = $"path could not be resolved: {e.Message}";
                return false;
            }

            if (string.Equals(fullCandidate, fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                refusal = "that is the data ROOT, which holds every session ever recorded";
                return false;
            }

            var parent = Path.GetDirectoryName(fullCandidate);
            if (parent == null || !string.Equals(
                    parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                refusal = $"'{fullCandidate}' is not a direct child of the data root '{fullRoot}'";
                return false;
            }

            var folderName = Path.GetFileName(fullCandidate);
            if (!string.Equals(folderName, runId, StringComparison.Ordinal))
            {
                refusal = $"folder '{folderName}' does not match the current run id '{runId}'";
                return false;
            }

            refusal = string.Empty;
            return true;
        }

        /// <summary>
        /// Marks the current run as discarded by writing a marker file into ITS OWN folder.
        /// Nothing is deleted. Returns the marker path, or empty if it could not be written.
        /// </summary>
        public static string MarkRunDiscarded(string runDirectory, string runId,
            string rootFolderName, string reason, string experimentSessionId, int runIndex)
        {
            if (!IsDiscardableRunDirectory(runDirectory, runId, rootFolderName, out var refusal))
            {
                Debug.LogWarning($"[IKEA_EEG] RESTART did not mark any folder as discarded: " +
                                 $"{refusal}. Nothing was changed on disk.");
                return string.Empty;
            }

            var path = Path.Combine(runDirectory, MarkerFileName);

            try
            {
                var text = new StringBuilder();
                text.AppendLine(DiscardedStatus);
                text.AppendLine();
                text.AppendLine("This run was DISCARDED by the RESTART control. Its data is NOT");
                text.AppendLine("part of the normal analysis output: no session summary and no");
                text.AppendLine("researcher summary were generated for it.");
                text.AppendLine();
                text.AppendLine("The raw event CSV and any WAV files are deliberately RETAINED,");
                text.AppendLine("so that a restart pressed by mistake has not destroyed data.");
                text.AppendLine("Exclude this folder from analysis by the presence of this file.");
                text.AppendLine();
                text.AppendLine($"run_session_id:        {runId}");
                text.AppendLine($"experiment_session_id: {experimentSessionId}");
                text.AppendLine($"run_index:             {runIndex}");
                text.AppendLine($"discarded_at:          {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                text.AppendLine($"reason:                {reason}");

                File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));

                Debug.Log($"[IKEA_EEG] Run {runId} marked {DiscardedStatus}. Files retained at:\n" +
                          $"    {runDirectory}");

                return path;
            }
            catch (Exception e)
            {
                Debug.LogError($"[IKEA_EEG] Could not write the discard marker to '{path}': " +
                               $"{e.Message}. Nothing was deleted.");
                return string.Empty;
            }
        }

        /// <summary>
        /// Deletes the current run's folder. OFF by default and used only when a researcher has
        /// explicitly enabled deletion; refuses anything <see cref="IsDiscardableRunDirectory"/>
        /// does not approve.
        /// </summary>
        /// <returns>True only if the directory was actually deleted.</returns>
        public static bool TryDeleteRunDirectory(string runDirectory, string runId,
            string rootFolderName, out string detail)
        {
            if (!IsDiscardableRunDirectory(runDirectory, runId, rootFolderName, out var refusal))
            {
                detail = $"REFUSED: {refusal}";
                Debug.LogWarning($"[IKEA_EEG] RESTART refused to delete anything — {refusal}");
                return false;
            }

            try
            {
                if (!Directory.Exists(runDirectory))
                {
                    detail = "the run directory does not exist";
                    return false;
                }

                Directory.Delete(runDirectory, recursive: true);
                detail = $"deleted {runDirectory}";
                Debug.LogWarning($"[IKEA_EEG] RESTART DELETED the current run's folder: " +
                                 $"{runDirectory}");
                return true;
            }
            catch (Exception e)
            {
                detail = $"delete failed: {e.Message}";
                Debug.LogError($"[IKEA_EEG] Could not delete '{runDirectory}': {e.Message}. " +
                               "The folder is left in place.");
                return false;
            }
        }
    }
}
