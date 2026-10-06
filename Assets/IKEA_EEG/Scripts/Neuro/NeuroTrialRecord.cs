using System.Globalization;

namespace IkeaEeg.Neuro
{
    /// <summary>
    /// One row of shadow_trial_decisions.csv: a session baseline, or one chair trial, as the
    /// neuroadaptive layer saw it in shadow.
    ///
    /// A SIBLING FILE, NOT NEW COLUMNS. shadow_decisions.csv is one row per feature WINDOW with a
    /// frozen 15-column schema; these rows are one per TRIAL (or per rest). Appending trial
    /// columns to the window file would leave them empty on almost every row and mix two grains
    /// in one table, so the window file stays exactly as it was.
    /// </summary>
    public sealed class NeuroTrialRecord
    {
        public const string RecordBaseline = "BASELINE";
        public const string RecordBaselineIncomplete = "BASELINE_INCOMPLETE";
        public const string RecordBaselineRepeatIgnored = "BASELINE_REPEAT_IGNORED";
        public const string RecordTrial = "TRIAL";
        public const string RecordTrialIncomplete = "TRIAL_INCOMPLETE";

        /// <summary>
        /// 27 columns, this order; extend by appending only. (An adaptive_design_difficulty column
        /// was removed before this file was first committed: it logged a design value beside the
        /// difficulty that actually ran, giving two difficulty histories for one trial.)
        ///
        /// chair_difficulty is the difficulty the experiment ACTUALLY ran, copied from its own
        /// event; proposed_next_difficulty is always empty while no policy is approved.
        /// </summary>
        public const string CsvHeader =
            "record_type,experiment_session_id,run_index,trial_number,trial_count," +
            "chair_difficulty," +
            "interval_start_lsl,interval_end_lsl,interval_seconds," +
            "windows_offered,windows_valid,theta_fc_summary,alpha_post_summary,aggregation_method," +
            "session_baseline_collected,baseline_valid_windows,baseline_theta_summary," +
            "baseline_alpha_summary,baseline_source_run," +
            "workload_level,indeterminate_reason,proposed_next_difficulty,proposal_reason," +
            "proposal_applied,adaptation_mode,controller_version,notes";

        public string recordType = string.Empty;
        public string experimentSessionId = string.Empty;
        public int runIndex;

        /// <summary>0 on baseline rows.</summary>
        public int trialNumber;
        public int trialCount;

        /// <summary>As the experiment logged it on the trial's own events.</summary>
        public NeuroDifficulty chairDifficulty;

        public double intervalStartLsl = double.NaN;
        public double intervalEndLsl = double.NaN;

        public FeatureWindowSummary windows = FeatureWindowSummary.Empty;

        public SessionBaselineSummary baseline = SessionBaselineSummary.None;

        /// <summary>Empty on baseline rows, where no estimate is made.</summary>
        public string workloadLevel = string.Empty;
        public string indeterminateReason = string.Empty;

        public NeuroDifficulty proposedNextDifficulty;
        public string proposalReason = string.Empty;

        public string notes = string.Empty;

        /// <summary>Empty, never a substituted zero: an absent value must read as absent.</summary>
        static string N(double v) =>
            double.IsNaN(v) || double.IsInfinity(v)
                ? string.Empty
                : v.ToString("G17", CultureInfo.InvariantCulture);

        static string I(int v) => v > 0 ? v.ToString(CultureInfo.InvariantCulture) : string.Empty;

        /// <summary>Free text cannot break the row: commas and line breaks are replaced.</summary>
        static string T(string s) =>
            string.IsNullOrEmpty(s)
                ? string.Empty
                : s.Replace(',', ';').Replace('\r', ' ').Replace('\n', ' ');

        public string ToCsvRow()
        {
            var c = CultureInfo.InvariantCulture;

            return string.Join(",",
                T(recordType),
                T(experimentSessionId),
                I(runIndex),
                I(trialNumber),
                I(trialCount),
                NeuroDifficultyLabels.ToLabel(chairDifficulty),
                N(intervalStartLsl),
                N(intervalEndLsl),
                N(intervalEndLsl - intervalStartLsl),
                windows.windowsOffered.ToString(c),
                windows.windowsValid.ToString(c),
                N(windows.thetaFc),
                N(windows.alphaPost),
                T(windows.aggregationMethod),
                baseline.collected ? "TRUE" : "FALSE",
                baseline.windows.windowsValid.ToString(c),
                N(baseline.windows.thetaFc),
                N(baseline.windows.alphaPost),
                I(baseline.sourceRunIndex),
                T(workloadLevel),
                T(indeterminateReason),
                NeuroDifficultyLabels.ToLabel(proposedNextDifficulty),
                T(proposalReason),
                // No code path applies a proposal; see AdaptivePolicy.ActiveAdaptationEnabled.
                "FALSE",
                AdaptivePolicy.ModeLabel,
                NeuroadaptiveController.Version,
                T(notes));
        }
    }
}
