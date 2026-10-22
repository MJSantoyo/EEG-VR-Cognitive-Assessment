using IkeaEeg.Data;

namespace IkeaEeg.Neuro
{
    /// <summary>
    /// How a PRE_TASK_REST actually ended, as the experiment's own END marker states it.
    /// Seeing an END is not enough: the experiment logs one for skipped and aborted rests too.
    /// </summary>
    public enum RestCompletion
    {
        /// <summary>No PRE_TASK_REST_END was observed (session/run changed, block cut off).</summary>
        EndNotObserved,

        /// <summary>The END said completed=TRUE and developer_skipped=FALSE.</summary>
        Completed,

        /// <summary>The END said developer_skipped=TRUE.</summary>
        DeveloperSkipped,

        /// <summary>The END said completed=FALSE without a developer skip: the run was aborted.</summary>
        Aborted,

        /// <summary>An END was seen but its notes do not confirm how the rest ended.</summary>
        Unconfirmed,
    }

    public static class RestCompletionLabels
    {
        /// <summary>
        /// Reads the completion of a rest from its END marker's notes ("key=VALUE; ..."), as the
        /// experiment's rest block writes them. Anything missing or contradictory
        /// is <see cref="RestCompletion.Unconfirmed"/>, never Completed.
        /// </summary>
        public static RestCompletion FromEndNotes(string notes)
        {
            var completed = Token(notes, "completed");
            var skipped = Token(notes, "developer_skipped");

            if (skipped == "TRUE")
                return RestCompletion.DeveloperSkipped;

            if (skipped != "FALSE")
                return RestCompletion.Unconfirmed;

            if (completed == "TRUE")
                return RestCompletion.Completed;

            // The experiment writes completed=FALSE only for a skip or an abort.
            return completed == "FALSE" ? RestCompletion.Aborted : RestCompletion.Unconfirmed;
        }

        public static string ToLabel(RestCompletion c)
        {
            switch (c)
            {
                case RestCompletion.Completed: return "COMPLETED";
                case RestCompletion.DeveloperSkipped: return "DEVELOPER_SKIPPED";
                case RestCompletion.Aborted: return "ABORTED";
                case RestCompletion.Unconfirmed: return "UNCONFIRMED";
                default: return "END_NOT_OBSERVED";
            }
        }

        static string Token(string notes, string key)
        {
            if (string.IsNullOrEmpty(notes))
                return null;

            foreach (var part in notes.Split(';'))
            {
                var kv = part.Split('=');

                if (kv.Length == 2 && kv[0].Trim() == key)
                    return kv[1].Trim();
            }

            return null;
        }
    }

    /// <summary>
    /// The resting reference of ONE experiment session, as recorded. Immutable.
    ///
    /// <see cref="collected"/> means "a PRE_TASK_REST whose END marker confirmed completion ran
    /// in this session and at least one usable window fell inside it". A skipped, aborted or
    /// unconfirmed rest is never collected, whatever windows it holds. It says NOTHING about
    /// scientific validity: no duration, window count or stability criterion for a baseline has
    /// been approved, so none is applied.
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

        /// <summary>How the rest ended, from its END marker.</summary>
        public readonly RestCompletion restCompletion;

        public readonly FeatureWindowSummary windows;

        public SessionBaselineSummary(string experimentSessionId, int sourceRunIndex,
            double intervalStartLsl, double intervalEndLsl, RestCompletion restCompletion,
            FeatureWindowSummary windows)
        {
            this.experimentSessionId = experimentSessionId ?? string.Empty;
            this.sourceRunIndex = sourceRunIndex;
            this.intervalStartLsl = intervalStartLsl;
            this.intervalEndLsl = intervalEndLsl;
            this.restCompletion = restCompletion;
            this.windows = windows;
        }

        /// <summary>True only when the END marker confirmed the rest ran to completion.</summary>
        public bool restCompleted => restCompletion == RestCompletion.Completed;

        public bool collected => restCompleted && windows.hasValidWindows;

        /// <summary>Why this rest is not the session baseline; empty when <see cref="collected"/>.</summary>
        public string rejectionReason =>
            !restCompleted
                ? "REST_" + RestCompletionLabels.ToLabel(restCompletion)
                : windows.hasValidWindows ? string.Empty : "NO_VALID_REST_WINDOWS";

        public double intervalSeconds => intervalEndLsl - intervalStartLsl;

        public static SessionBaselineSummary None =>
            new SessionBaselineSummary(string.Empty, 0, double.NaN, double.NaN,
                RestCompletion.EndNotObserved, FeatureWindowSummary.Empty);
    }

    /// <summary>
    /// Receives the feature windows of PRE_TASK_REST and holds the SESSION-LEVEL summary.
    ///
    /// LIFETIME. One instance per experiment session. It is filled by the first PRE_TASK_REST
    /// that is <see cref="SessionBaselineSummary.collected"/> and is then kept for every later run
    /// of that session (NEW TRIAL). A rejected attempt leaves it empty, so a later complete rest
    /// can still fill it. <see cref="ResetForNewExperimentSession"/> empties it (RESTART). A
    /// second rest in the same session cannot replace an accepted summary; the coordinator
    /// records it as ignored.
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

        /// <summary>How the rest being collected ended; EndNotObserved until its END is seen.</summary>
        public RestCompletion completion { get; private set; }

        /// <summary>True once a collected rest has become this session's summary.</summary>
        public bool hasAcceptedSummary { get; private set; }

        /// <summary>The session's summary; <see cref="SessionBaselineSummary.None"/> until one exists.</summary>
        public SessionBaselineSummary summary { get; private set; } = SessionBaselineSummary.None;

        public void Begin(string experimentSessionId, int runIndex, double openLsl)
        {
            m_ExperimentSessionId = experimentSessionId ?? string.Empty;
            m_RunIndex = runIndex;
            m_Collector.Open(openLsl);
            isCollecting = true;
            completion = RestCompletion.EndNotObserved;
        }

        public void End(double closeLsl, RestCompletion restCompletion)
        {
            if (!isCollecting)
                return;

            m_Collector.Close(closeLsl);

            // An END whose completion was not stated is still an END: never EndNotObserved.
            completion = restCompletion == RestCompletion.EndNotObserved
                ? RestCompletion.Unconfirmed
                : restCompletion;
        }

        public void Offer(LatestEegFeatures features)
        {
            if (isCollecting)
                m_Collector.Offer(features);
        }

        public bool IsSettled(double lastWindowEnd) =>
            isCollecting && m_Collector.IsSettled(lastWindowEnd);

        /// <summary>
        /// Ends collection and returns what was gathered. Only a <c>collected</c> result (END
        /// confirmed completion, at least one valid window) becomes the session's summary; any
        /// other result is returned with its <c>rejectionReason</c> and its windows are dropped.
        /// </summary>
        public SessionBaselineSummary Finalise()
        {
            var result = new SessionBaselineSummary(m_ExperimentSessionId, m_RunIndex,
                m_Collector.openLsl, m_Collector.closeLsl, completion, m_Collector.Summarize());

            isCollecting = false;
            completion = RestCompletion.EndNotObserved;
            m_Collector.Clear();

            if (result.collected && !hasAcceptedSummary)
            {
                summary = result;
                hasAcceptedSummary = true;
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
            completion = RestCompletion.EndNotObserved;
            hasAcceptedSummary = false;
            summary = SessionBaselineSummary.None;
        }
    }
}
