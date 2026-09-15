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

        public ShadowDecision lastDecision { get; private set; }
        public ShadowBaseline baseline => m_Baseline;

        void OnEnable()
        {
            if (m_Pipeline == null)
                m_Pipeline = FindAnyObjectByType<EegFeaturePipeline>();

            if (m_Pipeline == null || m_Subscribed)
                return;

            m_Pipeline.featuresPublished += OnFeaturesPublished;
            m_Subscribed = true;
        }

        void OnDisable()
        {
            if (m_Pipeline == null || !m_Subscribed)
                return;

            m_Pipeline.featuresPublished -= OnFeaturesPublished;
            m_Subscribed = false;
        }

        /// <summary>Clears the window counter and any supplied baseline. This component only.</summary>
        public void ResetSession()
        {
            m_Baseline.Reset();
            m_WindowIndex = 0;
        }

        void OnFeaturesPublished(LatestEegFeatures features)
        {
            var decision = Evaluate(features);
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
