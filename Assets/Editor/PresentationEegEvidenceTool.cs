using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using IkeaEeg.Data;

namespace IkeaEeg.EditorTools
{
    /// <summary>
    /// EDITOR-ONLY capture of EEG technical evidence for the Week 6 presentation.
    ///
    /// WHAT THIS TOOL DOES AND DOES NOT DO:
    /// it INVOKES the project's existing diagnostics and RENDERS THEIR ACTUAL OUTPUT into
    /// readable 1920x1080 panels. It computes no EEG value of its own, and every number that
    /// reaches a panel was produced by the real implementation during this run. The tool writes
    /// the complete unedited report next to each panel as a .txt, so a panel can always be
    /// checked against the full output it was cut from.
    ///
    /// Alongside each panel it records the test name, the wall-clock timestamp, the source type
    /// and whether the input was real EEG or synthetic — because "a spectrum was computed" and
    /// "a spectrum of real brain activity was computed" are different claims.
    ///
    /// TWO KINDS OF EVIDENCE, NEVER MIXED:
    ///
    ///   LIVE   — EegDiagnostics.TestEventWindowReport() and EegSpectralDiagnostics.Report().
    ///            Both open a real LSL inlet. They can only produce evidence while AURA is
    ///            acquiring and transmitting. If no stream resolves, this tool writes NO panel:
    ///            an image captioned with a connection failure would be read off a slide as a
    ///            broken pipeline, and an image captioned with anything else would be a lie.
    ///
    ///   OFFLINE — the real EegSpectralAnalyzer (Hann window, radix-2 FFT, Welch PSD, band
    ///            power) driven with a SYNTHETIC two-tone signal generated here. This validates
    ///            the DSP itself and needs no headset. Every panel it produces is stamped
    ///            SYNTHETIC INPUT — NOT EEG, in the header, in the footer and in the filename's
    ///            manifest entry. It is evidence that the maths is correct. It is NOT evidence
    ///            about anyone's brain.
    ///
    /// The output folder is OUTSIDE this Unity project, next to the presentation, so nothing
    /// here is imported as an asset.
    ///
    /// NOTHING IN THIS FILE IS REFERENCED BY RUNTIME CODE. It lives in an Editor folder, so it
    /// is excluded from every build, and it modifies no scene, no asset and no pipeline setting.
    /// </summary>
    public static class PresentationEegEvidenceTool
    {
        // ---------------------------------------------------------------------------------
        // Output
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Resolved relative to the project so a moved checkout still finds it:
        /// &lt;...&gt;/Unity Projects/IKEA_EEG  ->  &lt;...&gt;/Unity Projects/Week6_Presentation/evidence
        /// </summary>
        static string OutputFolder
        {
            get
            {
                var projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
                var projectsRoot = Directory.GetParent(projectRoot)!.FullName;
                return Path.Combine(projectsRoot, "Week6_Presentation", "evidence");
            }
        }

        const int k_Width = 1920;
        const int k_Height = 1080;

        // Panel geometry, in canvas units (which are 1:1 with output pixels).
        const float k_Margin = 56f;
        const float k_AccentHeight = 10f;

        static readonly Color k_Background = new Color(0.055f, 0.067f, 0.086f);
        static readonly Color k_Panel = new Color(0.086f, 0.102f, 0.129f);
        static readonly Color k_Body = new Color(0.878f, 0.898f, 0.925f);
        static readonly Color k_Muted = new Color(0.588f, 0.635f, 0.702f);
        static readonly Color k_RealAccent = new Color(0.259f, 0.702f, 0.451f);
        static readonly Color k_SyntheticAccent = new Color(0.925f, 0.682f, 0.239f);

        // ---------------------------------------------------------------------------------
        // Entry points
        // ---------------------------------------------------------------------------------

        [MenuItem("IKEA_EEG/Presentation/EEG Evidence/Capture LIVE EEG Evidence (AURA must be streaming)", false, 420)]
        public static void CaptureLiveMenu()
        {
            var ok = CaptureLive(out var report);
            Debug.Log(report);
            EditorUtility.DisplayDialog("IKEA_EEG EEG evidence", report, "OK");

            if (!ok)
            {
                Debug.LogWarning("[IKEA_EEG] No LIVE evidence was written. See the report above.");
            }
        }

        [MenuItem("IKEA_EEG/Presentation/EEG Evidence/Capture OFFLINE DSP Self-Test (no AURA needed)", false, 421)]
        public static void CaptureOfflineMenu()
        {
            CaptureOffline(out var report);
            Debug.Log(report);
            EditorUtility.DisplayDialog("IKEA_EEG EEG evidence", report, "OK");
        }

        [MenuItem("IKEA_EEG/Presentation/EEG Evidence/Capture Everything Available", false, 422)]
        public static void CaptureAllMenu()
        {
            CaptureOffline(out var offline);
            CaptureLive(out var live);

            var report = offline + Environment.NewLine + live;
            Debug.Log(report);
            EditorUtility.DisplayDialog("IKEA_EEG EEG evidence", report, "OK");
        }

        /// <summary>Batch entry point for the offline self-test.</summary>
        public static void CaptureOfflineFromCommandLine()
        {
            CaptureOffline(out var report);
            Debug.Log(report);
        }

        /// <summary>Batch entry point for the live capture.</summary>
        public static void CaptureLiveFromCommandLine()
        {
            var ok = CaptureLive(out var report);
            Debug.Log(report);

            if (!ok)
                Debug.LogWarning("[IKEA_EEG] No LIVE evidence was written.");
        }

        /// <summary>Batch entry point for both.</summary>
        public static void CaptureAllFromCommandLine()
        {
            CaptureOffline(out var offline);
            Debug.Log(offline);

            CaptureLive(out var live);
            Debug.Log(live);
        }

        // ---------------------------------------------------------------------------------
        // LIVE capture
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Runs the two live diagnostics and writes their evidence panels.
        ///
        /// Returns false when nothing was written, which is the normal outcome with no headset
        /// connected. Writing a panel anyway would put a picture of a connection failure into a
        /// folder whose whole purpose is to show that the pipeline works.
        /// </summary>
        public static bool CaptureLive(out string report)
        {
            var log = new StringBuilder();
            log.AppendLine("[IKEA_EEG] ===== EEG EVIDENCE — LIVE CAPTURE =====");

            var folder = OutputFolder;
            Directory.CreateDirectory(folder);

            var wrote = 0;

            // ---- Event-centred window ------------------------------------------------------
            // Real EEG, synthetic researcher marker. EegDiagnostics stamps the marker from
            // liblsl's own local_clock() — the same clock the samples carry — because an event
            // timestamped from any other clock could not fall inside a window at all.
            var eventReport = EegDiagnostics.TestEventWindowReport();

            if (Connected(eventReport))
            {
                WriteRaw(folder, "EEG_03_Event_Centered_Window_RAW.txt", eventReport);

                RenderPanel(
                    Path.Combine(folder, "EEG_03_Event_Centered_Window.png"),
                    "EEG_03",
                    "Event-centred EEG window retrieval",
                    "IKEA_EEG > EEG > Test Event Window  (EegDiagnostics.TestEventWindowReport)",
                    SourceKind.RealEegSyntheticEvent,
                    "REAL AURA EEG SAMPLES  +  SYNTHETIC TEST EVENT",
                    eventReport,
                    "Window is 1.0 s before / 2.0 s after the marker. The EEG is real. " +
                    "The marker is generated by the diagnostic from liblsl local_clock(), not " +
                    "by a participant action. Full unedited output: EEG_03_Event_Centered_Window_RAW.txt");

                log.AppendLine("  EEG_03_Event_Centered_Window.png      WRITTEN (stream connected)");
                wrote++;
            }
            else
            {
                log.AppendLine("  EEG_03  NOT WRITTEN — no AURA stream resolved.");
                log.AppendLine(FirstFailLine(eventReport));
            }

            // ---- Spectral window ------------------------------------------------------------
            var spectralReport = EegSpectralDiagnostics.Report();

            if (Connected(spectralReport))
            {
                WriteRaw(folder, "EEG_04_06_Spectral_Window_RAW.txt", spectralReport);

                RenderPanel(
                    Path.Combine(folder, "EEG_04_Spectral_Window_Input.png"),
                    "EEG_04",
                    "Spectral analysis — acquired input window",
                    "IKEA_EEG > EEG > Analyze Spectral Window  (EegSpectralDiagnostics.Report)",
                    SourceKind.RealEeg,
                    "REAL AURA EEG — LIVE LSL STREAM",
                    ExtractSections(spectralReport, "INPUT:", "WINDOW:"),
                    "Excerpt: the INPUT and WINDOW sections of the live report. The analysis " +
                    "window in the shipped implementation is 4.0 s, not 8.0 s. Full unedited " +
                    "output: EEG_04_06_Spectral_Window_RAW.txt");

                log.AppendLine("  EEG_04_Spectral_Window_Input.png      WRITTEN (stream connected)");
                wrote++;

                RenderPanel(
                    Path.Combine(folder, "EEG_06_Theta_Alpha_Results.png"),
                    "EEG_06",
                    "Theta and alpha band results, per channel and per ROI",
                    "IKEA_EEG > EEG > Analyze Spectral Window  (EegSpectralDiagnostics.Report)",
                    SourceKind.RealEeg,
                    "REAL AURA EEG — LIVE LSL STREAM",
                    ExtractSections(spectralReport, "QUALITY:", "PER CHANNEL", "ROI:",
                        "DERIVED", "BASELINE:", "UNITS:"),
                    "Excerpt: the QUALITY, PER CHANNEL, ROI, DERIVED, BASELINE and UNITS " +
                    "sections of the live report. Powers are in AURA native units, NOT µV² — " +
                    "the stream publishes no scaling. Full output: EEG_04_06_Spectral_Window_RAW.txt");

                log.AppendLine("  EEG_06_Theta_Alpha_Results.png        WRITTEN (stream connected)");
                wrote++;

                // The live preprocessing block is the authoritative version of EEG_05's
                // configuration half, so it replaces the offline placeholder when available.
                RenderPanel(
                    Path.Combine(folder, "EEG_05_Hann_Preprocessing.png"),
                    "EEG_05",
                    "Preprocessing, Hann windowing and spectral resolution",
                    "IKEA_EEG > EEG > Analyze Spectral Window  +  offline DSP self-test",
                    SourceKind.RealEeg,
                    "REAL AURA EEG — LIVE LSL STREAM",
                    ExtractSections(spectralReport, "PREPROCESSING:", "QUALITY:", "WELCH:")
                    + Environment.NewLine + Environment.NewLine
                    + OfflineDspSelfTestReport(),
                    "Upper half: live preprocessing and Welch configuration from the real run. " +
                    "Lower half: the same EegSpectralAnalyzer exercised offline on a synthetic " +
                    "two-tone signal, which is what makes the window and FFT figures checkable.");

                log.AppendLine("  EEG_05_Hann_Preprocessing.png         REWRITTEN with live data");
                wrote++;
            }
            else
            {
                log.AppendLine("  EEG_04  NOT WRITTEN — no AURA stream resolved.");
                log.AppendLine("  EEG_06  NOT WRITTEN — no AURA stream resolved.");
                log.AppendLine(FirstFailLine(spectralReport));
            }

            // ---- Supporting: 8 s timestamp continuity ----------------------------------------
            // Raw text only. This is the project's ONLY eight-second EEG test; it measures
            // timestamp continuity, not spectra.
            var continuityReport = EegDiagnostics.ContinuityReport();

            if (Connected(continuityReport))
            {
                WriteRaw(folder, "EEG_SUPPORTING_Timestamp_Continuity_8s_RAW.txt", continuityReport);
                log.AppendLine("  EEG_SUPPORTING_Timestamp_Continuity_8s_RAW.txt  WRITTEN");
            }

            log.AppendLine();
            log.AppendLine(wrote > 0
                ? $"  {wrote} live panel(s) written to {folder}"
                : "  NOTHING WRITTEN. AURA is not resolving on this network. Start AURA " +
                  "acquisition and LSL transmission, then run this command again.");

            report = log.ToString();
            return wrote > 0;
        }

        /// <summary>
        /// True when the diagnostic actually opened an inlet and read the stream's metadata.
        ///
        /// Both reports print their stream metadata ONLY after a successful Connect, so the
        /// presence of that block is the honest test — far more reliable than scanning for the
        /// word FAIL, which legitimately appears in reports that did connect.
        /// </summary>
        static bool Connected(string report)
        {
            return report != null &&
                   (report.Contains("stream name:") || report.Contains("INPUT:"));
        }

        static string FirstFailLine(string report)
        {
            var line = report?.Split('\n')
                .FirstOrDefault(l => l.Contains("FAIL"))?.TrimEnd();

            return string.IsNullOrEmpty(line) ? "          (no FAIL line in the report)" : "        " + line.Trim();
        }

        // ---------------------------------------------------------------------------------
        // OFFLINE capture
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Writes EEG_05 from the offline DSP self-test. Needs no headset and no LSL stream.
        /// </summary>
        public static void CaptureOffline(out string report)
        {
            var log = new StringBuilder();
            log.AppendLine("[IKEA_EEG] ===== EEG EVIDENCE — OFFLINE DSP SELF-TEST =====");

            var folder = OutputFolder;
            Directory.CreateDirectory(folder);

            var body = OfflineDspSelfTestReport();

            WriteRaw(folder, "EEG_05_Hann_Preprocessing_RAW.txt", body);

            RenderPanel(
                Path.Combine(folder, "EEG_05_Hann_Preprocessing.png"),
                "EEG_05",
                "Hann windowing, FFT and spectral resolution — offline self-test",
                "EegSpectralAnalyzer.HannWindow / Fft / Welch / BandPower (offline)",
                SourceKind.Synthetic,
                "SYNTHETIC INPUT — NOT EEG.  No headset, no LSL stream, no participant.",
                body,
                "Every number above was measured by calling the shipped EegSpectralAnalyzer " +
                "during this run. It validates the DSP, not the acquisition. " +
                "Full output: EEG_05_Hann_Preprocessing_RAW.txt");

            log.AppendLine($"  EEG_05_Hann_Preprocessing.png         WRITTEN (synthetic input)");
            log.AppendLine($"  written to {folder}");

            report = log.ToString();
        }

        /// <summary>
        /// Exercises the REAL spectral analyser and reports what it actually returned.
        ///
        /// The labels below are written here; every VALUE is read back from the shipped
        /// implementation on this run. Nothing is a constant copied out of the source, and no
        /// expected value is printed as if it were a result — where a property is checked
        /// (Hann symmetry, COLA sum), the measured figure is printed beside the criterion so the
        /// check can be disagreed with.
        ///
        /// The input is a two-tone sinusoid at 6 Hz (theta band) and 10 Hz (alpha band). It is
        /// chosen precisely BECAUSE the right answer is known in advance: if the analyser
        /// recovers those two peaks and puts the power in the right bands, the window, the FFT
        /// and the band integration are all working.
        /// </summary>
        static string OfflineDspSelfTestReport()
        {
            var sb = new StringBuilder();
            var invariant = CultureInfo.InvariantCulture;

            sb.AppendLine("OFFLINE DSP SELF-TEST — SYNTHETIC INPUT, NOT EEG");
            sb.AppendLine("  Real code under test: IkeaEeg.Data.EegSpectralAnalyzer");
            sb.AppendLine("  Input: generated two-tone sinusoid. No headset, no LSL, no participant.");
            sb.AppendLine();

            // ---- Signal definition -----------------------------------------------------------
            const double rateHz = 250.0;          // the rate AURA is documented to run at
            const double seconds = 4.0;           // the analysis window the runtime uses
            const double thetaHz = 6.0;
            const double alphaHz = 10.0;
            const double thetaAmp = 2.0;
            const double alphaAmp = 1.0;

            var n = (int)Math.Round(rateHz * seconds);
            var signal = new double[n];

            for (var i = 0; i < n; i++)
            {
                var t = i / rateHz;
                signal[i] = thetaAmp * Math.Sin(2.0 * Math.PI * thetaHz * t) +
                            alphaAmp * Math.Sin(2.0 * Math.PI * alphaHz * t);
            }

            sb.AppendLine("SYNTHETIC SIGNAL:");
            sb.AppendLine($"  sample rate:     {rateHz.ToString("F1", invariant)} Hz " +
                          "(the rate AURA is documented to run at)");
            sb.AppendLine($"  duration:        {seconds.ToString("F1", invariant)} s " +
                          "(the window length the runtime analyses)");
            sb.AppendLine($"  samples:         {n}");
            sb.AppendLine($"  component 1:     {thetaHz.ToString("F1", invariant)} Hz, " +
                          $"amplitude {thetaAmp.ToString("F1", invariant)}  -> THETA band (4-8 Hz)");
            sb.AppendLine($"  component 2:     {alphaHz.ToString("F1", invariant)} Hz, " +
                          $"amplitude {alphaAmp.ToString("F1", invariant)}  -> ALPHA band (8-12 Hz)");
            sb.AppendLine();

            // ---- Hann window -----------------------------------------------------------------
            var segmentSamples = (int)Math.Round(2.0 * rateHz);
            var window = EegSpectralAnalyzer.HannWindow(segmentSamples);

            double sum = 0d, sumSq = 0d, symmetryError = 0d;

            for (var i = 0; i < window.Length; i++)
            {
                sum += window[i];
                sumSq += window[i] * window[i];
            }

            // A periodic Hann is symmetric about its centre EXCLUDING the first sample.
            for (var i = 1; i < window.Length / 2; i++)
                symmetryError = Math.Max(symmetryError,
                    Math.Abs(window[i] - window[window.Length - i]));

            // Constant-overlap-add at 50%: w[i] + w[i + N/2] should be 1 everywhere.
            var half = window.Length / 2;
            double colaMin = double.MaxValue, colaMax = double.MinValue;

            for (var i = 0; i < half; i++)
            {
                var v = window[i] + window[i + half];
                colaMin = Math.Min(colaMin, v);
                colaMax = Math.Max(colaMax, v);
            }

            sb.AppendLine("HANN WINDOW  (EegSpectralAnalyzer.HannWindow — measured this run):");
            sb.AppendLine($"  length:              {window.Length} samples (2.0 s segment)");
            sb.AppendLine($"  w[0]:                {window[0].ToString("F10", invariant)}");
            sb.AppendLine($"  w[N/2]:              {window[half].ToString("F10", invariant)}");
            sb.AppendLine($"  w[N-1]:              {window[window.Length - 1].ToString("F10", invariant)}");
            sb.AppendLine($"  sum(w):              {sum.ToString("F6", invariant)}");
            sb.AppendLine($"  sum(w^2):            {sumSq.ToString("F6", invariant)}   " +
                          "(this is the Welch normaliser)");
            sb.AppendLine($"  symmetry error:      {symmetryError.ToString("E3", invariant)}   " +
                          "(periodic Hann is symmetric about the centre excluding w[0])");
            sb.AppendLine($"  50% overlap-add:     min {colaMin.ToString("F10", invariant)}, " +
                          $"max {colaMax.ToString("F10", invariant)}   " +
                          "(criterion: constant 1.0 — this is why 50% overlap is used)");
            sb.AppendLine();

            // ---- Welch PSD --------------------------------------------------------------------
            var psd = EegSpectralAnalyzer.Welch(signal, rateHz);

            sb.AppendLine("WELCH PSD  (EegSpectralAnalyzer.Welch — measured this run):");
            sb.AppendLine("  Describe(): " + psd.Describe());
            sb.AppendLine($"  segments averaged:   {psd.segmentCount}");
            sb.AppendLine($"  segment length:      {psd.segmentSamples} samples");
            sb.AppendLine($"  FFT length:          {psd.fftLength} (radix-2, zero-padded)");
            sb.AppendLine($"  FFT bin spacing:     {psd.binSpacingHz.ToString("F6", invariant)} Hz " +
                          "(grid spacing — improved by zero padding)");
            sb.AppendLine($"  PHYSICAL resolution: {psd.physicalResolutionHz.ToString("F6", invariant)} Hz " +
                          "(1 / segment duration — NOT improved by zero padding)");
            sb.AppendLine($"  bins returned:       {psd.psd.Length}");
            sb.AppendLine();

            // ---- Band power and peaks ----------------------------------------------------------
            var theta = EegSpectralAnalyzer.BandPower(psd, EegBand.Theta);
            var alpha = EegSpectralAnalyzer.BandPower(psd, EegBand.Alpha);
            var thetaPeak = EegSpectralAnalyzer.PeakFrequency(psd, EegBand.Theta.lowHz, EegBand.Theta.highHz);
            var alphaPeak = EegSpectralAnalyzer.PeakFrequency(psd, EegBand.Alpha.lowHz, EegBand.Alpha.highHz);

            sb.AppendLine("BAND POWER  (EegSpectralAnalyzer.BandPower / PeakFrequency — measured):");
            sb.AppendLine($"  theta band:          {EegBand.Theta.lowHz.ToString("F1", invariant)}" +
                          $"-{EegBand.Theta.highHz.ToString("F1", invariant)} Hz");
            sb.AppendLine($"  theta power:         {theta.ToString("E6", invariant)}");
            sb.AppendLine($"  theta peak:          {thetaPeak.ToString("F4", invariant)} Hz   " +
                          $"(input component was {thetaHz.ToString("F1", invariant)} Hz)");
            sb.AppendLine($"  alpha band:          {EegBand.Alpha.lowHz.ToString("F1", invariant)}" +
                          $"-{EegBand.Alpha.highHz.ToString("F1", invariant)} Hz");
            sb.AppendLine($"  alpha power:         {alpha.ToString("E6", invariant)}");
            sb.AppendLine($"  alpha peak:          {alphaPeak.ToString("F4", invariant)} Hz   " +
                          $"(input component was {alphaHz.ToString("F1", invariant)} Hz)");

            var expectedRatio = (thetaAmp * thetaAmp) / (alphaAmp * alphaAmp);
            var measuredRatio = alpha > 0d ? theta / alpha : double.NaN;

            sb.AppendLine($"  theta/alpha power:   {measuredRatio.ToString("F4", invariant)}   " +
                          $"(amplitude ratio {thetaAmp}:{alphaAmp} predicts " +
                          $"{expectedRatio.ToString("F1", invariant)} for power)");
            sb.AppendLine();

            // ---- Implemented quality checks ----------------------------------------------------
            // Enumerated from the shipped enum, so this list cannot drift from the code.
            sb.AppendLine("SIGNAL-QUALITY CHECKS IMPLEMENTED  (enumerated from EegQualityFlags):");

            foreach (var name in Enum.GetNames(typeof(EegQualityFlags)))
            {
                if (name == "None")
                    continue;

                sb.AppendLine($"    - {name}");
            }

            sb.AppendLine("  These are the flags the runtime can raise on a real window. This");
            sb.AppendLine("  offline test does not exercise them: they need acquired EEG.");
            sb.AppendLine();

            sb.AppendLine("WINDOW RETRIEVAL STATES  (enumerated from EegWindowStatus):");

            foreach (var name in Enum.GetNames(typeof(EegWindowStatus)))
                sb.AppendLine($"    - {name}");

            return sb.ToString().TrimEnd();
        }

        // ---------------------------------------------------------------------------------
        // Report text handling
        // ---------------------------------------------------------------------------------

        /// <summary>Section headers the spectral report prints, in the order it prints them.</summary>
        static readonly string[] k_KnownSections =
        {
            "INPUT:", "PREPROCESSING:", "QUALITY:", "WINDOW:", "WELCH:",
            "PER CHANNEL", "ROI:", "DERIVED", "BASELINE:", "UNITS:",
        };

        /// <summary>
        /// Selects whole sections of a real report, verbatim.
        ///
        /// Lines are never edited, reordered or summarised — a section is either included in
        /// full or left out, and the untouched report is always written beside the panel as a
        /// .txt so the cut can be checked. The preamble before the first section header is
        /// always kept, because it carries the report's own title.
        /// </summary>
        static string ExtractSections(string report, params string[] wanted)
        {
            if (string.IsNullOrEmpty(report))
                return string.Empty;

            var sb = new StringBuilder();
            var current = string.Empty;
            var keeping = true;      // the preamble

            foreach (var raw in report.Replace("\r\n", "\n").Split('\n'))
            {
                var trimmed = raw.TrimStart();
                var header = k_KnownSections.FirstOrDefault(h => trimmed.StartsWith(h, StringComparison.Ordinal));

                if (header != null)
                {
                    current = header;
                    keeping = wanted.Any(w => header.StartsWith(w, StringComparison.Ordinal));
                }

                if (keeping)
                    sb.AppendLine(raw.TrimEnd());
            }

            return sb.ToString().TrimEnd();
        }

        static void WriteRaw(string folder, string fileName, string text)
        {
            File.WriteAllText(Path.Combine(folder, fileName),
                $"# Captured {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz} by " +
                $"IkeaEeg.EditorTools.PresentationEegEvidenceTool{Environment.NewLine}" +
                $"# Unity {Application.unityVersion}{Environment.NewLine}" +
                $"# Complete unedited diagnostic output.{Environment.NewLine}{Environment.NewLine}" +
                text, new UTF8Encoding(false));
        }

        // ---------------------------------------------------------------------------------
        // Panel rendering
        // ---------------------------------------------------------------------------------

        enum SourceKind
        {
            RealEeg,
            RealEegSyntheticEvent,
            Synthetic,
        }

        /// <summary>
        /// Lays the report out on a real Unity canvas and photographs it with an orthographic
        /// camera, so the text is rendered by TextMeshPro rather than drawn into pixels here.
        ///
        /// The canvas is built far below the world, carries HideFlags.DontSave and is destroyed
        /// immediately, so no scene is touched and nothing can survive into an asset.
        /// </summary>
        static void RenderPanel(string path, string evidenceId, string title, string testName,
            SourceKind kind, string sourceBanner, string body, string footer)
        {
            var font = FindFont();

            if (font == null)
            {
                Debug.LogError("[IKEA_EEG] No TMP font asset available; cannot render EEG evidence.");
                return;
            }

            var accent = kind == SourceKind.RealEeg ? k_RealAccent : k_SyntheticAccent;
            var origin = new Vector3(0f, -2000f, 0f);

            var canvasGo = new GameObject("__IKEA_EEG_EVIDENCE_PANEL__",
                typeof(RectTransform), typeof(Canvas)) { hideFlags = HideFlags.DontSave };

            try
            {
                canvasGo.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;

                var root = canvasGo.GetComponent<RectTransform>();
                root.sizeDelta = new Vector2(k_Width, k_Height);
                canvasGo.transform.position = origin;
                canvasGo.transform.rotation = Quaternion.identity;
                canvasGo.transform.localScale = Vector3.one * 0.01f;

                var halfW = k_Width * 0.5f;
                var halfH = k_Height * 0.5f;

                AddImage(root, "BG", Vector2.zero, new Vector2(k_Width, k_Height), k_Background);

                // Accent bar across the very top: green for acquired EEG, amber for synthetic.
                AddImage(root, "Accent", new Vector2(0f, halfH - k_AccentHeight * 0.5f),
                    new Vector2(k_Width, k_AccentHeight), accent);

                // ---- Header --------------------------------------------------------------
                var headerTop = halfH - k_AccentHeight;

                AddText(root, "Id", new Vector2(-halfW + k_Margin, headerTop - 34f),
                    new Vector2(300f, 44f), evidenceId, font, 30f, TextAlignmentOptions.TopLeft,
                    accent, bold: true);

                AddText(root, "Title", new Vector2(-halfW + k_Margin, headerTop - 78f),
                    new Vector2(k_Width - k_Margin * 2f, 50f), title, font, 36f,
                    TextAlignmentOptions.TopLeft, Color.white, bold: true);

                AddText(root, "Banner", new Vector2(-halfW + k_Margin, headerTop - 132f),
                    new Vector2(k_Width - k_Margin * 2f, 40f), sourceBanner, font, 26f,
                    TextAlignmentOptions.TopLeft, accent, bold: true);

                // ---- Provenance block ----------------------------------------------------
                var meta = new StringBuilder();
                meta.AppendLine($"test            {testName}");
                meta.AppendLine($"captured        {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
                meta.AppendLine($"source type     {Describe(kind)}");
                meta.AppendLine($"input           {(kind == SourceKind.Synthetic ? "SYNTHETIC — generated by the test" : "REAL — acquired from the AURA LSL stream")}");
                meta.Append($"environment     Unity {Application.unityVersion}, Editor, Play Mode NOT entered");

                AddText(root, "Meta", new Vector2(-halfW + k_Margin, headerTop - 186f),
                    new Vector2(k_Width - k_Margin * 2f, 130f), meta.ToString(), font, 21f,
                    TextAlignmentOptions.TopLeft, k_Muted);

                // ---- Body ----------------------------------------------------------------
                const float bodyTop = 200f;
                const float bodyBottom = -400f;
                const float bodyHeight = bodyTop - bodyBottom;

                AddImage(root, "BodyPlate", new Vector2(0f, (bodyTop + bodyBottom) * 0.5f),
                    new Vector2(k_Width - k_Margin * 2f + 28f, bodyHeight + 28f), k_Panel);

                var lines = body.Replace("\r\n", "\n").Split('\n');

                // A report long enough to need shrinking below reading size gets two columns
                // instead. Half the width at twice the type is the same information, legible
                // from the back of a room; the previous single column bottomed out at 10 px.
                if (lines.Length > 42)
                {
                    var split = FindColumnBreak(lines);
                    var columnWidth = (k_Width - k_Margin * 2f - 40f) * 0.5f;

                    AddBodyColumn(root, "BodyLeft", new Vector2(-halfW + k_Margin, bodyTop),
                        new Vector2(columnWidth, bodyHeight),
                        string.Join("\n", lines.Take(split)), font);

                    AddBodyColumn(root, "BodyRight",
                        new Vector2(-halfW + k_Margin + columnWidth + 40f, bodyTop),
                        new Vector2(columnWidth, bodyHeight),
                        string.Join("\n", lines.Skip(split)), font);
                }
                else
                {
                    AddBodyColumn(root, "Body", new Vector2(-halfW + k_Margin, bodyTop),
                        new Vector2(k_Width - k_Margin * 2f, bodyHeight), body, font);
                }

                // ---- Footer --------------------------------------------------------------
                AddText(root, "Footer", new Vector2(-halfW + k_Margin, bodyBottom - 34f),
                    new Vector2(k_Width - k_Margin * 2f, 96f), footer, font, 19f,
                    TextAlignmentOptions.TopLeft, k_Muted);

                Canvas.ForceUpdateCanvases();

                RenderOrthographic(origin + new Vector3(0f, 0f, -10f), k_Height * 0.01f * 0.5f, path);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(canvasGo);
            }
        }

        static string Describe(SourceKind kind)
        {
            switch (kind)
            {
                case SourceKind.RealEeg:
                    return "LIVE AURA EEG over LSL";
                case SourceKind.RealEegSyntheticEvent:
                    return "LIVE AURA EEG over LSL + SYNTHETIC researcher event marker";
                default:
                    return "OFFLINE SELF-TEST — synthetic signal, no acquisition";
            }
        }

        /// <summary>
        /// A TMP font asset to render with. TMP's configured default first, then the package's
        /// own resource, then anything in the project — this tool may run in batch mode with no
        /// scene open, so it cannot rely on finding a label to borrow a font from.
        /// </summary>
        static TMP_FontAsset FindFont()
        {
            if (TMP_Settings.defaultFontAsset != null)
                return TMP_Settings.defaultFontAsset;

            var resource = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
            if (resource != null)
                return resource;

            foreach (var guid in AssetDatabase.FindAssets("t:TMP_FontAsset"))
            {
                var asset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(
                    AssetDatabase.GUIDToAssetPath(guid));

                if (asset != null)
                    return asset;
            }

            return null;
        }

        /// <summary>
        /// Where to break a report into two columns: the blank line closest to the halfway
        /// point, so a section is never sliced across the gutter.
        /// </summary>
        static int FindColumnBreak(IReadOnlyList<string> lines)
        {
            var target = lines.Count / 2;
            var best = target;
            var bestDistance = int.MaxValue;

            for (var i = 1; i < lines.Count; i++)
            {
                if (lines[i].Trim().Length != 0)
                    continue;

                var distance = Math.Abs(i - target);

                if (distance >= bestDistance)
                    continue;

                bestDistance = distance;
                best = i + 1;      // start the next column after the blank line
            }

            return Mathf.Clamp(best, 1, lines.Count - 1);
        }

        static void AddBodyColumn(RectTransform parent, string name, Vector2 topLeft,
            Vector2 size, string text, TMP_FontAsset font)
        {
            var label = AddText(parent, name, topLeft, size, text, font, 18f,
                TextAlignmentOptions.TopLeft, k_Body);

            label.enableAutoSizing = true;
            label.fontSizeMin = 12f;
            label.fontSizeMax = 18f;
            label.ForceMeshUpdate();
        }

        static void AddImage(RectTransform parent, string name, Vector2 anchoredPosition,
            Vector2 size, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image))
            {
                hideFlags = HideFlags.DontSave,
            };

            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = anchoredPosition;

            go.GetComponent<Image>().color = color;
        }

        /// <summary>Top-left anchored text, so blocks stack predictably down the panel.</summary>
        static TextMeshProUGUI AddText(RectTransform parent, string name, Vector2 topLeft,
            Vector2 size, string text, TMP_FontAsset font, float fontSize,
            TextAlignmentOptions alignment, Color color, bool bold = false)
        {
            var go = new GameObject(name, typeof(RectTransform)) { hideFlags = HideFlags.DontSave };

            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = size;
            rect.anchoredPosition = topLeft;

            var label = go.AddComponent<TextMeshProUGUI>();
            label.font = font;
            label.text = text;
            label.fontSize = fontSize;
            label.alignment = alignment;
            label.color = color;
            label.richText = false;      // reports contain characters TMP would parse as tags
            label.lineSpacing = -8f;

            if (bold)
                label.fontStyle = FontStyles.Bold;

            label.ForceMeshUpdate();
            return label;
        }

        static void RenderOrthographic(Vector3 position, float orthographicSize, string path)
        {
            var go = new GameObject("__IKEA_EEG_EVIDENCE_CAMERA__") { hideFlags = HideFlags.DontSave };
            RenderTexture rt = null;
            Texture2D texture = null;

            try
            {
                go.transform.SetPositionAndRotation(position, Quaternion.identity);

                var camera = go.AddComponent<Camera>();
                camera.orthographic = true;
                camera.orthographicSize = orthographicSize;
                camera.aspect = (float)k_Width / k_Height;
                camera.cullingMask = ~0;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = k_Background;
                camera.nearClipPlane = 0.05f;
                camera.farClipPlane = 20f;

                var urp = go.GetComponent<UniversalAdditionalCameraData>()
                          ?? go.AddComponent<UniversalAdditionalCameraData>();
                urp.renderShadows = false;
                urp.renderPostProcessing = false;

                rt = new RenderTexture(k_Width, k_Height, 24, RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.sRGB) { antiAliasing = 4 };
                rt.Create();

                if (!TrySubmitRenderRequest(camera, rt))
                {
                    camera.targetTexture = rt;
                    camera.Render();
                    camera.targetTexture = null;
                }

                var previous = RenderTexture.active;
                RenderTexture.active = rt;

                texture = new Texture2D(k_Width, k_Height, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, k_Width, k_Height), 0, 0);
                texture.Apply();

                RenderTexture.active = previous;

                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            finally
            {
                if (texture != null)
                    UnityEngine.Object.DestroyImmediate(texture);

                if (rt != null)
                {
                    rt.Release();
                    UnityEngine.Object.DestroyImmediate(rt);
                }

                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        static bool TrySubmitRenderRequest(Camera camera, RenderTexture destination)
        {
            try
            {
                var request = new UniversalRenderPipeline.SingleCameraRequest
                {
                    destination = destination,
                };

                if (!RenderPipeline.SupportsRenderRequest(camera, request))
                    return false;

                RenderPipeline.SubmitRenderRequest(camera, request);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
