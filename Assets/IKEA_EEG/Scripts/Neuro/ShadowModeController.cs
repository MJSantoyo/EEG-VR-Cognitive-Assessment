using UnityEngine;
using IkeaEeg.Data;

namespace IkeaEeg.Neuro
{
    /// <summary>
    /// SHADOW MODE. Observes the EEG feature pipeline, applies a provisional decision rule and
    /// records the result. It changes NOTHING about the experiment.
    ///
    /// THE ISOLATION IS THE POINT, AND IT IS STRUCTURAL. This class holds no reference of any
    /// experiment type — no ExperimentManager, no ExperimentConfig, no ChairSelectionTask, no
    /// ExperimentUIController — so there is no path through which it could alter difficulty,
    /// timing, stimuli or UI even by mistake. ExperimentSelfTest asserts that by reflection and
    /// by source scan, so the guarantee is enforced rather than promised.
    ///
    /// It is also strictly downstream: it subscribes to a read-only event and never calls back
    /// into the pipeline, so it cannot perturb filtering, windowing or spectral processing.
    ///
    /// PHASE 1 BEHAVIOUR: no approved numerical threshold rule exists yet, so every window is
    /// reported as Indeterminate with reason NoApprovedRule. That is the correct output, not a
    /// stub — the thresholds are pending scientific review and must not be invented here.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShadowModeController : MonoBehaviour
    {
        /// <summary>Bump on any change to the decision path. Written into every row.</summary>
        public const string ControllerVersion = "shadow-1.0.0-phase1";

        /// <summary>
        /// Identifies the screening rule the rows were produced under. "pipeline-default"
        /// means the only screening applied is the EEG pipeline's own quality assessment; no
        /// additional outlier rule has been approved.
        /// </summary>
        public const string QualityRuleVersion = "pipeline-default/unreviewed-thresholds";

        /// <summary>
        /// Pinned. Not read from AuraMontageConfig, whose mappingSource defaults to
        /// HumanVerifiedAcquisitionUi without any physical check having been performed. It
        /// becomes VERIFIED only when a documented electrode-placement verification exists.
        /// </summary>
        public const string MontageStatus = "UNVERIFIED";

        [Header("Source")]
        [Tooltip("The feature pipeline to observe. Resolved from the scene when left empty.")]
        [SerializeField] EegFeaturePipeline m_Pipeline;

        [Header("Output")]
        [Tooltip("Writes shadow_decisions.csv beside the session's raw EEG. Optional.")]
        [SerializeField] ShadowDecisionSink m_Sink;

        [Header("Decision rule")]
        [Tooltip("OFF until numerical thresholds pass scientific review. While off, every " +
                 "window is reported Indeterminate / NoApprovedRule, which is the honest state.")]
        [SerializeField] bool m_ApprovedRuleAvailable;

        readonly ShadowBaseline m_Baseline = new ShadowBaseline();

        long m_WindowIndex;
        bool m_Subscribed;

        public ShadowDecision lastDecision { get; private set; }
        public int acceptedBaselineWindows => m_Baseline.acceptedWindowCount;

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

        /// <summary>Clears the session baseline. Affects this component only.</summary>
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
        /// Pure and deterministic given the features and the accumulated baseline: the same
        /// sequence of windows always yields the same sequence of decisions. Public so the
        /// self test can drive it without a scene, a pipeline or a headset.
        /// </summary>
        public ShadowDecision Evaluate(LatestEegFeatures features)
        {
            var index = m_WindowIndex++;

            if (features == null)
            {
                return Undecided(index, 0d, 0d, 0, 0, double.NaN, double.NaN,
                    ShadowRejection.FeatureInvalid);
            }

            var theta = features.frontalTheta;
            var alpha = features.posteriorAlpha;
            var validChannels = CountValidChannels(features);

            // --- screening, in the order that makes the reason most specific ---------------
            ShadowRejection rejection;

            if (!features.featureValidity)
                rejection = ShadowRejection.FeatureInvalid;
            else if (!features.roiValid || !string.IsNullOrEmpty(features.roiContamination))
                rejection = ShadowRejection.RoiContaminated;
            else if (!features.frontalThetaValid || !features.posteriorAlphaValid ||
                     double.IsNaN(theta) || double.IsNaN(alpha))
                rejection = ShadowRejection.RoiValueMissing;
            else
                rejection = ShadowRejection.None;

            if (rejection != ShadowRejection.None)
            {
                // Rejected windows still appear in the output, with their reason. They are
                // simply not allowed to shape the baseline.
                return Undecided(index, features.windowStart, features.windowEnd,
                    features.channelCount, validChannels, theta, alpha, rejection);
            }

            m_Baseline.Accumulate(theta, alpha);

            if (!m_Baseline.isValid)
            {
                var reason = m_Baseline.acceptedWindowCount < ShadowBaseline.MinimumWindows
                    ? ShadowRejection.BaselineInsufficient
                    : ShadowRejection.BaselineDegenerate;

                return Undecided(index, features.windowStart, features.windowEnd,
                    features.channelCount, validChannels, theta, alpha, reason);
            }

            var normalized = m_Baseline.Normalise(theta, alpha);

            if (double.IsNaN(normalized))
            {
                return Undecided(index, features.windowStart, features.windowEnd,
                    features.channelCount, validChannels, theta, alpha,
                    ShadowRejection.BaselineDegenerate);
            }

            // --- the rule ------------------------------------------------------------------
            // There is no approved rule yet. Reporting Indeterminate here — while still
            // recording the normalised index that a future rule would consume — is what keeps
            // the output honest and the thresholds reviewable rather than accidental.
            var level = m_ApprovedRuleAvailable
                ? Classify(normalized)
                : WorkloadLevel.Indeterminate;

            var ruleRejection = m_ApprovedRuleAvailable
                ? ShadowRejection.None
                : ShadowRejection.NoApprovedRule;

            return new ShadowDecision(index, features.windowStart, features.windowEnd,
                features.channelCount, validChannels, theta, alpha,
                m_Baseline.thetaBaseline, m_Baseline.alphaBaseline, normalized,
                m_Baseline.isValid, level, ruleRejection,
                MontageStatus, QualityRuleVersion, ControllerVersion);
        }

        /// <summary>
        /// Placeholder for the reviewed rule. Unreachable while no rule is approved, and left
        /// returning Indeterminate so that enabling the flag without supplying thresholds
        /// cannot silently start emitting labels.
        /// </summary>
        static WorkloadLevel Classify(double normalizedIndex) => WorkloadLevel.Indeterminate;

        ShadowDecision Undecided(long index, double start, double end, int channels,
            int validChannels, double theta, double alpha, ShadowRejection reason) =>
            new ShadowDecision(index, start, end, channels, validChannels, theta, alpha,
                m_Baseline.thetaBaseline, m_Baseline.alphaBaseline, double.NaN,
                m_Baseline.isValid, WorkloadLevel.Indeterminate, reason,
                MontageStatus, QualityRuleVersion, ControllerVersion);

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
