using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using IkeaEeg.Interaction;

namespace IkeaEeg.Localization
{
    /// <summary>
    /// The single source of participant-facing text.
    ///
    /// Everything that shows words to a participant asks this service for a KEY. Nothing
    /// branches on the language, and no participant-facing literal exists anywhere else — so
    /// the whole UI switches language in one assignment, and a missing translation is a
    /// detectable condition rather than an English string leaking into a Spanish session.
    ///
    /// The language belongs to the EXPERIMENT SESSION: chosen once before Area 0, preserved
    /// across NEW TRIAL, and cleared only by RESTART (a new participant).
    /// </summary>
    public static class ExperimentLocalization
    {
        static ExperimentLanguage s_Language = ExperimentLanguage.None;
        static bool s_FontFallbackReady;
        static string s_FontFallbackDetail = "not attempted";

        /// <summary>The language in force. None until the participant chooses.</summary>
        public static ExperimentLanguage language => s_Language;

        /// <summary>True once a language has been chosen for this experiment session.</summary>
        public static bool hasLanguage => s_Language != ExperimentLanguage.None;

        /// <summary>The code written to the data: EN / ES / JA, or empty.</summary>
        public static string languageCode => ExperimentLanguages.ToCode(s_Language);

        /// <summary>Raised after the language changes, so live UI can re-read its strings.</summary>
        public static event Action languageChanged;

        /// <summary>How the Japanese glyph fallback resolved. Reported by the self test.</summary>
        public static string fontFallbackDetail => s_FontFallbackDetail;

        public static void SetLanguage(ExperimentLanguage newLanguage)
        {
            if (s_Language == newLanguage)
                return;

            s_Language = newLanguage;

            // Japanese needs glyphs the default Latin font does not contain. Set up the
            // fallback BEFORE anything renders, so no participant ever sees tofu boxes.
            if (newLanguage == ExperimentLanguage.Japanese)
                EnsureJapaneseFontFallback();

            languageChanged?.Invoke();
        }

        /// <summary>Clears the language. Used by RESTART, which means a new participant.</summary>
        public static void ClearLanguage()
        {
            if (s_Language == ExperimentLanguage.None)
                return;

            s_Language = ExperimentLanguage.None;
            languageChanged?.Invoke();
        }

        // ---------------------------------------------------------------------------------
        // Lookup
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// The string for a key in the language in force.
        ///
        /// A missing key returns a VISIBLE marker rather than an empty string or an English
        /// fallback: silence would hide the gap, and English inside a Spanish session is
        /// exactly the failure this service exists to prevent.
        /// </summary>
        public static string Get(string key)
        {
            if (string.IsNullOrEmpty(key))
                return string.Empty;

            if (!LocalizationTable.TryGet(key, out var entry))
            {
                Debug.LogError($"[IKEA_EEG] Missing localization key '{key}'.");
                return $"#{key}#";
            }

            var value = entry.For(s_Language == ExperimentLanguage.None
                ? ExperimentLanguage.English
                : s_Language);

            if (string.IsNullOrEmpty(value))
            {
                Debug.LogError($"[IKEA_EEG] Localization key '{key}' has no " +
                               $"{s_Language} text.");
                return $"#{key}#";
            }

            return value;
        }

        /// <summary>Get with {PLACEHOLDER} substitution, e.g. Format(key, "N", "2").</summary>
        public static string Format(string key, params string[] pairs)
        {
            var text = Get(key);

            for (var i = 0; i + 1 < pairs.Length; i += 2)
                text = text.Replace("{" + pairs[i] + "}", pairs[i + 1]);

            return text;
        }

        // ---------------------------------------------------------------------------------
        // Chair attributes
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// The participant-facing name of a chair attribute.
        ///
        /// The INTERNAL enum value is what gets logged and what the generator reasons about;
        /// this only decides what the participant reads. Renaming a displayed term therefore
        /// never touches the data or the reproducibility of a seed.
        /// </summary>
        public static string ColorName(ChairColor color) =>
            Get(LocKeys.ColorPrefix + color);

        /// <summary>
        /// The participant-facing name of an AREA 0 PRACTICE colour.
        ///
        /// Separate from <see cref="ColorName"/> because the two rooms need different wordings
        /// for the same internal enum: a chair is feminine in Spanish (ROJA) and takes the long
        /// Japanese colour form (黄色), while a practice object is neither. Both derive from the
        /// SAME ChairColor value, so the two can never drift apart in what they mean — only in
        /// how they read.
        /// </summary>
        public static string PracticeColorName(ChairColor color) =>
            Get(LocKeys.PracticeColorPrefix + color);

        public static string SizeName(ChairSize size) => Get(LocKeys.SizePrefix + size);

        public static string ShapeName(ChairShape shape) => Get(LocKeys.ShapePrefix + shape);

        /// <summary>The three target attributes, one per line, in the language in force.</summary>
        public static string TargetLines(ChairSpec spec)
        {
            return $"{ColorName(spec.color)}\n{SizeName(spec.size)}\n{ShapeName(spec.shape)}";
        }

        // ---------------------------------------------------------------------------------
        // Japanese glyphs
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Japanese-capable fonts to try, in order of preference. All ship with Windows.
        ///
        /// Noto Sans JP first: it is SIL Open Font License, so a project-local font asset made
        /// from it carries no redistribution problem. The others are fallbacks used only if it
        /// is absent.
        /// </summary>
        static readonly string[] k_JapaneseFontFamilies =
        {
            "Noto Sans JP",
            "Yu Gothic UI",
            "Yu Gothic",
            "Meiryo",
            "MS Gothic",
        };

        /// <summary>
        /// Adds a Japanese font to TMP's global fallback list, so every existing label can
        /// render kana and kanji without any of them being re-authored.
        ///
        /// The font asset is created from an INSTALLED SYSTEM FONT at run time
        /// (TMP_FontAsset.CreateFontAsset(family, style)) in dynamic mode: no font file is
        /// copied into the project, nothing is downloaded, and no font is redistributed. The
        /// atlas is rasterised on demand for exactly the glyphs the localized strings use.
        ///
        /// NOTE FOR A STANDALONE QUEST BUILD: this relies on OS-installed fonts, which exist on
        /// the PC (the experiment runs over Quest Link). An on-device build would need a font
        /// asset committed to the project instead — recorded here so the limitation is not
        /// discovered in a headset.
        /// </summary>
        public static bool EnsureJapaneseFontFallback()
        {
            if (s_FontFallbackReady)
                return true;

            var installed = new HashSet<string>(Font.GetOSInstalledFontNames() ?? Array.Empty<string>());

            foreach (var family in k_JapaneseFontFamilies)
            {
                var present = false;
                foreach (var name in installed)
                {
                    if (name.StartsWith(family, StringComparison.OrdinalIgnoreCase))
                    {
                        present = true;
                        break;
                    }
                }

                if (!present)
                    continue;

                TMP_FontAsset fontAsset = null;

                try
                {
                    fontAsset = TMP_FontAsset.CreateFontAsset(family, "Regular");
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[IKEA_EEG] Could not build a font asset from '{family}': " +
                                     $"{e.Message}");
                }

                if (fontAsset == null)
                    continue;

                fontAsset.name = $"IKEA_EEG_JP_{family}";

                // Registered globally, so every TMP label in the scene inherits it. Existing
                // labels keep their own font for Latin text and only fall through for glyphs
                // they lack.
                TMP_Settings.fallbackFontAssets?.Insert(0, fontAsset);

                s_FontFallbackReady = true;
                s_FontFallbackDetail = $"using installed system font '{family}' (dynamic, " +
                                       "no file copied, nothing downloaded)";

                Debug.Log($"[IKEA_EEG] Japanese glyph fallback ready: {s_FontFallbackDetail}");
                return true;
            }

            s_FontFallbackDetail = "NO Japanese-capable font found among " +
                                   string.Join(", ", k_JapaneseFontFamilies) +
                                   " — Japanese text would render as empty boxes";

            Debug.LogError($"[IKEA_EEG] {s_FontFallbackDetail}. Japanese must not be offered " +
                           "until a Japanese font is available.");
            return false;
        }

        /// <summary>True when Japanese can actually be rendered on this machine.</summary>
        public static bool CanRenderJapanese()
        {
            return s_FontFallbackReady || EnsureJapaneseFontFallback();
        }

        /// <summary>Test hook. Resets the service to its pre-selection state.</summary>
        public static void ResetForTesting()
        {
            s_Language = ExperimentLanguage.None;
        }
    }
}
