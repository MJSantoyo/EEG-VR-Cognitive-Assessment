using System;
using System.Collections.Generic;
using UnityEngine;

namespace IkeaEeg.Data
{
    /// <summary>Where a piece of configuration came from. Provenance, not decoration.</summary>
    public enum EegConfigSource
    {
        /// <summary>Nothing established it. Never treat as configured.</summary>
        Unknown = 0,

        /// <summary>
        /// Published by the LSL stream itself, in its StreamInfo description. The strongest
        /// form: it travels with the data and cannot drift from it.
        /// </summary>
        LslStreamMetadata = 1,

        /// <summary>
        /// Read off the acquisition application's UI by a person and typed in here.
        ///
        /// Trustworthy but NOT self-verifying: it describes how the amplifier was configured at
        /// the moment somebody looked, and it will silently become wrong if that configuration
        /// changes without this file changing too.
        /// </summary>
        HumanVerifiedAcquisitionUi = 2,
    }

    /// <summary>One acquisition channel and the electrode it is wired to.</summary>
    [Serializable]
    public class EegChannelMapping
    {
        [Tooltip("1-based channel number as it appears in the acquisition UI and in stream order.")]
        public int channelNumber = 1;

        [Tooltip("Electrode label, e.g. F3. 10-20 naming as shown in the acquisition UI.")]
        public string label = string.Empty;

        /// <summary>0-based index into a RawEegSample's channel array.</summary>
        public int SampleIndex => channelNumber - 1;
    }

    /// <summary>A named group of electrodes an aggregated feature is computed over.</summary>
    [Serializable]
    public class EegRoi
    {
        public string roiName = string.Empty;

        [Tooltip("Electrode labels, resolved through the montage — never raw indices.")]
        public List<string> labels = new List<string>();
    }

    /// <summary>
    /// The electrode montage and acquisition provenance for the AURA stream.
    ///
    /// ============================ WHY THIS FILE EXISTS ============================
    /// The AURA LSL stream publishes an EMPTY &lt;desc/&gt;. It carries no channel labels, no
    /// units and no filter state — verified live by pulling the full StreamInfo XML from the
    /// sender. Everything in this file therefore comes from a PERSON reading the AURA
    /// acquisition UI, not from the stream.
    ///
    /// That distinction is recorded on every field group via <see cref="EegConfigSource"/>, and
    /// it matters: metadata that travels with the data cannot drift from it, whereas a montage
    /// typed in here becomes silently wrong the moment somebody re-wires the cap or changes the
    /// amplifier configuration without editing this file. Any feature computed through this
    /// mapping inherits that caveat and must carry it into its own provenance.
    ///
    /// WHAT THIS FILE DOES NOT DO: it applies no filter, computes no spectrum and touches no
    /// sample. It states what the channels ARE. The processing that follows must resolve
    /// electrodes BY LABEL through this mapping — never by writing channels[1], channels[2],
    /// channels[3] and hoping the cap has not moved.
    /// ==============================================================================
    ///
    /// Create via: Assets ▸ Create ▸ IKEA_EEG ▸ AURA Montage Config
    /// </summary>
    [CreateAssetMenu(fileName = "AuraMontage_Default",
        menuName = "IKEA_EEG/AURA Montage Config", order = 2)]
    public class AuraMontageConfig : ScriptableObject
    {
        // ---- Provenance -------------------------------------------------------------------

        [Header("Provenance")]
        [Tooltip("Where the channel mapping below came from.")]
        public EegConfigSource mappingSource = EegConfigSource.HumanVerifiedAcquisitionUi;

        [Tooltip("Where the acquisition filter state below came from.")]
        public EegConfigSource filterStateSource = EegConfigSource.HumanVerifiedAcquisitionUi;

        [Tooltip("Free text: who verified this, when, and against what.")]
        [TextArea(2, 4)]
        public string verificationNote =
            "Channel order and filter state read from the AURA acquisition UI by the " +
            "researcher. The LSL stream publishes an empty <desc/> and provides none of this " +
            "information, so this configuration is NOT self-verifying: if the cap or the " +
            "amplifier configuration changes, this asset must be updated by hand.";

        // ---- Montage ----------------------------------------------------------------------

        [Header("Channel mapping (acquisition order)")]
        [Tooltip("One entry per stream channel, in stream order. Channel numbers are 1-based.")]
        public List<EegChannelMapping> channels = new List<EegChannelMapping>
        {
            new EegChannelMapping { channelNumber = 1, label = "Fp1" },
            new EegChannelMapping { channelNumber = 2, label = "F3" },
            new EegChannelMapping { channelNumber = 3, label = "Fz" },
            new EegChannelMapping { channelNumber = 4, label = "F4" },
            new EegChannelMapping { channelNumber = 5, label = "Cz" },
            new EegChannelMapping { channelNumber = 6, label = "P3" },
            new EegChannelMapping { channelNumber = 7, label = "Pz" },
            new EegChannelMapping { channelNumber = 8, label = "P4" },
        };

        // ---- ROIs -------------------------------------------------------------------------

        public const string FrontalThetaRoi = "FRONTAL_THETA";
        public const string PosteriorAlphaRoi = "POSTERIOR_ALPHA";

        [Header("Regions of interest")]
        [Tooltip("Electrode groups aggregated features are computed over. Named by LABEL so a " +
                 "change to the montage moves the ROI with it automatically.")]
        public List<EegRoi> regions = new List<EegRoi>
        {
            new EegRoi
            {
                roiName = FrontalThetaRoi,
                labels = new List<string> { "F3", "Fz", "F4" },
            },
            new EegRoi
            {
                roiName = PosteriorAlphaRoi,
                labels = new List<string> { "P3", "Pz", "P4" },
            },
        };

        // ---- Acquisition-side filter state -------------------------------------------------

        [Header("Acquisition filter state (as configured in AURA)")]
        [Tooltip("TRUE when AURA is applying a mains notch before publishing. Human-verified OFF.")]
        public bool acquisitionNotchEnabled;

        [Tooltip("TRUE when AURA is applying a band-pass before publishing. Human-verified OFF.")]
        public bool acquisitionBandpassEnabled;

        [Tooltip("Details of the acquisition-side filtering, when any is enabled.")]
        [TextArea(1, 3)]
        public string acquisitionFilterDetail =
            "AURA UI shows 'No Notch' and 'No Filtering'. The stream itself states nothing.";

        /// <summary>
        /// True when the acquisition chain is doing no filtering of its own.
        ///
        /// This is the permission gate for Unity-side preprocessing: filtering an already
        /// filtered signal compounds the passband edges and attenuates theta and alpha by an
        /// unknown amount, while still producing numbers that look entirely plausible.
        /// </summary>
        public bool acquisitionIsUnfiltered =>
            !acquisitionNotchEnabled && !acquisitionBandpassEnabled;

        // ---- Units --------------------------------------------------------------------------

        [Header("Amplitude units")]
        [Tooltip("OFF until AURA's source or documentation confirms the scaling of the float32 " +
                 "values. The UI showing a µV axis is not the same as the stream carrying µV.")]
        public bool unitsConfirmed;

        [Tooltip("What one sample value means. Used verbatim in reports.")]
        public string amplitudeUnitLabel = "AURA native units";

        /// <summary>
        /// The unit string for a POWER value, derived from the amplitude unit.
        ///
        /// Never "µV²/Hz" while <see cref="unitsConfirmed"/> is false: labelling a power
        /// spectral density with a physical unit it has not been shown to have is exactly the
        /// kind of claim that survives into a paper unchallenged.
        /// </summary>
        public string PowerSpectralDensityUnitLabel =>
            unitsConfirmed ? $"{amplitudeUnitLabel}²/Hz" : "AURA-native-units²/Hz";

        public string PowerUnitLabel =>
            unitsConfirmed ? $"{amplitudeUnitLabel}²" : "AURA-native-units²";

        // ---- Resolution ---------------------------------------------------------------------

        /// <summary>
        /// The 0-based sample index for an electrode label, or -1 when the montage has no such
        /// electrode.
        ///
        /// -1 rather than a fallback index, deliberately: a caller that cannot find P3 must stop
        /// and say so, not quietly analyse whatever channel happened to be nearby.
        /// </summary>
        public int IndexOfLabel(string label)
        {
            if (channels == null || string.IsNullOrWhiteSpace(label))
                return -1;

            for (var i = 0; i < channels.Count; i++)
            {
                if (channels[i] != null &&
                    string.Equals(channels[i].label, label.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return channels[i].SampleIndex;
                }
            }

            return -1;
        }

        /// <summary>The electrode label for a 0-based sample index, or empty.</summary>
        public string LabelOfIndex(int sampleIndex)
        {
            if (channels == null)
                return string.Empty;

            foreach (var channel in channels)
            {
                if (channel != null && channel.SampleIndex == sampleIndex)
                    return channel.label;
            }

            return string.Empty;
        }

        public EegRoi FindRoi(string roiName)
        {
            if (regions == null)
                return null;

            foreach (var roi in regions)
            {
                if (roi != null &&
                    string.Equals(roi.roiName, roiName, StringComparison.OrdinalIgnoreCase))
                {
                    return roi;
                }
            }

            return null;
        }

        /// <summary>
        /// The sample indices an ROI covers, or null when ANY of its electrodes is missing.
        ///
        /// All-or-nothing on purpose. A "frontal theta" averaged over two electrodes because the
        /// third could not be resolved is not frontal theta with a caveat — it is a different
        /// measurement wearing the same name.
        /// </summary>
        public int[] ResolveRoi(string roiName, out string problem)
        {
            var roi = FindRoi(roiName);

            if (roi == null || roi.labels == null || roi.labels.Count == 0)
            {
                problem = $"the montage defines no ROI named '{roiName}'";
                return null;
            }

            var indices = new int[roi.labels.Count];

            for (var i = 0; i < roi.labels.Count; i++)
            {
                indices[i] = IndexOfLabel(roi.labels[i]);

                if (indices[i] < 0)
                {
                    problem = $"ROI '{roiName}' needs electrode '{roi.labels[i]}', which this " +
                              "montage does not map to any channel";
                    return null;
                }
            }

            problem = string.Empty;
            return indices;
        }

        /// <summary>
        /// Checks the montage against a live stream's channel count, and against itself.
        ///
        /// The channel-count check is the one that catches a re-configured amplifier: a montage
        /// describing eight electrodes applied to a six-channel stream would otherwise resolve
        /// indices that do not exist.
        /// </summary>
        public bool Validate(int streamChannelCount, out string problem)
        {
            if (channels == null || channels.Count == 0)
            {
                problem = "the montage maps no channels";
                return false;
            }

            if (streamChannelCount > 0 && channels.Count != streamChannelCount)
            {
                problem = $"the montage describes {channels.Count} channels but the stream " +
                          $"carries {streamChannelCount}. The configuration and the amplifier " +
                          "disagree; do not analyse until this is resolved.";
                return false;
            }

            var seenNumbers = new HashSet<int>();
            var seenLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var channel in channels)
            {
                if (channel == null || string.IsNullOrWhiteSpace(channel.label))
                {
                    problem = "a channel has no electrode label";
                    return false;
                }

                if (channel.channelNumber < 1)
                {
                    problem = $"channel number {channel.channelNumber} is not 1-based";
                    return false;
                }

                if (!seenNumbers.Add(channel.channelNumber))
                {
                    problem = $"channel {channel.channelNumber} is mapped twice";
                    return false;
                }

                if (!seenLabels.Add(channel.label.Trim()))
                {
                    problem = $"electrode '{channel.label}' is mapped to more than one channel";
                    return false;
                }
            }

            if (regions != null)
            {
                foreach (var roi in regions)
                {
                    if (roi == null)
                        continue;

                    if (ResolveRoi(roi.roiName, out var roiProblem) == null)
                    {
                        problem = roiProblem;
                        return false;
                    }
                }
            }

            problem = string.Empty;
            return true;
        }

        /// <summary>One line of provenance to attach to any feature computed through this.</summary>
        public string DescribeProvenance()
        {
            var mapping = new List<string>();

            if (channels != null)
            {
                foreach (var channel in channels)
                {
                    if (channel != null)
                        mapping.Add($"CH{channel.channelNumber}={channel.label}");
                }
            }

            return $"montage_source={mappingSource}; " +
                   $"montage={string.Join(",", mapping)}; " +
                   $"filter_state_source={filterStateSource}; " +
                   $"acquisition_notch={(acquisitionNotchEnabled ? "ON" : "OFF")}; " +
                   $"acquisition_bandpass={(acquisitionBandpassEnabled ? "ON" : "OFF")}; " +
                   $"acquisition_unfiltered={(acquisitionIsUnfiltered ? "TRUE" : "FALSE")}; " +
                   $"units_confirmed={(unitsConfirmed ? "TRUE" : "FALSE")}; " +
                   $"amplitude_units={amplitudeUnitLabel}";
        }

        /// <summary>
        /// The human-verified configuration as of 2026-08-21, for code that has no asset.
        ///
        /// The serialized defaults above already carry these values; this exists so a diagnostic
        /// can run without an asset having been created, and so the verified configuration has
        /// exactly one definition.
        /// </summary>
        public static AuraMontageConfig CreateHumanVerifiedDefault()
        {
            return CreateInstance<AuraMontageConfig>();
        }
    }
}
