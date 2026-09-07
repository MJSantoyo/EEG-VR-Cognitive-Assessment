using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using IkeaEeg.Experiment;
using IkeaEeg.Interaction;
using IkeaEeg.Localization;
using Debug = UnityEngine.Debug;

namespace IkeaEeg.EditorTools
{
    /// <summary>
    /// Produces the spoken familiarization narration for Area 0, in every language that has a
    /// LOCAL voice installed.
    ///
    /// Same offline, authoring-time approach as the word clips — the local Windows voices, no
    /// cloud service, no API key, no network, no runtime TTS dependency — but a SEPARATE tool
    /// writing to SEPARATE per-language folders.
    ///
    /// WHY SEPARATE FROM THE WORD CLIPS: the verbal-memory word clips are experimental stimulus
    /// material. Their generator, folder and assignment are untouched. Instructional narration
    /// is not a memory stimulus and must never be able to end up in a word set.
    ///
    /// LANGUAGE HONESTY: a language is only generated if a voice for THAT language is installed.
    /// An English voice is never used to read Spanish or Japanese — the result would be
    /// unintelligible and would misrepresent what the participant was given. A language with no
    /// voice is left with no narration, its text still fully localized, and the gap is reported.
    /// </summary>
    public static class NarrationClipGenerator
    {
        const string k_ScriptPath = "Assets/IKEA_EEG/Editor/GenerateNarrationClips.ps1";
        const string k_OutputRoot = "Assets/IKEA_EEG/Audio/Narration";

        const string k_IntroId = "Familiarization_Intro";
        const string k_PracticeIntroId = "Practice_Intro";

        // Practice feedback. The id suffix matches ExperimentManager.PracticeFeedbackIds so the
        // clip that is WRITTEN and the clip that is LOOKED UP are named by the same value.
        const string k_FeedbackPrefix = "Practice_Feedback_";

        /// <summary>Recognition-mode encoding INSTRUCTION. Never an encoding word.</summary>
        const string k_RecognitionInstructionId = "Recognition_Encoding_Instructions";

        /// <summary>GENERAL Area B task instructions. Never a trial's target characteristics.</summary>
        const string k_AreaBInstructionId = "AreaB_Task_Instructions";

        /// <summary>Recognition DELAYED-phase instructions. Never a stimulus word.</summary>
        const string k_RecognitionDelayedId = "Recognition_Delayed_Instructions";

        /// <summary>
        /// One language's narration script and the local voice it needs.
        ///
        /// The voice is matched by CULTURE (en / es / ja), not by name, so the tool works on any
        /// machine with a suitable voice rather than only on the one it was written on.
        /// </summary>
        class LanguageScript
        {
            public ExperimentLanguage language;
            public string culturePrefix;
            public string intro;
            public string practiceIntro;

            /// <summary>
            /// One spoken prompt per practice colour, keyed by the ChairColor ENUM rather than
            /// by a colour string. The clip that gets written and the clip that gets played are
            /// then addressed by the same typed value, so a language can never be given the
            /// prompt for a colour it did not ask for.
            /// </summary>
            public Dictionary<ChairColor, string> colorPrompts;

            /// <summary>
            /// Practice feedback lines, keyed by the ids in
            /// <see cref="ExperimentManager.PracticeFeedbackIds"/>.
            /// </summary>
            public Dictionary<string, string> feedback;

            /// <summary>
            /// Recognition encoding INSTRUCTIONS. Null for a language whose Recognition wording
            /// has not been approved — that language then runs the instruction text-only rather
            /// than being given an unreviewed translation in a synthetic voice.
            /// </summary>
            public string recognitionInstructions;

            /// <summary>
            /// GENERAL Area B task instructions — how the chair task works.
            ///
            /// MUST NOT contain a colour, a size or a shape that could name a trial's target:
            /// the participant is meant to read those off the panel and solve the match
            /// visually. Naming them aloud would hand over the answer.
            /// </summary>
            public string areaBInstructions;

            /// <summary>Recognition delayed-phase instructions. Null where not approved.</summary>
            public string recognitionDelayedInstructions;
        }

        static readonly LanguageScript[] k_Scripts =
        {
            new LanguageScript
            {
                language = ExperimentLanguage.English,
                culturePrefix = "en",
                intro =
                    "Welcome. Before beginning the cognitive task, take a moment to become " +
                    "familiar with the controls. " +
                    "Point toward an object using either controller. " +
                    "You may select using either the front index trigger or the side grip " +
                    "trigger. " +
                    "Complete the short practice task, then select Start Experiment when you " +
                    "are ready.",
                practiceIntro = "Now practice by selecting the requested color object.",
                colorPrompts = new Dictionary<ChairColor, string>
                {
                    { ChairColor.Red, "Select the red object." },
                    { ChairColor.Blue, "Select the blue object." },
                    { ChairColor.Yellow, "Select the yellow object." },
                    { ChairColor.Green, "Select the green object." },
                },
                feedback = new Dictionary<string, string>
                {
                    { ExperimentManager.PracticeFeedbackIds.Correct, "Good job." },
                    { ExperimentManager.PracticeFeedbackIds.Another, "Please select another one." },
                    { ExperimentManager.PracticeFeedbackIds.Incorrect,
                        "That is not correct. Please try again." },
                    { ExperimentManager.PracticeFeedbackIds.Complete,
                        "Great. You are ready to begin the experiment." },
                },
                recognitionInstructions =
                    "You will see a total of 15 words, one at a time. " +
                    "Try to remember them, because you will be asked about them later.",

                // Deliberately generic: no colour, no size, no shape category is named, so the
                // narration can never leak a trial's answer.
                areaBInstructions =
                    "Each chair has a shape, a colour and a size. " +
                    "You will be shown one of each. " +
                    "Select the one chair that matches all three. " +
                    "Use the controller trigger to select. " +
                    "Press ready when you understand the task.",

                recognitionDelayedInstructions =
                    "You will now be shown words again. " +
                    "For each word, select whether you saw it earlier.",
            },
            new LanguageScript
            {
                language = ExperimentLanguage.Spanish,
                culturePrefix = "es",
                intro =
                    "Bienvenido. Antes de comenzar la tarea cognitiva, tómese un momento para " +
                    "familiarizarse con los controles. " +
                    "Apunte a un objeto con cualquiera de los dos mandos. " +
                    "Puede seleccionar con el gatillo delantero del dedo índice o con el " +
                    "gatillo lateral. " +
                    "Complete la breve tarea de práctica y luego pulse Comenzar Experimento " +
                    "cuando esté listo.",
                practiceIntro = "Ahora practique seleccionando el objeto del color indicado.",
                colorPrompts = new Dictionary<ChairColor, string>
                {
                    { ChairColor.Red, "Seleccione el objeto rojo." },
                    { ChairColor.Blue, "Seleccione el objeto azul." },
                    { ChairColor.Yellow, "Seleccione el objeto amarillo." },
                    { ChairColor.Green, "Seleccione el objeto verde." },
                },
                feedback = new Dictionary<string, string>
                {
                    { ExperimentManager.PracticeFeedbackIds.Correct, "Muy bien." },
                    { ExperimentManager.PracticeFeedbackIds.Another, "Por favor, seleccione otro." },
                    { ExperimentManager.PracticeFeedbackIds.Incorrect,
                        "Eso no es correcto. Inténtelo de nuevo." },
                    { ExperimentManager.PracticeFeedbackIds.Complete,
                        "Excelente. Está listo para comenzar el experimento." },
                },

                // DELIBERATELY NULL. The Recognition protocol's participant-facing wording has
                // not been approved for Spanish, and a synthetic voice reading an unreviewed
                // translation of a methodological instruction would be worse than silence: the
                // gap is visible, a wrong translation would not be. Spanish runs the Recognition
                // instruction text-only until approved wording exists.
                recognitionInstructions = null,
            },
            new LanguageScript
            {
                language = ExperimentLanguage.Japanese,
                culturePrefix = "ja",
                intro =
                    "ようこそ。認知課題を始める前に、少し操作に慣れてください。" +
                    "どちらのコントローラーでも、物体を指してください。" +
                    "人さし指の前のトリガーでも、横のグリップトリガーでも選べます。" +
                    "短い練習を終えたら、準備ができたときに「実験を開始」を選んでください。",
                practiceIntro = "では、指定された色の物体を選ぶ練習をしましょう。",
                colorPrompts = new Dictionary<ChairColor, string>
                {
                    { ChairColor.Red, "赤い物体を選んでください。" },
                    { ChairColor.Blue, "青い物体を選んでください。" },
                    { ChairColor.Yellow, "黄色い物体を選んでください。" },
                    { ChairColor.Green, "緑の物体を選んでください。" },
                },
                feedback = new Dictionary<string, string>
                {
                    { ExperimentManager.PracticeFeedbackIds.Correct, "よくできました。" },
                    { ExperimentManager.PracticeFeedbackIds.Another, "もう一つ選んでください。" },
                    { ExperimentManager.PracticeFeedbackIds.Incorrect,
                        "それは正しくありません。もう一度お試しください。" },
                    { ExperimentManager.PracticeFeedbackIds.Complete,
                        "素晴らしいです。実験を開始する準備ができました。" },
                },

                // DELIBERATELY NULL — same reason as Spanish above.
                recognitionInstructions = null,
                areaBInstructions = null,
                recognitionDelayedInstructions = null,
            },
        };

        [MenuItem("IKEA_EEG/Generate Familiarization Narration (local TTS)", false, 42)]
        public static void GenerateMenu()
        {
            Generate();
        }

        /// <summary>Batch-mode entry point.</summary>
        public static void GenerateFromCommandLine()
        {
            Generate();
        }

        public static bool Generate()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                Debug.LogError("[IKEA_EEG] Narration generation uses the local Windows speech " +
                               "engine and only runs in the Windows Editor.");
                return false;
            }

            var scriptPath = Path.GetFullPath(k_ScriptPath);
            if (!File.Exists(scriptPath))
            {
                Debug.LogError($"[IKEA_EEG] Narration script missing: {k_ScriptPath}");
                return false;
            }

            var config = AssetDatabase.LoadAssetAtPath<ExperimentConfig>(
                ExperimentAssetBuilder.ConfigPath);

            if (config == null)
            {
                Debug.LogError("[IKEA_EEG] No ExperimentConfig; cannot assign narration.");
                return false;
            }

            config.familiarizationNarrationClips = new List<NarrationClipEntry>();
            config.practiceIntroNarrationClips = new List<NarrationClipEntry>();
            config.practiceColorNarrationClips = new List<NarrationClipEntry>();
            config.practiceFeedbackNarrationClips = new List<NarrationClipEntry>();
            config.recognitionInstructionNarrationClips = new List<NarrationClipEntry>();
            config.areaBInstructionNarrationClips = new List<NarrationClipEntry>();
            config.recognitionDelayedNarrationClips = new List<NarrationClipEntry>();

            var report = new StringBuilder();
            report.AppendLine("[IKEA_EEG] ===== NARRATION GENERATION =====");

            var anyGenerated = false;

            foreach (var script in k_Scripts)
            {
                var code = ExperimentLanguages.ToCode(script.language);
                var voice = FindVoiceForCulture(script.culturePrefix);

                if (string.IsNullOrEmpty(voice))
                {
                    // Explicitly NOT generated with another language's voice.
                    report.AppendLine($"  {code}: NO LOCAL VOICE for culture " +
                                      $"'{script.culturePrefix}-*'. Narration NOT generated. " +
                                      "Text localization is unaffected; this language runs " +
                                      "text-only and no other language's voice is substituted.");
                    continue;
                }

                var folder = $"{k_OutputRoot}/{code}";
                Directory.CreateDirectory(Path.GetFullPath(folder));

                var linesPath = Path.Combine(Path.GetTempPath(),
                    $"ikea_eeg_narration_{code}_{Guid.NewGuid():N}.txt");

                try
                {
                    var lines = new StringBuilder();
                    lines.Append(k_IntroId).Append('|').Append(script.intro).Append('\n');
                    lines.Append(k_PracticeIntroId).Append('|').Append(script.practiceIntro)
                         .Append('\n');

                    foreach (var pair in script.colorPrompts)
                        lines.Append(ClipIdFor(pair.Key)).Append('|').Append(pair.Value)
                             .Append('\n');

                    if (script.feedback != null)
                    {
                        foreach (var pair in script.feedback)
                            lines.Append(k_FeedbackPrefix).Append(pair.Key).Append('|')
                                 .Append(pair.Value).Append('\n');
                    }

                    // Generated ONLY for a language whose Recognition wording is approved.
                    // ES/JA are null on purpose: an unreviewed translation of a
                    // methodological instruction, read by a synthetic voice, would be worse
                    // than the visible gap of running that instruction text-only.
                    if (!string.IsNullOrEmpty(script.recognitionInstructions))
                    {
                        lines.Append(k_RecognitionInstructionId).Append('|')
                             .Append(script.recognitionInstructions).Append('\n');
                    }

                    if (!string.IsNullOrEmpty(script.areaBInstructions))
                    {
                        lines.Append(k_AreaBInstructionId).Append('|')
                             .Append(script.areaBInstructions).Append('\n');
                    }

                    if (!string.IsNullOrEmpty(script.recognitionDelayedInstructions))
                    {
                        lines.Append(k_RecognitionDelayedId).Append('|')
                             .Append(script.recognitionDelayedInstructions).Append('\n');
                    }

                    File.WriteAllText(linesPath, lines.ToString(), new UTF8Encoding(false));

                    if (!RunScript(scriptPath, Path.GetFullPath(folder), linesPath, voice))
                    {
                        report.AppendLine($"  {code}: generation FAILED (voice '{voice}').");
                        continue;
                    }
                }
                finally
                {
                    try
                    {
                        if (File.Exists(linesPath))
                            File.Delete(linesPath);
                    }
                    catch
                    {
                        // A leftover temp file is not worth failing the generation over.
                    }
                }

                anyGenerated = true;
                report.AppendLine($"  {code}: generated with local voice '{voice}'.");
            }

            AssetDatabase.Refresh();

            // ---- Assign whatever actually exists ------------------------------------------
            foreach (var script in k_Scripts)
            {
                var code = ExperimentLanguages.ToCode(script.language);

                var intro = LoadClip(code, k_IntroId);
                if (intro != null)
                    config.familiarizationNarrationClips.Add(new NarrationClipEntry
                        { id = code, clip = intro });

                var practiceIntro = LoadClip(code, k_PracticeIntroId);
                if (practiceIntro != null)
                    config.practiceIntroNarrationClips.Add(new NarrationClipEntry
                        { id = code, clip = practiceIntro });

                foreach (var pair in script.colorPrompts)
                {
                    var clip = LoadClip(code, ClipIdFor(pair.Key));
                    if (clip == null)
                        continue;

                    // Keyed "<LANG>_<COLOR>", e.g. ES_BLUE — built by the SAME helper the
                    // run-time lookup calls, from the SAME ChairColor value that named the file.
                    // The mapping cannot drift because there is only one expression of it.
                    config.practiceColorNarrationClips.Add(new NarrationClipEntry
                    {
                        id = PracticeColors.NarrationKey(script.language, pair.Key),
                        clip = clip,
                    });
                }

                if (script.feedback != null)
                {
                    foreach (var pair in script.feedback)
                    {
                        var feedbackClip = LoadClip(code, k_FeedbackPrefix + pair.Key);
                        if (feedbackClip == null)
                            continue;

                        // Keyed "<LANG>_<ID>" by the same expression ExperimentConfig looks it
                        // up with, so the mapping cannot drift.
                        config.practiceFeedbackNarrationClips.Add(new NarrationClipEntry
                        {
                            id = $"{code}_{pair.Key}",
                            clip = feedbackClip,
                        });
                    }
                }

                var recognitionClip = LoadClip(code, k_RecognitionInstructionId);
                if (recognitionClip != null)
                {
                    config.recognitionInstructionNarrationClips.Add(new NarrationClipEntry
                        { id = code, clip = recognitionClip });
                }

                var areaBClip = LoadClip(code, k_AreaBInstructionId);
                if (areaBClip != null)
                {
                    config.areaBInstructionNarrationClips.Add(new NarrationClipEntry
                        { id = code, clip = areaBClip });
                }

                var delayedClip = LoadClip(code, k_RecognitionDelayedId);
                if (delayedClip != null)
                {
                    config.recognitionDelayedNarrationClips.Add(new NarrationClipEntry
                        { id = code, clip = delayedClip });
                }
            }

            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssets();

            report.AppendLine($"  assigned: {config.practiceFeedbackNarrationClips.Count} " +
                              "practice-feedback, " +
                              $"{config.recognitionInstructionNarrationClips.Count} " +
                              "recognition-instruction (EN only by design),");
            report.AppendLine($"  assigned: {config.familiarizationNarrationClips.Count} intro, " +
                              $"{config.practiceIntroNarrationClips.Count} practice lead-in, " +
                              $"{config.practiceColorNarrationClips.Count} colour prompt(s).");

            Debug.Log(report.ToString().TrimEnd());
            return anyGenerated;
        }

        /// <summary>
        /// The name of an installed, enabled voice for a culture, or empty.
        ///
        /// Asking the speech engine directly is the only honest way to know: a language is
        /// generated because a voice for it EXISTS on this machine, never because it was
        /// assumed to.
        /// </summary>
        public static string FindVoiceForCulture(string culturePrefix)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -Command \"Add-Type -AssemblyName System.Speech; " +
                            "$s = New-Object System.Speech.Synthesis.SpeechSynthesizer; " +
                            "$s.GetInstalledVoices() | Where-Object { $_.Enabled } | " +
                            "ForEach-Object { $_.VoiceInfo.Culture.Name + '|' + " +
                            "$_.VoiceInfo.Name }; $s.Dispose()\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            try
            {
                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                        return string.Empty;

                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    foreach (var line in output.Split('\n'))
                    {
                        var trimmed = line.Trim();
                        var separator = trimmed.IndexOf('|');
                        if (separator < 1)
                            continue;

                        var culture = trimmed.Substring(0, separator);
                        var name = trimmed.Substring(separator + 1);

                        if (culture.StartsWith(culturePrefix, StringComparison.OrdinalIgnoreCase))
                            return name;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[IKEA_EEG] Could not enumerate local voices: {e.Message}");
            }

            return string.Empty;
        }

        /// <summary>
        /// The file name of a practice colour prompt, e.g. Practice_BLUE. Derived from the
        /// canonical colour name so the file, the config key and the run-time lookup all come
        /// from the one ChairColor value.
        /// </summary>
        static string ClipIdFor(ChairColor color) =>
            $"Practice_{PracticeColors.CanonicalName(color)}";

        /// <summary>
        /// Every language/colour pair this tool is responsible for producing. Exposed so the
        /// self test can assert the full 3 x 4 mapping against the generator's own list rather
        /// than against a second copy of it.
        /// </summary>
        public static IEnumerable<(ExperimentLanguage language, ChairColor color, string key)>
            ExpectedPracticeColorMappings()
        {
            foreach (var script in k_Scripts)
            {
                foreach (var pair in script.colorPrompts)
                {
                    yield return (script.language, pair.Key,
                        PracticeColors.NarrationKey(script.language, pair.Key));
                }
            }
        }

        static AudioClip LoadClip(string languageCode, string clipId)
        {
            return AssetDatabase.LoadAssetAtPath<AudioClip>(
                $"{k_OutputRoot}/{languageCode}/{clipId}.wav");
        }

        static bool RunScript(string scriptPath, string outputDir, string linesPath, string voice)
        {
            var arguments =
                $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" " +
                $"-OutputDir \"{outputDir}\" -LinesFile \"{linesPath}\" -VoiceName \"{voice}\"";

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            try
            {
                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        Debug.LogError("[IKEA_EEG] Could not start powershell.exe.");
                        return false;
                    }

                    var output = process.StandardOutput.ReadToEnd();
                    var error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    if (!string.IsNullOrWhiteSpace(output))
                        Debug.Log($"[IKEA_EEG] Narration output:\n{output.Trim()}");

                    if (process.ExitCode != 0)
                    {
                        Debug.LogError($"[IKEA_EEG] Narration generation failed " +
                                       $"(exit {process.ExitCode}): {error.Trim()}");
                        return false;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[IKEA_EEG] Narration generation threw: {e.Message}");
                return false;
            }

            return true;
        }
    }
}
