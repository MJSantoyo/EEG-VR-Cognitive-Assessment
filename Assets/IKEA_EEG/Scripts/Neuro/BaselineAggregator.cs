using IkeaEeg.Data;

namespace IkeaEeg.Neuro
{
    /// <summary>
    /// The resting reference of ONE experiment session, as recorded. Immutable.
    ///
    /// <see cref="collected"/> means "a PRE_TASK_REST ran to its end in this session and at least
    /// one usable window fell inside it". It says NOTHING about scientific validity: no duration,
    /// window count or stability criterion for a baseline has been approved, so none is applied.
    /// This summary is deliberately never handed to <see cref="ShadowBaseline"/>, whose
    /// <c>SupplyApprovedBaseline</c> stays reserved for an approved protocol.
    /// </summary>
    public readonly struct SessionBaselineSummary
    {
        public readonly string experimentSessionId;

        /// <summary>The run (1-based) whose PRE_TASK_REST produced this summary.</summary>
        public readonly int sourceRunIndex;

        public readonly double intervalStartLsl;
        public readonly double intervalEndLsl;

        /// <summary>True only when PRE_TASK_REST_END was observed for the rest.</summary>
        public readonly bool restCompleted;

        public readonly FeatureWindowSummary windows;

        public SessionBaselineSummary(string experimentSessionId, int sourceRunIndex,
            double intervalStartLsl, double intervalEndLsl, bool restCompleted,
            FeatureWindowSummary windows)
        {
            this.experimentSessionId = experimentSessionId ?? string.Empty;
            this.sourceRunIndex = sourceRunIndex;
            this.intervalStartLsl = intervalStartLsl;
            this.intervalEndLsl = intervalEndLsl;
            this.restCompleted = restCompleted;
            this.windows = windows;
        }

        public bool collected => restCompleted && windows.hasValidWindows;

        public double intervalSeconds => intervalEndLsl - intervalStartLsl;

        public static SessionBaselineSummary None =>
            new SessionBaselineSummary(string.Empty, 0, double.NaN, double.NaN, false,
                FeatureWindowSummary.Empty);
    }

    /// <summary>
    /// Receives the feature windows of PRE_TASK_REST and holds the SESSION-LEVEL summary.
    ///
    /// LIFETIME. One instance per experiment session. It is filled by the first PRE_TASK_REST
    /// that runs to its end and is then kept for every later run of that session (NEW TRIAL).
    /// <see cref="ResetForNewExperimentSession"/> empties it (RESTART). A second rest in the same
    /// session cannot replace a completed summary; the coordinator records it as ignored.
    ///
    /// Nothing here normalizes anything. The exact normalization formula is not defined yet.
    /// </summary>
    public sealed class BaselineAggregator
    {
        readonly IntervalWindowCollector m_Collector = new IntervalWindowCollector();

        string m_ExperimentSessionId = string.Empty;
        int m_RunIndex;

        /// <summary>True between PRE_TASK_REST_START and finalisation.</summary>
        public bool isCollecting { get; private set; }

        /// <summary>True once PRE_TASK_REST_END has been seen for the rest being collected.</summary>
        public bool restEnded { get; private set; }

        /// <summary>True once a rest that ran to its end has been finalised in this session.</summary>
        public bool hasCompletedSummary { get; private set; }

        /// <summary>The session's summary; <see cref="SessionBaselineSummary.None"/> until one exists.</summary>
        public SessionBaselineSummary summary { get; private set; } = SessionBaselineSummary.None;

        public void Begin(string experimentSessionId, int runIndex, double openLsl)
        {
            m_ExperimentSessionId = experimentSessionId ?? string.Empty;
            m_RunIndex = runIndex;
            m_Collector.Open(openLsl);
            isCollecting = true;
            restEnded = false;
        }

        public void End(double closeLsl)
        {
            if (!isCollecting)
                return;

            m_Collector.Close(closeLsl);
            restEnded = true;
        }

        public void Offer(LatestEegFeatures features)
        {
            if (isCollecting)
                m_Collector.Offer(features);
        }

        public bool IsSettled(double lastWindowEnd) =>
            isCollecting && m_Collector.IsSettled(lastWindowEnd);

        /// <summary>
        /// Ends collection and returns what was gathered. A rest whose END was never seen
        /// (aborted, restarted) is returned with <c>restCompleted = false</c> and does not become
        /// the session's summary.
        /// </summary>
        public SessionBaselineSummary Finalise()
        {
            var result = new SessionBaselineSummary(m_ExperimentSessionId, m_RunIndex,
                m_Collector.openLsl, m_Collector.closeLsl, restEnded, m_Collector.Summarize());

            isCollecting = false;
            restEnded = false;
            m_Collector.Clear();

            if (result.restCompleted && !hasCompletedSummary)
            {
                summary = result;
                hasCompletedSummary = true;
            }

            return result;
        }

        /// <summary>RESTART: a new experiment session owes its own PRE_TASK_REST.</summary>
        public void ResetForNewExperimentSession()
        {
            m_Collector.Clear();
            m_ExperimentSessionId = string.Empty;
            m_RunIndex = 0;
            isCollecting = false;
            restEnded = false;
            hasCompletedSummary = false;
            summary = SessionBaselineSummary.None;
        }
    }
}
