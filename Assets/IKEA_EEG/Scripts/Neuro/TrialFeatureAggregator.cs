using IkeaEeg.Data;

namespace IkeaEeg.Neuro
{
    /// <summary>
    /// What one chair trial's EEG windows add up to. Immutable.
    /// </summary>
    public readonly struct TrialFeatureSummary
    {
        /// <summary>1-based, as the chair_trial_index column writes it.</summary>
        public readonly int trialNumber;

        /// <summary>Trials planned for the block; 0 when the event did not say.</summary>
        public readonly int trialCount;

        /// <summary>The difficulty the trial actually ran at, read off the event log.</summary>
        public readonly NeuroDifficulty difficulty;

        public readonly double intervalStartLsl;
        public readonly double intervalEndLsl;

        /// <summary>
        /// True only when CHAIR_TRIAL_END was observed. An incomplete trial informs nothing.
        /// </summary>
        public readonly bool completed;

        public readonly FeatureWindowSummary windows;

        public TrialFeatureSummary(int trialNumber, int trialCount, NeuroDifficulty difficulty,
            double intervalStartLsl, double intervalEndLsl, bool completed,
            FeatureWindowSummary windows)
        {
            this.trialNumber = trialNumber;
            this.trialCount = trialCount;
            this.difficulty = difficulty;
            this.intervalStartLsl = intervalStartLsl;
            this.intervalEndLsl = intervalEndLsl;
            this.completed = completed;
            this.windows = windows;
        }

        public double intervalSeconds => intervalEndLsl - intervalStartLsl;

        /// <summary>True when the block has no trial after this one.</summary>
        public bool isLastTrial => trialCount > 0 && trialNumber >= trialCount;
    }

    /// <summary>
    /// Receives the feature windows of ONE active chair trial and produces its summary.
    ///
    /// THE TRIAL INTERVAL IS A PLACEHOLDER DEFINITION: from CHAIR_TARGET_ONSET (the three target
    /// attributes become visible — response time zero) to CHAIR_SELECTION_END (the one selection
    /// is registered). That is the period of active search, and it excludes the pre-target gap and
    /// the feedback screen. Whether that is the right epoch for a workload estimate — and how
    /// short a trial may be before it yields too few windows — is a scientific decision that has
    /// not been made. A fast trial can legitimately produce zero windows; the summary says so
    /// rather than borrowing data from outside the interval.
    ///
    /// The aggregation statistic is the documented placeholder in <see cref="FeatureWindowSummary"/>.
    /// </summary>
    public sealed class TrialFeatureAggregator
    {
        readonly IntervalWindowCollector m_Collector = new IntervalWindowCollector();

        public int trialNumber { get; private set; }
        public int trialCount { get; private set; }
        public NeuroDifficulty difficulty { get; private set; }

        /// <summary>True between CHAIR_TARGET_ONSET and finalisation.</summary>
        public bool isActive { get; private set; }

        /// <summary>True once CHAIR_TRIAL_END was seen for the active trial.</summary>
        public bool completed { get; private set; }

        public void Begin(int trialNumber, int trialCount, NeuroDifficulty difficulty,
            double openLsl)
        {
            this.trialNumber = trialNumber;
            this.trialCount = trialCount;
            this.difficulty = difficulty;
            m_Collector.Open(openLsl);
            isActive = true;
            completed = false;
        }

        /// <summary>CHAIR_SELECTION_END: no window ending after this moment belongs to the trial.</summary>
        public void CloseInterval(double closeLsl)
        {
            if (isActive && !m_Collector.isClosed)
                m_Collector.Close(closeLsl);
        }

        /// <summary>CHAIR_TRIAL_END: the trial ran to its end.</summary>
        public void MarkCompleted()
        {
            if (isActive)
                completed = true;
        }

        public void Offer(LatestEegFeatures features)
        {
            if (isActive)
                m_Collector.Offer(features);
        }

        /// <summary>Completed, closed, and no further in-interval window can still arrive.</summary>
        public bool IsSettled(double lastWindowEnd) =>
            isActive && completed && m_Collector.IsSettled(lastWindowEnd);

        public TrialFeatureSummary Finalise()
        {
            var result = new TrialFeatureSummary(trialNumber, trialCount, difficulty,
                m_Collector.openLsl, m_Collector.closeLsl, completed, m_Collector.Summarize());

            isActive = false;
            completed = false;
            m_Collector.Clear();

            return result;
        }

        public void Discard()
        {
            isActive = false;
            completed = false;
            m_Collector.Clear();
        }
    }
}
