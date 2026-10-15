using System.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using IkeaEeg.Localization;

namespace IkeaEeg.EditorTools
{
    /// <summary>
    /// Creates the project-owned Japanese TMP font asset and registers it as a TMP global
    /// fallback.
    ///
    /// WHY A GLOBAL FALLBACK. The Latin primary (LiberationSans SDF) has no kana or kanji. A
    /// fallback serialized into TMP Settings is loaded with TMP itself, so the very first frame
    /// of the language screen can already draw 日本語 — no script has to run first. Every label
    /// keeps its own font for Latin text and only falls through for glyphs it lacks, so no UI
    /// font is replaced.
    ///
    /// SOURCE. Assets/IKEA_EEG/Fonts/NotoSansJP/NotoSansJP-Regular.otf, the static Regular
    /// cut of Noto Sans JP 2.004 from notofonts/noto-cjk (SIL OFL 1.1, OFL.txt alongside).
    /// Committed, not gitignored: the OFL allows bundling and redistribution with software.
    ///
    /// DYNAMIC ATLAS. Glyphs are rasterised from the bundled font file on demand, so the
    /// committed asset carries no pre-baked atlas. TMP adds glyphs to the asset while the
    /// Editor renders Japanese; <see cref="ClearDynamicData"/> empties it again before a commit
    /// (builds clear it anyway: clearDynamicDataOnBuild follows TMP Settings, which is on).
    ///
    /// IDEMPOTENT. Re-running reuses the existing asset and never adds a second fallback entry.
    /// It touches only the font asset and the m_fallbackFontAssets list of TMP Settings.
    /// </summary>
    public static class JapaneseFontAssetSetup
    {
        public const string FontFolder = "Assets/IKEA_EEG/Fonts/NotoSansJP";
        public const string SourceFontPath = FontFolder + "/NotoSansJP-Regular.otf";
        public const string FontAssetPath = FontFolder + "/Resources/" +
                                            ExperimentLocalization.ProjectJapaneseFontResource +
                                            ".asset";

        // 204 distinct CJK characters are used by the localization table today. A 1024² page
        // at 48 pt / 5 px padding holds roughly 290 of them; multi-atlas adds a page if a
        // future localization change needs more, rather than failing.
        const int k_SamplingPointSize = 48;
        const int k_AtlasPadding = 5;
        const int k_AtlasSize = 1024;

        [MenuItem("IKEA_EEG/Fonts/Set Up Japanese Font Fallback", false, 60)]
        public static void SetUpMenu() => SetUp();

        [MenuItem("IKEA_EEG/Fonts/Clear Japanese Font Dynamic Data", false, 61)]
        public static void ClearDynamicDataMenu() => ClearDynamicData();

        /// <summary>Batch-mode entry point.</summary>
        public static void SetUpFromCommandLine()
        {
            var ok = SetUp();
            EditorApplication.Exit(ok ? 0 : 1);
        }

        public static bool SetUp()
        {
            var fontAsset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath);

            if (fontAsset == null)
            {
                fontAsset = CreateFontAsset();
                if (fontAsset == null)
                    return false;
            }
            else
            {
                Debug.Log($"[IKEA_EEG] Reusing existing Japanese font asset at {FontAssetPath}.");
            }

            var settings = TMP_Settings.instance;
            if (settings == null)
            {
                Debug.LogError("[IKEA_EEG] TMP Settings not found; import TMP Essential Resources.");
                return false;
            }

            var fallbacks = TMP_Settings.fallbackFontAssets;
            if (fallbacks == null)
            {
                fallbacks = new System.Collections.Generic.List<TMP_FontAsset>();
                TMP_Settings.fallbackFontAssets = fallbacks;
            }

            // Drop nulls and any second copy, then make sure exactly one entry remains.
            var before = fallbacks.Count;
            fallbacks.RemoveAll(f => f == null || f == fontAsset);
            fallbacks.Insert(0, fontAsset);

            if (fallbacks.Count != before)
            {
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssetIfDirty(settings);
            }

            Debug.Log($"[IKEA_EEG] TMP global fallback list: " +
                      string.Join(", ", fallbacks.Select(f => f.name)));
            return true;
        }

        static TMP_FontAsset CreateFontAsset()
        {
            var font = AssetDatabase.LoadAssetAtPath<Font>(SourceFontPath);
            if (font == null)
            {
                Debug.LogError($"[IKEA_EEG] Japanese source font missing at {SourceFontPath}.");
                return null;
            }

            var fontAsset = TMP_FontAsset.CreateFontAsset(font, k_SamplingPointSize,
                k_AtlasPadding, GlyphRenderMode.SDFAA, k_AtlasSize, k_AtlasSize,
                AtlasPopulationMode.Dynamic, enableMultiAtlasSupport: true);

            if (fontAsset == null)
            {
                Debug.LogError($"[IKEA_EEG] TMP could not load {SourceFontPath}.");
                return null;
            }

            var folder = FontFolder + "/Resources";
            if (!AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder(FontFolder, "Resources");

            fontAsset.name = ExperimentLocalization.ProjectJapaneseFontResource;
            AssetDatabase.CreateAsset(fontAsset, FontAssetPath);

            // The atlas and material are sub-assets, as TMP's own Create > Font Asset makes them.
            var atlas = fontAsset.atlasTextures[0];
            atlas.name = fontAsset.name + " Atlas";
            AssetDatabase.AddObjectToAsset(atlas, fontAsset);

            var material = fontAsset.material;
            material.name = fontAsset.name + " Material";
            AssetDatabase.AddObjectToAsset(material, fontAsset);

            EditorUtility.SetDirty(fontAsset);
            AssetDatabase.SaveAssetIfDirty(fontAsset);

            Debug.Log($"[IKEA_EEG] Created dynamic Japanese font asset {FontAssetPath} " +
                      $"({k_AtlasSize}² atlas, {k_SamplingPointSize} pt, multi-atlas on).");
            return fontAsset;
        }

        /// <summary>Empties the dynamic atlas so the committed asset stays small.</summary>
        public static void ClearDynamicData()
        {
            var fontAsset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath);
            if (fontAsset == null)
                return;

            fontAsset.ClearFontAssetData(setAtlasSizeToZero: true);
            EditorUtility.SetDirty(fontAsset);
            AssetDatabase.SaveAssetIfDirty(fontAsset);
            Debug.Log($"[IKEA_EEG] Cleared dynamic glyph data of {FontAssetPath}.");
        }
    }
}
