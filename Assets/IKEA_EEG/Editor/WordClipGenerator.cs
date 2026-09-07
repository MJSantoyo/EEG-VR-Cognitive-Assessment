using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using IkeaEeg.Experiment;
using IkeaEeg.Localization;
using IkeaEeg.Memory;
using Debug = UnityEngine.Debug;

namespace IkeaEeg.EditorTools
{
    /// <summary>
    /// Produces the spoken-word WAV assets for the verbal memory task.
    ///
    /// WHY THIS DESIGN:
    ///   * The words must be HEARD, not read, so each word needs real audio.
    ///   * Runtime text-to-speech was rejected: the Windows speech API plays through the OS
    ///     mixer rather than an AudioSource, so WORD_PRESENTED could not be tied to a clip
    ///     onset (a hard requirement), and none of it exists on a standalone Quest build.
    ///   * So speech is synthesised ONCE, here, at authoring time, using the LOCAL Windows
    ///     voice — offline, no cloud service, no API key, no network. The result is committed
    ///     as ordinary WAV assets, giving the running experiment zero TTS dependency.
    ///   * The clips are stored ON THE WORD SET, so a new word set gets its own recordings and
    ///     the list stays fully configurable.
    ///
    /// The generated clips are placeholders in the sense that they are synthetic; replacing
    /// any of them with a human recording is just dragging a WAV onto the word set.
    /// </summary>
    public static class WordClipGenerator
    {
        const string k_ScriptPath = "Assets/IKEA_EEG/Editor/GenerateWordClips.ps1";
        const string k_OutputRoot = "Assets/IKEA_EEG/Audio/Words";

        [MenuItem("IKEA_EEG/Generate Spoken Word Clips (local TTS)", false, 41)]
        public static void GenerateMenu()
        {
            var wordList = AssetDatabase.LoadAssetAtPath<WordListDefinition>(
                ExperimentAssetBuilder.WordListPath);

            if (wordList == null)
            {
                Debug.LogError($"[IKEA_EEG] No WordListDefinition at {ExperimentAssetBuilder.WordListPath}. " +
                               "Run IKEA_EEG > Build Experiment Scene first.");
                return;
            }

            GenerateForAllSets(wordList);
        }

        /// <summary>Batch-mode entry point.</summary>
        public static void GenerateFromCommandLine()
        {
            GenerateMenu();
        }

        /// <summary>
        /// Generates clips ONLY for sets that are missing one.
        ///
        /// WHY THIS EXISTS SEPARATELY: the full generator re-synthesises and re-trims every
        /// set. Re-cutting audio a session may already have used would change the stimulus
        /// after the fact — the trim is measured per clip, so a regenerated "River" is not
        /// byte-identical to the one a participant heard. This adds the new language sets while
        /// leaving every existing recording exactly as it is.
        /// </summary>
        [MenuItem("IKEA_EEG/Generate Spoken Word Clips — MISSING ONLY (local TTS)", false, 42)]
        public static void GenerateMissingMenu()
        {
            var wordList = AssetDatabase.LoadAssetAtPath<WordListDefinition>(
                ExperimentAssetBuilder.WordListPath);

            if (wordList == null)
            {
                Debug.LogError($"[IKEA_EEG] No WordListDefinition at " +
                               $"{ExperimentAssetBuilder.WordListPath}.");
                return;
            }

            GenerateMissingOnly(wordList);
        }

        /// <summary>Batch-mode entry point for the missing-only generator.</summary>
        public static void GenerateMissingFromCommandLine()
        {
            GenerateMissingMenu();
        }

        public static bool GenerateMissingOnly(WordListDefinition wordList)
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                Debug.LogError("[IKEA_EEG] Spoken word generation only runs in the Windows Editor.");
                return false;
            }

            var allOk = true;
            var generated = 0;
            var skipped = 0;
            var report = new StringBuilder();

            for (var setIndex = 0; setIndex < wordList.setCount; setIndex++)
            {
                var set = wordList.GetSet(setIndex);

                if (set == null || set.words.Count == 0)
                    continue;

                if (set.HasAllClips())
                {
                    skipped++;
                    report.AppendLine($"    {set.EffectiveId(),-24} {set.language,-8} " +
                                      "already has every clip — untouched");
                    continue;
                }

                if (GenerateForSet(wordList, setIndex))
                {
                    generated++;
                    report.AppendLine($"    {set.EffectiveId(),-24} {set.language,-8} generated");
                }
                else
                {
                    allOk = false;
                    report.AppendLine($"    {set.EffectiveId(),-24} {set.language,-8} FAILED");
                }
            }

            EditorUtility.SetDirty(wordList);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[IKEA_EEG] ===== SPOKEN WORD CLIPS (missing only) =====\n{report}" +
                      $"  generated: {generated}, left untouched: {skipped}");

            return allOk;
        }

        public static bool GenerateForAllSets(WordListDefinition wordList)
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                Debug.LogError("[IKEA_EEG] Spoken word generation uses the local Windows speech " +
                               "engine and only runs in the Windows Editor. On another platform, " +
                               "supply the WAV files manually and assign them to the word set.");
                return false;
            }

            var allOk = true;

            for (var setIndex = 0; setIndex < wordList.setCount; setIndex++)
            {
                var set = wordList.GetSet(setIndex);
                if (set == null || set.words.Count == 0)
                    continue;

                if (!GenerateForSet(wordList, setIndex))
                    allOk = false;
            }

            EditorUtility.SetDirty(wordList);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            return allOk;
        }

        public static bool GenerateForSet(WordListDefinition wordList, int setIndex)
        {
            var set = wordList.GetSet(setIndex);
            if (set == null)
                return false;

            var folderName = SanitiseFolderName(set.setName);
            var relativeDir = $"{k_OutputRoot}/{folderName}";
            var absoluteDir = ToAbsolute(relativeDir);

            Directory.CreateDirectory(absoluteDir);

            var scriptAbsolute = ToAbsolute(k_ScriptPath);
            if (!File.Exists(scriptAbsolute))
            {
                Debug.LogError($"[IKEA_EEG] Missing generation script at {k_ScriptPath}.");
                return false;
            }

            // The set's OWN language decides the voice. A word set is language-specific data,
            // so the culture is never taken from the machine's default or from the previous
            // set — an English engine reading "まぐろ" would produce a stimulus nobody could
            // recognise, and the generator refuses rather than substituting.
            var culture = CulturePrefixFor(set.language);

            // Written as UTF-8 rather than passed on the command line: the console codepage
            // mangles accented Spanish and Japanese on the way through.
            var wordsFile = Path.Combine(Path.GetTempPath(),
                $"ikea_eeg_words_{set.EffectiveId()}_{System.Guid.NewGuid():N}.txt");

            File.WriteAllLines(wordsFile, set.words, new UTF8Encoding(false));

            Debug.Log($"[IKEA_EEG] Synthesising {set.words.Count} spoken word(s) for " +
                      $"'{set.setName}' [{set.language} / {culture}-*] into {relativeDir} " +
                      "using a LOCAL Windows voice of that culture (offline — no cloud " +
                      "service is contacted, and no other language's voice is substituted).");

            bool ran;
            string output;
            string error;

            try
            {
                ran = RunPowerShell(scriptAbsolute, absoluteDir, wordsFile, culture,
                    out output, out error);
            }
            finally
            {
                try
                {
                    if (File.Exists(wordsFile))
                        File.Delete(wordsFile);
                }
                catch
                {
                    // A leftover temp file is not worth failing the generation over.
                }
            }

            if (!ran)
            {
                Debug.LogError($"[IKEA_EEG] Speech generation failed for '{set.setName}' " +
                               $"({set.language}).\n{output}\n{error}");
                return false;
            }

            Debug.Log($"[IKEA_EEG] Local TTS output:\n{output.Trim()}");

            AssetDatabase.Refresh();

            // --- Trim, then assign ---------------------------------------------------------
            set.wordClips = new List<AudioClip>();
            var report = new StringBuilder();
            var ok = true;

            for (var i = 0; i < set.words.Count; i++)
            {
                var safe = Sanitise(set.words[i]);
                var relativePath = $"{relativeDir}/{i + 1:D2}_{safe}.wav";
                var absolutePath = ToAbsolute(relativePath);

                if (!File.Exists(absolutePath))
                {
                    Debug.LogError($"[IKEA_EEG] Expected clip not produced: {relativePath}");
                    set.wordClips.Add(null);
                    ok = false;
                    continue;
                }

                var trimReport = TrimInPlace(absolutePath);
                report.AppendLine($"    [{i + 1}] {set.words[i],-10} {trimReport}");

                AssetDatabase.ImportAsset(relativePath, ImportAssetOptions.ForceUpdate);
                ConfigureImporter(relativePath);

                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(relativePath);
                set.wordClips.Add(clip);

                if (clip == null)
                {
                    Debug.LogError($"[IKEA_EEG] Could not load AudioClip at {relativePath}");
                    ok = false;
                }
            }

            Debug.Log($"[IKEA_EEG] '{set.setName}' spoken clips ready:\n{report}");

            EditorUtility.SetDirty(wordList);
            return ok;
        }

        // ---------------------------------------------------------------------------------
        // Trimming
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Removes the variable leading silence the speech engine emits.
        ///
        /// This is not cosmetic. Measured on the prototype set, the raw clips carried between
        /// 118 ms and 235 ms of silence before the voice — so WORD_PRESENTED, stamped at clip
        /// onset, would sit a DIFFERENT distance ahead of the actual word for every item.
        /// That ~117 ms of jitter is larger than the auditory ERP components this experiment
        /// will eventually measure, so it is removed at the source.
        /// </summary>
        static string TrimInPlace(string absolutePath)
        {
            if (!WavUtility.Load(absolutePath, out var samples, out var channels, out var sampleRate))
                return "(could not read — left untrimmed)";

            var before = WavUtility.MeasureSilence(samples);
            if (!before.hasAudio)
                return "(silent clip — left untrimmed)";

            var leadMs = before.LeadMilliseconds(sampleRate, channels);
            var originalMs = samples.Length / (float)(sampleRate * Mathf.Max(1, channels)) * 1000f;

            var trimmed = WavUtility.TrimSilence(samples, sampleRate, channels);
            if (!WavUtility.SaveSamples(absolutePath, trimmed, channels, sampleRate))
                return "(trim write failed)";

            var newMs = trimmed.Length / (float)(sampleRate * Mathf.Max(1, channels)) * 1000f;
            var after = WavUtility.MeasureSilence(trimmed);
            var newLeadMs = after.LeadMilliseconds(sampleRate, channels);

            return $"{originalMs,6:F0} ms -> {newMs,6:F0} ms   lead {leadMs,5:F0} ms -> {newLeadMs,4:F0} ms";
        }

        static void ConfigureImporter(string relativePath)
        {
            var importer = AssetImporter.GetAtPath(relativePath) as AudioImporter;
            if (importer == null)
                return;

            var settings = importer.defaultSampleSettings;

            // Decompress on load + no compression: the clip must be ready the instant it is
            // scheduled. A streamed or compressed clip can start late, which would silently
            // add latency between the marker and the spoken word.
            settings.loadType = AudioClipLoadType.DecompressOnLoad;
            settings.compressionFormat = AudioCompressionFormat.PCM;
            settings.preloadAudioData = true;

            importer.defaultSampleSettings = settings;
            importer.forceToMono = true;
            importer.loadInBackground = false;

            importer.SaveAndReimport();
        }

        // ---------------------------------------------------------------------------------
        // Process plumbing
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// The speech-engine culture a word set's language requires.
        ///
        /// Returned as a PREFIX ("es"), not a full culture ("es-ES"), so any regional voice of
        /// the right language qualifies — es-MX is a perfectly good Spanish voice — while a
        /// voice of the WRONG language never does.
        /// </summary>
        static string CulturePrefixFor(ExperimentLanguage language)
        {
            switch (language)
            {
                case ExperimentLanguage.Spanish: return "es";
                case ExperimentLanguage.Japanese: return "ja";
                default: return "en";
            }
        }

        static bool RunPowerShell(string scriptPath, string outputDir, string wordsFile,
            string culturePrefix, out string output, out string error)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass " +
                            $"-File \"{scriptPath}\" -OutputDir \"{outputDir}\" " +
                            $"-WordsFile \"{wordsFile}\" -CulturePrefix \"{culturePrefix}\"",
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using (var process = Process.Start(psi))
            {
                if (process == null)
                {
                    output = string.Empty;
                    error = "could not start powershell.exe";
                    return false;
                }

                output = process.StandardOutput.ReadToEnd();
                error = process.StandardError.ReadToEnd();
                process.WaitForExit(120000);

                return process.ExitCode == 0;
            }
        }

        static string ToAbsolute(string assetRelativePath)
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
            return Path.Combine(projectRoot, assetRelativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        static string Sanitise(string value)
        {
            var sb = new StringBuilder();
            foreach (var c in value)
            {
                if (char.IsLetterOrDigit(c))
                    sb.Append(c);
            }

            return sb.ToString();
        }

        static string SanitiseFolderName(string value)
        {
            var sb = new StringBuilder();
            foreach (var c in value)
            {
                if (char.IsLetterOrDigit(c))
                    sb.Append(c);
                else if (c == ' ' || c == '_' || c == '-')
                    sb.Append('_');
            }

            var result = sb.ToString().Trim('_');
            while (result.Contains("__"))
                result = result.Replace("__", "_");

            return string.IsNullOrEmpty(result) ? "Set" : result;
        }
    }
}
