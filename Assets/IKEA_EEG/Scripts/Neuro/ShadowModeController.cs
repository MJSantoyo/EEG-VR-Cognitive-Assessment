using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using IkeaEeg.Data;

namespace IkeaEeg.Neuro
{
    /// <summary>
    /// SHADOW MODE. Observes the EEG feature pipeline, records what it sees, and changes
    /// NOTHING about the experiment.
    ///
    /// THE ISOLATION IS STRUCTURAL. This class holds no reference of any experiment type — no
    /// ExperimentManager, no ExperimentConfig, no ChairSelectionTask, no ExperimentUIController,
    /// no teleporter — so there is no path through which it could alter difficulty, timing,
    /// stimuli, navigation or UI even by mistake. ExperimentSelfTest asserts that by reflection
    /// and by source scan, so the guarantee is enforced rather than promised. It is also
    /// strictly downstream: it subscribes to a read-only event and never calls back into the
    /// pipeline, so it cannot perturb filtering, windowing or spectral processing.
    ///
    /// PHASE 1 BEHAVIOUR IS DELIBERATELY UNDECIDED. Three scientific prerequisites are missing:
    /// the montage is unverified, no normalization method is frozen, and no approved baseline
    /// protocol exists. Every one of them is a gate, and in Phase 1 the first of them always
    /// stops the chain. The output is therefore INDETERMINATE for every window, with a reason
    /// naming the obstacle. That is the correct result, not a stub.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShadowModeController : MonoBehaviour
    {
        /// <summary>Bump on any change to the decision path. Written into every row.</summary>
        public const string ControllerVersion = "shadow-1.1.0-phase1";

        /// <summary>
        /// Identifies the screening rule the rows were produced under. "pipeline-default"
        /// means the only screening applied is the EEG pipeline's own quality assessment; no
        /// additional outlier rule has been approved, and none is applied here.
        /// </summary>
        public const string QualityRuleVersion = "pipeline-default/unreviewed-thresholds";

        /// <summary>
        /// Pinned. Not read from AuraMontageConfig, whose mappingSource defaults to
        /// HumanVerifiedAcquisitionUi without any physical check having been performed. It
        /// becomes VERIFIED only when a documented electrode-placement verification exists.
        /// </summary>
        /// <summary>
        /// Written verbatim into every shadow row, so a row carries its own provenance and an
        /// analysis never has to ask a separate file what the montage status was that day.
        ///
        /// Traceable on purpose: it names WHAT was verified and WHEN, rather than asserting a
        /// bare "VERIFIED" that nobody could later audit.
        /// </summary>
        public const string MontageStatus = "PHYSICALLY_VERIFIED_2026-09-15";

        // ---- Scientific prerequisites, all absent in Phase 1 ---------------------------
        // Compile-time constants rather than inspector toggles: none of these may be switched
        // on from the editor by anyone who has not done the underlying scientific work.

        /// <summary>
        /// Requires a documented physical electrode-placement verification.
        ///
        /// TRUE since 2026-09-15: the researcher traced each electrode to its acquisition
        /// channel on the hardware and confirmed CH1=Fp1, CH2=F3, CH3=Fz, CH4=F4, CH5=Cz,
        /// CH6=P3, CH7=Pz, CH8=P4. See AuraMontageConfig.verificationNote, which is the record.
        ///
        /// WHAT THIS DOES NOT UNLOCK. Nothing that produces a label. The gate chain continues to
        /// NormalizationApproved, which is still false, so the reported rejection simply moves
        /// from MontageUnverified to NoApprovedNormalization. Beyond that, Row() hard-codes
        /// WorkloadLevel.Indeterminate unconditionally — there is no branch anywhere in this
        /// class that can emit LOW, MODERATE or HIGH, whatever the gates say. Knowing which
        /// electrode is which was never the thing standing between this project and a workload
        /// label; an approved normalization method and reviewed cut-offs are, and neither exists.
        /// </summary>
        public const bool MontageVerified = true;

        /// <summary>Requires a frozen, reviewed normalization method.</summary>
        public const bool NormalizationApproved = false;

        /// <summary>Requires reviewed numerical cut-offs for LOW / MODERATE / HIGH.</summary>
        public const bool DecisionRuleApproved = false;

        [Header("Source")]
        [Tooltip("The feature pipeline to observe. Resolved from the scene when left empty.")]
        [SerializeField] EegFeaturePipeline m_Pipeline;

        [Header("Output")]
        [Tooltip("Writes shadow_decisions.csv beside the session's raw EEG. Optional.")]
        [SerializeField] ShadowDecisionSink m_Sink;

        /// <summary>
        /// Holds a baseline only if an approved provider supplies one. Nothing in Phase 1 does,
        /// and task windows are never accumulated into it.
        /// </summary>
        readonly ShadowBaseline m_Baseline = new ShadowBaseline();

        long m_WindowIndex;
        bool m_Subscribed;

        // ---- Runtime counters ------------------------------------------------------------
        // Added after a real AURA run produced a header-only shadow CSV. Each counter isolates
        // one link, so the next run says WHICH one is dead instead of leaving it to be inferred
        // from an empty file. They are diagnostics only and feed nothing.

        /// <summary>featuresPublished callbacks actually received.</summary>
        public long windowsObserved { get; private set; }

        /// <summary>Decisions produced. Equals windowsObserved unless something threw.</summary>
        public long decisionsGenerated { get; private set; }

        /// <summary>True once the controller has confirmed its subscription to a pipeline.</summary>
        public bool isSubscribed => m_Subscribed;

        public ShadowDecision lastDecision { get; private set; }
        public ShadowBaseline baseline => m_Baseline;

        void OnEnable()
        {
            if (m_Pipeline == null)
                m_Pipeline = FindAnyObjectByType<EegFeaturePipeline>();

            Subscribe();
        }

        /// <summary>
        /// Starts observing <see cref="m_Pipeline"/>, if it is not already.
        ///
        /// Idempotent, and the ONLY place the subscription is made — so "am I observing?" has a
        /// single answer rather than one per call site.
        /// </summary>
        void Subscribe()
        {
            if (m_Pipeline == null || m_Subscribed)
                return;

            m_Pipeline.featuresPublished += OnFeaturesPublished;
            m_Subscribed = true;

            Debug.Log($"[IKEA_EEG] Shadow mode observing '{m_Pipeline.gameObject.name}' " +
                      $"(publishes continuously: {m_Pipeline.publishesContinuously}, " +
                      $"every {m_Pipeline.publishIntervalSeconds:F1} s). " +
                      $"Sink: {(m_Sink != null ? "wired" : "MISSING — rows cannot be written")}.");
        }

        void Unsubscribe()
        {
            if (m_Pipeline == null || !m_Subscribed)
                return;

            m_Pipeline.featuresPublished -= OnFeaturesPublished;
            m_Subscribed = false;
        }

        /// <summary>
        /// Tallies why this window was rejected, and logs the reason when it CHANGES.
        ///
        /// Logged on change rather than every window: at one window per four seconds a 17-minute
        /// run is 250 lines, and 250 identical lines hide the one transition that matters. The
        /// full tally is printed once at the end regardless.
        /// </summary>
        void RecordRejection(LatestEegFeatures features, ShadowDecision decision)
        {
            if (features == null)
                return;

            // GROUPED BY CAUSE, not by window. The tally is keyed on the SIGNATURE, which
            // omits the raw sample count: that number jitters by a sample or two between
            // otherwise identical windows, and keying on it fragmented one cause across dozens
            // of rows differing only in "(999 samples)" versus "(1000 samples)". The grouping
            // changed; not one validity decision did — both strings are read from a window
            // that has already been evaluated.
            var cause = decision.rejectedReason == ShadowRejection.FeatureInvalid
                ? "FeatureInvalid :: " + features.ValiditySignature()
                : decision.rejectedReason.ToString();

            m_RejectionTally.TryGetValue(cause, out var count);
            m_RejectionTally[cause] = count + 1;

            if (cause == m_LastBreakdown)
                return;

            m_LastBreakdown = cause;

            // The console line keeps the FULL breakdown, sample count included. Reading one
            // window is the case where that number is worth having.
            var reason = decision.rejectedReason == ShadowRejection.FeatureInvalid
                ? "FeatureInvalid :: " + features.ValidityBreakdown()
                : cause;

            Debug.Log($"[IKEA_EEG] Shadow window {windowsObserved}: {reason}");

            // The named channels, when the pipeline has them. A flag name says a window was
            // flagged; this says which electrode to go and look at.
            var detail = features.Explain();

            if (!string.IsNullOrEmpty(detail))
                Debug.Log("[IKEA_EEG] Shadow window detail:" + System.Environment.NewLine + detail);
        }

        /// <summary>The rejection tally, one line per distinct reason, most frequent first.</summary>
        public string DescribeRejections()
        {
            if (m_RejectionTally.Count == 0)
                return "no windows observed";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"windows observed: {windowsObserved}");

            foreach (var pair in m_RejectionTally.OrderByDescending(p => p.Value))
                sb.AppendLine($"  {pair.Value,6} x  {pair.Key}");

            return sb.ToString();
        }

        void OnDisable()
        {
            if (!m_Subscribed)
                return;

            Unsubscribe();

            var nl = System.Environment.NewLine;

            Debug.Log($"[IKEA_EEG] Shadow mode stopped observing. Windows observed: " +
                      $"{windowsObserved}; decisions generated: {decisionsGenerated}." + nl +
                      "Rejection breakdown:" + nl + DescribeRejections());
        }

        /// <summary>Clears the window counter and any supplied baseline. This component only.</summary>
        /// <summary>
        /// Scene-builder wiring. Serialized references only — it changes no behaviour, approves
        /// nothing and cannot open a gate.
        ///
        /// The controller finds the pipeline itself in OnEnable when this is not called, so this
        /// exists to make the wiring EXPLICIT in the generated scene rather than implicit in a
        /// FindAnyObjectByType at run time.
        /// </summary>
        public void Configure(EegFeaturePipeline pipeline, ShadowDecisionSink sink)
        {
            // RE-POINT, not just assign. OnEnable runs the moment the component is added, and
            // it falls back to FindAnyObjectByType when no pipeline is wired yet — so a
            // Configure arriving afterwards used to leave the controller still observing
            // whatever that search happened to return. Now it detaches from the old publisher
            // and attaches to the one it was actually given.
            if (!ReferenceEquals(m_Pipeline, pipeline))
                Unsubscribe();

            m_Pipeline = pipeline;
            m_Sink = sink;

            if (isActiveAndEnabled)
                Subscribe();
        }

        public void ResetSession()
        {
            m_Baseline.Reset();
            m_WindowIndex = 0;
        }

        // ---- Rejection diagnostics -------------------------------------------------------
        // A real run produced 17 rows, all FEATUREINVALID, with eight valid channels and finite
        // theta and alpha — and no way to tell WHY, because featureValidity collapses eleven
        // quality flags and the ROI check into one bit before it reaches the CSV. The pipeline
        // had already computed the reason; it was simply being discarded here. These read it
        // back out. Nothing is recomputed and no criterion is second-guessed.

        readonly Dictionary<string, long> m_RejectionTally = new Dictionary<string, long>();
        string m_LastBreakdown = string.Empty;

        /// <summary>How many windows each distinct rejection reason accounted for.</summary>
        public IReadOnlyDictionary<string, long> rejectionTally => m_RejectionTally;

        void OnFeaturesPublished(LatestEegFeatures features)
        {
            windowsObserved++;

            var decision = Evaluate(features);
            decisionsGenerated++;

            RecordRejection(features, decision);
            lastDecision = decision;

            if (m_Sink != null)
                m_Sink.Write(decision);
        }

        /// <summary>
        /// The gate chain, as one pure function.
        ///
        /// Separated out so the precedence is inspectable and testable without a scene, a
        /// pipeline or hardware — and so that a gate which Phase 1 never reaches, such as
        /// <see cref="ShadowRejection.NoApprovedNormalization"/>, is demonstrably wired rather
        /// than dead code. Returns <see cref="ShadowRejection.None"/> only when every
        /// prerequisite is met.
        /// </summary>
        public static ShadowRejection FirstUnmetGate(bool featureValid, bool roiClean,
            bool roiValuesPresent, bool montageVerified, bool normalizationApproved,
            bool baselineValid, bool decisionRuleApproved)
        {
            if (!featureValid) return ShadowRejection.FeatureInvalid;
            if (!roiClean) return ShadowRejection.RoiContaminated;
            if (!roiValuesPresent) return ShadowRejection.RoiValueMissing;
            if (!montageVerified) return ShadowRejection.MontageUnverified;
            if (!normalizationApproved) return ShadowRejection.NoApprovedNormalization;
            if (!baselineValid) return ShadowRejection.BaselineUnavailable;
            if (!decisionRuleApproved) return ShadowRejection.NoApprovedRule;

            return ShadowRejection.None;
        }

        /// <summary>
        /// Pure and deterministic given the features and the current baseline: the same
        /// sequence of windows always yields the same sequence of rows. Public so the self
        /// test can drive it without a scene, a pipeline or a headset.
        /// </summary>
        public ShadowDecision Evaluate(LatestEegFeatures features)
        {
            var index = m_WindowIndex++;

            if (features == null)
                return Row(index, 0d, 0d, 0, double.NaN, double.NaN, ShadowRejection.FeatureInvalid);

            var theta = features.frontalTheta;
            var alpha = features.posteriorAlpha;

            var featureValid = features.featureValidity;
            var roiClean = features.roiValid && string.IsNullOrEmpty(features.roiContamination);
            var roiValuesPresent = features.frontalThetaValid && features.posteriorAlphaValid &&
                                   !double.IsNaN(theta) && !double.IsNaN(alpha);

            var gate = FirstUnmetGate(featureValid, roiClean, roiValuesPresent,
                MontageVerified, NormalizationApproved, m_Baseline.isValid, DecisionRuleApproved);

            // NO ACCUMULATION. A window is never folded into a baseline here: a baseline
            // derived from the activity being measured is not a baseline, and the criteria for
            // building one have not been approved. m_Baseline only ever holds what an approved
            // provider supplied.
            //
            // NO NORMALIZATION. normalized_index stays NaN until a method is frozen; computing
            // a ratio here would be choosing a formula on this file's own authority.
            return Row(index, features.windowStart, features.windowEnd,
                CountValidChannels(features), theta, alpha, gate);
        }

        /// <summary>
        /// Builds the row. Every window produces one, accepted or rejected, so the output is a
        /// complete census of what the pipeline published.
        /// </summary>
        ShadowDecision Row(long index, double start, double end, int validChannels,
            double theta, double alpha, ShadowRejection gate)
        {
            // ALWAYS Indeterminate in Phase 1, unconditionally.
            //
            // Clearing every gate would be necessary before a label could be emitted, but it
            // would not be sufficient: mapping a normalized index onto LOW / MODERATE / HIGH
            // needs reviewed cut-offs, and none exist. Rather than write a branch that looks
            // like it could produce a label, this states the fact. The cut-offs and the
            // mapping arrive together, or not at all.
            const WorkloadLevel level = WorkloadLevel.Indeterminate;

            return new ShadowDecision(
                index, start, end, validChannels,
                theta, alpha,
                m_Baseline.thetaBaseline, m_Baseline.alphaBaseline,
                double.NaN,                      // normalized_index: unimplemented by decision
                level,
                MontageStatus,
                m_Baseline.isValid,
                QualityRuleVersion,
                ControllerVersion,
                gate);
        }

        static int CountValidChannels(LatestEegFeatures features)
        {
            if (features.thetaPerChannel == null)
                return 0;

            var degraded = features.degradedChannels ?? System.Array.Empty<int>();
            var valid = 0;

            for (var i = 0; i < features.thetaPerChannel.Length; i++)
            {
                if (double.IsNaN(features.thetaPerChannel[i]))
                    continue;

                var isDegraded = false;
                for (var d = 0; d < degraded.Length; d++)
                {
                    if (degraded[d] != i)
                        continue;

                    isDegraded = true;
                    break;
                }

                if (!isDegraded)
                    valid++;
            }

            return valid;
        }
    }
}
