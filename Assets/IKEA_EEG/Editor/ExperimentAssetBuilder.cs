using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using IkeaEeg.Experiment;
using IkeaEeg.Interaction;
using IkeaEeg.Localization;

namespace IkeaEeg.EditorTools
{
    /// <summary>
    /// Creates the project's materials and configuration assets.
    ///
    /// Everything here is idempotent: running it twice updates the existing assets instead of
    /// creating duplicates, so the scene builder can be re-run safely while iterating.
    /// </summary>
    public static class ExperimentAssetBuilder
    {
        public const string RootFolder = "Assets/IKEA_EEG";
        public const string MaterialsFolder = RootFolder + "/Materials";
        public const string ScenesFolder = RootFolder + "/Scenes";
        public const string DataFolder = RootFolder + "/Data";
        public const string PrefabsFolder = RootFolder + "/Prefabs";

        public const string WordListPath = DataFolder + "/WordList_Default.asset";
        public const string ConfigPath = DataFolder + "/ExperimentConfig_Default.asset";

        /// <summary>Material keys used by the scene builder.</summary>
        public class MaterialSet
        {
            public Material floor;
            public Material wall;
            public Material ceiling;
            public Material facade;
            public Material doorFrame;
            public Material accent;
            public Material uiPanel;
            public Material chairHover;
            public Material chairCorrect;
            public Material chairIncorrect;

            /// <summary>Area 0: persistent look of a correctly-selected practice object.</summary>
            public Material practiceSuccess;

            /// <summary>Area 0: brief, neutral look of a wrongly-selected practice object.</summary>
            public Material practiceNeutral;

            /// <summary>Area 0 controller-help markers, colour-matched to their callout text.</summary>
            public Material markerIndexTrigger;
            public Material markerGripTrigger;
            public Material markerThumbstick;
            public readonly Dictionary<ChairColor, Material> chairColors =
                new Dictionary<ChairColor, Material>();
        }

        // ---------------------------------------------------------------------------------
        // Folders
        // ---------------------------------------------------------------------------------

        public static void EnsureFolders()
        {
            EnsureFolder("Assets", "IKEA_EEG");
            foreach (var sub in new[]
                     {
                         "Scenes", "Scripts", "Prefabs", "Materials", "Audio", "UI", "Data", "Editor",
                     })
            {
                EnsureFolder(RootFolder, sub);
            }
        }

        static void EnsureFolder(string parent, string child)
        {
            var path = $"{parent}/{child}";
            if (!AssetDatabase.IsValidFolder(path))
                AssetDatabase.CreateFolder(parent, child);
        }

        // ---------------------------------------------------------------------------------
        // Materials
        // ---------------------------------------------------------------------------------

        public static MaterialSet BuildMaterials()
        {
            var set = new MaterialSet
            {
                // Neutral grey prototype palette — visual fidelity is explicitly not a goal.
                floor = CreateOrUpdate("M_Floor", new Color(0.42f, 0.42f, 0.44f), 0f, 0.85f),
                wall = CreateOrUpdate("M_Wall", new Color(0.62f, 0.62f, 0.63f), 0f, 0.95f),
                ceiling = CreateOrUpdate("M_Ceiling", new Color(0.78f, 0.78f, 0.79f), 0f, 0.95f),
                facade = CreateOrUpdate("M_Facade", new Color(0.30f, 0.31f, 0.34f), 0f, 0.8f),
                doorFrame = CreateOrUpdate("M_DoorFrame", new Color(0.16f, 0.17f, 0.19f), 0f, 0.7f),
                accent = CreateOrUpdate("M_Accent", new Color(0.10f, 0.45f, 0.75f), 0f, 0.6f),
                uiPanel = CreateOrUpdate("M_UIPanel", new Color(0.08f, 0.08f, 0.10f), 0f, 1f),

                // Feedback materials: deliberately far from every chair colour so the
                // post-selection state is unambiguous on camera and in the headset.
                chairHover = CreateOrUpdate("M_Chair_Hover", new Color(0.95f, 0.95f, 0.55f), 0f, 0.5f),
                chairCorrect = CreateOrUpdate("M_Chair_SelectedCorrect", new Color(0.10f, 0.95f, 0.45f), 0f, 0.4f),
                chairIncorrect = CreateOrUpdate("M_Chair_SelectedIncorrect", new Color(0.95f, 0.35f, 0.10f), 0f, 0.4f),

                // Area 0 practice feedback. Success is bright and emissive-looking so it is
                // unmistakable and clearly different from every practice colour; the neutral
                // one is a dim grey that says "registered, not the one" without saying "wrong".
                practiceSuccess = CreateOrUpdate("M_Practice_Success", new Color(0.20f, 1f, 0.55f), 0f, 0.25f),
                practiceNeutral = CreateOrUpdate("M_Practice_Neutral", new Color(0.55f, 0.55f, 0.58f), 0f, 0.7f),

                // Controller-help markers. Each matches the bullet colour of its callout line,
                // so the marker on the model and the words beside it are linked by more than
                // position.
                markerIndexTrigger = CreateOrUpdate("M_Marker_IndexTrigger", new Color(0.31f, 0.61f, 1f), 0f, 0.3f),
                markerGripTrigger = CreateOrUpdate("M_Marker_GripTrigger", new Color(0.20f, 0.89f, 0.54f), 0f, 0.3f),
                markerThumbstick = CreateOrUpdate("M_Marker_Thumbstick", new Color(1f, 0.85f, 0.31f), 0f, 0.3f),
            };

            foreach (ChairColor color in System.Enum.GetValues(typeof(ChairColor)))
            {
                set.chairColors[color] = CreateOrUpdate(
                    $"M_Chair_{color}", ChairAttributeVisuals.ToUnityColor(color), 0f, 0.65f);
            }

            return set;
        }

        static Material CreateOrUpdate(string assetName, Color color, float metallic, float smoothnessInverse)
        {
            var path = $"{MaterialsFolder}/{assetName}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);

            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                // Should not happen (URP 17 is in the manifest) but never leave the caller
                // with a null material — a magenta object is easier to debug than a crash.
                shader = Shader.Find("Standard") ?? Shader.Find("Unlit/Color");
                Debug.LogWarning("[IKEA_EEG] URP Lit shader not found; falling back to " +
                                 $"'{shader?.name}'. Materials may look wrong.");
            }

            if (material == null)
            {
                material = new Material(shader) { name = assetName };
                AssetDatabase.CreateAsset(material, path);
            }
            else if (material.shader != shader)
            {
                material.shader = shader;
            }

            SetColor(material, color);

            if (material.HasProperty("_Metallic"))
                material.SetFloat("_Metallic", metallic);

            // URP uses _Smoothness; we pass a "roughness-like" value so callers can think in
            // matte-to-glossy terms.
            if (material.HasProperty("_Smoothness"))
                material.SetFloat("_Smoothness", Mathf.Clamp01(1f - smoothnessInverse));

            EditorUtility.SetDirty(material);
            return material;
        }

        static void SetColor(Material material, Color color)
        {
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", color);

            if (material.HasProperty("_Color"))
                material.SetColor("_Color", color);
        }

        // ---------------------------------------------------------------------------------
        // Configuration assets
        // ---------------------------------------------------------------------------------

        public const string RecognitionWordListPath =
            DataFolder + "/RecognitionWordList_PLACEHOLDER.asset";

        /// <summary>
        /// Creates the recognition stimulus asset if it does not exist, populated with clearly
        /// marked development placeholders.
        ///
        /// THE PLACEHOLDERS ARE NOT CNS STIMULI AND MUST NEVER BE PRESENTED AS SUCH. The
        /// official CNS Vital Signs word list has not been established from authorised
        /// methodological documentation, so nothing here attempts to reproduce, approximate or
        /// guess it. These are ordinary high-frequency English nouns chosen only so the
        /// architecture can be exercised end to end before the real list arrives — they carry no
        /// norms, no frequency matching, no imageability control and no clinical meaning.
        ///
        /// The asset's own filename, its provenance field and its validatedForResearch flag all
        /// say so, so a file produced from them is self-describing without this project to hand.
        ///
        /// NEVER OVERWRITES an existing asset: as with the word list above, a list the
        /// researcher has edited is never stomped.
        /// </summary>
        public static RecognitionWordList BuildRecognitionWordList()
        {
            var asset = AssetDatabase.LoadAssetAtPath<RecognitionWordList>(RecognitionWordListPath);

            if (asset != null)
                return asset;

            asset = ScriptableObject.CreateInstance<RecognitionWordList>();
            AssetDatabase.CreateAsset(asset, RecognitionWordListPath);

            // 15 targets + 15 immediate lures + 15 delayed lures. The COUNTS follow the
            // CNS-derived structure described in the brief; the WORDS are placeholders.
            asset.SetLists(
                new List<string>
                {
                    "TABLE", "RIVER", "CANDLE", "MARKET", "PILLOW",
                    "GARDEN", "BOTTLE", "WINDOW", "FOREST", "BASKET",
                    "MIRROR", "TUNNEL", "SADDLE", "HARBOR", "LANTERN",
                },
                new List<string>
                {
                    "COPPER", "MEADOW", "KETTLE", "STATION", "BLANKET",
                    "ORCHARD", "BUCKET", "CORNER", "VALLEY", "RIBBON",
                    "PICTURE", "BRIDGE", "COLLAR", "ISLAND", "CANDLE_B",
                },
                new List<string>
                {
                    "PEBBLE", "CHIMNEY", "WAGON", "PASTURE", "CUSHION",
                    "THICKET", "JUG", "LEDGE", "CANYON", "LACE",
                    "PORTRAIT", "TOWER", "CUFF", "PENINSULA", "TORCH",
                },
                validated: false,
                provenanceNote:
                    "DEVELOPMENT ONLY — NOT CNS STIMULI.\n" +
                    "Generated placeholders for functional testing of the recognition " +
                    "architecture. These are NOT the CNS Vital Signs word list and were not " +
                    "derived from it. They are uncontrolled for frequency, imageability, " +
                    "length and semantic category, carry no norms, and must not be used for " +
                    "data collection or reported as a memory measure. Replace with the " +
                    "authorised list and set validatedForResearch before any participant runs.");

            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();

            Debug.LogWarning($"[IKEA_EEG] Created {RecognitionWordListPath} with DEVELOPMENT " +
                             "PLACEHOLDER words. These are NOT CNS stimuli. Replace them before " +
                             "any data collection.");

            return asset;
        }

        public static WordListDefinition BuildWordList()
        {
            var asset = AssetDatabase.LoadAssetAtPath<WordListDefinition>(WordListPath);
            var created = false;

            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<WordListDefinition>();
                AssetDatabase.CreateAsset(asset, WordListPath);
                created = true;
            }

            // Only populate on first creation — never stomp a list the researcher has edited.
            if (created)
            {
                asset.SetSets(new List<WordSet>
                {
                    new WordSet
                    {
                        wordSetId = "SET_A",
                        setName = "Set A (prototype)",
                        // Five concrete, high-frequency, mutually unrelated nouns with no
                        // furniture/retail association, so Area B cannot prime or interfere
                        // with recall.
                        words = new List<string> { "River", "Copper", "Lantern", "Falcon", "Sugar" },
                    },
                    new WordSet
                    {
                        wordSetId = "SET_B",
                        setName = "Set B (parallel, spare)",
                        words = new List<string> { "Harbour", "Velvet", "Compass", "Thunder", "Pepper" },
                    },
                }, 5);

                EditorUtility.SetDirty(asset);
            }
            else
            {
                // Migration for word lists authored before ids existed: give each set an
                // explicit id derived from its label. Nothing else about the list is touched,
                // and a set that already has an id keeps it.
                var stamped = 0;
                for (var i = 0; i < asset.setCount; i++)
                {
                    var set = asset.GetSet(i);
                    if (set == null || !string.IsNullOrWhiteSpace(set.wordSetId))
                        continue;

                    set.wordSetId = i == 0 ? "SET_A" : i == 1 ? "SET_B" : set.EffectiveId();
                    stamped++;
                }

                if (stamped > 0)
                {
                    EditorUtility.SetDirty(asset);
                    Debug.Log($"[IKEA_EEG] Stamped word_set_id onto {stamped} existing word set(s).");
                }

                // Language tagging for sets authored before languages existed. They are the
                // English prototype sets and are marked as such — English is the only language
                // with a usable word list in this pass.
                var tagged = 0;
                for (var i = 0; i < asset.setCount; i++)
                {
                    var set = asset.GetSet(i);
                    if (set == null || !string.IsNullOrEmpty(set.validationNote))
                        continue;

                    set.language = ExperimentLanguage.English;
                    set.validatedForResearch = false;
                    set.validationNote =
                        "PROTOTYPE (English). Chosen for development, not validated as a " +
                        "verbal-memory instrument. Spanish and Japanese need their own " +
                        "language-specific lists — NOT translations of this one.";
                    tagged++;
                }

                if (tagged > 0)
                {
                    EditorUtility.SetDirty(asset);
                    Debug.Log($"[IKEA_EEG] Tagged {tagged} existing word set(s) as English " +
                              "prototype sets.");
                }
            }

            EnsureLanguageSets(asset);

            return asset;
        }

        /// <summary>
        /// Adds the Spanish and Japanese word sets, drawn from the research-sourced pools.
        ///
        /// IDEMPOTENT AND ADDITIVE. A set whose id already exists is left exactly as it is —
        /// including its clips and any edit a researcher has made — so re-running the builder
        /// never re-cuts a list a session may already have used. The English prototype sets are
        /// not touched at all.
        ///
        /// HOW THE SUBSETS ARE CUT: the runtime presents <see cref="WordListDefinition.wordsPerSet"/>
        /// words (five). Each published form of fifteen is therefore cut into consecutive,
        /// non-overlapping blocks IN PUBLISHED ORDER — items 1-5, 6-10, 11-15 — and each block
        /// becomes one set. Consecutive-in-order rather than random because the rule has to be
        /// stateable in a methods section and reproducible without this project's assets; the
        /// start index and the words themselves are recorded on the set either way.
        ///
        /// A trailing block shorter than the required count is DISCARDED rather than padded
        /// from another form: mixing items across parallel forms would destroy the only
        /// property that makes them parallel.
        /// </summary>
        static void EnsureLanguageSets(WordListDefinition asset)
        {
            var perSet = Mathf.Max(1, asset.wordsPerSet);

            var existing = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < asset.setCount; i++)
            {
                var set = asset.GetSet(i);
                if (set != null)
                    existing.Add(set.EffectiveId());
            }

            var additions = new List<WordSet>();

            foreach (var form in WordSourcePools.AllForms())
            {
                var blocks = form.words.Count / perSet;

                for (var block = 0; block < blocks; block++)
                {
                    var start = block * perSet;
                    var id = $"{form.formId}_{(char)('A' + block)}";

                    if (existing.Contains(id))
                        continue;

                    var words = form.words.GetRange(start, perSet);

                    additions.Add(new WordSet
                    {
                        wordSetId = id,
                        setName = $"{form.formLabel} — items {start + 1}-{start + perSet}",
                        language = form.language,

                        // FALSE, deliberately. The WORDS are research-sourced; this five-item
                        // subset presented under a different procedure is not a validated
                        // instrument, and flagging it as one would be the single most
                        // misleading thing this file could do.
                        validatedForResearch = false,
                        validationNote = WordSourcePools.ValidationNote(form.language),

                        wordSource = form.source,
                        sourceForm = form.formId,
                        sourceStartIndex = start,
                        sourceFormWordCount = form.words.Count,

                        words = words,
                    });
                }
            }

            if (additions.Count == 0)
                return;

            var all = new List<WordSet>();
            for (var i = 0; i < asset.setCount; i++)
                all.Add(asset.GetSet(i));

            all.AddRange(additions);
            asset.SetSets(all, perSet);

            EditorUtility.SetDirty(asset);

            Debug.Log($"[IKEA_EEG] Added {additions.Count} language-specific word set(s) of " +
                      $"{perSet} words each, drawn in published order from the research-sourced " +
                      "pools:\n    " +
                      string.Join("\n    ", additions.ConvertAll(s =>
                          $"{s.EffectiveId(),-24} {s.language,-8} {string.Join(", ", s.words)}")) +
                      "\n  These are RESEARCH-SOURCED STIMULI presented under an ADAPTED " +
                      "procedure. None is a standardized RAVLT administration, and no subset " +
                      "is claimed to be psychometrically equivalent to the form it came from.");
        }

        public static ExperimentConfig BuildConfig(WordListDefinition wordList)
        {
            var asset = AssetDatabase.LoadAssetAtPath<ExperimentConfig>(ConfigPath);
            var created = false;

            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<ExperimentConfig>();
                AssetDatabase.CreateAsset(asset, ConfigPath);
                created = true;
            }

            // The word list reference is always (re)linked; the tunables are left alone once
            // the asset exists so re-running the builder never silently resets a protocol.
            asset.wordList = wordList;

            // The recognition list is linked ONLY when the field is empty. Filling an empty new
            // field is not the same as overwriting a choice: if the researcher has pointed this
            // at their own asset, that reference is left exactly as they set it.
            if (asset.recognitionWordList == null)
            {
                asset.recognitionWordList = BuildRecognitionWordList();

                Debug.LogWarning("[IKEA_EEG] ExperimentConfig had no recognitionWordList; linked " +
                                 $"the PLACEHOLDER asset at {RecognitionWordListPath}. Those " +
                                 "words are NOT CNS stimuli — replace before data collection.");
            }

            if (created)
            {
                asset.wordSetIndex = 0;
                asset.targetChair = new ChairSpec(ChairColor.Blue, ChairSize.Large, ChairShape.Solid);
            }

            // The difficulty profiles are protocol definitions, not free-form tunables: an
            // empty or malformed set would silently stop every chair trial from generating.
            // They are repaired (never overwritten when already valid) on every build.
            if (asset.difficultyProfiles == null || asset.difficultyProfiles.Count == 0)
            {
                asset.difficultyProfiles = DifficultyProfile.CreateDefaults();
                Debug.Log("[IKEA_EEG] Installed the default LOW/MEDIUM/HIGH difficulty profiles " +
                          "(prototype defaults — see ExperimentConfig).");
            }

            if (asset.difficultySequence == null || asset.difficultySequence.Count == 0)
            {
                asset.difficultySequence = new List<DifficultyLevel>
                {
                    DifficultyLevel.Low,
                    DifficultyLevel.Medium,
                    DifficultyLevel.High,
                };
            }

            if (asset.chairTrialsPerRun < 1)
                asset.chairTrialsPerRun = 3;

            // ---- Migration of superseded participant-facing wording -------------------------
            // Both of these used to read "Good job. Proceed to the next stage." That line is
            // wrong in both places now: it is shown after EVERY trial (not just the last), and
            // the end-of-block panel now carries the accuracy result instead. Replaced ONLY
            // when the value is still the old default, so custom wording is never overwritten.
            const string supersededWording = "Good job. Proceed to the next stage.";

            if (asset.chairSelectedFeedbackText == supersededWording)
            {
                asset.chairSelectedFeedbackText = "Selection recorded.";
                Debug.Log("[IKEA_EEG] Updated the per-trial chair feedback text: it is shown " +
                          "after every trial, so it no longer says 'proceed to the next stage'.");
            }

            if (asset.chairBlockCompleteText == supersededWording)
            {
                asset.chairBlockCompleteText = "Press EXIT SHOWROOM to continue.";
                Debug.Log("[IKEA_EEG] Updated the end-of-block footer text: the block result " +
                          "now shows the participant's accuracy above it.");
            }

            EditorUtility.SetDirty(asset);
            return asset;
        }

        /// <summary>Writes the data-location README next to the generated assets.</summary>
        public static void WriteDataReadme()
        {
            var path = Path.Combine(Application.dataPath, "IKEA_EEG/Data/README_DATA_LOCATION.txt");
            var persistent = Application.persistentDataPath.Replace('/', Path.DirectorySeparatorChar);

            var text =
                "IKEA_EEG — where the experiment data is written\n" +
                "================================================\n\n" +
                "CSV (one per session):\n" +
                $"  {persistent}\\IKEA_EEG_Data\\<session_id>\\events_<session_id>.csv\n\n" +
                "Recall audio (one WAV per recall phase per trial):\n" +
                $"  {persistent}\\IKEA_EEG_Data\\<session_id>\\audio\\<trial_id>_immediate_recall.wav\n" +
                $"  {persistent}\\IKEA_EEG_Data\\<session_id>\\audio\\<trial_id>_delayed_recall.wav\n\n" +
                "The absolute path is also printed to the Unity Console at SESSION_START.\n" +
                "Application.persistentDataPath for this project resolves to:\n" +
                $"  {persistent}\n\n" +
                "Derived files written next to the event CSV at the end of a session:\n" +
                $"  session_summary_<session_id>.csv        one row per chair trial\n" +
                $"  researcher_summary_<session_id>.txt     human-readable debrief\n" +
                $"  manual_recall_scoring_<session_id>.csv  blank form for offline recall scoring\n" +
                "These are DERIVED. The event CSV and the WAV files are the authoritative record\n" +
                "and are never modified by them.\n\n" +
                "CSV columns (in order):\n" +
                "  timestamp_absolute, timestamp_relative, session_id, trial_id, experiment_state,\n" +
                "  room, event_type, object_id, target_color, target_size, target_shape,\n" +
                "  selected_color, selected_size, selected_shape, correct, response_time_ms,\n" +
                "  word_index, expected_word, recall_phase, transcript, elapsed_trial_time, notes,\n" +
                "  chair_trial_index, chair_trial_count, difficulty, randomization_seed,\n" +
                "  word_set_id, clip_name, scheduled_audio_time, confirmed_audio_time\n\n" +
                "The last eight columns were APPENDED, so the first 22 keep their original\n" +
                "names and positions and older analysis scripts still work.\n\n" +
                "CHAIR SHAPE LABELS CHANGED — READ BEFORE POOLING SESSIONS\n" +
                "------------------------------------------------------------\n" +
                "The target_shape / selected_shape values were renamed to remove two ambiguous,\n" +
                "aesthetic categories. The GEOMETRY did not change; only the words did:\n\n" +
                "    old value    new value    what the chair actually looks like\n" +
                "    Modern    -> Solid        one continuous flat back panel\n" +
                "    Classic   -> Slatted      separate horizontal back bars, with gaps\n" +
                "    Rounded   -> Curved       cylindrical parts, curved back\n\n" +
                "Sessions recorded BEFORE this change contain the old words and sessions after\n" +
                "it contain the new ones. A file's shape vocabulary therefore identifies which\n" +
                "build produced it. Pooling old and new sessions requires mapping one set onto\n" +
                "the other using the table above — do NOT assume a reader will do this, and do\n" +
                "not rewrite historical files in place.\n\n" +
                "timestamp_relative is seconds since SESSION_START from a monotonic Stopwatch;\n" +
                "elapsed_trial_time is seconds since the current TRIAL_START.\n" +
                "randomization_seed appears on EVERY row: it is all that is needed to regenerate\n" +
                "the session's chair trials.\n" +
                "Empty cells mean 'this column does not apply to this event type'.\n";

            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? string.Empty);
            File.WriteAllText(path, text);
        }
    }
}
