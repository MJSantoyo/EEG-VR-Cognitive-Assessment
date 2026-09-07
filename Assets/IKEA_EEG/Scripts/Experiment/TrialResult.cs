using System.Collections.Generic;
using System.Globalization;
using System.Text;
using IkeaEeg.Core;
using IkeaEeg.Interaction;
using IkeaEeg.Memory;

namespace IkeaEeg.Experiment
{
    /// <summary>
    /// Everything worth summarising about one completed trial.
    ///
    /// This is a convenience view for the on-screen results panel and for debugging — the
    /// CSV remains the authoritative record. Nothing here is used to make experimental
    /// decisions.
    /// </summary>
    public class TrialResult
    {
        public string trialId = string.Empty;
        public string wordSetName = string.Empty;
        public List<string> presentedWords = new List<string>();

        public bool chairSelectionMade;

        /// <summary>The most recent chair selection. With multiple trials this is the last one.</summary>
        public ChairSelectionResult chairResult;

        /// <summary>How many chair trials the participant completed in Area B.</summary>
        public int chairTrialsCompleted;

        /// <summary>How many chair trials this run was configured to contain.</summary>
        public int chairTrialsPlanned;

        public RecordingInfo immediateRecall;
        public RecordingInfo delayedRecall;

        public double totalTrialSeconds;

        public void Reset()
        {
            trialId = string.Empty;
            wordSetName = string.Empty;
            presentedWords.Clear();
            chairSelectionMade = false;
            chairResult = default;
            chairTrialsCompleted = 0;
            chairTrialsPlanned = 0;
            immediateRecall = null;
            delayedRecall = null;
            totalTrialSeconds = 0d;
        }

        /// <summary>Human-readable summary shown on the Area C results panel.</summary>
        public string BuildSummaryText(bool revealChairCorrectness)
        {
            var sb = new StringBuilder();

            sb.AppendLine("<b>TRIAL COMPLETE</b>");
            sb.AppendLine();

            // Both durations use the same "X.XXX s (XXXX ms)" form — see TimeFormat.
            sb.AppendFormat("Total trial duration: <b>{0}</b>\n",
                TimeFormat.FromSeconds(totalTrialSeconds));
            sb.AppendFormat("Trial id: {0}\n", string.IsNullOrEmpty(trialId) ? "-" : trialId);
            sb.AppendLine();

            if (chairSelectionMade)
            {
                sb.AppendFormat("Chair trials completed: <b>{0}/{1}</b>\n",
                    chairTrialsCompleted, chairTrialsPlanned);
                sb.AppendFormat("Last chair selected: <b>{0}</b>\n", chairResult.chairId);
                sb.AppendFormat("Last response time: <b>{0}</b>\n",
                    TimeFormat.FromMilliseconds(chairResult.responseTimeMs));

                // Per-trial correctness stays off this panel unless the protocol explicitly
                // allows performance feedback — the participant should not be scoring
                // themselves between the encoding and the delayed recall.
                if (revealChairCorrectness)
                {
                    sb.AppendFormat("Last result: <b>{0}</b>\n",
                        chairResult.correct ? "CORRECT" : "INCORRECT");
                }
            }
            else
            {
                sb.AppendLine("Chair selected: none");
            }

            sb.AppendLine();
            sb.AppendFormat("Words presented ({0}): {1}\n",
                string.IsNullOrEmpty(wordSetName) ? "set" : wordSetName,
                string.Join(", ", presentedWords));

            sb.AppendLine();
            sb.AppendFormat("Immediate recall audio: {0}\n", DescribeRecording(immediateRecall));
            sb.AppendFormat("Delayed recall audio: {0}\n", DescribeRecording(delayedRecall));

            return sb.ToString();
        }

        static string DescribeRecording(RecordingInfo info)
        {
            if (info == null)
                return "not recorded";

            if (info.signalSilent)
                return $"SILENT — no usable signal ({info.signal.peakDbfs:F0} dBFS peak)";

            if (!info.audioCaptured)
                return $"NOT captured ({info.failureReason})";

            return string.Format(CultureInfo.InvariantCulture,
                "saved, {0}, peak {1:F0} dBFS",
                TimeFormat.FromMilliseconds(info.durationMs), info.signal.peakDbfs);
        }
    }
}
