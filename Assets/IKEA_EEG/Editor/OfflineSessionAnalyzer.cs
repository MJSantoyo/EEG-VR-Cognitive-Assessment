using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using IkeaEeg.Data;

namespace IkeaEeg.EditorTools
{
    /// <summary>
    /// POST-SESSION OFFLINE EEG REPLAY. Loads one already-recorded run from disk, re-runs the
    /// PROJECT'S OWN preprocessing and spectral code over it, and writes plots, numeric summaries
    /// and a Markdown report into an <c>offline_analysis/</c> folder inside that session.
    ///
    /// ENGINEERING VALIDATION ONLY. Nothing here interprets a number. There is no claim about
    /// cognitive workload, attention, memory, effort or clinical state anywhere in this file or in
    /// anything it writes, and band powers are reported as engineering measurements of a recorded
    /// signal — not as evidence about the participant.
    ///
    /// WHY IT REUSES RATHER THAN REIMPLEMENTS. Every number that could be got wrong quietly is
    /// produced by the class the live session already uses:
    ///
    ///   * <see cref="EegRecordedWindowValidator.Load"/>          parses the recorded CSV
    ///   * <see cref="EegRecordedWindowValidator.CheckAnalysisDomain"/> decides whether the
    ///     recorded analysis clock can be aligned to events at all
    ///   * <see cref="EegAnalysisTimebase"/> (via ReconstructAnalysis) rebuilds it when it cannot
    ///   * <see cref="EegBandpassFilter"/>                        filters
    ///   * <see cref="EegSpectralAnalyzer.Welch"/> / BandPower    estimates spectra and band power
    ///   * <see cref="AuraMontageConfig"/>                        names electrodes and ROIs
    ///
    /// A second, private implementation of any of those would agree with nothing and would prove
    /// nothing about what the live pipeline does. What IS written fresh here is bookkeeping:
    /// locating files, cutting intervals, tabulating, and drawing — none of which changes a value.
    ///
    /// TWO THINGS ARE KNOWINGLY DUPLICATED, both documented at their use sites: the Flatline and
    /// SaturationLike rules (private to a MonoBehaviour that cannot run without a live receiver),
    /// and the gap rule (a constructor default of RawEegRingBuffer). Both are stated in the
    /// generated report rather than left for a reader to discover.
    ///
    /// UNITS. Amplitudes are written and plotted EXACTLY as recorded, in AURA native units. The
    /// source scaling is not formally verified, so nothing is converted to microvolts and no plot
    /// is labelled with one.
    ///
    /// THIS TOOL NEVER WRITES OUTSIDE ITS OWN OUTPUT FOLDER and never modifies a recorded file.
    /// </summary>
    public static class OfflineSessionAnalyzer
    {
        public const string OutputFolderName = "offline_analysis";

        // ---------------------------------------------------------------------------------
        // Options
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Everything the run needs. The preprocessing numbers default to the values serialized on
        /// the experiment scene's EegFeaturePipeline; <see cref="configSource"/> records where the
        /// values in force actually came from, so a report can never imply the live configuration
        /// was read when it was not.
        /// </summary>
        public class Options
        {
            public string sessionId = string.Empty;

            public double highPassHz = 1.0;
            public double lowPassHz = 40.0;
            public bool notchEnabled;
            public double notchHz = EegBandpassFilter.DefaultNotchHz;
            public double notchQ = EegBandpassFilter.DefaultNotchQ;

            /// <summary>Spectral window length, matching EegFeaturePipeline's m_WindowSeconds.</summary>
            public double spectralWindowSeconds = 4.0;

            /// <summary>Welch segment length, matching EegFeaturePipeline's m_WelchSegmentSeconds.</summary>
            public double welchSegmentSeconds = 2.0;

            public double welchOverlapFraction = 0.5;

            /// <summary>
            /// FFT length. 512 is not an independent choice: at 250 Hz a 2 s segment is 500
            /// samples and the live path's NextPowerOfTwo(500) is 512, so passing it explicitly
            /// reproduces the live path rather than departing from it.
            /// </summary>
            public int fftLength = 512;

            /// <summary>Step between successive spectral windows in the time course.</summary>
            public double timeCourseStepSeconds = 1.0;

            /// <summary>Stimulus-aligned interval: item onset through onset + this.</summary>
            public double stimulusEpochSeconds = 2.0;

            /// <summary>Response-aligned interval: this much ending at the response.</summary>
            public double responseEpochSeconds = 2.0;

            public string configSource = "built-in defaults (live scene not read)";

            /// <summary>
            /// The cross-channel and degradation thresholds, shared with the live pipeline.
            ///
            /// Passed in rather than hard-coded here so the offline replay judges a recording by
            /// the same rules the live session would have applied to it. Defaults are documented
            /// field by field on EegQualityThresholds.
            /// </summary>
            public EegQualityThresholds qualityThresholds = EegQualityThresholds.Default;

            public Options Clone()
            {
                return (Options)MemberwiseClone();
            }
        }

        // ---------------------------------------------------------------------------------
        // Result
        // ---------------------------------------------------------------------------------

        public class Result
        {
            public bool ok;
            public string sessionId = string.Empty;
            public string outputFolder = string.Empty;
            public string summary = string.Empty;

            public readonly List<string> filesWritten = new List<string>();
            public readonly List<string> warnings = new List<string>();
            public readonly List<string> failures = new List<string>();

            public string OneLine()
            {
                return (ok ? "OK" : "FAILED") + " — " + sessionId + " — " +
                       filesWritten.Count + " file(s), " + warnings.Count + " warning(s), " +
                       failures.Count + " failure(s)";
            }
        }

        // ---------------------------------------------------------------------------------
        // Bands (taken from the project, not redefined)
        // ---------------------------------------------------------------------------------

        static readonly EegBand k_Theta = EegBand.Theta;   // 4-8 Hz
        static readonly EegBand k_Alpha = EegBand.Alpha;   // 8-12 Hz

        /// <summary>
        /// The frontocentral theta set THIS ANALYSIS was asked for. It is NOT the project's
        /// FRONTAL_THETA ROI, which is F3/Fz/F4 with no Cz — the difference is reported rather
        /// than reconciled, because silently redefining a project ROI would change what every
        /// other tool means by the same name.
        /// </summary>
        static readonly string[] k_FrontocentralLabels = { "F3", "Fz", "F4", "Cz" };

        static readonly string[] k_PosteriorLabels = { "P3", "Pz", "P4" };

        // ---------------------------------------------------------------------------------
        // Entry point
        // ---------------------------------------------------------------------------------

        public static Result Run(Options options)
        {
            var result = new Result { sessionId = options != null ? options.sessionId : string.Empty };

            if (options == null || string.IsNullOrEmpty(options.sessionId))
            {
                result.failures.Add("no session id supplied");
                result.summary = "No session id supplied.";
                return result;
            }

            var sessionFolder = Path.Combine(EegRecordedWindowValidator.DataRoot(), options.sessionId);

            if (!Directory.Exists(sessionFolder))
            {
                result.failures.Add("session folder not found: " + sessionFolder);
                result.summary = "Session folder not found: " + sessionFolder;
                return result;
            }

            var output = Path.Combine(sessionFolder, OutputFolderName);
            Directory.CreateDirectory(output);
            result.outputFolder = output;

            var report = new StringBuilder();

            // ---- Events ------------------------------------------------------------------
            var eventPath = FindEventFile(sessionFolder);
            OfflineSessionEvents events = null;

            if (string.IsNullOrEmpty(eventPath))
            {
                result.warnings.Add("no events_*.csv in the session folder — no phase " +
                                    "annotation and no recognition analysis are possible");
            }
            else
            {
                events = OfflineSessionEvents.Load(eventPath, out var eventProblem);

                if (events == null)
                    result.warnings.Add("could not read the event CSV: " + eventProblem);
            }

            // ---- Raw EEG -----------------------------------------------------------------
            var rawPath = EegRecordedWindowValidator.FindRawEegFile(options.sessionId,
                out var rawProblem);

            if (string.IsNullOrEmpty(rawPath))
            {
                // A run with no EEG is a legitimate outcome, not a crash. Everything that can
                // still be said about the session is written, and the report says plainly that
                // no EEG analysis was performed.
                result.failures.Add("no raw EEG in this session: " + rawProblem);

                WriteNoEegReport(output, options, events, rawProblem, result);

                result.ok = false;
                result.summary = "No raw EEG recorded for " + options.sessionId +
                                 ". A report describing what the session does contain was written " +
                                 "to " + output + ".";
                return result;
            }

            var recording = EegRecordedWindowValidator.Load(rawPath, out var loadProblem);

            if (recording == null || recording.sampleCount == 0)
            {
                result.failures.Add("could not parse " + rawPath + ": " + loadProblem);
                result.summary = "Could not parse the raw EEG file: " + loadProblem;
                return result;
            }

            // ---- Which clock can be cut against ------------------------------------------
            var domain = EegRecordedWindowValidator.CheckAnalysisDomain(recording);
            var reconstructed = false;
            var reanchors = 0;

            if (!domain.ok)
            {
                result.warnings.Add("recorded analysis timeline unusable — " + domain.verdict +
                                    " Rebuilt from lsl_timestamp_local_raw through the " +
                                    "production EegAnalysisTimebase.");

                reanchors = EegRecordedWindowValidator.ReconstructAnalysis(recording, out _);
                reconstructed = true;
            }

            var analysis = new AnalysisRun
            {
                options = options,
                recording = recording,
                events = events,
                domain = domain,
                analysisReconstructed = reconstructed,
                reanchorCount = reanchors,
                rawPath = rawPath,
                eventPath = eventPath,
                outputFolder = output,
                result = result,
            };

            try
            {
                analysis.Execute(report);
                result.ok = result.failures.Count == 0;
            }
            catch (Exception e)
            {
                result.failures.Add(e.GetType().Name + ": " + e.Message);
                result.ok = false;

                report.AppendLine();
                report.AppendLine("## ANALYSIS ABORTED");
                report.AppendLine();
                report.AppendLine("`" + e.GetType().Name + ": " + e.Message + "`");
                report.AppendLine();
                report.AppendLine("Everything above was written before the failure.");

                Debug.LogError("[IKEA_EEG] Offline analysis failed: " + e);
            }

            var reportPath = Path.Combine(output, "report.md");
            File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
            result.filesWritten.Add(reportPath);

            result.summary = result.OneLine();
            return result;
        }

        static string FindEventFile(string sessionFolder)
        {
            var matches = Directory.GetFiles(sessionFolder, "events_*.csv");

            if (matches.Length == 0)
                return string.Empty;

            Array.Sort(matches);
            return matches[0];
        }

        static string FindSummaryFile(string sessionFolder)
        {
            var matches = Directory.GetFiles(sessionFolder, "session_summary_*.csv");

            if (matches.Length == 0)
                return string.Empty;

            Array.Sort(matches);
            return matches[0];
        }

        // ---------------------------------------------------------------------------------
        // The no-EEG report
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Writes a report for a session that recorded no EEG.
        ///
        /// Exists so that "this run has no EEG" is a WRITTEN, dated finding sitting next to the
        /// data rather than a console line someone has to remember. It states what the session
        /// does contain, which is what makes it possible to tell an aborted run apart from a
        /// failed acquisition.
        /// </summary>
        static void WriteNoEegReport(string output, Options options, OfflineSessionEvents events,
            string rawProblem, Result result)
        {
            var report = new StringBuilder();

            report.AppendLine("# Offline EEG analysis — " + options.sessionId);
            report.AppendLine();
            report.AppendLine("Generated " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture) + " by `IKEA_EEG/EEG/Offline Session Analysis`.");
            report.AppendLine();
            report.AppendLine("## NO EEG ANALYSIS WAS PERFORMED");
            report.AppendLine();
            report.AppendLine("This session contains **no raw EEG file**, so none of the requested");
            report.AppendLine("outputs — traces, PSD, band power, phase contrasts or recognition");
            report.AppendLine("epochs — could be produced. No plot in this folder is derived from");
            report.AppendLine("EEG, because there is none to derive it from.");
            report.AppendLine();
            report.AppendLine("Reason reported by the file search: `" + rawProblem + "`");
            report.AppendLine();

            if (events != null)
            {
                report.AppendLine("## What this session does contain");
                report.AppendLine();
                report.AppendLine("| Property | Value |");
                report.AppendLine("|---|---|");
                report.AppendLine("| Event rows | " + events.rows.Count + " |");
                report.AppendLine("| Event clock span | " + PlotFormat.F(events.spanSeconds, 3) + " s |");
                report.AppendLine("| Protocol mode logged | " +
                                  (string.IsNullOrEmpty(events.protocolMode)
                                      ? "(empty)"
                                      : events.protocolMode) + " |");
                report.AppendLine("| Recognition items | " + events.recognitionItems.Count + " |");
                report.AppendLine();

                report.AppendLine("### Phases");
                report.AppendLine();
                report.AppendLine("| Phase | Present | Start | End | Duration (s) | Derivation |");
                report.AppendLine("|---|---|---|---|---|---|");

                foreach (var phase in events.phases)
                {
                    report.AppendLine("| " + phase.name + " | " + (phase.present ? "yes" : "**no**") +
                                      " | " + (phase.present ? PlotFormat.F(phase.start, 3) : "—") +
                                      " | " + (phase.present ? PlotFormat.F(phase.end, 3) : "—") +
                                      " | " + (phase.present
                                          ? PlotFormat.F(phase.durationSeconds, 3)
                                          : "—") +
                                      " | " + phase.derivation + " |");
                }

                report.AppendLine();

                report.AppendLine("### Last 12 events");
                report.AppendLine();
                report.AppendLine("```");

                var from = Math.Max(0, events.rows.Count - 12);

                for (var i = from; i < events.rows.Count; i++)
                {
                    var row = events.rows[i];
                    report.AppendLine(PlotFormat.F(row.relativeSeconds, 3).PadLeft(10) + "  " +
                                      row.eventType.PadRight(30) + row.experimentState);
                }

                report.AppendLine("```");
                report.AppendLine();
            }
            else
            {
                report.AppendLine("No event CSV could be read either, so nothing can be said about");
                report.AppendLine("what this session contained.");
                report.AppendLine();
            }

            report.AppendLine("## What would have to change");
            report.AppendLine();
            report.AppendLine("A session usable for this analysis needs an AURA EEG stream connected");
            report.AppendLine("and recording for the duration of the run. Whether that stream was");
            report.AppendLine("present is a fact about the acquisition setup at recording time and");
            report.AppendLine("cannot be recovered from these files after the fact.");
            report.AppendLine();

            var path = Path.Combine(output, "report.md");
            File.WriteAllText(path, report.ToString(), new UTF8Encoding(false));
            result.filesWritten.Add(path);
        }

        // ---------------------------------------------------------------------------------
        // The analysis itself
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// One offline run. Kept as an instance so the many intermediate arrays are scoped to it
        /// rather than living in statics that a second run would inherit.
        /// </summary>
        class AnalysisRun
        {
            internal Options options;
            internal EegRecordedWindowValidator.Recording recording;
            internal OfflineSessionEvents events;
            internal EegRecordedWindowValidator.DomainCheck domain;
            internal bool analysisReconstructed;
            internal int reanchorCount;
            internal string rawPath;
            internal string eventPath;
            internal string outputFolder;
            internal Result result;

            AuraMontageConfig m_Montage;
            string[] m_Labels;

            double[] m_Time;              // analysis timestamps, session-relative seconds
            double[] m_AbsoluteTime;      // analysis timestamps, raw LSL clock
            double[][] m_Raw;             // [channel][sample]
            double[][] m_Filtered;        // [channel][sample]

            double m_EffectiveFs;
            double m_SettlingSamples;
            int m_ChannelCount;
            int m_SampleCount;
            double m_T0;

            // QC
            int m_NonMonotonic;
            int m_GapCount;
            double m_LargestGapSeconds;
            double m_LargestGapAt;
            double m_GapToleranceSeconds;

            readonly List<string> m_FlatChannels = new List<string>();
            readonly List<string> m_SaturatedChannels = new List<string>();
            readonly List<string> m_NonFiniteChannels = new List<string>();
            readonly List<string> m_DiscontinuousChannels = new List<string>();

            double[] m_MeanStep;
            double[] m_MaxStep;
            double[] m_MaxStepAt;

            // ---- Shared quality rules ------------------------------------------------------
            // The SAME classes the live pipeline uses, so this replay reports what the live
            // session would have flagged rather than a second opinion from a private copy.

            EegQualityThresholds m_Thresholds;
            EegChannelHealthTracker m_Health;

            List<EegChannelQualityRules.SimilarPair> m_NearIdenticalPairs =
                new List<EegChannelQualityRules.SimilarPair>();

            double m_HighestCorrelation = double.NaN;
            double[][] m_PairCorrelation;

            // Time course
            List<double> m_WindowCentre;
            List<double[]> m_WindowTheta;   // per window, per channel
            List<double[]> m_WindowAlpha;
            List<bool> m_WindowValid;
            List<string> m_WindowPhase;
            int m_InvalidWindows;

            /// <summary>
            /// Per-window ROI validity, captured as the health tracker advances.
            ///
            /// Recorded window by window rather than read once at the end, because the tracker's
            /// FINAL state answers the wrong question. An electrode that failed for a minute and
            /// then recovered leaves the tracker healthy, and a report that only asked at the end
            /// would print "ROI valid: yes" for a session in which the ROI was unusable for a
            /// third of its windows.
            /// </summary>
            List<bool> m_WindowFrontocentralValid;
            List<bool> m_WindowPosteriorValid;

            int[] m_FrontocentralIndices;
            int[] m_PosteriorIndices;
            int[] m_ProjectFrontalIndices;

            internal void Execute(StringBuilder report)
            {
                Prepare();
                Filter();
                MeasureTiming();
                AssessChannels();
                AssessNearIdentity();
                ComputeTimeCourse();

                WriteHeader(report);
                WriteQcSection(report);
                WriteChannelHealthSection(report);

                PlotTraces("raw_traces.png", m_Raw, "RAW (as recorded, AURA native units)");
                PlotTraces("filtered_traces.png", m_Filtered,
                    "FILTERED " + FilterDescription());

                WriteSessionPsd(report);
                WritePhaseSection(report);
                WriteTimeCourseSection(report);
                WriteRecognitionSection(report);
                WriteLimitations(report);
            }

            // ---- Setup ---------------------------------------------------------------

            void Prepare()
            {
                m_ChannelCount = recording.channelCount;
                m_SampleCount = recording.sampleCount;

                m_Montage = AuraMontageConfig.CreateHumanVerifiedDefault();

                m_Labels = new string[m_ChannelCount];

                for (var c = 0; c < m_ChannelCount; c++)
                {
                    var label = m_Montage.LabelOfIndex(c);
                    m_Labels[c] = string.IsNullOrEmpty(label)
                        ? "CH" + (c + 1).ToString(CultureInfo.InvariantCulture)
                        : label;
                }

                m_Thresholds = options.qualityThresholds.Sanitised();

                // The SAME state machine the live pipeline runs, fed the same way: windows in
                // order, clean windows only. That is what makes "would the live session have
                // caught this" a question this replay can actually answer.
                m_Health = new EegChannelHealthTracker(m_ChannelCount, m_Labels, m_Thresholds);

                m_FrontocentralIndices = ResolveLabels(k_FrontocentralLabels, "frontocentral theta");
                m_PosteriorIndices = ResolveLabels(k_PosteriorLabels, "posterior alpha");

                var projectRoi = m_Montage.ResolveRoi(AuraMontageConfig.FrontalThetaRoi,
                    out var roiProblem);

                m_ProjectFrontalIndices = projectRoi;

                if (projectRoi == null)
                {
                    result.warnings.Add("the project FRONTAL_THETA ROI could not be resolved: " +
                                        roiProblem);
                }

                m_AbsoluteTime = new double[m_SampleCount];
                m_Time = new double[m_SampleCount];
                m_Raw = new double[m_ChannelCount][];

                for (var c = 0; c < m_ChannelCount; c++)
                    m_Raw[c] = new double[m_SampleCount];

                m_T0 = recording.analysisTimestamps[0];

                for (var i = 0; i < m_SampleCount; i++)
                {
                    m_AbsoluteTime[i] = recording.analysisTimestamps[i];
                    m_Time[i] = m_AbsoluteTime[i] - m_T0;

                    var sample = recording.samples[i];

                    for (var c = 0; c < m_ChannelCount; c++)
                        m_Raw[c][i] = c < sample.Length ? sample[c] : double.NaN;
                }

                // EFFECTIVE rate, from the analysis timestamps, as requested. The nominal rate is
                // kept and reported alongside; it is never substituted for this one.
                var span = m_AbsoluteTime[m_SampleCount - 1] - m_AbsoluteTime[0];

                m_EffectiveFs = m_SampleCount > 1 && span > 0d
                    ? (m_SampleCount - 1) / span
                    : recording.nominalRateHz;

                if (m_EffectiveFs <= 0d)
                    throw new InvalidOperationException("could not establish a sample rate");
            }

            int[] ResolveLabels(string[] labels, string what)
            {
                var indices = new List<int>();

                foreach (var label in labels)
                {
                    var index = m_Montage.IndexOfLabel(label);

                    if (index < 0 || index >= m_ChannelCount)
                    {
                        result.warnings.Add("electrode " + label + " is not present in this " +
                                            "recording; it is excluded from " + what);
                        continue;
                    }

                    indices.Add(index);
                }

                return indices.ToArray();
            }

            string FilterDescription()
            {
                return "(" + PlotFormat.F(options.highPassHz, 2) + "-" +
                       PlotFormat.F(options.lowPassHz, 2) + " Hz" +
                       (options.notchEnabled
                           ? ", notch " + PlotFormat.F(options.notchHz, 1) + " Hz Q " +
                             PlotFormat.F(options.notchQ, 1)
                           : ", no notch") + ")";
            }

            // ---- Filtering -----------------------------------------------------------

            /// <summary>
            /// Runs the PRODUCTION <see cref="EegBandpassFilter"/> across the whole recording, one
            /// channel state per channel, in recorded order — the same per-sample call the live
            /// pipeline makes.
            ///
            /// The filter is constructed at the EFFECTIVE rate rather than the nominal one, so its
            /// cutoffs sit where they are meant to on this recording's actual timeline.
            /// </summary>
            void Filter()
            {
                var filter = new EegBandpassFilter(m_ChannelCount, m_EffectiveFs,
                    options.highPassHz, options.lowPassHz,
                    options.notchEnabled, options.notchHz, options.notchQ);

                m_SettlingSamples = filter.SettlingSamples;

                m_Filtered = new double[m_ChannelCount][];

                for (var c = 0; c < m_ChannelCount; c++)
                    m_Filtered[c] = new double[m_SampleCount];

                for (var i = 0; i < m_SampleCount; i++)
                for (var c = 0; c < m_ChannelCount; c++)
                    m_Filtered[c][i] = filter.Process(c, m_Raw[c][i]);
            }

            // ---- Timing QC -----------------------------------------------------------

            void MeasureTiming()
            {
                // The gap rule mirrors RawEegRingBuffer's constructor default of four nominal
                // sample intervals. It is restated here because that value lives in a default
                // parameter on a class this tool does not instantiate for the whole recording;
                // if that default changes, this line must change with it.
                var nominalInterval = recording.nominalRateHz > 0d
                    ? 1.0 / recording.nominalRateHz
                    : 1.0 / m_EffectiveFs;

                m_GapToleranceSeconds = nominalInterval * 4.0;
                m_LargestGapSeconds = 0d;
                m_LargestGapAt = double.NaN;

                for (var i = 1; i < m_SampleCount; i++)
                {
                    var step = m_AbsoluteTime[i] - m_AbsoluteTime[i - 1];

                    if (step <= 0d)
                        m_NonMonotonic++;

                    if (step > m_GapToleranceSeconds)
                    {
                        m_GapCount++;

                        if (step > m_LargestGapSeconds)
                        {
                            m_LargestGapSeconds = step;
                            m_LargestGapAt = m_AbsoluteTime[i - 1];
                        }
                    }
                }
            }

            /// <summary>
            /// Flat / saturated / non-finite / discontinuous channel detection, over the whole
            /// recording.
            ///
            /// NO LONGER DUPLICATED. Both the measurement and the rules now come from
            /// <see cref="EegChannelQualityRules"/>, which is the same code the live pipeline
            /// runs. This file previously carried its own copy of the Flatline, SaturationLike
            /// and AbruptDiscontinuity rules because they were private to a MonoBehaviour that
            /// could not run against a file; extracting them removed that copy, so the two can no
            /// longer drift apart.
            ///
            /// The discontinuity check matters more than it looks: a step shared by every channel
            /// at the same instant is an amplifier or reference event, and a 4 s window containing
            /// one produces a band power orders of magnitude above its neighbours — which then
            /// dominates any mean it lands in.
            /// </summary>
            void AssessChannels()
            {
                m_MeanStep = new double[m_ChannelCount];
                m_MaxStep = new double[m_ChannelCount];
                m_MaxStepAt = new double[m_ChannelCount];

                for (var c = 0; c < m_ChannelCount; c++)
                {
                    var stats = EegChannelQualityRules.Measure(m_Raw[c]);
                    var reason = EegChannelQualityRules.EvaluateWindow(stats, m_Thresholds);

                    m_MeanStep[c] = stats.meanStep;
                    m_MaxStep[c] = stats.maxStep;
                    m_MaxStepAt[c] = stats.maxStepIndex >= 0 &&
                                     stats.maxStepIndex < m_AbsoluteTime.Length
                        ? m_AbsoluteTime[stats.maxStepIndex]
                        : double.NaN;

                    if ((reason & EegDegradationReason.NonFinite) != 0)
                        m_NonFiniteChannels.Add(m_Labels[c] + " (" + stats.nonFinite + " sample(s))");

                    if ((reason & EegDegradationReason.Flatline) != 0)
                        m_FlatChannels.Add(m_Labels[c]);

                    if ((reason & EegDegradationReason.Saturation) != 0)
                        m_SaturatedChannels.Add(m_Labels[c]);

                    if ((reason & EegDegradationReason.Discontinuity) != 0)
                    {
                        m_DiscontinuousChannels.Add(m_Labels[c] + " (step " +
                                                    PlotFormat.Sci(stats.maxStep) + " = " +
                                                    PlotFormat.F(stats.stepRatio, 0) +
                                                    "x mean, at " +
                                                    PlotFormat.F(m_MaxStepAt[c] - m_T0, 3) + " s)");
                    }
                }
            }

            /// <summary>
            /// Measures how similar the channels are to one another, on the FILTERED signal.
            ///
            /// The filtered signal, not the raw, and the distinction decides whether the number
            /// means anything: raw AURA channels share a large DC offset and a slow common drift,
            /// and correlating those gives a high value for almost any pair on almost any
            /// recording. After the 1 Hz high-pass what is left is the part that could have been
            /// independent — so a correlation that is still near 1 says the channels carry the
            /// same signal, rather than saying they share a baseline.
            ///
            /// The full pairwise matrix is kept, not just the flagged pairs: a recording sitting
            /// just under the threshold is exactly what a researcher needs to see, and a check
            /// that only prints failures hides it.
            /// </summary>
            void AssessNearIdentity()
            {
                m_PairCorrelation = new double[m_ChannelCount][];

                for (var a = 0; a < m_ChannelCount; a++)
                {
                    m_PairCorrelation[a] = new double[m_ChannelCount];

                    for (var b = 0; b < m_ChannelCount; b++)
                        m_PairCorrelation[a][b] = double.NaN;
                }

                // Post-settling only: the filter's start-up transient is common to every channel
                // by construction, and including it would manufacture the very similarity this
                // check is looking for.
                var start = (int)Math.Ceiling(m_SettlingSamples);
                var count = m_SampleCount - start;

                if (count < 2 || m_ChannelCount < 2)
                    return;

                var slices = new double[m_ChannelCount][];

                for (var c = 0; c < m_ChannelCount; c++)
                {
                    var slice = new double[count];
                    Array.Copy(m_Filtered[c], start, slice, 0, count);
                    slices[c] = slice;
                }

                for (var a = 0; a < m_ChannelCount; a++)
                {
                    for (var b = a + 1; b < m_ChannelCount; b++)
                    {
                        var r = EegChannelQualityRules.Correlation(slices[a], slices[b]);

                        m_PairCorrelation[a][b] = r;
                        m_PairCorrelation[b][a] = r;

                        if (double.IsNaN(r))
                            continue;

                        var magnitude = Math.Abs(r);

                        if (double.IsNaN(m_HighestCorrelation) || magnitude > m_HighestCorrelation)
                            m_HighestCorrelation = magnitude;
                    }
                }

                m_NearIdenticalPairs = EegChannelQualityRules.FindNearIdenticalPairs(
                    slices, m_ChannelCount, m_Thresholds.nearIdenticalCorrelation);

                if (m_NearIdenticalPairs.Count >= m_Thresholds.nearIdenticalMinimumPairs)
                {
                    result.warnings.Add(m_NearIdenticalPairs.Count + " channel pair(s) correlate " +
                                        "at or above " +
                                        PlotFormat.F(m_Thresholds.nearIdenticalCorrelation, 4) +
                                        " on filtered signal — the channels are not independent, " +
                                        "so any regional contrast between them compares a signal " +
                                        "with itself");
                }
            }

            // ---- Spectra -------------------------------------------------------------

            /// <summary>
            /// Welch PSD of one channel over a sample range, through the project's analyzer, with
            /// the run's configured segment length, overlap and FFT length.
            /// </summary>
            PsdResult Psd(double[] series, int start, int count)
            {
                if (start < 0 || count <= 0 || start + count > series.Length)
                    return null;

                var slice = new double[count];
                Array.Copy(series, start, slice, 0, count);

                for (var i = 0; i < count; i++)
                {
                    if (double.IsNaN(slice[i]) || double.IsInfinity(slice[i]))
                        return null;
                }

                try
                {
                    return EegSpectralAnalyzer.Welch(slice, m_EffectiveFs,
                        options.welchSegmentSeconds, options.welchOverlapFraction,
                        options.fftLength);
                }
                catch (Exception)
                {
                    // A slice too short for one segment is not an error worth aborting the run
                    // for; it is an invalid window and is counted as one.
                    return null;
                }
            }

            /// <summary>Index of the first sample at or after a timestamp, or -1.</summary>
            int IndexAtOrAfter(double timestamp)
            {
                var lo = 0;
                var hi = m_SampleCount - 1;

                if (timestamp <= m_AbsoluteTime[0])
                    return 0;

                if (timestamp > m_AbsoluteTime[hi])
                    return -1;

                while (lo < hi)
                {
                    var mid = (lo + hi) / 2;

                    if (m_AbsoluteTime[mid] < timestamp)
                        lo = mid + 1;
                    else
                        hi = mid;
                }

                return lo;
            }

            /// <summary>
            /// The sample range covering [from, to] in the analysis clock, or count 0 when the
            /// interval is not fully inside the recording.
            /// </summary>
            bool RangeFor(double from, double to, out int start, out int count)
            {
                start = 0;
                count = 0;

                if (double.IsNaN(from) || double.IsNaN(to) || to <= from)
                    return false;

                if (from < m_AbsoluteTime[0] || to > m_AbsoluteTime[m_SampleCount - 1])
                    return false;

                var a = IndexAtOrAfter(from);
                var b = IndexAtOrAfter(to);

                if (a < 0)
                    return false;

                if (b < 0)
                    b = m_SampleCount - 1;

                if (b <= a)
                    return false;

                start = a;
                count = b - a;
                return true;
            }

            /// <summary>True when the interval contains a gap larger than the tolerance.</summary>
            bool HasGap(int start, int count)
            {
                for (var i = start + 1; i < start + count; i++)
                {
                    if (m_AbsoluteTime[i] - m_AbsoluteTime[i - 1] > m_GapToleranceSeconds)
                        return true;
                }

                return false;
            }

            // ---- Time course ---------------------------------------------------------

            /// <summary>
            /// Slides the configured spectral window across the whole filtered recording and
            /// records theta and alpha per channel for each position.
            ///
            /// A window is INVALID, and stored as NaN rather than dropped, when it starts inside
            /// the filter's settling time, contains a timing gap, or is too short for one Welch
            /// segment. Keeping the position and marking it invalid means the time axis of every
            /// plot stays honest about where there is no measurement.
            /// </summary>
            void ComputeTimeCourse()
            {
                m_WindowCentre = new List<double>();
                m_WindowTheta = new List<double[]>();
                m_WindowAlpha = new List<double[]>();
                m_WindowValid = new List<bool>();
                m_WindowPhase = new List<string>();
                m_WindowFrontocentralValid = new List<bool>();
                m_WindowPosteriorValid = new List<bool>();

                var windowSamples = (int)Math.Round(options.spectralWindowSeconds * m_EffectiveFs);
                var stepSamples = Math.Max(1,
                    (int)Math.Round(options.timeCourseStepSeconds * m_EffectiveFs));

                if (windowSamples < 8 || windowSamples > m_SampleCount)
                {
                    result.warnings.Add("the recording is shorter than one " +
                                        PlotFormat.F(options.spectralWindowSeconds, 2) +
                                        " s spectral window; no band-power time course was " +
                                        "produced");
                    return;
                }

                for (var start = 0; start + windowSamples <= m_SampleCount; start += stepSamples)
                {
                    var centre = m_AbsoluteTime[start + windowSamples / 2];

                    var theta = new double[m_ChannelCount];
                    var alpha = new double[m_ChannelCount];

                    var valid = start >= m_SettlingSamples && !HasGap(start, windowSamples);

                    for (var c = 0; c < m_ChannelCount; c++)
                    {
                        if (!valid)
                        {
                            theta[c] = double.NaN;
                            alpha[c] = double.NaN;
                            continue;
                        }

                        var psd = Psd(m_Filtered[c], start, windowSamples);

                        if (psd == null)
                        {
                            valid = false;
                            theta[c] = double.NaN;
                            alpha[c] = double.NaN;
                            continue;
                        }

                        theta[c] = EegSpectralAnalyzer.BandPower(psd, k_Theta);
                        alpha[c] = EegSpectralAnalyzer.BandPower(psd, k_Alpha);
                    }

                    if (!valid)
                    {
                        m_InvalidWindows++;
                    }
                    else
                    {
                        // Only clean windows reach the health tracker, exactly as in the live
                        // path: a window the analysis has already rejected says nothing about an
                        // electrode, and letting it into the per-channel baselines would make the
                        // start of every recording look like a fault.
                        var slices = new double[m_ChannelCount][];

                        for (var c = 0; c < m_ChannelCount; c++)
                        {
                            var slice = new double[windowSamples];
                            Array.Copy(m_Filtered[c], start, slice, 0, windowSamples);
                            slices[c] = slice;
                        }

                        var statsSet = ChannelStatsSet.Measure(slices, m_ChannelCount);
                        m_Health.Submit(centre, statsSet, theta, RoisContaining);
                    }

                    // Sampled here, immediately after the tracker advanced, so it reflects the
                    // health state AT THIS WINDOW rather than at the end of the recording. A
                    // window that was itself invalid cannot support a valid ROI either.
                    m_WindowFrontocentralValid.Add(valid &&
                        !RoiHasDegradedChannel(m_FrontocentralIndices, out _));

                    m_WindowPosteriorValid.Add(valid &&
                        !RoiHasDegradedChannel(m_PosteriorIndices, out _));

                    m_WindowCentre.Add(centre);
                    m_WindowTheta.Add(theta);
                    m_WindowAlpha.Add(alpha);
                    m_WindowValid.Add(valid);
                    m_WindowPhase.Add(events != null ? events.PhaseAt(centre) : string.Empty);
                }
            }

            /// <summary>
            /// The ROI names a channel belongs to, so a health transition can say which feature
            /// it invalidates. Mirrors EegFeaturePipeline.RoisContaining.
            /// </summary>
            string RoisContaining(int channel)
            {
                var names = new List<string>();

                foreach (var index in m_FrontocentralIndices)
                {
                    if (index != channel)
                        continue;

                    names.Add("FRONTOCENTRAL_THETA (F3/Fz/F4/Cz)");
                    break;
                }

                foreach (var index in m_PosteriorIndices)
                {
                    if (index != channel)
                        continue;

                    names.Add("POSTERIOR_ALPHA (P3/Pz/P4)");
                    break;
                }

                foreach (var index in m_ProjectFrontalIndices ?? Array.Empty<int>())
                {
                    if (index != channel)
                        continue;

                    names.Add("FRONTAL_THETA (project ROI, F3/Fz/F4)");
                    break;
                }

                return string.Join(", ", names);
            }

            static int CountFalse(List<bool> flags)
            {
                if (flags == null)
                    return 0;

                var n = 0;

                foreach (var flag in flags)
                {
                    if (!flag)
                        n++;
                }

                return n;
            }

            /// <summary>True when an ROI contains a channel the tracker has declared degraded.</summary>
            bool RoiHasDegradedChannel(int[] indices, out string problem)
            {
                problem = string.Empty;

                if (indices == null || m_Health == null)
                    return false;

                foreach (var index in indices)
                {
                    if (!m_Health.IsDegraded(index))
                        continue;

                    var since = m_Health.DegradedSince(index);

                    problem = m_Labels[index] + " degraded since t=" +
                              (double.IsNaN(since)
                                  ? "unknown"
                                  : PlotFormat.F(since - m_T0, 1) + " s") +
                              " (" + EegChannelQualityRules.Describe(m_Health.ReasonFor(index)) +
                              ")";

                    return true;
                }

                return false;
            }

            /// <summary>Mean across a set of channel indices, NaN if any contributor is NaN.</summary>
            static double RoiMean(double[] perChannel, int[] indices)
            {
                if (indices == null || indices.Length == 0)
                    return double.NaN;

                var sum = 0d;

                foreach (var index in indices)
                {
                    if (index < 0 || index >= perChannel.Length)
                        return double.NaN;

                    var v = perChannel[index];

                    if (double.IsNaN(v) || double.IsInfinity(v))
                        return double.NaN;

                    sum += v;
                }

                return sum / indices.Length;
            }

            // ---- Report: header ------------------------------------------------------

            void WriteHeader(StringBuilder report)
            {
                report.AppendLine("# Offline EEG analysis — " + options.sessionId);
                report.AppendLine();
                report.AppendLine("Generated " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss",
                                      CultureInfo.InvariantCulture) +
                                  " by `IKEA_EEG/EEG/Offline Session Analysis`.");
                report.AppendLine();
                report.AppendLine("**Engineering and exploratory validation only.** Every number");
                report.AppendLine("below describes a recorded signal and the code that processed it.");
                report.AppendLine("Nothing here is evidence about cognition, workload, memory or");
                report.AppendLine("clinical state, and no such interpretation is offered or implied.");
                report.AppendLine();

                report.AppendLine("## Inputs");
                report.AppendLine();
                report.AppendLine("| File | Path |");
                report.AppendLine("|---|---|");
                report.AppendLine("| Raw EEG | `" + rawPath + "` |");
                report.AppendLine("| Events | `" +
                                  (string.IsNullOrEmpty(eventPath) ? "(none)" : eventPath) + "` |");

                var summaryPath = FindSummaryFile(Path.GetDirectoryName(rawPath));
                report.AppendLine("| Session summary | `" +
                                  (string.IsNullOrEmpty(summaryPath) ? "(none)" : summaryPath) +
                                  "` |");
                report.AppendLine();

                report.AppendLine("## Parameters actually used");
                report.AppendLine();
                report.AppendLine("Preprocessing values came from: **" + options.configSource + "**.");
                report.AppendLine();
                report.AppendLine("| Parameter | Value | Source |");
                report.AppendLine("|---|---|---|");
                report.AppendLine("| High-pass | " + PlotFormat.F(options.highPassHz, 3) +
                                  " Hz | EegFeaturePipeline.m_HighPassHz |");
                report.AppendLine("| Low-pass | " + PlotFormat.F(options.lowPassHz, 3) +
                                  " Hz | EegFeaturePipeline.m_LowPassHz |");
                report.AppendLine("| Notch | " + (options.notchEnabled
                                      ? PlotFormat.F(options.notchHz, 2) + " Hz, Q " +
                                        PlotFormat.F(options.notchQ, 2)
                                      : "**disabled**") +
                                  " | EegFeaturePipeline.m_NotchEnabled |");
                report.AppendLine("| Filter | 4th-order Butterworth band-pass, cascaded biquads | " +
                                  "EegBandpassFilter (production class) |");
                report.AppendLine("| Filter settling | " + m_SettlingSamples + " samples (" +
                                  PlotFormat.F(m_SettlingSamples / m_EffectiveFs, 2) +
                                  " s) | EegBandpassFilter.SettlingSamples |");
                report.AppendLine("| Spectral window | " +
                                  PlotFormat.F(options.spectralWindowSeconds, 2) +
                                  " s | EegFeaturePipeline.m_WindowSeconds |");
                report.AppendLine("| Welch segment | " +
                                  PlotFormat.F(options.welchSegmentSeconds, 2) +
                                  " s | EegFeaturePipeline.m_WelchSegmentSeconds |");
                report.AppendLine("| Welch overlap | " +
                                  PlotFormat.F(options.welchOverlapFraction * 100d, 0) +
                                  " % | EegSpectralAnalyzer.Welch default |");
                report.AppendLine("| Window function | periodic Hann | EegSpectralAnalyzer.HannWindow |");
                report.AppendLine("| FFT length | " + options.fftLength +
                                  " | requested; equals NextPowerOfTwo(" +
                                  (int)Math.Round(options.welchSegmentSeconds * m_EffectiveFs) +
                                  ") that the live path derives at this rate |");
                report.AppendLine("| Theta | " + PlotFormat.F(k_Theta.lowHz, 1) + "–" +
                                  PlotFormat.F(k_Theta.highHz, 1) + " Hz | EegBand.Theta |");
                report.AppendLine("| Alpha | " + PlotFormat.F(k_Alpha.lowHz, 1) + "–" +
                                  PlotFormat.F(k_Alpha.highHz, 1) + " Hz | EegBand.Alpha |");
                report.AppendLine("| Sample rate used | " + PlotFormat.F(m_EffectiveFs, 4) +
                                  " Hz (EFFECTIVE, from analysis timestamps) | this tool |");
                report.AppendLine("| Nominal rate | " + PlotFormat.F(recording.nominalRateHz, 3) +
                                  " Hz | raw_eeg header |");
                report.AppendLine("| Amplitude units | AURA native units, unconverted | " +
                                  "AuraMontageConfig.amplitudeUnitLabel |");
                report.AppendLine("| PSD units | " + m_Montage.PowerSpectralDensityUnitLabel +
                                  " | AuraMontageConfig |");
                report.AppendLine();

                report.AppendLine("### Electrode mapping used");
                report.AppendLine();
                report.AppendLine("| Stream channel | Electrode |");
                report.AppendLine("|---|---|");

                for (var c = 0; c < m_ChannelCount; c++)
                    report.AppendLine("| ch" + (c + 1) + " | " + m_Labels[c] + " |");

                report.AppendLine();
                report.AppendLine("Mapping source: `AuraMontageConfig.CreateHumanVerifiedDefault()`.");
                report.AppendLine("The raw file's own header disclaims a verified montage (`" +
                                  "montage_unverified=" + recording.montageUnverified +
                                  "`); the mapping above is applied by this tool from the project");
                report.AppendLine("configuration, not read from the stream.");
                report.AppendLine();
            }

            // ---- Report: QC ----------------------------------------------------------

            void WriteQcSection(StringBuilder report)
            {
                report.AppendLine("## Timing and QC");
                report.AppendLine();
                report.AppendLine("| Measure | Value |");
                report.AppendLine("|---|---|");
                report.AppendLine("| Samples | " + m_SampleCount + " |");
                report.AppendLine("| Channels | " + m_ChannelCount + " |");
                report.AppendLine("| Recorded span | " +
                                  PlotFormat.F(m_AbsoluteTime[m_SampleCount - 1] -
                                               m_AbsoluteTime[0], 3) + " s |");
                report.AppendLine("| Effective Fs (analysis clock) | " +
                                  PlotFormat.F(m_EffectiveFs, 4) + " Hz |");
                report.AppendLine("| Nominal Fs | " + PlotFormat.F(recording.nominalRateHz, 3) +
                                  " Hz |");
                report.AppendLine("| Effective / nominal | " +
                                  PlotFormat.F(100d * m_EffectiveFs / recording.nominalRateHz, 3) +
                                  " % |");
                report.AppendLine("| Timestamps monotonic | " +
                                  (m_NonMonotonic == 0
                                      ? "yes"
                                      : "**no — " + m_NonMonotonic + " non-increasing step(s)**") +
                                  " |");
                report.AppendLine("| Gap tolerance | " +
                                  PlotFormat.F(m_GapToleranceSeconds * 1000d, 2) +
                                  " ms (4 nominal intervals) |");
                report.AppendLine("| Gaps | " + m_GapCount +
                                  (m_GapCount > 0
                                      ? ", largest " + PlotFormat.F(m_LargestGapSeconds * 1000d, 2) +
                                        " ms at " + PlotFormat.F(m_LargestGapAt, 3)
                                      : "") + " |");
                report.AppendLine("| Flat channels | " +
                                  (m_FlatChannels.Count == 0
                                      ? "none"
                                      : "**" + string.Join(", ", m_FlatChannels) + "**") + " |");
                report.AppendLine("| Saturated / stuck channels | " +
                                  (m_SaturatedChannels.Count == 0
                                      ? "none"
                                      : "**" + string.Join(", ", m_SaturatedChannels) + "**") + " |");
                report.AppendLine("| Non-finite samples | " +
                                  (m_NonFiniteChannels.Count == 0
                                      ? "none"
                                      : "**" + string.Join(", ", m_NonFiniteChannels) + "**") + " |");
                report.AppendLine("| Abrupt discontinuities | " +
                                  (m_DiscontinuousChannels.Count == 0
                                      ? "none"
                                      : "**" + m_DiscontinuousChannels.Count +
                                        " channel(s)** — see below") + " |");
                report.AppendLine("| Spectral windows | " + (m_WindowCentre?.Count ?? 0) + " |");
                report.AppendLine("| Invalid windows | " + m_InvalidWindows +
                                  " (settling, gap, or too short for one Welch segment) |");
                report.AppendLine();

                report.AppendLine("### Analysis clock");
                report.AppendLine();
                report.AppendLine("`" + domain.verdict + "`");
                report.AppendLine();

                if (analysisReconstructed)
                {
                    report.AppendLine("The recorded `lsl_timestamp_analysis` column was **not**");
                    report.AppendLine("usable, so the analysis timeline was rebuilt from");
                    report.AppendLine("`lsl_timestamp_local_raw` through the production");
                    report.AppendLine("`EegAnalysisTimebase` (" + reanchorCount + " re-anchor(s)).");
                    report.AppendLine("Every epoch below was cut against that rebuilt timeline.");
                }
                else
                {
                    report.AppendLine("The recorded `lsl_timestamp_analysis` column was used as");
                    report.AppendLine("written. Median offset from the raw local clock: " +
                                      PlotFormat.F(domain.medianOffsetSeconds * 1000d, 2) + " ms.");
                }

                report.AppendLine();

                if (m_DiscontinuousChannels.Count > 0)
                {
                    report.AppendLine("### Abrupt discontinuities");
                    report.AppendLine();
                    report.AppendLine("Channels carrying a single sample-to-sample step more than");
                    report.AppendLine("20x their own mean step. When several channels share a step");
                    report.AppendLine("at the same instant, the cause is upstream of the scalp — an");
                    report.AppendLine("amplifier, reference or connection event — and every");
                    report.AppendLine("spectral window overlapping it is contaminated.");
                    report.AppendLine();

                    foreach (var channel in m_DiscontinuousChannels)
                        report.AppendLine("* " + channel);

                    report.AppendLine();

                    var when = new List<double>();

                    foreach (var at in m_MaxStepAt)
                    {
                        if (!double.IsNaN(at))
                            when.Add(at);
                    }

                    if (when.Count >= 2)
                    {
                        var spreadMs = (Max(when) - Min(when)) * 1000d;

                        report.AppendLine("Largest step across channels spans " +
                                          PlotFormat.F(spreadMs, 2) + " ms" +
                                          (spreadMs < 50d
                                              ? " — **simultaneous across channels**, so this is a " +
                                                "recording event, not a per-electrode one."
                                              : "."));
                        report.AppendLine();
                    }

                    result.warnings.Add(m_DiscontinuousChannels.Count + " channel(s) contain an " +
                                        "abrupt discontinuity; windows overlapping it are " +
                                        "contaminated and are flagged, not removed");
                }

                // Per-channel amplitude statistics.
                var channelCsv = new StringBuilder();
                channelCsv.AppendLine("channel,electrode,min,max,range,mean,rms,flat,saturated," +
                                      "non_finite_samples,mean_step,max_step,max_step_ratio," +
                                      "max_step_at_relative_s,abrupt_discontinuity");

                report.AppendLine("### Per-channel amplitude (AURA native units, raw)");
                report.AppendLine();
                report.AppendLine("| Electrode | Min | Max | Range | Mean | RMS | Max step / mean step |");
                report.AppendLine("|---|---|---|---|---|---|---|");

                for (var c = 0; c < m_ChannelCount; c++)
                {
                    double min = double.MaxValue, max = double.MinValue, sum = 0d, sumSq = 0d;
                    var n = 0;
                    var nonFinite = 0;

                    foreach (var v in m_Raw[c])
                    {
                        if (double.IsNaN(v) || double.IsInfinity(v))
                        {
                            nonFinite++;
                            continue;
                        }

                        if (v < min) min = v;
                        if (v > max) max = v;
                        sum += v;
                        sumSq += v * v;
                        n++;
                    }

                    var mean = n > 0 ? sum / n : double.NaN;
                    var rms = n > 0 ? Math.Sqrt(sumSq / n) : double.NaN;
                    var range = n > 0 ? max - min : double.NaN;

                    var ratio = m_MeanStep[c] > 0d ? m_MaxStep[c] / m_MeanStep[c] : double.NaN;
                    var abrupt = m_MeanStep[c] > 0d && m_MaxStep[c] > m_MeanStep[c] * 20.0;

                    report.AppendLine("| " + m_Labels[c] + " | " + PlotFormat.Sci(min) + " | " +
                                      PlotFormat.Sci(max) + " | " + PlotFormat.Sci(range) + " | " +
                                      PlotFormat.Sci(mean) + " | " + PlotFormat.Sci(rms) + " | " +
                                      (abrupt ? "**" + PlotFormat.F(ratio, 0) + "x**"
                                          : PlotFormat.F(ratio, 0) + "x") + " |");

                    channelCsv.AppendLine("ch" + (c + 1) + "," + m_Labels[c] + "," +
                                          PlotFormat.R(min) + "," + PlotFormat.R(max) + "," +
                                          PlotFormat.R(range) + "," + PlotFormat.R(mean) + "," +
                                          PlotFormat.R(rms) + "," +
                                          (m_FlatChannels.Contains(m_Labels[c]) ? "1" : "0") + "," +
                                          (m_SaturatedChannels.Contains(m_Labels[c]) ? "1" : "0") +
                                          "," + nonFinite + "," +
                                          PlotFormat.R(m_MeanStep[c]) + "," +
                                          PlotFormat.R(m_MaxStep[c]) + "," +
                                          PlotFormat.R(ratio) + "," +
                                          PlotFormat.R(m_MaxStepAt[c] - m_T0) + "," +
                                          (abrupt ? "1" : "0"));
                }

                report.AppendLine();
                WriteFile("qc_channels.csv", channelCsv.ToString());

                var timingCsv = new StringBuilder();
                timingCsv.AppendLine("measure,value");
                timingCsv.AppendLine("samples," + m_SampleCount);
                timingCsv.AppendLine("channels," + m_ChannelCount);
                timingCsv.AppendLine("span_seconds," +
                                     PlotFormat.R(m_AbsoluteTime[m_SampleCount - 1] -
                                                  m_AbsoluteTime[0]));
                timingCsv.AppendLine("effective_fs_hz," + PlotFormat.R(m_EffectiveFs));
                timingCsv.AppendLine("nominal_fs_hz," + PlotFormat.R(recording.nominalRateHz));
                timingCsv.AppendLine("non_monotonic_steps," + m_NonMonotonic);
                timingCsv.AppendLine("gap_tolerance_seconds," +
                                     PlotFormat.R(m_GapToleranceSeconds));
                timingCsv.AppendLine("gap_count," + m_GapCount);
                timingCsv.AppendLine("largest_gap_seconds," + PlotFormat.R(m_LargestGapSeconds));
                timingCsv.AppendLine("analysis_clock_reconstructed," +
                                     (analysisReconstructed ? "1" : "0"));
                timingCsv.AppendLine("reanchor_count," + reanchorCount);
                timingCsv.AppendLine("spectral_windows," + (m_WindowCentre?.Count ?? 0));
                timingCsv.AppendLine("invalid_windows," + m_InvalidWindows);
                WriteFile("qc_timing.csv", timingCsv.ToString());
            }

            // ---- Report: channel independence and health ------------------------------

            /// <summary>
            /// Reports the two checks that need more than one window or more than one channel:
            /// whether the channels are independent of each other, and whether any of them broke
            /// during the run.
            /// </summary>
            void WriteChannelHealthSection(StringBuilder report)
            {
                report.AppendLine("## Channel independence and health");
                report.AppendLine();
                report.AppendLine("Rules and thresholds are the project's own");
                report.AppendLine("(`EegChannelQualityRules` / `EegChannelHealthTracker`), the same");
                report.AppendLine("code the live pipeline runs. Every threshold is an **engineering");
                report.AppendLine("heuristic**, relative and unit-free — none is a scientific");
                report.AppendLine("constant and none is in microvolts:");
                report.AppendLine();
                report.AppendLine("> " + m_Thresholds.Describe());
                report.AppendLine();

                // ---- Independence --------------------------------------------------------
                report.AppendLine("### Inter-channel similarity");
                report.AppendLine();
                report.AppendLine("Pearson correlation on the **filtered** post-settling signal.");
                report.AppendLine("Filtered, not raw: these channels share a large DC offset and a");
                report.AppendLine("slow common drift, and correlating those would score almost any");
                report.AppendLine("pair near 1 regardless of what the electrodes were doing.");
                report.AppendLine();

                if (m_ChannelCount >= 2 && m_PairCorrelation != null)
                {
                    report.Append("| |");

                    for (var c = 0; c < m_ChannelCount; c++)
                        report.Append(' ').Append(m_Labels[c]).Append(" |");

                    report.AppendLine();
                    report.Append("|---|");

                    for (var c = 0; c < m_ChannelCount; c++)
                        report.Append("---|");

                    report.AppendLine();

                    for (var a = 0; a < m_ChannelCount; a++)
                    {
                        report.Append("| **").Append(m_Labels[a]).Append("** |");

                        for (var b = 0; b < m_ChannelCount; b++)
                        {
                            if (a == b)
                            {
                                report.Append(" — |");
                                continue;
                            }

                            var r = m_PairCorrelation[a][b];

                            if (double.IsNaN(r))
                            {
                                report.Append(" n/a |");
                                continue;
                            }

                            var flagged = Math.Abs(r) >= m_Thresholds.nearIdenticalCorrelation;

                            report.Append(' ')
                                .Append(flagged ? "**" : "")
                                .Append(PlotFormat.F(r, 4))
                                .Append(flagged ? "**" : "")
                                .Append(" |");
                        }

                        report.AppendLine();
                    }

                    report.AppendLine();
                }

                report.AppendLine("Highest absolute pairwise correlation: **" +
                                  (double.IsNaN(m_HighestCorrelation)
                                      ? "n/a"
                                      : PlotFormat.F(m_HighestCorrelation, 6)) + "**");
                report.AppendLine();

                if (m_NearIdenticalPairs.Count >= m_Thresholds.nearIdenticalMinimumPairs)
                {
                    report.AppendLine("**NEAR-IDENTICAL CHANNELS: " + m_NearIdenticalPairs.Count +
                                      " pair(s) at or above " +
                                      PlotFormat.F(m_Thresholds.nearIdenticalCorrelation, 4) + ".**");
                    report.AppendLine();
                    report.AppendLine("These channels carry too little independent variance to be");
                    report.AppendLine("treated as separate measurements, so a regional contrast");
                    report.AppendLine("between them is comparing a signal with itself.");
                    report.AppendLine();
                    report.AppendLine("This is an engineering observation about the recording. It");
                    report.AppendLine("does **not** identify a cause — reference configuration, a");
                    report.AppendLine("montage error, a driver fault and a genuinely");
                    report.AppendLine("common-mode-dominated recording all look the same from here —");
                    report.AppendLine("and it makes no claim about physiology.");
                    report.AppendLine();
                }
                else
                {
                    report.AppendLine("No pair reached the near-identity threshold.");
                    report.AppendLine();
                }

                // ---- Health --------------------------------------------------------------
                report.AppendLine("### Channel degradation / dropout");
                report.AppendLine();
                report.AppendLine("Each channel is judged against **its own** established baseline,");
                report.AppendLine("window by window, so an electrode that worked and then stopped is");
                report.AppendLine("visible even when the whole cap is noisy. A channel must fail for");
                report.AppendLine(m_Thresholds.degradationConsecutiveWindows +
                                  " consecutive window(s) to be called degraded, and pass for " +
                                  m_Thresholds.recoveryWindows);
                report.AppendLine("to be called recovered.");
                report.AppendLine();

                var transitions = m_Health != null
                    ? m_Health.transitions
                    : (IReadOnlyList<EegQcTransition>)Array.Empty<EegQcTransition>();

                if (transitions.Count == 0)
                {
                    report.AppendLine("No channel changed health state during this recording.");
                    report.AppendLine();
                }
                else
                {
                    report.AppendLine("| t (s) | Electrode | State | Reason | Affected feature |");
                    report.AppendLine("|---|---|---|---|---|");

                    foreach (var transition in transitions)
                    {
                        report.AppendLine("| " + PlotFormat.F(transition.timestamp - m_T0, 1) +
                                          " | **" + transition.electrode + "** | " +
                                          (transition.degraded ? "**DEGRADED**" : "recovered") +
                                          " | " + transition.detail + " | " +
                                          (string.IsNullOrEmpty(transition.affectedRois)
                                              ? "(in no ROI)"
                                              : transition.affectedRois) + " |");
                    }

                    report.AppendLine();

                    var degraded = m_Health.DegradedChannels();

                    if (degraded.Length > 0)
                    {
                        var names = new List<string>();

                        foreach (var c in degraded)
                            names.Add(m_Labels[c]);

                        report.AppendLine("Still degraded at the end of the recording: **" +
                                          string.Join(", ", names) + "**.");
                        report.AppendLine();

                        result.warnings.Add("channel(s) " + string.Join(", ", names) +
                                            " ended the recording in a degraded state");
                    }
                }

                // ---- ROI validity --------------------------------------------------------
                report.AppendLine("### ROI validity");
                report.AppendLine();
                report.AppendLine("A regional feature is INVALID when any electrode it averages is");
                report.AppendLine("degraded. **No electrode is ever substituted** and the ROI is not");
                report.AppendLine("re-averaged over the survivors: an ROI over two electrodes");
                report.AppendLine("instead of three is a different measurement wearing the same");
                report.AppendLine("name. The value is still computed and still printed below, so");
                report.AppendLine("what was rejected stays inspectable — it must not be used.");
                report.AppendLine();
                report.AppendLine("Counted PER WINDOW, not at the end of the run. A channel that");
                report.AppendLine("failed and later recovered leaves the tracker healthy, so asking");
                report.AppendLine("only at the end would report an ROI as valid for a session in");
                report.AppendLine("which it was unusable for much of its length.");
                report.AppendLine();
                report.AppendLine("| Feature | Electrodes | Valid windows | Invalid windows | Invalid |");
                report.AppendLine("|---|---|---|---|---|");

                var total = m_WindowFrontocentralValid != null
                    ? m_WindowFrontocentralValid.Count
                    : 0;

                var frontoInvalid = CountFalse(m_WindowFrontocentralValid);
                var posteriorInvalid = CountFalse(m_WindowPosteriorValid);

                report.AppendLine("| Frontocentral theta | " +
                                  string.Join("/", k_FrontocentralLabels) + " | " +
                                  (total - frontoInvalid) + " | " + frontoInvalid + " | " +
                                  (frontoInvalid > 0
                                      ? "**" + PlotFormat.F(100d * frontoInvalid /
                                                            Math.Max(1, total), 1) + " %**"
                                      : "0 %") + " |");

                report.AppendLine("| Posterior alpha | " + string.Join("/", k_PosteriorLabels) +
                                  " | " + (total - posteriorInvalid) + " | " + posteriorInvalid +
                                  " | " + (posteriorInvalid > 0
                                      ? "**" + PlotFormat.F(100d * posteriorInvalid /
                                                            Math.Max(1, total), 1) + " %**"
                                      : "0 %") + " |");

                report.AppendLine();

                report.AppendLine("State at the END of the recording:");
                report.AppendLine();

                var frontoBad = RoiHasDegradedChannel(m_FrontocentralIndices, out var frontoProblem);
                var posteriorBad = RoiHasDegradedChannel(m_PosteriorIndices, out var posteriorProblem);
                var projectBad = RoiHasDegradedChannel(m_ProjectFrontalIndices, out var projectProblem);

                report.AppendLine("* Frontocentral theta — " +
                                  (frontoBad ? "**INVALID**: " + frontoProblem : "valid"));
                report.AppendLine("* Posterior alpha — " +
                                  (posteriorBad ? "**INVALID**: " + posteriorProblem : "valid"));
                report.AppendLine("* FRONTAL_THETA (project ROI) — " +
                                  (projectBad ? "**INVALID**: " + projectProblem : "valid"));
                report.AppendLine();

                if (frontoInvalid > 0 || posteriorInvalid > 0)
                {
                    result.warnings.Add("ROI features were INVALID for part of this recording " +
                                        "(frontocentral theta " + frontoInvalid + "/" + total +
                                        " windows, posterior alpha " + posteriorInvalid + "/" +
                                        total + "); the values are printed but those windows " +
                                        "must not be used");
                }

                // ---- CSVs ----------------------------------------------------------------
                var transitionCsv = new StringBuilder();
                transitionCsv.AppendLine("timestamp_lsl,timestamp_relative_s,electrode,channel," +
                                         "state,reason,detail,affected_features");

                foreach (var transition in transitions)
                {
                    transitionCsv.AppendLine(
                        PlotFormat.R(transition.timestamp) + "," +
                        PlotFormat.R(transition.timestamp - m_T0) + "," +
                        PlotFormat.Csv(transition.electrode) + "," +
                        "ch" + (transition.channelIndex + 1) + "," +
                        (transition.degraded ? "DEGRADED" : "RECOVERED") + "," +
                        PlotFormat.Csv(EegChannelQualityRules.Describe(transition.reason)) + "," +
                        PlotFormat.Csv(transition.detail) + "," +
                        PlotFormat.Csv(transition.affectedRois));
                }

                WriteFile("eeg_qc_transitions.csv", transitionCsv.ToString());

                var correlationCsv = new StringBuilder();
                correlationCsv.Append("electrode");

                for (var c = 0; c < m_ChannelCount; c++)
                    correlationCsv.Append(",").Append(m_Labels[c]);

                correlationCsv.AppendLine();

                for (var a = 0; a < m_ChannelCount; a++)
                {
                    correlationCsv.Append(m_Labels[a]);

                    for (var b = 0; b < m_ChannelCount; b++)
                    {
                        correlationCsv.Append(",").Append(a == b
                            ? "1"
                            : PlotFormat.R(m_PairCorrelation[a][b]));
                    }

                    correlationCsv.AppendLine();
                }

                WriteFile("channel_correlation.csv", correlationCsv.ToString());
            }

            // ---- Report: session PSD -------------------------------------------------

            void WriteSessionPsd(StringBuilder report)
            {
                report.AppendLine("## Power spectral density");
                report.AppendLine();

                // One PSD per channel across the whole post-settling recording. This is a
                // different estimate from the 4 s windows used for the time course — averaged
                // over far more segments — and is labelled as such wherever it appears.
                var start = (int)Math.Ceiling(m_SettlingSamples);
                var count = m_SampleCount - start;

                if (count < (int)Math.Round(options.welchSegmentSeconds * m_EffectiveFs))
                {
                    report.AppendLine("The recording is too short for a session-wide PSD after the");
                    report.AppendLine("filter's settling time.");
                    report.AppendLine();
                    return;
                }

                var psds = new PsdResult[m_ChannelCount];

                for (var c = 0; c < m_ChannelCount; c++)
                    psds[c] = Psd(m_Filtered[c], start, count);

                PlotPsd("psd_session.png", psds,
                    "SESSION PSD — FILTERED " + FilterDescription(),
                    "post-settling span " + PlotFormat.F(count / m_EffectiveFs, 1) + " s");

                var csv = new StringBuilder();
                csv.Append("frequency_hz");

                for (var c = 0; c < m_ChannelCount; c++)
                    csv.Append(",").Append(m_Labels[c]);

                csv.AppendLine();

                var reference = FirstNonNull(psds);

                if (reference != null)
                {
                    for (var k = 0; k < reference.frequencies.Length; k++)
                    {
                        if (reference.frequencies[k] > options.lowPassHz + 10d)
                            break;

                        csv.Append(PlotFormat.R(reference.frequencies[k]));

                        for (var c = 0; c < m_ChannelCount; c++)
                        {
                            csv.Append(",").Append(psds[c] != null
                                ? PlotFormat.R(psds[c].psd[k])
                                : "NaN");
                        }

                        csv.AppendLine();
                    }

                    report.AppendLine("Session-wide Welch estimate over the post-settling span (" +
                                      PlotFormat.F(count / m_EffectiveFs, 1) + " s). " +
                                      reference.Describe() + ".");
                    report.AppendLine();
                    report.AppendLine("![Session PSD](psd_session.png)");
                    report.AppendLine();

                    report.AppendLine("### Band power over the whole post-settling recording");
                    report.AppendLine();
                    report.AppendLine("| Electrode | Theta 4–8 Hz | Alpha 8–12 Hz |");
                    report.AppendLine("|---|---|---|");

                    for (var c = 0; c < m_ChannelCount; c++)
                    {
                        var theta = psds[c] != null
                            ? EegSpectralAnalyzer.BandPower(psds[c], k_Theta)
                            : double.NaN;

                        var alpha = psds[c] != null
                            ? EegSpectralAnalyzer.BandPower(psds[c], k_Alpha)
                            : double.NaN;

                        report.AppendLine("| " + m_Labels[c] + " | " + PlotFormat.Sci(theta) +
                                          " | " + PlotFormat.Sci(alpha) + " |");
                    }

                    report.AppendLine();
                    report.AppendLine("Units: " + m_Montage.PowerUnitLabel + ".");
                    report.AppendLine();
                }

                WriteFile("psd_session.csv", csv.ToString());
            }

            static PsdResult FirstNonNull(PsdResult[] psds)
            {
                foreach (var psd in psds)
                {
                    if (psd != null)
                        return psd;
                }

                return null;
            }

            // ---- Report: phases ------------------------------------------------------

            void WritePhaseSection(StringBuilder report)
            {
                report.AppendLine("## Experimental phases");
                report.AppendLine();

                if (events == null)
                {
                    report.AppendLine("No event CSV was read, so no phase annotation is available.");
                    report.AppendLine();
                    return;
                }

                report.AppendLine("| Phase | Present | Start (LSL) | Duration (s) | EEG coverage | Derivation |");
                report.AppendLine("|---|---|---|---|---|---|");

                var csv = new StringBuilder();
                csv.AppendLine("phase,present,start_lsl,end_lsl,duration_s,eeg_covered_s," +
                               "eeg_coverage_fraction,derivation");

                foreach (var phase in events.phases)
                {
                    var covered = 0d;

                    if (phase.present)
                    {
                        var from = Math.Max(phase.start, m_AbsoluteTime[0]);
                        var to = Math.Min(phase.end, m_AbsoluteTime[m_SampleCount - 1]);
                        covered = Math.Max(0d, to - from);
                    }

                    var fraction = phase.present && phase.durationSeconds > 0d
                        ? covered / phase.durationSeconds
                        : 0d;

                    report.AppendLine("| " + phase.name + " | " + (phase.present ? "yes" : "**no**") +
                                      " | " + (phase.present ? PlotFormat.F(phase.start, 3) : "—") +
                                      " | " + (phase.present
                                          ? PlotFormat.F(phase.durationSeconds, 2)
                                          : "—") +
                                      " | " + (phase.present
                                          ? PlotFormat.F(fraction * 100d, 1) + " %"
                                          : "—") +
                                      " | " + phase.derivation + " |");

                    csv.AppendLine(PlotFormat.Csv(phase.name) + "," + (phase.present ? "1" : "0") +
                                   "," + PlotFormat.R(phase.start) + "," +
                                   PlotFormat.R(phase.end) + "," +
                                   PlotFormat.R(phase.durationSeconds) + "," +
                                   PlotFormat.R(covered) + "," + PlotFormat.R(fraction) + "," +
                                   PlotFormat.Csv(phase.derivation));
                }

                report.AppendLine();
                WriteFile("phases.csv", csv.ToString());

                foreach (var phase in events.phases)
                {
                    if (!phase.present)
                    {
                        result.warnings.Add("phase " + phase.name +
                                            " could not be located in the event log");
                    }
                }

                report.AppendLine("![Raw traces](raw_traces.png)");
                report.AppendLine();
                report.AppendLine("![Filtered traces](filtered_traces.png)");
                report.AppendLine();

                // Per-phase PSD and band power.
                WritePhasePsd(report);
            }

            void WritePhasePsd(StringBuilder report)
            {
                var present = new List<OfflineSessionEvents.Phase>();

                foreach (var phase in events.phases)
                {
                    if (phase.present)
                        present.Add(phase);
                }

                if (present.Count == 0)
                    return;

                report.AppendLine("### Band power by phase");
                report.AppendLine();
                report.AppendLine("Welch estimate over each phase's own span, restricted to samples");
                report.AppendLine("after the filter settled. Frontocentral theta is the mean of " +
                                  string.Join("/", k_FrontocentralLabels) + "; posterior alpha is");
                report.AppendLine("the mean of " + string.Join("/", k_PosteriorLabels) + ".");
                report.AppendLine();
                report.AppendLine("| Phase | Frontocentral theta mean | median | Posterior alpha mean | median | Windows used |");
                report.AppendLine("|---|---|---|---|---|---|");

                var csv = new StringBuilder();
                csv.Append("phase,windows_used,frontocentral_theta_mean," +
                           "frontocentral_theta_median,posterior_alpha_mean," +
                           "posterior_alpha_median,project_frontal_theta_mean");

                for (var c = 0; c < m_ChannelCount; c++)
                    csv.Append(",theta_").Append(m_Labels[c]);

                for (var c = 0; c < m_ChannelCount; c++)
                    csv.Append(",alpha_").Append(m_Labels[c]);

                csv.AppendLine();

                var phaseNames = new List<string>();
                var phaseTheta = new List<double>();
                var phaseAlpha = new List<double>();

                foreach (var phase in present)
                {
                    var thetaSum = new double[m_ChannelCount];
                    var alphaSum = new double[m_ChannelCount];
                    var used = 0;

                    // ROI values kept per window as well as summed, so the phase can report a
                    // median beside its mean. One artefact-carrying window is enough to move a
                    // phase mean by orders of magnitude.
                    var roiTheta = new List<double>();
                    var roiAlpha = new List<double>();

                    for (var w = 0; w < m_WindowCentre.Count; w++)
                    {
                        if (!m_WindowValid[w])
                            continue;

                        var centre = m_WindowCentre[w];

                        if (centre < phase.start || centre > phase.end)
                            continue;

                        for (var c = 0; c < m_ChannelCount; c++)
                        {
                            thetaSum[c] += m_WindowTheta[w][c];
                            alphaSum[c] += m_WindowAlpha[w][c];
                        }

                        var windowTheta = RoiMean(m_WindowTheta[w], m_FrontocentralIndices);
                        var windowAlpha = RoiMean(m_WindowAlpha[w], m_PosteriorIndices);

                        if (!double.IsNaN(windowTheta))
                            roiTheta.Add(windowTheta);

                        if (!double.IsNaN(windowAlpha))
                            roiAlpha.Add(windowAlpha);

                        used++;
                    }

                    var thetaMean = new double[m_ChannelCount];
                    var alphaMean = new double[m_ChannelCount];

                    for (var c = 0; c < m_ChannelCount; c++)
                    {
                        thetaMean[c] = used > 0 ? thetaSum[c] / used : double.NaN;
                        alphaMean[c] = used > 0 ? alphaSum[c] / used : double.NaN;
                    }

                    var frontocentral = RoiMean(thetaMean, m_FrontocentralIndices);
                    var posterior = RoiMean(alphaMean, m_PosteriorIndices);
                    var projectFrontal = RoiMean(thetaMean, m_ProjectFrontalIndices);

                    var thetaMedian = roiTheta.Count > 0
                        ? EegFeaturePipeline.Median(roiTheta)
                        : double.NaN;

                    var alphaMedian = roiAlpha.Count > 0
                        ? EegFeaturePipeline.Median(roiAlpha)
                        : double.NaN;

                    report.AppendLine("| " + phase.name + " | " + PlotFormat.Sci(frontocentral) +
                                      " | " + PlotFormat.Sci(thetaMedian) + " | " +
                                      PlotFormat.Sci(posterior) + " | " +
                                      PlotFormat.Sci(alphaMedian) + " | " + used + " |");

                    csv.Append(PlotFormat.Csv(phase.name)).Append(",").Append(used).Append(",")
                        .Append(PlotFormat.R(frontocentral)).Append(",")
                        .Append(PlotFormat.R(thetaMedian)).Append(",")
                        .Append(PlotFormat.R(posterior)).Append(",")
                        .Append(PlotFormat.R(alphaMedian)).Append(",")
                        .Append(PlotFormat.R(projectFrontal));

                    for (var c = 0; c < m_ChannelCount; c++)
                        csv.Append(",").Append(PlotFormat.R(thetaMean[c]));

                    for (var c = 0; c < m_ChannelCount; c++)
                        csv.Append(",").Append(PlotFormat.R(alphaMean[c]));

                    csv.AppendLine();

                    if (used > 0)
                    {
                        // The bars carry the MEDIAN across each phase's windows. A mean would
                        // draw one artefact-carrying window as if it were the phase.
                        phaseNames.Add(phase.name);
                        phaseTheta.Add(thetaMedian);
                        phaseAlpha.Add(alphaMedian);
                    }
                }

                report.AppendLine();
                report.AppendLine("Units: " + m_Montage.PowerUnitLabel +
                                  ". These are averages of the same 4 s windows plotted in the");
                report.AppendLine("time courses below; no baseline correction and no artefact");
                report.AppendLine("rejection has been applied.");
                report.AppendLine();

                WriteFile("bandpower_by_phase.csv", csv.ToString());

                if (phaseNames.Count > 0)
                {
                    PlotPhaseBars("phase_bandpower.png", phaseNames, phaseTheta, phaseAlpha);
                    report.AppendLine("![Band power by phase](phase_bandpower.png)");
                    report.AppendLine();
                }
            }

            // ---- Report: time courses ------------------------------------------------

            void WriteTimeCourseSection(StringBuilder report)
            {
                if (m_WindowCentre == null || m_WindowCentre.Count == 0)
                    return;

                report.AppendLine("## Band-power time courses");
                report.AppendLine();
                report.AppendLine("One point per " + PlotFormat.F(options.spectralWindowSeconds, 1) +
                                  " s window, stepped by " +
                                  PlotFormat.F(options.timeCourseStepSeconds, 1) +
                                  " s. Invalid windows are gaps in the line, not zeros.");
                report.AppendLine();

                var csv = new StringBuilder();
                csv.Append("window_centre_lsl,window_centre_relative_s,valid," +
                           "frontocentral_roi_valid,posterior_roi_valid,phase," +
                           "frontocentral_theta,posterior_alpha,project_frontal_theta");

                for (var c = 0; c < m_ChannelCount; c++)
                    csv.Append(",theta_").Append(m_Labels[c]);

                for (var c = 0; c < m_ChannelCount; c++)
                    csv.Append(",alpha_").Append(m_Labels[c]);

                csv.AppendLine();

                for (var w = 0; w < m_WindowCentre.Count; w++)
                {
                    csv.Append(PlotFormat.R(m_WindowCentre[w])).Append(",")
                        .Append(PlotFormat.R(m_WindowCentre[w] - m_T0)).Append(",")
                        .Append(m_WindowValid[w] ? "1" : "0").Append(",")
                        .Append(w < m_WindowFrontocentralValid.Count &&
                                m_WindowFrontocentralValid[w] ? "1" : "0").Append(",")
                        .Append(w < m_WindowPosteriorValid.Count &&
                                m_WindowPosteriorValid[w] ? "1" : "0").Append(",")
                        .Append(PlotFormat.Csv(m_WindowPhase[w])).Append(",")
                        .Append(PlotFormat.R(RoiMean(m_WindowTheta[w], m_FrontocentralIndices)))
                        .Append(",")
                        .Append(PlotFormat.R(RoiMean(m_WindowAlpha[w], m_PosteriorIndices)))
                        .Append(",")
                        .Append(PlotFormat.R(RoiMean(m_WindowTheta[w], m_ProjectFrontalIndices)));

                    for (var c = 0; c < m_ChannelCount; c++)
                        csv.Append(",").Append(PlotFormat.R(m_WindowTheta[w][c]));

                    for (var c = 0; c < m_ChannelCount; c++)
                        csv.Append(",").Append(PlotFormat.R(m_WindowAlpha[w][c]));

                    csv.AppendLine();
                }

                WriteFile("bandpower_timecourse.csv", csv.ToString());

                PlotBandTimeCourse("frontocentral_theta_timecourse.png",
                    m_WindowTheta, m_FrontocentralIndices, k_FrontocentralLabels,
                    "FRONTOCENTRAL THETA 4-8 HZ", "theta band power");

                PlotBandTimeCourse("posterior_alpha_timecourse.png",
                    m_WindowAlpha, m_PosteriorIndices, k_PosteriorLabels,
                    "POSTERIOR ALPHA 8-12 HZ", "alpha band power");

                report.AppendLine("![Frontocentral theta](frontocentral_theta_timecourse.png)");
                report.AppendLine();
                report.AppendLine("![Posterior alpha](posterior_alpha_timecourse.png)");
                report.AppendLine();
            }

            // ---- Report: recognition -------------------------------------------------

            /// <summary>One recognition epoch's measurements, for one alignment.</summary>
            class Epoch
            {
                internal OfflineSessionEvents.RecognitionItem item;
                internal bool valid;
                internal string invalidReason = string.Empty;
                internal double frontocentralTheta = double.NaN;
                internal double posteriorAlpha = double.NaN;
                internal double[] theta;
                internal double[] alpha;
                internal double[] roiTrace;     // frontocentral mean, filtered, for the mean trace
            }

            void WriteRecognitionSection(StringBuilder report)
            {
                report.AppendLine("## Recognition epochs");
                report.AppendLine();

                if (events == null || events.recognitionItems.Count == 0)
                {
                    report.AppendLine("This run logged no recognition items, so no");
                    report.AppendLine("stimulus-aligned or response-aligned analysis was produced.");
                    report.AppendLine();
                    return;
                }

                // The condition summary is appended to twice, once per alignment. Any file left
                // by an earlier run of this tool is removed first, so a re-run replaces its
                // predecessor instead of accumulating a second copy of every row.
                var summaryPath = Path.Combine(outputFolder, "recognition_condition_summary.csv");

                if (File.Exists(summaryPath))
                    File.Delete(summaryPath);

                report.AppendLine("Item count: **" + events.recognitionItems.Count + "** (" +
                                  events.recognitionItemsWithoutResponse +
                                  " without a logged response).");
                report.AppendLine();
                report.AppendLine("Classification (TARGET/LURE, HIT/MISS/CORRECTREJECTION/");
                report.AppendLine("FALSEALARM, IMMEDIATE/DELAYED) is read from the event CSV");
                report.AppendLine("exactly as the run recorded it. Nothing is re-scored here.");
                report.AppendLine();

                report.AppendLine("### Outcomes as logged");
                report.AppendLine();
                report.AppendLine("| Phase | " + "Outcome counts |");
                report.AppendLine("|---|---|");

                foreach (var phase in new[] { "IMMEDIATE", "DELAYED" })
                {
                    var counts = events.OutcomeCounts(phase);
                    var parts = new List<string>();

                    foreach (var pair in counts)
                        parts.Add(pair.Key + " " + pair.Value);

                    parts.Sort(StringComparer.Ordinal);

                    report.AppendLine("| " + phase + " | " +
                                      (parts.Count == 0 ? "none" : string.Join(", ", parts)) + " |");
                }

                report.AppendLine();

                // ---- Cut both alignments -------------------------------------------
                var stimulus = new List<Epoch>();
                var response = new List<Epoch>();

                var traceSamples = (int)Math.Round(options.stimulusEpochSeconds * m_EffectiveFs);

                foreach (var item in events.recognitionItems)
                {
                    stimulus.Add(CutEpoch(item, item.onsetTimestamp,
                        item.onsetTimestamp + options.stimulusEpochSeconds, traceSamples));

                    if (item.responded)
                    {
                        response.Add(CutEpoch(item,
                            item.responseTimestamp - options.responseEpochSeconds,
                            item.responseTimestamp, traceSamples));
                    }
                    else
                    {
                        response.Add(new Epoch
                        {
                            item = item,
                            valid = false,
                            invalidReason = "no logged response",
                        });
                    }
                }

                WriteEpochCsv(stimulus, response);

                report.AppendLine("### Stimulus-aligned: item onset to onset + " +
                                  PlotFormat.F(options.stimulusEpochSeconds, 1) + " s");
                report.AppendLine();
                WriteEpochSummary(report, stimulus, "stimulus");

                report.AppendLine("### Response-aligned: the " +
                                  PlotFormat.F(options.responseEpochSeconds, 1) +
                                  " s ending at RECOGNITION_RESPONSE");
                report.AppendLine();
                report.AppendLine("A **candidate** interval, as requested. It is not a validated");
                report.AppendLine("response-locked window: its length overlaps the preceding");
                report.AppendLine("stimulus for every item whose reaction time was under " +
                                  PlotFormat.F(options.responseEpochSeconds, 1) + " s,");
                report.AppendLine("and this run's reaction times are mostly under 1 s. The overlap");
                report.AppendLine("is quantified per item in `recognition_epochs.csv`.");
                report.AppendLine();
                WriteEpochSummary(report, response, "response");

                PlotEpochBars("recognition_bandpower_stimulus_aligned.png", stimulus,
                    "STIMULUS-ALIGNED (ONSET TO +" +
                    PlotFormat.F(options.stimulusEpochSeconds, 0) + "S)");

                PlotEpochBars("recognition_bandpower_response_aligned.png", response,
                    "RESPONSE-ALIGNED (FINAL " +
                    PlotFormat.F(options.responseEpochSeconds, 0) + "S BEFORE RESPONSE)");

                PlotEpochMeanTrace("recognition_mean_trace_stimulus_aligned.png", stimulus,
                    traceSamples);

                report.AppendLine("![Stimulus-aligned band power](recognition_bandpower_stimulus_aligned.png)");
                report.AppendLine();
                report.AppendLine("![Response-aligned band power](recognition_bandpower_response_aligned.png)");
                report.AppendLine();
                report.AppendLine("![Stimulus-aligned mean trace](recognition_mean_trace_stimulus_aligned.png)");
                report.AppendLine();
            }

            Epoch CutEpoch(OfflineSessionEvents.RecognitionItem item, double from, double to,
                int traceSamples)
            {
                var epoch = new Epoch { item = item };

                if (!RangeFor(from, to, out var start, out var count))
                {
                    epoch.valid = false;
                    epoch.invalidReason = "interval falls outside the recorded EEG";
                    return epoch;
                }

                if (start < m_SettlingSamples)
                {
                    epoch.valid = false;
                    epoch.invalidReason = "inside the filter's settling time";
                    return epoch;
                }

                if (HasGap(start, count))
                {
                    epoch.valid = false;
                    epoch.invalidReason = "contains a timing gap";
                    return epoch;
                }

                epoch.theta = new double[m_ChannelCount];
                epoch.alpha = new double[m_ChannelCount];

                for (var c = 0; c < m_ChannelCount; c++)
                {
                    var psd = Psd(m_Filtered[c], start, count);

                    if (psd == null)
                    {
                        epoch.valid = false;
                        epoch.invalidReason = "too short for one Welch segment";
                        return epoch;
                    }

                    epoch.theta[c] = EegSpectralAnalyzer.BandPower(psd, k_Theta);
                    epoch.alpha[c] = EegSpectralAnalyzer.BandPower(psd, k_Alpha);
                }

                epoch.frontocentralTheta = RoiMean(epoch.theta, m_FrontocentralIndices);
                epoch.posteriorAlpha = RoiMean(epoch.alpha, m_PosteriorIndices);

                // Time-domain trace of the frontocentral mean, for the averaged view. Resampled
                // by nearest index only — no interpolation, no baseline correction.
                epoch.roiTrace = new double[traceSamples];

                for (var i = 0; i < traceSamples; i++)
                {
                    var index = start + (int)Math.Round((double)i * count / traceSamples);

                    if (index >= m_SampleCount)
                        index = m_SampleCount - 1;

                    var sum = 0d;
                    var n = 0;

                    foreach (var channel in m_FrontocentralIndices)
                    {
                        sum += m_Filtered[channel][index];
                        n++;
                    }

                    epoch.roiTrace[i] = n > 0 ? sum / n : double.NaN;
                }

                epoch.valid = true;
                return epoch;
            }

            void WriteEpochCsv(List<Epoch> stimulus, List<Epoch> response)
            {
                var csv = new StringBuilder();

                csv.Append("item_id,phase,item_class,presentation_order,response,outcome," +
                           "reaction_time_ms,onset_lsl,response_lsl," +
                           "stimulus_valid,stimulus_invalid_reason," +
                           "stimulus_frontocentral_theta,stimulus_posterior_alpha," +
                           "response_valid,response_invalid_reason," +
                           "response_frontocentral_theta,response_posterior_alpha," +
                           "response_window_overlaps_stimulus_onset");

                for (var c = 0; c < m_ChannelCount; c++)
                    csv.Append(",stim_theta_").Append(m_Labels[c]);

                for (var c = 0; c < m_ChannelCount; c++)
                    csv.Append(",stim_alpha_").Append(m_Labels[c]);

                csv.AppendLine();

                for (var i = 0; i < stimulus.Count; i++)
                {
                    var s = stimulus[i];
                    var r = i < response.Count ? response[i] : null;
                    var item = s.item;

                    var overlaps = item.responded &&
                                   !double.IsNaN(item.reactionMs) &&
                                   item.reactionMs / 1000d < options.responseEpochSeconds;

                    csv.Append(PlotFormat.Csv(item.itemId)).Append(",")
                        .Append(PlotFormat.Csv(item.phase)).Append(",")
                        .Append(PlotFormat.Csv(item.itemClass)).Append(",")
                        .Append(item.order).Append(",")
                        .Append(PlotFormat.Csv(item.response)).Append(",")
                        .Append(PlotFormat.Csv(item.outcome)).Append(",")
                        .Append(PlotFormat.R(item.reactionMs)).Append(",")
                        .Append(PlotFormat.R(item.onsetTimestamp)).Append(",")
                        .Append(PlotFormat.R(item.responseTimestamp)).Append(",")
                        .Append(s.valid ? "1" : "0").Append(",")
                        .Append(PlotFormat.Csv(s.invalidReason)).Append(",")
                        .Append(PlotFormat.R(s.frontocentralTheta)).Append(",")
                        .Append(PlotFormat.R(s.posteriorAlpha)).Append(",")
                        .Append(r != null && r.valid ? "1" : "0").Append(",")
                        .Append(PlotFormat.Csv(r != null ? r.invalidReason : "no epoch")).Append(",")
                        .Append(PlotFormat.R(r != null ? r.frontocentralTheta : double.NaN))
                        .Append(",")
                        .Append(PlotFormat.R(r != null ? r.posteriorAlpha : double.NaN)).Append(",")
                        .Append(overlaps ? "1" : "0");

                    for (var c = 0; c < m_ChannelCount; c++)
                    {
                        csv.Append(",").Append(PlotFormat.R(
                            s.theta != null ? s.theta[c] : double.NaN));
                    }

                    for (var c = 0; c < m_ChannelCount; c++)
                    {
                        csv.Append(",").Append(PlotFormat.R(
                            s.alpha != null ? s.alpha[c] : double.NaN));
                    }

                    csv.AppendLine();
                }

                WriteFile("recognition_epochs.csv", csv.ToString());
            }

            /// <summary>The condition groupings the report tabulates and the plots draw.</summary>
            static readonly (string label, Func<OfflineSessionEvents.RecognitionItem, bool> test)[]
                k_Conditions =
                {
                    ("IMMEDIATE", item => item.isImmediate),
                    ("DELAYED", item => item.isDelayed),
                    ("IMM TARGET", item => item.isImmediate && item.isTarget),
                    ("IMM LURE", item => item.isImmediate && item.isLure),
                    ("DEL TARGET", item => item.isDelayed && item.isTarget),
                    ("DEL LURE", item => item.isDelayed && item.isLure),
                    ("HIT", item => Is(item.outcome, "HIT")),
                    ("MISS", item => Is(item.outcome, "MISS")),
                    ("CORRECT REJ", item => Is(item.outcome, "CORRECTREJECTION")),
                    ("FALSE ALARM", item => Is(item.outcome, "FALSEALARM")),
                };

            static bool Is(string a, string b)
            {
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }

            /// <summary>
            /// Tabulates one alignment by condition.
            ///
            /// The MEDIAN is reported next to the mean, and is not a robustness flourish: a single
            /// window containing an amplifier step carries a band power several decades above its
            /// neighbours, and it moves the mean of its condition by more than the whole spread of
            /// the remaining epochs. Reporting only the mean would present that one artefact as a
            /// property of the condition. Nothing is excluded — the outlying epochs are counted
            /// and listed instead.
            /// </summary>
            void WriteEpochSummary(StringBuilder report, List<Epoch> epochs, string alignment)
            {
                report.AppendLine("| Condition | n valid | n total | Frontocentral theta mean ± SD | median | Posterior alpha mean ± SD | median | Outliers |");
                report.AppendLine("|---|---|---|---|---|---|---|---|");

                var csv = new StringBuilder();
                csv.AppendLine("alignment,condition,n_total,n_valid,frontocentral_theta_mean," +
                               "frontocentral_theta_sd,frontocentral_theta_median," +
                               "posterior_alpha_mean,posterior_alpha_sd,posterior_alpha_median," +
                               "outlier_epochs");

                var flagged = new List<string>();

                foreach (var condition in k_Conditions)
                {
                    var theta = new List<double>();
                    var alpha = new List<double>();
                    var contributing = new List<Epoch>();
                    var total = 0;

                    foreach (var epoch in epochs)
                    {
                        if (!condition.test(epoch.item))
                            continue;

                        total++;

                        if (!epoch.valid)
                            continue;

                        if (!double.IsNaN(epoch.frontocentralTheta))
                        {
                            theta.Add(epoch.frontocentralTheta);
                            contributing.Add(epoch);
                        }

                        if (!double.IsNaN(epoch.posteriorAlpha))
                            alpha.Add(epoch.posteriorAlpha);
                    }

                    MeanSd(theta, out var thetaMean, out var thetaSd);
                    MeanSd(alpha, out var alphaMean, out var alphaSd);

                    var thetaMedian = theta.Count > 0
                        ? EegFeaturePipeline.Median(theta)
                        : double.NaN;

                    var alphaMedian = alpha.Count > 0
                        ? EegFeaturePipeline.Median(alpha)
                        : double.NaN;

                    // The two-decade threshold is the project's own: EegFeaturePipeline uses
                    // k_OutlierDecades = 2.0 to decide that a channel's power is unlike its
                    // neighbours'. The same distance is applied here between an epoch and its
                    // condition's median.
                    var outliers = 0;

                    if (thetaMedian > 0d)
                    {
                        for (var i = 0; i < theta.Count; i++)
                        {
                            if (theta[i] <= 0d)
                                continue;

                            var decades = Math.Abs(Math.Log10(theta[i] / thetaMedian));

                            if (decades < 2.0)
                                continue;

                            outliers++;

                            var item = contributing[i].item;

                            var note = alignment + " / " + condition.label + ": item " +
                                       item.phase + " #" + item.order + " (" + item.itemClass +
                                       ", " + item.outcome + ") theta " +
                                       PlotFormat.Sci(theta[i]) + " is " +
                                       PlotFormat.F(decades, 1) +
                                       " decades from the condition median";

                            if (!flagged.Contains(note))
                                flagged.Add(note);
                        }
                    }

                    report.AppendLine("| " + condition.label + " | " + theta.Count + " | " + total +
                                      " | " + PlotFormat.Sci(thetaMean) + " ± " +
                                      PlotFormat.Sci(thetaSd) + " | " +
                                      PlotFormat.Sci(thetaMedian) + " | " +
                                      PlotFormat.Sci(alphaMean) + " ± " +
                                      PlotFormat.Sci(alphaSd) + " | " +
                                      PlotFormat.Sci(alphaMedian) + " | " +
                                      (outliers == 0 ? "—" : "**" + outliers + "**") + " |");

                    csv.AppendLine(alignment + "," + PlotFormat.Csv(condition.label) + "," + total +
                                   "," + theta.Count + "," + PlotFormat.R(thetaMean) + "," +
                                   PlotFormat.R(thetaSd) + "," + PlotFormat.R(thetaMedian) + "," +
                                   PlotFormat.R(alphaMean) + "," + PlotFormat.R(alphaSd) + "," +
                                   PlotFormat.R(alphaMedian) + "," + outliers);
                }

                report.AppendLine();
                report.AppendLine("Units: " + m_Montage.PowerUnitLabel + ". Descriptive only —");
                report.AppendLine("no inferential test is computed, and with these counts none");
                report.AppendLine("would be meaningful.");
                report.AppendLine();

                if (flagged.Count > 0)
                {
                    report.AppendLine("**Outlying epochs (kept, not removed):**");
                    report.AppendLine();

                    foreach (var note in flagged)
                        report.AppendLine("* " + note);

                    report.AppendLine();
                    report.AppendLine("Where an outlier is present, read the median column. The");
                    report.AppendLine("mean of that condition describes the artefact more than it");
                    report.AppendLine("describes the remaining epochs.");
                    report.AppendLine();

                    result.warnings.Add(alignment + "-aligned epochs include " + flagged.Count +
                                        " item(s) more than 2 decades from their condition " +
                                        "median; the affected means are not usable");
                }

                AppendFile("recognition_condition_summary.csv", csv.ToString(),
                    "alignment,condition,n_total,n_valid,frontocentral_theta_mean," +
                    "frontocentral_theta_sd,frontocentral_theta_median," +
                    "posterior_alpha_mean,posterior_alpha_sd,posterior_alpha_median," +
                    "outlier_epochs");
            }

            static double Min(List<double> values)
            {
                var min = double.MaxValue;

                foreach (var v in values)
                {
                    if (v < min)
                        min = v;
                }

                return min;
            }

            static double Max(List<double> values)
            {
                var max = double.MinValue;

                foreach (var v in values)
                {
                    if (v > max)
                        max = v;
                }

                return max;
            }

            static void MeanSd(List<double> values, out double mean, out double sd)
            {
                mean = double.NaN;
                sd = double.NaN;

                if (values == null || values.Count == 0)
                    return;

                var sum = 0d;

                foreach (var v in values)
                    sum += v;

                mean = sum / values.Count;

                if (values.Count < 2)
                {
                    sd = 0d;
                    return;
                }

                var acc = 0d;

                foreach (var v in values)
                    acc += (v - mean) * (v - mean);

                sd = Math.Sqrt(acc / (values.Count - 1));
            }

            // ---- Report: limitations -------------------------------------------------

            void WriteLimitations(StringBuilder report)
            {
                report.AppendLine("## Warnings and limitations");
                report.AppendLine();

                if (result.warnings.Count == 0)
                {
                    report.AppendLine("No warnings were raised during this run.");
                }
                else
                {
                    foreach (var warning in result.warnings)
                        report.AppendLine("* " + warning);
                }

                report.AppendLine();
                report.AppendLine("### Standing limitations of this tool");
                report.AppendLine();
                report.AppendLine("1. **Units are not verified.** Amplitudes are AURA native units");
                report.AppendLine("   throughout. `AuraMontageConfig.unitsConfirmed` is false, so no");
                report.AppendLine("   value here is in microvolts and none should be compared with a");
                report.AppendLine("   published figure in microvolts.");
                report.AppendLine("2. **The montage is applied, not measured.** The raw file");
                report.AppendLine("   disclaims a verified electrode map. ch1–ch8 are labelled");
                report.AppendLine("   Fp1/F3/Fz/F4/Cz/P3/Pz/P4 from the project configuration. If the");
                report.AppendLine("   cap was worn or wired differently, every regional label above");
                report.AppendLine("   is wrong in the same way.");
                report.AppendLine("3. **No re-referencing, no artefact rejection, no interpolation.**");
                report.AppendLine("   CSP, FBCSP, CAR, ASR and ICA are not implemented in this");
                report.AppendLine("   project and are not applied here. Ocular and movement artefacts");
                report.AppendLine("   are present in these numbers.");
                report.AppendLine("4. **No baseline correction.** Band powers are absolute, in the");
                report.AppendLine("   signal's own units. There is no pre-stimulus baseline and no");
                report.AppendLine("   normalisation across phases, so between-phase differences");
                report.AppendLine("   include any drift in impedance or amplifier state.");
                report.AppendLine("5. **A 2 s epoch yields ONE Welch segment.** With the project's");
                report.AppendLine("   2 s segment length, the recognition epochs are single-segment");
                report.AppendLine("   periodograms, not averaged spectra. They are far noisier than");
                report.AppendLine("   the 4 s windows used for the time courses, and the two are not");
                report.AppendLine("   directly comparable.");
                report.AppendLine("6. **The response-aligned window is a candidate, not a design.**");
                report.AppendLine("   See the note in the recognition section.");
                report.AppendLine("7. **Flatline and SaturationLike are re-implemented here** from");
                report.AppendLine("   `EegFeaturePipeline.AssessChannel`, which is private to a");
                report.AppendLine("   MonoBehaviour that cannot run against a file. The rules match");
                report.AppendLine("   as of this writing; they are not shared code and could drift.");
                report.AppendLine("8. **No statistical inference.** Means, medians and standard");
                report.AppendLine("   deviations are descriptive. No test, correction or effect size");
                report.AppendLine("   is computed.");
                report.AppendLine("9. **Outliers are flagged, never removed.** Epochs and windows");
                report.AppendLine("   containing amplifier steps stay in every mean. Where the");
                report.AppendLine("   outlier column is non-zero, the mean of that condition is");
                report.AppendLine("   describing an artefact; the median column beside it is the");
                report.AppendLine("   one to read. Introducing artefact rejection would be a change");
                report.AppendLine("   to the project's preprocessing and is deliberately not made");
                report.AppendLine("   here.");
                report.AppendLine();

                report.AppendLine("### Requested steps that are NOT implemented in this project");
                report.AppendLine();
                report.AppendLine("Reported rather than silently substituted:");
                report.AppendLine();
                report.AppendLine("* The live pipeline builds its filter and its Welch estimate at");
                report.AppendLine("  the stream's **nominal** rate (`m_Receiver.metadata.nominalSrate`).");
                report.AppendLine("  This offline run uses the **effective** rate measured from the");
                report.AppendLine("  analysis timestamps, as requested. On this recording the two");
                report.AppendLine("  differ by " +
                                  PlotFormat.F(Math.Abs(m_EffectiveFs - recording.nominalRateHz) /
                                               recording.nominalRateHz * 100d, 4) +
                                  " %, which moves the frequency axis by that");
                report.AppendLine("  fraction and does not change the segment length in samples.");
                report.AppendLine("* The project's `FRONTAL_THETA` ROI is **F3/Fz/F4** — it does not");
                report.AppendLine("  include Cz. The frontocentral set requested for this analysis");
                report.AppendLine("  (**F3/Fz/F4/Cz**) is therefore NOT the project ROI. Both are");
                report.AppendLine("  computed and reported separately in the CSVs");
                report.AppendLine("  (`frontocentral_theta_*` versus `project_frontal_theta_*`).");
                report.AppendLine("  Neither definition was changed.");
                report.AppendLine();

                report.AppendLine("### Still requires live validation");
                report.AppendLine();
                report.AppendLine("* Whether the electrode mapping matches the physical cap.");
                report.AppendLine("* Whether AURA's native units have a known scaling to volts.");
                report.AppendLine("* Whether the marker-to-EEG alignment is accurate in absolute");
                report.AppendLine("  terms; this tool can only show that the two clocks agree, not");
                report.AppendLine("  that either matches the moment the participant saw the stimulus.");
                report.AppendLine("* Everything about display latency between the logged onset and");
                report.AppendLine("  the photons reaching the eye.");
                report.AppendLine();
            }

            // ---- Plots ---------------------------------------------------------------

            void PlotTraces(string fileName, double[][] data, string title)
            {
                const int width = 1600;
                const int laneHeight = 110;
                const int marginLeft = 90;
                const int marginRight = 220;

                // Leaves room for the title, the subtitle and the phase legend, which is drawn
                // along the top-left rather than in the right-hand gutter: the gutter is only
                // wide enough for an electrode label, and a phase name overran it.
                const int marginTop = 86;

                var height = marginTop + laneHeight * m_ChannelCount + 60;
                var plot = new OfflinePlot(width, height, OfflinePlot.Background);

                plot.Text(marginLeft, 16, title, OfflinePlot.Ink, 2);
                plot.Text(marginLeft, 42, options.sessionId + "  |  " + m_SampleCount +
                                          " samples  |  effective fs " +
                                          PlotFormat.F(m_EffectiveFs, 3) + " Hz  |  " +
                                          "AURA native units, unconverted",
                    OfflinePlot.Muted);

                var xMin = 0d;
                var xMax = m_Time[m_SampleCount - 1];

                for (var c = 0; c < m_ChannelCount; c++)
                {
                    var top = marginTop + c * laneHeight;

                    double min = double.MaxValue, max = double.MinValue;

                    for (var i = 0; i < m_SampleCount; i++)
                    {
                        var v = data[c][i];

                        if (double.IsNaN(v) || double.IsInfinity(v))
                            continue;

                        if (v < min) min = v;
                        if (v > max) max = v;
                    }

                    if (min > max)
                    {
                        min = -1d;
                        max = 1d;
                    }

                    var pad = (max - min) * 0.05;

                    var axes = new PlotAxes(plot, marginLeft, top,
                        width - marginLeft - marginRight, laneHeight - 18,
                        xMin, xMax, min - pad, max + pad);

                    ShadePhases(axes);

                    axes.Frame(OfflinePlot.Grid);
                    axes.YTicks(2, v => PlotFormat.Sci(v, 1), OfflinePlot.Grid, OfflinePlot.Muted);
                    axes.Series(m_Time, data[c], OfflinePlot.ChannelColour(c));

                    plot.Text(axes.right + 10, top + 4, m_Labels[c], OfflinePlot.ChannelColour(c), 2);
                    plot.Text(axes.right + 10, top + 24,
                        "ch" + (c + 1).ToString(CultureInfo.InvariantCulture), OfflinePlot.Muted);

                    if (c == m_ChannelCount - 1)
                    {
                        axes.XTicks(10, v => PlotFormat.F(v, 0), OfflinePlot.Grid,
                            OfflinePlot.Muted);

                        plot.TextCentred((axes.left + axes.right) / 2, axes.bottom + 22,
                            "SECONDS FROM FIRST EEG SAMPLE", OfflinePlot.Ink);
                    }
                }

                DrawPhaseLegend(plot, marginLeft, 62);
                WritePlot(fileName, plot);
            }

            void PlotPsd(string fileName, PsdResult[] psds, string title, string subtitle)
            {
                const int width = 1400;
                const int height = 760;
                const int marginLeft = 110;
                const int marginRight = 190;

                var plot = new OfflinePlot(width, height, OfflinePlot.Background);

                plot.Text(marginLeft, 16, title, OfflinePlot.Ink, 2);
                plot.Text(marginLeft, 42, subtitle + "  |  " + options.sessionId, OfflinePlot.Muted);

                var fMax = Math.Min(options.lowPassHz + 10d, m_EffectiveFs / 2d);

                // Log10 of PSD, because band powers on this hardware span several decades and a
                // linear axis would collapse every channel but the largest onto the baseline.
                var yMin = double.MaxValue;
                var yMax = double.MinValue;

                foreach (var psd in psds)
                {
                    if (psd == null)
                        continue;

                    for (var k = 0; k < psd.frequencies.Length; k++)
                    {
                        if (psd.frequencies[k] > fMax || psd.frequencies[k] < 0.5)
                            continue;

                        if (psd.psd[k] <= 0d)
                            continue;

                        var v = Math.Log10(psd.psd[k]);

                        if (v < yMin) yMin = v;
                        if (v > yMax) yMax = v;
                    }
                }

                if (yMin > yMax)
                {
                    yMin = -1d;
                    yMax = 1d;
                }

                var axes = new PlotAxes(plot, marginLeft, 80,
                    width - marginLeft - marginRight, height - 160,
                    0d, fMax, yMin - 0.2, yMax + 0.2);

                axes.Frame(OfflinePlot.Grid);
                axes.YTicks(8, v => PlotFormat.F(v, 1), OfflinePlot.Grid, OfflinePlot.Muted);
                axes.XTicks(9, v => PlotFormat.F(v, 0), OfflinePlot.Grid, OfflinePlot.Muted);

                // Band edges, so theta and alpha can be read off the plot directly.
                axes.VMarker(k_Theta.lowHz, OfflinePlot.Muted);
                axes.VMarker(k_Theta.highHz, OfflinePlot.Muted);
                axes.VMarker(k_Alpha.highHz, OfflinePlot.Muted);

                plot.TextCentred(axes.X((k_Theta.lowHz + k_Theta.highHz) / 2), axes.top + 6,
                    "THETA", OfflinePlot.Muted);
                plot.TextCentred(axes.X((k_Alpha.lowHz + k_Alpha.highHz) / 2), axes.top + 6,
                    "ALPHA", OfflinePlot.Muted);

                for (var c = 0; c < m_ChannelCount; c++)
                {
                    if (psds[c] == null)
                        continue;

                    var xs = new List<double>();
                    var ys = new List<double>();

                    for (var k = 0; k < psds[c].frequencies.Length; k++)
                    {
                        if (psds[c].frequencies[k] > fMax)
                            break;

                        xs.Add(psds[c].frequencies[k]);
                        ys.Add(psds[c].psd[k] > 0d ? Math.Log10(psds[c].psd[k]) : double.NaN);
                    }

                    axes.Series(xs, ys, OfflinePlot.ChannelColour(c));

                    var y = 90 + c * 26;
                    plot.FillRect(axes.right + 16, y + 2, 14, 10, OfflinePlot.ChannelColour(c));
                    plot.Text(axes.right + 36, y, m_Labels[c], OfflinePlot.Ink, 2);
                }

                plot.TextCentred((axes.left + axes.right) / 2, axes.bottom + 26,
                    "FREQUENCY (HZ)", OfflinePlot.Ink);

                plot.TextVertical(20, axes.bottom - 40,
                    "LOG10 PSD (" + m_Montage.PowerSpectralDensityUnitLabel + ")",
                    OfflinePlot.Ink);

                WritePlot(fileName, plot);
            }

            void PlotBandTimeCourse(string fileName, List<double[]> perWindow, int[] indices,
                string[] labels, string title, string yLabel)
            {
                const int width = 1600;
                const int height = 620;
                const int marginLeft = 110;
                const int marginRight = 210;

                var plot = new OfflinePlot(width, height, OfflinePlot.Background);

                plot.Text(marginLeft, 16, title, OfflinePlot.Ink, 2);
                plot.Text(marginLeft, 42, options.sessionId + "  |  " +
                                          PlotFormat.F(options.spectralWindowSeconds, 0) +
                                          " s windows, " +
                                          PlotFormat.F(options.timeCourseStepSeconds, 0) +
                                          " s step  |  " + m_InvalidWindows +
                                          " invalid window(s) drawn as gaps", OfflinePlot.Muted);

                var xs = new List<double>();

                foreach (var centre in m_WindowCentre)
                    xs.Add(centre - m_T0);

                var yMin = double.MaxValue;
                var yMax = double.MinValue;

                for (var w = 0; w < perWindow.Count; w++)
                {
                    if (!m_WindowValid[w])
                        continue;

                    foreach (var index in indices)
                    {
                        var v = perWindow[w][index];

                        if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0d)
                            continue;

                        var l = Math.Log10(v);

                        if (l < yMin) yMin = l;
                        if (l > yMax) yMax = l;
                    }
                }

                if (yMin > yMax)
                {
                    yMin = -1d;
                    yMax = 1d;
                }

                var axes = new PlotAxes(plot, marginLeft, 80,
                    width - marginLeft - marginRight, height - 170,
                    0d, m_Time[m_SampleCount - 1], yMin - 0.2, yMax + 0.2);

                ShadePhases(axes);
                axes.Frame(OfflinePlot.Grid);
                axes.YTicks(6, v => PlotFormat.F(v, 1), OfflinePlot.Grid, OfflinePlot.Muted);
                axes.XTicks(10, v => PlotFormat.F(v, 0), OfflinePlot.Grid, OfflinePlot.Muted);

                var legendY = 90;

                for (var i = 0; i < indices.Length; i++)
                {
                    var index = indices[i];
                    var ys = new List<double>();

                    for (var w = 0; w < perWindow.Count; w++)
                    {
                        var v = m_WindowValid[w] ? perWindow[w][index] : double.NaN;
                        ys.Add(!double.IsNaN(v) && v > 0d ? Math.Log10(v) : double.NaN);
                    }

                    axes.Series(xs, ys, OfflinePlot.ChannelColour(index));

                    plot.FillRect(axes.right + 16, legendY + 2, 14, 10,
                        OfflinePlot.ChannelColour(index));

                    plot.Text(axes.right + 36, legendY,
                        i < labels.Length ? labels[i] : m_Labels[index], OfflinePlot.Ink, 2);

                    legendY += 26;
                }

                // ROI mean, drawn last and in ink so it sits above the contributors.
                var roi = new List<double>();

                for (var w = 0; w < perWindow.Count; w++)
                {
                    var v = m_WindowValid[w] ? RoiMean(perWindow[w], indices) : double.NaN;
                    roi.Add(!double.IsNaN(v) && v > 0d ? Math.Log10(v) : double.NaN);
                }

                axes.Series(xs, roi, OfflinePlot.Ink);

                plot.FillRect(axes.right + 16, legendY + 2, 14, 10, OfflinePlot.Ink);
                plot.Text(axes.right + 36, legendY, "ROI MEAN", OfflinePlot.Ink, 2);

                plot.TextCentred((axes.left + axes.right) / 2, axes.bottom + 26,
                    "SECONDS FROM FIRST EEG SAMPLE", OfflinePlot.Ink);

                plot.TextVertical(20, axes.bottom - 30,
                    "LOG10 " + yLabel.ToUpperInvariant(), OfflinePlot.Ink);

                DrawPhaseLegend(plot, marginLeft, height - 40);
                WritePlot(fileName, plot);
            }

            void PlotPhaseBars(string fileName, List<string> names, List<double> theta,
                List<double> alpha)
            {
                const int width = 1100;
                const int height = 560;
                const int marginLeft = 120;

                var plot = new OfflinePlot(width, height, OfflinePlot.Background);

                plot.Text(marginLeft, 16, "BAND POWER BY PHASE", OfflinePlot.Ink, 2);
                plot.Text(marginLeft, 42, options.sessionId +
                                          "  |  log10, AURA native units squared  |  " +
                                          "MEDIAN across the 4 s windows inside each phase",
                    OfflinePlot.Muted);

                var values = new List<double>();

                foreach (var v in theta)
                {
                    if (!double.IsNaN(v) && v > 0d)
                        values.Add(Math.Log10(v));
                }

                foreach (var v in alpha)
                {
                    if (!double.IsNaN(v) && v > 0d)
                        values.Add(Math.Log10(v));
                }

                var yMin = double.MaxValue;
                var yMax = double.MinValue;

                foreach (var v in values)
                {
                    if (v < yMin) yMin = v;
                    if (v > yMax) yMax = v;
                }

                if (yMin > yMax)
                {
                    yMin = 0d;
                    yMax = 1d;
                }

                var floor = yMin - (yMax - yMin) * 0.15 - 0.1;

                var axes = new PlotAxes(plot, marginLeft, 80, width - marginLeft - 60, height - 190,
                    -0.5, names.Count - 0.5, floor, yMax + (yMax - yMin) * 0.1 + 0.1);

                axes.Frame(OfflinePlot.Grid);
                axes.YTicks(6, v => PlotFormat.F(v, 1), OfflinePlot.Grid, OfflinePlot.Muted);

                var thetaColour = OfflinePlot.ChannelColour(1);
                var alphaColour = OfflinePlot.ChannelColour(5);

                for (var i = 0; i < names.Count; i++)
                {
                    if (!double.IsNaN(theta[i]) && theta[i] > 0d)
                        axes.Bar(i - 0.18, 0.15, Math.Log10(theta[i]), floor, thetaColour);

                    if (!double.IsNaN(alpha[i]) && alpha[i] > 0d)
                        axes.Bar(i + 0.18, 0.15, Math.Log10(alpha[i]), floor, alphaColour);

                    plot.TextCentred(axes.X(i), axes.bottom + 10, names[i].ToUpperInvariant(),
                        OfflinePlot.Ink);
                }

                plot.FillRect(marginLeft, height - 46, 14, 10, thetaColour);
                plot.Text(marginLeft + 22, height - 48, "FRONTOCENTRAL THETA (F3 FZ F4 CZ)",
                    OfflinePlot.Ink);

                plot.FillRect(marginLeft + 400, height - 46, 14, 10, alphaColour);
                plot.Text(marginLeft + 422, height - 48, "POSTERIOR ALPHA (P3 PZ P4)",
                    OfflinePlot.Ink);

                plot.TextVertical(20, axes.bottom - 30, "LOG10 BAND POWER", OfflinePlot.Ink);

                WritePlot(fileName, plot);
            }

            void PlotEpochBars(string fileName, List<Epoch> epochs, string title)
            {
                const int width = 1300;
                const int height = 620;
                const int marginLeft = 120;

                var plot = new OfflinePlot(width, height, OfflinePlot.Background);

                plot.Text(marginLeft, 16, "RECOGNITION BAND POWER — " + title, OfflinePlot.Ink, 2);
                plot.Text(marginLeft, 42, options.sessionId +
                                          "  |  log10, AURA native units squared  |  " +
                                          "single-segment periodogram per epoch  |  " +
                                          "bars are the MEDIAN across epochs",
                    OfflinePlot.Muted);

                // MEDIAN, not mean, for the same reason the phase chart uses one: a single epoch
                // carrying an amplifier step sits several decades above the rest and would be
                // drawn as though it were the condition. The mean and SD are still reported in
                // full in the report table and in recognition_condition_summary.csv.
                var labels = new List<string>();
                var thetaMean = new List<double>();
                var thetaSd = new List<double>();
                var alphaMean = new List<double>();
                var alphaSd = new List<double>();
                var counts = new List<int>();

                foreach (var condition in k_Conditions)
                {
                    var theta = new List<double>();
                    var alpha = new List<double>();

                    foreach (var epoch in epochs)
                    {
                        if (!condition.test(epoch.item) || !epoch.valid)
                            continue;

                        if (!double.IsNaN(epoch.frontocentralTheta))
                            theta.Add(epoch.frontocentralTheta);

                        if (!double.IsNaN(epoch.posteriorAlpha))
                            alpha.Add(epoch.posteriorAlpha);
                    }

                    labels.Add(condition.label);

                    thetaMean.Add(theta.Count > 0
                        ? EegFeaturePipeline.Median(theta)
                        : double.NaN);

                    alphaMean.Add(alpha.Count > 0
                        ? EegFeaturePipeline.Median(alpha)
                        : double.NaN);

                    // No whisker: a standard deviation drawn around a median mixes two summaries
                    // and would imply a symmetric spread this distribution does not have.
                    thetaSd.Add(double.NaN);
                    alphaSd.Add(double.NaN);

                    counts.Add(theta.Count);
                }

                var yMin = double.MaxValue;
                var yMax = double.MinValue;

                for (var i = 0; i < labels.Count; i++)
                {
                    Consider(thetaMean[i], thetaSd[i], ref yMin, ref yMax);
                    Consider(alphaMean[i], alphaSd[i], ref yMin, ref yMax);
                }

                if (yMin > yMax)
                {
                    yMin = 0d;
                    yMax = 1d;
                }

                var floor = yMin - (yMax - yMin) * 0.15 - 0.1;

                var axes = new PlotAxes(plot, marginLeft, 80, width - marginLeft - 60, height - 210,
                    -0.6, labels.Count - 0.4, floor, yMax + (yMax - yMin) * 0.1 + 0.1);

                axes.Frame(OfflinePlot.Grid);
                axes.YTicks(6, v => PlotFormat.F(v, 1), OfflinePlot.Grid, OfflinePlot.Muted);

                var thetaColour = OfflinePlot.ChannelColour(1);
                var alphaColour = OfflinePlot.ChannelColour(5);

                for (var i = 0; i < labels.Count; i++)
                {
                    DrawBarWithSd(plot, axes, i - 0.18, thetaMean[i], thetaSd[i], floor,
                        thetaColour);

                    DrawBarWithSd(plot, axes, i + 0.18, alphaMean[i], alphaSd[i], floor,
                        alphaColour);

                    plot.TextCentred(axes.X(i), axes.bottom + 10, labels[i], OfflinePlot.Ink);
                    plot.TextCentred(axes.X(i), axes.bottom + 22,
                        "n=" + counts[i].ToString(CultureInfo.InvariantCulture),
                        OfflinePlot.Muted);
                }

                plot.FillRect(marginLeft, height - 52, 14, 10, thetaColour);
                plot.Text(marginLeft + 22, height - 54, "FRONTOCENTRAL THETA (F3 FZ F4 CZ)",
                    OfflinePlot.Ink);

                plot.FillRect(marginLeft + 400, height - 52, 14, 10, alphaColour);
                plot.Text(marginLeft + 422, height - 54, "POSTERIOR ALPHA (P3 PZ P4)",
                    OfflinePlot.Ink);

                plot.Text(marginLeft, height - 34,
                    "DESCRIPTIVE ONLY. NO STATISTICAL TEST. NOT A COGNITIVE MEASURE.",
                    OfflinePlot.Warn);

                plot.TextVertical(20, axes.bottom - 30, "LOG10 BAND POWER", OfflinePlot.Ink);

                WritePlot(fileName, plot);
            }

            /// <summary>
            /// Widens the axis range to hold one bar and its error bar.
            ///
            /// When the SD exceeds the mean — routine here, because one artefact epoch can carry a
            /// condition's SD far above its mean — the lower whisker is at a NEGATIVE power, which
            /// has no place on a log axis. It is dropped rather than clamped: clamping it to a
            /// tiny positive number puts log10 at about -323 and drags the whole axis down with
            /// it, which is what made every real bar unreadable. This matches exactly what
            /// <see cref="DrawBarWithSd"/> draws, so the axis always contains what is on it.
            /// </summary>
            static void Consider(double mean, double sd, ref double yMin, ref double yMax)
            {
                if (double.IsNaN(mean) || mean <= 0d)
                    return;

                var spread = double.IsNaN(sd) ? 0d : sd;

                var logMean = Math.Log10(mean);
                var lowValue = mean - spread;
                var lo = lowValue > 0d ? Math.Log10(lowValue) : logMean;
                var hi = Math.Log10(mean + spread);

                if (lo < yMin) yMin = lo;
                if (hi > yMax) yMax = hi;
            }

            static void DrawBarWithSd(OfflinePlot plot, PlotAxes axes, double centre, double mean,
                double sd, double floor, Color32 colour)
            {
                if (double.IsNaN(mean) || mean <= 0d)
                    return;

                var logMean = Math.Log10(mean);
                axes.Bar(centre, 0.15, logMean, floor, colour);

                if (double.IsNaN(sd) || sd <= 0d)
                    return;

                var hi = Math.Log10(mean + sd);
                var loValue = mean - sd;
                var lo = loValue > 0d ? Math.Log10(loValue) : logMean;

                var x = axes.X(centre);
                plot.VLine(x, axes.Y(hi), axes.Y(lo), OfflinePlot.Ink);
                plot.HLine(x - 5, x + 5, axes.Y(hi), OfflinePlot.Ink);
                plot.HLine(x - 5, x + 5, axes.Y(lo), OfflinePlot.Ink);
            }

            void PlotEpochMeanTrace(string fileName, List<Epoch> epochs, int traceSamples)
            {
                const int width = 1300;
                const int height = 560;
                const int marginLeft = 120;
                const int marginRight = 240;

                var plot = new OfflinePlot(width, height, OfflinePlot.Background);

                plot.Text(marginLeft, 16,
                    "STIMULUS-ALIGNED MEAN FILTERED AMPLITUDE — FRONTOCENTRAL MEAN",
                    OfflinePlot.Ink, 2);

                plot.Text(marginLeft, 42, options.sessionId + "  |  " + FilterDescription() +
                                          "  |  NO baseline correction, NO artefact rejection, " +
                                          "NO trial exclusion", OfflinePlot.Muted);

                var groups = new (string label, Func<OfflineSessionEvents.RecognitionItem, bool> test,
                    int colour)[]
                {
                    ("IMM TARGET", item => item.isImmediate && item.isTarget, 1),
                    ("IMM LURE", item => item.isImmediate && item.isLure, 0),
                    ("DEL TARGET", item => item.isDelayed && item.isTarget, 3),
                    ("DEL LURE", item => item.isDelayed && item.isLure, 5),
                };

                var means = new List<double[]>();
                var counts = new List<int>();

                foreach (var group in groups)
                {
                    var accumulator = new double[traceSamples];
                    var n = 0;

                    foreach (var epoch in epochs)
                    {
                        if (!epoch.valid || epoch.roiTrace == null || !group.test(epoch.item))
                            continue;

                        for (var i = 0; i < traceSamples; i++)
                            accumulator[i] += epoch.roiTrace[i];

                        n++;
                    }

                    if (n > 0)
                    {
                        for (var i = 0; i < traceSamples; i++)
                            accumulator[i] /= n;
                    }
                    else
                    {
                        for (var i = 0; i < traceSamples; i++)
                            accumulator[i] = double.NaN;
                    }

                    means.Add(accumulator);
                    counts.Add(n);
                }

                var yMin = double.MaxValue;
                var yMax = double.MinValue;

                foreach (var mean in means)
                {
                    foreach (var v in mean)
                    {
                        if (double.IsNaN(v) || double.IsInfinity(v))
                            continue;

                        if (v < yMin) yMin = v;
                        if (v > yMax) yMax = v;
                    }
                }

                if (yMin > yMax)
                {
                    yMin = -1d;
                    yMax = 1d;
                }

                var pad = (yMax - yMin) * 0.1;

                var axes = new PlotAxes(plot, marginLeft, 80,
                    width - marginLeft - marginRight, height - 170,
                    0d, options.stimulusEpochSeconds, yMin - pad, yMax + pad);

                axes.Frame(OfflinePlot.Grid);
                axes.YTicks(6, v => PlotFormat.Sci(v, 1), OfflinePlot.Grid, OfflinePlot.Muted);
                axes.XTicks(8, v => PlotFormat.F(v, 2), OfflinePlot.Grid, OfflinePlot.Muted);
                axes.HRule(0d, OfflinePlot.Muted);

                var xs = new List<double>();

                for (var i = 0; i < traceSamples; i++)
                    xs.Add(options.stimulusEpochSeconds * i / traceSamples);

                var legendY = 90;

                for (var g = 0; g < groups.Length; g++)
                {
                    var colour = OfflinePlot.ChannelColour(groups[g].colour);
                    axes.Series(xs, means[g], colour);

                    plot.FillRect(axes.right + 16, legendY + 2, 14, 10, colour);
                    plot.Text(axes.right + 36, legendY, groups[g].label, OfflinePlot.Ink, 2);
                    plot.Text(axes.right + 36, legendY + 18,
                        "n=" + counts[g].ToString(CultureInfo.InvariantCulture),
                        OfflinePlot.Muted);

                    legendY += 44;
                }

                plot.TextCentred((axes.left + axes.right) / 2, axes.bottom + 26,
                    "SECONDS FROM ITEM ONSET", OfflinePlot.Ink);

                plot.TextVertical(20, axes.bottom - 30,
                    "FILTERED AMPLITUDE (AURA NATIVE UNITS)", OfflinePlot.Ink);

                plot.Text(marginLeft, height - 34,
                    "ENGINEERING VIEW ONLY. THIS IS NOT AN ERP AND CARRIES NO COGNITIVE CLAIM.",
                    OfflinePlot.Warn);

                WritePlot(fileName, plot);
            }

            // ---- Plot helpers --------------------------------------------------------

            static readonly Color32[] k_PhaseColours =
            {
                new Color32(90, 140, 220, 255),    // Encoding
                new Color32(230, 160, 60, 255),    // Immediate Recognition
                new Color32(110, 190, 120, 255),   // Area B
                new Color32(200, 110, 200, 255),   // Delayed Recognition
            };

            void ShadePhases(PlotAxes axes)
            {
                if (events == null)
                    return;

                for (var i = 0; i < events.phases.Count; i++)
                {
                    var phase = events.phases[i];

                    if (!phase.present)
                        continue;

                    axes.Band(phase.start - m_T0, phase.end - m_T0,
                        k_PhaseColours[i % k_PhaseColours.Length], 0.16);
                }
            }

            void DrawPhaseLegend(OfflinePlot plot, int x, int y)
            {
                if (events == null)
                    return;

                var cursor = x;

                for (var i = 0; i < events.phases.Count; i++)
                {
                    var phase = events.phases[i];

                    if (!phase.present)
                        continue;

                    plot.FillRect(cursor, y, 12, 10,
                        k_PhaseColours[i % k_PhaseColours.Length]);

                    plot.Text(cursor + 18, y - 1, phase.name.ToUpperInvariant(), OfflinePlot.Ink);

                    cursor += 30 + OfflinePlot.TextWidth(phase.name, 1);
                }
            }

            // ---- Output --------------------------------------------------------------

            void WritePlot(string fileName, OfflinePlot plot)
            {
                var path = Path.Combine(outputFolder, fileName);
                var written = plot.Save(path);

                if (string.IsNullOrEmpty(written))
                    result.failures.Add("could not write plot " + fileName);
                else
                    result.filesWritten.Add(written);
            }

            void WriteFile(string fileName, string contents)
            {
                var path = Path.Combine(outputFolder, fileName);

                try
                {
                    File.WriteAllText(path, contents, new UTF8Encoding(false));
                    result.filesWritten.Add(path);
                }
                catch (Exception e)
                {
                    result.failures.Add("could not write " + fileName + ": " + e.Message);
                }
            }

            /// <summary>Appends rows to a CSV, writing the header only when creating it.</summary>
            void AppendFile(string fileName, string contents, string header)
            {
                var path = Path.Combine(outputFolder, fileName);

                try
                {
                    if (!File.Exists(path))
                    {
                        File.WriteAllText(path, contents, new UTF8Encoding(false));
                        result.filesWritten.Add(path);
                        return;
                    }

                    // Drop the repeated header line before appending.
                    var body = contents;

                    if (body.StartsWith(header, StringComparison.Ordinal))
                    {
                        var newline = body.IndexOf('\n');

                        if (newline >= 0)
                            body = body.Substring(newline + 1);
                    }

                    File.AppendAllText(path, body, new UTF8Encoding(false));
                }
                catch (Exception e)
                {
                    result.failures.Add("could not append to " + fileName + ": " + e.Message);
                }
            }
        }
    }
}
