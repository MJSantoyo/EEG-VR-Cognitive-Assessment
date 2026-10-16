using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using IkeaEeg.Experiment;
using IkeaEeg.Localization;

namespace IkeaEeg.EditorTools
{
    /// <summary>
    /// Builds the nine round × language RecognitionWordList assets (A_EN … C_JA) from
    /// Documentation/VR_Memory_Content/VR_Memory_Content.csv, so the 135 concepts are never
    /// copied by hand.
    ///
    /// ALL-OR-NOTHING. The whole CSV is parsed and checked (15 targets + 15 immediate lures +
    /// 15 delayed lures per round, unique stable IDs, no blank text in any language) before a
    /// single asset is written. A malformed file changes nothing.
    ///
    /// NEVER VALIDATED. Every asset is written with validatedForResearch = false whatever the CSV
    /// says: the content is draft_pending_review, not native-reviewed, not CNS, not RAVLT.
    ///
    /// Re-importing updates the existing assets in place (GUIDs, and therefore the config
    /// references, are kept). Romaji is never imported: it is for review only.
    /// </summary>
    public static class RecognitionListImporter
    {
        public const string CsvRelativePath = "Documentation/VR_Memory_Content/VR_Memory_Content.csv";
        public const string OutputFolder = ExperimentAssetBuilder.DataFolder + "/RecognitionLists";
        public const int ItemsPerRole = 15;

        public static readonly string[] ListIds = { "A", "B", "C" };

        static readonly string[] Roles = { "target", "immediate_lure", "delayed_lure" };

        public static string AssetPath(string listId, ExperimentLanguage language) =>
            $"{OutputFolder}/RecognitionList_{listId}_{ExperimentLanguages.ToCode(language)}.asset";

        [MenuItem("IKEA_EEG/Import Recognition Lists From CSV", false, 60)]
        public static void ImportMenu()
        {
            var lists = ImportAll();

            if (lists.Count > 0)
                LinkIntoConfig(lists);
        }

        /// <summary>Batch-mode entry point: import, link into the config, exit 0 / 1.</summary>
        public static void ImportFromCommandLine()
        {
            var lists = ImportAll();
            var ok = lists.Count > 0 && LinkIntoConfig(lists);
            EditorApplication.Exit(ok ? 0 : 1);
        }

        /// <summary>The nine assets in A_EN, A_ES, A_JA, B_EN … order, or empty on any error.</summary>
        public static List<RecognitionWordList> ImportAll()
        {
            var csvPath = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".",
                CsvRelativePath);

            if (!File.Exists(csvPath))
            {
                Debug.LogError($"[IKEA_EEG] Recognition list import: CSV not found at {csvPath}. " +
                               "Nothing was written.");
                return new List<RecognitionWordList>();
            }

            List<Row> rows;

            try
            {
                rows = ParseRows(File.ReadAllText(csvPath, Encoding.UTF8));
            }
            catch (FormatException e)
            {
                Debug.LogError($"[IKEA_EEG] Recognition list import: {e.Message} Nothing was written.");
                return new List<RecognitionWordList>();
            }

            if (!Check(rows, out var problem))
            {
                Debug.LogError($"[IKEA_EEG] Recognition list import: {problem} Nothing was written.");
                return new List<RecognitionWordList>();
            }

            ExperimentAssetBuilder.EnsureFolders();

            if (!AssetDatabase.IsValidFolder(OutputFolder))
                AssetDatabase.CreateFolder(ExperimentAssetBuilder.DataFolder, "RecognitionLists");

            var statuses = string.Join(",", rows.Select(r => r.reviewStatus).Distinct());
            var result = new List<RecognitionWordList>();

            foreach (var listId in ListIds)
            {
                foreach (var language in ExperimentLanguages.Selectable)
                    result.Add(WriteList(rows, listId, language, statuses));
            }

            AssetDatabase.SaveAssets();

            Debug.LogWarning($"[IKEA_EEG] Imported {result.Count} recognition lists into " +
                             $"{OutputFolder} from {CsvRelativePath}. DRAFT content " +
                             $"(review_status={statuses}); validatedForResearch=false on every list.");

            return result;
        }

        static RecognitionWordList WriteList(List<Row> rows, string listId,
            ExperimentLanguage language, string statuses)
        {
            var path = AssetPath(listId, language);
            var asset = AssetDatabase.LoadAssetAtPath<RecognitionWordList>(path);

            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<RecognitionWordList>();
                AssetDatabase.CreateAsset(asset, path);
            }

            List<Row> Role(string role) =>
                rows.Where(r => r.listId == listId && r.role == role).ToList();

            var targets = Role("target");
            var immediate = Role("immediate_lure");
            var delayed = Role("delayed_lure");

            asset.SetLists(
                targets.Select(r => r.Text(language)).ToList(),
                immediate.Select(r => r.Text(language)).ToList(),
                delayed.Select(r => r.Text(language)).ToList(),
                validated: false,
                provenanceNote:
                    "DRAFT — NOT VALIDATED — NOT CNS STIMULI — NOT RAVLT.\n" +
                    $"Generated content imported from {CsvRelativePath} (review_status={statuses}). " +
                    "Not reviewed by native speakers, no frequency/imageability norms, no pilot, " +
                    "no evidence of equivalence between lists or languages. Development and " +
                    "piloting only; must not be reported as a validated memory measure.");

            asset.SetIdentity(listId, language,
                targets.Select(r => r.stableId).ToList(),
                immediate.Select(r => r.stableId).ToList(),
                delayed.Select(r => r.stableId).ToList());

            EditorUtility.SetDirty(asset);
            return asset;
        }

        /// <summary>Points ExperimentConfig_Default at exactly these lists.</summary>
        public static bool LinkIntoConfig(List<RecognitionWordList> lists)
        {
            var config = AssetDatabase.LoadAssetAtPath<ExperimentConfig>(ExperimentAssetBuilder.ConfigPath);

            if (config == null)
            {
                Debug.LogError($"[IKEA_EEG] No config at {ExperimentAssetBuilder.ConfigPath}; " +
                               "the imported lists were not linked.");
                return false;
            }

            config.recognitionWordLists = new List<RecognitionWordList>(lists);
            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssets();

            Debug.Log($"[IKEA_EEG] Linked {lists.Count} recognition lists into " +
                      $"{ExperimentAssetBuilder.ConfigPath}.");
            return true;
        }

        // ---------------------------------------------------------------------------------

        public sealed class Row
        {
            public int round;
            public string listId, role, stableId, en, es, ja, reviewStatus;

            public string Text(ExperimentLanguage language)
            {
                switch (language)
                {
                    case ExperimentLanguage.English: return en;
                    case ExperimentLanguage.Spanish: return es;
                    case ExperimentLanguage.Japanese: return ja;
                    default: return string.Empty;
                }
            }
        }

        /// <summary>Parses the CSV text into rows. Throws FormatException on a malformed file.</summary>
        public static List<Row> ParseRows(string text)
        {
            var records = ParseCsv(text);

            if (records.Count == 0)
                throw new FormatException("the CSV is empty.");

            var header = records[0].Select(h => h.Trim()).ToList();

            int Column(string name)
            {
                var i = header.IndexOf(name);

                if (i < 0)
                    throw new FormatException($"the CSV has no '{name}' column.");

                return i;
            }

            int cRound = Column("round"), cList = Column("list_id"), cRole = Column("role"),
                cId = Column("stable_id"), cEn = Column("en"), cEs = Column("es"),
                cJa = Column("ja"), cStatus = Column("review_status");

            var rows = new List<Row>();

            for (var i = 1; i < records.Count; i++)
            {
                var r = records[i];

                if (r.Count == 1 && string.IsNullOrWhiteSpace(r[0]))
                    continue;

                if (r.Count != header.Count)
                    throw new FormatException($"CSV line {i + 1} has {r.Count} fields, expected {header.Count}.");

                if (!int.TryParse(r[cRound].Trim(), out var round))
                    throw new FormatException($"CSV line {i + 1}: round '{r[cRound]}' is not a number.");

                rows.Add(new Row
                {
                    round = round,
                    listId = r[cList].Trim(),
                    role = r[cRole].Trim(),
                    stableId = r[cId].Trim(),
                    en = r[cEn].Trim(),
                    es = r[cEs].Trim(),
                    ja = r[cJa].Trim(),
                    reviewStatus = r[cStatus].Trim(),
                });
            }

            return rows;
        }

        /// <summary>The structural rules every import must satisfy. Public for the self-test.</summary>
        public static bool Check(List<Row> rows, out string problem)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);

            foreach (var r in rows)
            {
                var expectedList = r.round >= 1 && r.round <= ListIds.Length ? ListIds[r.round - 1] : null;

                if (r.listId != expectedList)
                {
                    problem = $"{r.stableId}: round {r.round} must be list {expectedList ?? "(none)"}, not '{r.listId}'.";
                    return false;
                }

                if (!Roles.Contains(r.role))
                {
                    problem = $"{r.stableId}: unknown role '{r.role}'.";
                    return false;
                }

                if (string.IsNullOrEmpty(r.stableId) || !ids.Add(r.stableId))
                {
                    problem = $"stable_id '{r.stableId}' is blank or repeated.";
                    return false;
                }

                if (string.IsNullOrEmpty(r.en) || string.IsNullOrEmpty(r.es) || string.IsNullOrEmpty(r.ja))
                {
                    problem = $"{r.stableId}: a language column is blank.";
                    return false;
                }

                if (r.en.Contains("_"))
                {
                    problem = $"{r.stableId}: visible word '{r.en}' carries a suffix-style variant.";
                    return false;
                }
            }

            foreach (var listId in ListIds)
            {
                foreach (var role in Roles)
                {
                    var n = rows.Count(r => r.listId == listId && r.role == role);

                    if (n != ItemsPerRole)
                    {
                        problem = $"list {listId} has {n} '{role}' rows, expected {ItemsPerRole}.";
                        return false;
                    }
                }
            }

            problem = string.Empty;
            return true;
        }

        /// <summary>RFC 4180: quoted fields may contain commas, doubled quotes and newlines.</summary>
        static List<List<string>> ParseCsv(string text)
        {
            var records = new List<List<string>>();
            var record = new List<string>();
            var field = new StringBuilder();
            var quoted = false;

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];

                if (quoted)
                {
                    if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else field.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == ',') { record.Add(field.ToString()); field.Clear(); }
                else if (c == '\n' || c == '\r')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                        i++;

                    record.Add(field.ToString());
                    field.Clear();
                    records.Add(record);
                    record = new List<string>();
                }
                else if (c != '﻿') field.Append(c);
            }

            if (quoted)
                throw new FormatException("the CSV ends inside a quoted field.");

            if (field.Length > 0 || record.Count > 0)
            {
                record.Add(field.ToString());
                records.Add(record);
            }

            return records;
        }
    }
}
