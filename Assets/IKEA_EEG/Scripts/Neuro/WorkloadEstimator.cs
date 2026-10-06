namespace IkeaEeg.Neuro
{
    /// <summary>
    /// Why a trial produced no workload state. Recorded on EVERY trial row.
    ///
    /// Ordered DATA FIRST, then science: the first two members say what the recording failed to
    /// provide, the last two say what the project has not yet approved. A real session therefore
    /// reports the earliest real-data gap (no windows, no baseline) before falling through to the
    /// gates no amount of data can open.
    /// </summary>
    public enum WorkloadIndeterminateReason
    {
        None = 0,

        /// <summary>CHAIR_TRIAL_END was never observed; the trial was aborted or restarted.</summary>
        TrialIncomplete,

        /// <summary>No usable feature window lay inside the trial interval.</summary>
        NoValidTrialWindows,

        /// <summary>This experiment session has no completed PRE_TASK_REST with usable windows.</summary>
        BaselineUnavailable,

        /// <summary>No normalization method is approved (ShadowModeController.NormalizationApproved).</summary>
        NoApprovedNormalization,

        /// <summary>No approved workload formula or LOW/MODERATE/HIGH cut-offs exist.</summary>
        NoApprovedRule,
    }

    /// <summary>The estimator's output for one trial. Immutable.</summary>
    public readonly struct WorkloadEstimate
    {
        public readonly WorkloadLevel level;
        public readonly WorkloadIndeterminateReason reason;
        public readonly string estimatorVersion;

        public WorkloadEstimate(WorkloadLevel level, WorkloadIndeterminateReason reason,
            string estimatorVersion)
        {
            this.level = level;
            this.reason = reason;
            this.estimatorVersion = estimatorVersion ?? string.Empty;
        }
    }

    /// <summary>
    /// Baseline summary + trial summary in, workload state out.
    ///
    /// The interface exists so an approved estimator can replace the current one without the
    /// coordinator changing. No implementation that can emit a label exists.
    /// </summary>
    public interface IWorkloadEstimator
    {
        WorkloadEstimate Estimate(SessionBaselineSummary baseline, TrialFeatureSummary trial);
    }

    /// <summary>
    /// THE ONLY ESTIMATOR, AND IT ALWAYS ANSWERS INDETERMINATE.
    ///
    /// The reason is computed — it names the first unmet prerequisite — but the level is
    /// hard-coded, exactly as ShadowModeController.Row() hard-codes it. Clearing every
    /// prerequisite would be necessary for a label but not sufficient: no normalization method,
    /// no workload formula and no theta/alpha cut-offs have been approved, and inventing any of
    /// them here would be choosing a scientific method on this file's own authority. The formula
    /// and the cut-offs arrive together, in a new implementation, or not at all.
    /// </summary>
    public sealed class IndeterminateWorkloadEstimator : IWorkloadEstimator
    {
        public const string Version = "workload-indeterminate-1.0.0";

        public WorkloadEstimate Estimate(SessionBaselineSummary baseline, TrialFeatureSummary trial)
        {
            const WorkloadLevel level = WorkloadLevel.Indeterminate;

            return new WorkloadEstimate(level, FirstUnmetPrerequisite(baseline, trial), Version);
        }

        /// <summary>
        /// Never returns <see cref="WorkloadIndeterminateReason.None"/>: even with normalization
        /// approved, no implementation of an approved rule exists in this class, so the last
        /// answer is always <see cref="WorkloadIndeterminateReason.NoApprovedRule"/>.
        /// </summary>
        public static WorkloadIndeterminateReason FirstUnmetPrerequisite(
            SessionBaselineSummary baseline, TrialFeatureSummary trial) =>
            FirstUnmetPrerequisite(baseline, trial, ShadowModeController.NormalizationApproved);

        /// <summary>The same chain with the normalization gate as a parameter, for the self test.</summary>
        public static WorkloadIndeterminateReason FirstUnmetPrerequisite(
            SessionBaselineSummary baseline, TrialFeatureSummary trial, bool normalizationApproved)
        {
            if (!trial.completed) return WorkloadIndeterminateReason.TrialIncomplete;
            if (!trial.windows.hasValidWindows) return WorkloadIndeterminateReason.NoValidTrialWindows;
            if (!baseline.collected) return WorkloadIndeterminateReason.BaselineUnavailable;

            return normalizationApproved
                ? WorkloadIndeterminateReason.NoApprovedRule
                : WorkloadIndeterminateReason.NoApprovedNormalization;
        }
    }
}
