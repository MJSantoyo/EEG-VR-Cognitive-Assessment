using System;
using System.Globalization;
using UnityEngine;
using IkeaEeg.Core;
using IkeaEeg.Data;

namespace IkeaEeg.Neuro
{
    /// <summary>
    /// The fields of one experiment event that the neuro layer needs, copied out at once.
    ///
    /// Copied because the logger reuses a single scratch event object for every row: holding the
    /// reference would read the NEXT event's values. Also lets the self test drive the
    /// coordinator without an EventLogger.
    /// </summary>
    public readonly struct NeuroPhaseEvent
    {
        public readonly string eventType;
        public readonly string experimentSessionId;
        public readonly int runIndex;
        public readonly int chairTrialIndex;
        public readonly int chairTrialCount;
        public readonly NeuroDifficulty difficulty;

        /// <summary>The event's lsl_timestamp; NaN when liblsl was absent and the column was empty.</summary>
        public readonly double lslTimestamp;

        public NeuroPhaseEvent(string eventType, string experimentSessionId, int runIndex,
            int chairTrialIndex, int chairTrialCount, NeuroDifficulty difficulty,
            double lslTimestamp)
        {
            this.eventType = eventType ?? string.Empty;
            this.experimentSessionId = experimentSessionId ?? string.Empty;
            this.runIndex = runIndex;
            this.chairTrialIndex = chairTrialIndex;
            this.chairTrialCount = chairTrialCount;
            this.difficulty = difficulty;
            this.lslTimestamp = lslTimestamp;
        }

        public static NeuroPhaseEvent From(ExperimentEvent evt) =>
            new NeuroPhaseEvent(evt.eventType, evt.experimentSessionId, ParseInt(evt.runIndex),
                ParseInt(evt.chairTrialIndex), ParseInt(evt.chairTrialCount),
                NeuroDifficultyLabels.Parse(evt.difficulty), ParseDouble(evt.lslTimestamp));

        static int ParseInt(string s) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

        static double ParseDouble(string s) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? v
                : double.NaN;
    }

    /// <summary>
    /// NEUROADAPTIVE COORDINATOR — ARCHITECTURE ONLY, RUNNING IN SHADOW.
    ///
    /// Wires the planned chain end to end:
    ///
    ///   PRE_TASK_REST windows -> BaselineAggregator -> session baseline summary
    ///   chair trial N windows -> TrialFeatureAggregator -> trial summary
    ///   (baseline, trial)     -> IWorkloadEstimator     -> workload state (INDETERMINATE)
    ///   (workload, difficulty)-> IAdaptivePolicy        -> proposal for trial N+1 (none)
    ///
    /// and writes one row per baseline and per trial. It changes nothing about the experiment.
    ///
    /// ONE-WAY BY CONSTRUCTION. It reads the event log through EventBus.eventPublished — the
    /// bus's existing read-only notification — and reads feature windows handed to it by
    /// ShadowModeController. It holds no reference to any experiment type and calls nothing back:
    /// it cannot reach the chair task, the difficulty schedule, timing or UI. A proposal is
    /// stored and logged; nothing in the experiment reads it.
    ///
    /// TRIAL BOUNDARIES come from events the experiment already logs: CHAIR_TARGET_ONSET opens a
    /// trial's interval, CHAIR_SELECTION_END closes it, CHAIR_TRIAL_END marks it complete. Trial N
    /// is summarised only after it has ended, and its proposal is addressed to trial N+1 only.
    ///
    /// BASELINE LIFETIME follows the experiment_session_id stamped on every event. NEW TRIAL keeps
    /// the id, so the session's baseline is reused by the new run; RESTART opens a new id, which
    /// clears the baseline until the new session's own PRE_TASK_REST has run.
    ///
    /// A plain class, not a component: it adds nothing to any scene and is hosted by the one
    /// ShadowModeController the validators already require.
    /// </summary>
    public sealed class NeuroadaptiveController
    {
        /// <summary>Bump on any change to how rows are produced. Written into every row.</summary>
        public const string Version = "neuroadaptive-0.1.1-shadow";

        readonly BaselineAggregator m_Baseline = new BaselineAggregator();
        readonly TrialFeatureAggregator m_Trial = new TrialFeatureAggregator();
        readonly IWorkloadEstimator m_Estimator;
        readonly IAdaptivePolicy m_Policy;

        string m_ExperimentSessionId = string.Empty;
        int m_RunIndex;
        double m_LastWindowEnd = double.NaN;

        /// <summary>A second PRE_TASK_REST in a session that already has its baseline.</summary>
        bool m_RepeatRestOpen;
        double m_RepeatRestStart = double.NaN;

        /// <summary>The proposal made after trial N, addressed to <see cref="m_ProposalForTrial"/>.</summary>
        DifficultyProposal m_Proposal = DifficultyProposal.None(string.Empty);
        int m_ProposalForTrial;

        EventBus m_Bus;

        public NeuroadaptiveController(IWorkloadEstimator estimator = null,
            IAdaptivePolicy policy = null)
        {
            m_Estimator = estimator ?? new IndeterminateWorkloadEstimator();
            m_Policy = policy ?? new NoEegChangePolicy();
        }

        /// <summary>Raised for every row produced. The host forwards it to the sink.</summary>
        public event Action<NeuroTrialRecord> recordProduced;

        public string experimentSessionId => m_ExperimentSessionId;
        public int runIndex => m_RunIndex;
        public SessionBaselineSummary sessionBaseline => m_Baseline.summary;
        public bool isCollectingBaseline => m_Baseline.isCollecting;
        public bool isTrialActive => m_Trial.isActive;
        public long recordsProduced { get; private set; }
        public NeuroTrialRecord lastRecord { get; private set; }

        /// <summary>The most recent proposal. Diagnostic only: nothing applies it.</summary>
        public DifficultyProposal lastProposal => m_Proposal;

        /// <summary>The trial <see cref="lastProposal"/> is addressed to (N+1); 0 when none.</summary>
        public int lastProposalForTrial => m_ProposalForTrial;

        // ---- Inputs ------------------------------------------------------------------------

        public bool isAttached => m_Bus != null;

        /// <summary>Starts observing the event log. Idempotent.</summary>
        public void Attach(EventBus bus)
        {
            if (bus == null || ReferenceEquals(bus, m_Bus))
                return;

            Detach();
            m_Bus = bus;
            m_Bus.eventPublished += OnExperimentEvent;
        }

        public void Detach()
        {
            if (m_Bus == null)
                return;

            m_Bus.eventPublished -= OnExperimentEvent;
            m_Bus = null;
        }

        /// <summary>
        /// CONTAINED. EventBus raises eventPublished without a guard, inside the experiment's own
        /// Log() call, so an exception escaping here would surface in the experiment. It never
        /// does: anything thrown is logged and dropped.
        /// </summary>
        void OnExperimentEvent(ExperimentEvent evt)
        {
            if (evt == null)
                return;

            try
            {
                HandlePhaseEvent(NeuroPhaseEvent.From(evt));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[IKEA_EEG] Neuroadaptive shadow ignored an error on " +
                                 $"'{evt.eventType}': {e.Message}");
            }
        }

        /// <summary>One published feature window. Called by the host for every window.</summary>
        public void OfferWindow(LatestEegFeatures features)
        {
            if (features == null)
                return;

            if (!double.IsNaN(features.windowEnd))
                m_LastWindowEnd = features.windowEnd;

            m_Baseline.Offer(features);
            m_Trial.Offer(features);

            FinaliseSettled();
        }

        /// <summary>One experiment event. Public so the self test can drive it directly.</summary>
        public void HandlePhaseEvent(NeuroPhaseEvent e)
        {
            TrackSessionAndRun(e);

            switch (e.eventType)
            {
                case EventTypes.PreTaskRestStart:
                    OnRestStart(e);
                    break;

                case EventTypes.PreTaskRestEnd:
                    OnRestEnd(e);
                    break;

                case EventTypes.ChairTrialStart:
                    FinaliseOutstanding("next chair trial started");
                    break;

                case EventTypes.ChairTargetOnset:
                    OnTrialOnset(e);
                    break;

                case EventTypes.ChairSelectionEnd:
                    if (m_Trial.isActive && e.chairTrialIndex == m_Trial.trialNumber)
                        m_Trial.CloseInterval(e.lslTimestamp);
                    break;

                case EventTypes.ChairTrialEnd:
                    if (m_Trial.isActive && e.chairTrialIndex == m_Trial.trialNumber)
                        m_Trial.MarkCompleted();

                    FinaliseSettled();
                    break;

                case EventTypes.ChairBlockComplete:
                case EventTypes.SessionEnd:
                    FinaliseOutstanding(e.eventType);
                    break;
            }
        }

        // ---- Session / run lifecycle --------------------------------------------------------

        void TrackSessionAndRun(NeuroPhaseEvent e)
        {
            if (!string.IsNullOrEmpty(e.experimentSessionId) &&
                e.experimentSessionId != m_ExperimentSessionId)
            {
                // RESTART (or a developer recovery): a new participant sitting. Whatever was still
                // open belongs to the old session and is closed under the old ids first.
                if (m_ExperimentSessionId.Length > 0)
                    FinaliseOutstanding("experiment session changed");

                m_Baseline.ResetForNewExperimentSession();
                m_ExperimentSessionId = e.experimentSessionId;
                m_RunIndex = 0;
                ResetTrialChain();

                Debug.Log($"[IKEA_EEG] Neuroadaptive shadow: experiment session " +
                          $"{m_ExperimentSessionId}. Session baseline cleared; it is rebuilt from " +
                          "this session's PRE_TASK_REST.");
            }

            if (e.runIndex > 0 && e.runIndex != m_RunIndex)
            {
                // NEW TRIAL: a new run in the SAME session. The baseline is kept; only the
                // trial chain starts again, so trial 1 of this run is informed by nothing.
                FinaliseOutstanding("new run started");

                m_RunIndex = e.runIndex;
                ResetTrialChain();

                if (m_RunIndex > 1)
                {
                    Debug.Log($"[IKEA_EEG] Neuroadaptive shadow: run {m_RunIndex} reuses the " +
                              $"session baseline (collected={(m_Baseline.summary.collected ? "TRUE" : "FALSE")}" +
                              $", source run {m_Baseline.summary.sourceRunIndex}).");
                }
            }
        }

        void ResetTrialChain()
        {
            m_Trial.Discard();
            m_Proposal = DifficultyProposal.None(string.Empty);
            m_ProposalForTrial = 0;
        }

        // ---- Baseline -----------------------------------------------------------------------

        void OnRestStart(NeuroPhaseEvent e)
        {
            FinaliseOutstanding("PRE_TASK_REST started");

            if (m_Baseline.hasCompletedSummary)
            {
                // The baseline belongs to the session and is already taken. This rest is
                // recorded, not used.
                m_RepeatRestOpen = true;
                m_RepeatRestStart = e.lslTimestamp;
                return;
            }

            m_Baseline.Begin(m_ExperimentSessionId, m_RunIndex, e.lslTimestamp);
        }

        void OnRestEnd(NeuroPhaseEvent e)
        {
            if (m_RepeatRestOpen)
            {
                m_RepeatRestOpen = false;

                Emit(new NeuroTrialRecord
                {
                    recordType = NeuroTrialRecord.RecordBaselineRepeatIgnored,
                    intervalStartLsl = m_RepeatRestStart,
                    intervalEndLsl = e.lslTimestamp,
                    baseline = m_Baseline.summary,
                    notes = "a completed session baseline already exists; this rest was not used",
                });

                return;
            }

            m_Baseline.End(e.lslTimestamp);
            FinaliseSettled();
        }

        void FinaliseBaseline(string forcedBy)
        {
            var result = m_Baseline.Finalise();

            Emit(new NeuroTrialRecord
            {
                recordType = result.restCompleted
                    ? NeuroTrialRecord.RecordBaseline
                    : NeuroTrialRecord.RecordBaselineIncomplete,
                intervalStartLsl = result.intervalStartLsl,
                intervalEndLsl = result.intervalEndLsl,
                windows = result.windows,
                baseline = m_Baseline.summary,
                notes = (result.restCompleted
                            ? "session baseline summary; validity NOT assessed (no approved criterion)"
                            : "PRE_TASK_REST_END never observed; not used as the session baseline") +
                        (string.IsNullOrEmpty(forcedBy) ? string.Empty : $"; finalised by {forcedBy}") +
                        (double.IsNaN(result.intervalStartLsl) ? "; no lsl_timestamp on events" : string.Empty),
            });
        }

        // ---- Chair trials -------------------------------------------------------------------

        void OnTrialOnset(NeuroPhaseEvent e)
        {
            FinaliseOutstanding("next chair trial onset");

            // The difficulty recorded is the one the experiment's own event says ran. This layer
            // keeps no difficulty of its own for the trial.
            m_Trial.Begin(e.chairTrialIndex, e.chairTrialCount, e.difficulty, e.lslTimestamp);
        }

        void FinaliseTrial(string forcedBy)
        {
            var summary = m_Trial.Finalise();
            var baseline = m_Baseline.summary;

            var record = new NeuroTrialRecord
            {
                trialNumber = summary.trialNumber,
                trialCount = summary.trialCount,
                chairDifficulty = summary.difficulty,
                intervalStartLsl = summary.intervalStartLsl,
                intervalEndLsl = summary.intervalEndLsl,
                windows = summary.windows,
                baseline = baseline,
            };

            if (!summary.completed)
            {
                // Aborted or restarted mid-trial. It informs nothing, and the next trial gets no
                // proposal from it.
                record.recordType = NeuroTrialRecord.RecordTrialIncomplete;
                record.workloadLevel = WorkloadLevel.Indeterminate.ToString().ToUpperInvariant();
                record.indeterminateReason = ReasonLabel(WorkloadIndeterminateReason.TrialIncomplete);
                record.proposalReason = "TRIAL_INCOMPLETE: no proposal";
                record.notes = $"CHAIR_TRIAL_END never observed; finalised by {forcedBy}";

                m_Proposal = DifficultyProposal.None(record.proposalReason);
                m_ProposalForTrial = summary.trialNumber + 1;

                Emit(record);
                return;
            }

            var estimate = m_Estimator.Estimate(baseline, summary);

            // Trial N informs trial N+1 only. The last trial of the block is logged for the
            // record and proposes nothing, because there is no trial for it to inform.
            var proposal = summary.isLastTrial
                ? DifficultyProposal.None("LAST_TRIAL: no following trial; logged only")
                : m_Policy.Propose(estimate, summary.difficulty);

            m_Proposal = proposal;
            m_ProposalForTrial = summary.trialNumber + 1;

            record.recordType = NeuroTrialRecord.RecordTrial;
            record.workloadLevel = estimate.level.ToString().ToUpperInvariant();
            record.indeterminateReason = estimate.level == WorkloadLevel.Indeterminate
                ? ReasonLabel(estimate.reason)
                : string.Empty;
            record.proposedNextDifficulty = proposal.proposed;
            record.proposalReason = proposal.reason;
            record.notes = $"estimator={estimate.estimatorVersion}" +
                           (double.IsNaN(summary.intervalStartLsl) ? "; no lsl_timestamp on events" : string.Empty) +
                           (string.IsNullOrEmpty(forcedBy) ? string.Empty : $"; finalised by {forcedBy}");

            Emit(record);
        }

        // ---- Finalisation -------------------------------------------------------------------

        /// <summary>Finalises whatever has ended and can no longer receive an in-interval window.</summary>
        void FinaliseSettled()
        {
            if (m_Baseline.IsSettled(m_LastWindowEnd))
                FinaliseBaseline(string.Empty);

            if (m_Trial.IsSettled(m_LastWindowEnd))
                FinaliseTrial(string.Empty);
        }

        /// <summary>
        /// Finalises everything still open, because a later boundary has arrived. Used when no
        /// window can settle an interval (EEG absent) and when a run or session ends.
        /// </summary>
        public void FinaliseOutstanding(string forcedBy)
        {
            if (m_Baseline.isCollecting)
                FinaliseBaseline(forcedBy);

            if (m_Trial.isActive)
                FinaliseTrial(forcedBy);

            m_RepeatRestOpen = false;
        }

        void Emit(NeuroTrialRecord record)
        {
            record.experimentSessionId = m_ExperimentSessionId;
            record.runIndex = m_RunIndex;

            recordsProduced++;
            lastRecord = record;

            recordProduced?.Invoke(record);
        }

        static string ReasonLabel(WorkloadIndeterminateReason reason)
        {
            switch (reason)
            {
                case WorkloadIndeterminateReason.TrialIncomplete: return "TRIAL_INCOMPLETE";
                case WorkloadIndeterminateReason.NoValidTrialWindows: return "NO_VALID_TRIAL_WINDOWS";
                case WorkloadIndeterminateReason.BaselineUnavailable: return "BASELINE_UNAVAILABLE";
                case WorkloadIndeterminateReason.NoApprovedNormalization: return "NO_APPROVED_NORMALIZATION";
                case WorkloadIndeterminateReason.NoApprovedRule: return "NO_APPROVED_RULE";
                default: return string.Empty;
            }
        }
    }
}
