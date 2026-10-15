using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using IkeaEeg.XR;

namespace IkeaEeg.EditorTools
{
    /// <summary>
    /// Cleanup pass for the Encoding (Area A) Japanese city surround: measures how much of the
    /// participant's view each surround building actually fills, from the Spawn_A eye and a
    /// 1 m head-movement envelope around it, and disables the DISTANT buildings that fill
    /// (almost) none of it.
    ///
    /// Visibility is measured by ray casting against TEMPORARY MeshColliders (added in memory,
    /// never saved) on the rendered geometry of the Area A scene, so nearer buildings and the
    /// storefront occlude the ones behind them. Transparent and alpha-cutout surfaces (glass,
    /// foliage) are not treated as occluders, which errs toward keeping buildings. The scene's
    /// own colliders (door blockers, boundary walls) are ignored. It is a geometric estimate,
    /// not a render; the before/after captures of EncodingEnvironmentPolish are the check.
    ///
    /// Touches only GameObjects under Encoding_JapaneseEnvironment. Disables, never deletes:
    /// "Re-enable All" restores every building.
    /// </summary>
    public static class EncodingSurroundCleanup
    {
        const string k_ScenePath = "Assets/IKEA_EEG/Scenes/IKEA_EEG_AreaA.unity";

        static readonly string[] k_Groups =
            { "H_North", "H_South", "I_West", "I_East", "Infill_BackRight", "Infill_BackLeft" };

        // Spawn_A eye plus a 1 m head-movement envelope. Spawn_A is the only participant
        // position in Area A (encoding and immediate recognition both happen there).
        static readonly Vector3 k_Eye = new Vector3(0f, 1.60f, -0.70f);
        static readonly Vector3[] k_EyeEnvelope =
        {
            k_Eye,
            k_Eye + new Vector3(-1f, 0f, 0f), k_Eye + new Vector3(1f, 0f, 0f),
            k_Eye + new Vector3(0f, 0f, -1f), k_Eye + new Vector3(0f, 0f, 1f),
        };

        const float k_StepDeg = 0.5f;
        const float k_ElevMin = -10f;
        const float k_ElevMax = 70f;

        // ---- the cleanup rule ------------------------------------------------------------------
        // Everything within k_KeepWithinM (horizontal distance from the eye to the building's
        // footprint) is kept, visible or not: that is the near/mid composition, and it keeps the
        // surround robust to head movement beyond the 1 m envelope. Beyond it, a building is
        // kept only if it is visible: at least k_MinSpawnDeg2 from Spawn_A (a building seen
        // through a gap fills that gap; without it the gap shows sky) or k_MinEnvelopeDeg2 from
        // any envelope eye. Beyond k_OutlierM a building is an outlier and is always dropped.
        // (1 deg^2 is roughly a 1 x 1 degree patch.)
        const float k_KeepWithinM = 45f;
        const float k_OutlierM = 100f;
        const double k_MinSpawnDeg2 = 1.0;
        const double k_MinEnvelopeDeg2 = 10.0;

        // =====================================================================================
        // Entry points
        // =====================================================================================

        [MenuItem("IKEA_EEG/Visuals/Japanese City/Cleanup: Report Distant Outliers (no changes)", false, 230)]
        public static void ReportMenu()
        {
            var scene = SceneManager.GetSceneByPath(k_ScenePath);
            Debug.Log(scene.IsValid() && scene.isLoaded
                ? Analyze(scene, "OPEN SCENE", out _)
                : $"[IKEA_EEG] {k_ScenePath} is not open.");
        }

        [MenuItem("IKEA_EEG/Visuals/Japanese City/Cleanup: Disable Distant Outliers", false, 231)]
        public static void ApplyMenu()
        {
            var scene = SceneManager.GetSceneByPath(k_ScenePath);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                Debug.Log($"[IKEA_EEG] {k_ScenePath} is not open. Nothing was changed.");
                return;
            }

            var sb = new StringBuilder();
            if (Apply(scene, sb))
            {
                EditorSceneManager.MarkSceneDirty(scene);
                sb.AppendLine("[IKEA_EEG] The Area A scene is dirty — save it to keep the cleanup.");
            }
            Debug.Log(sb.ToString());
        }

        [MenuItem("IKEA_EEG/Visuals/Japanese City/Cleanup: Re-enable All Surround Buildings", false, 232)]
        public static void RestoreMenu()
        {
            var scene = SceneManager.GetSceneByPath(k_ScenePath);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                Debug.Log($"[IKEA_EEG] {k_ScenePath} is not open. Nothing was changed.");
                return;
            }

            var n = Restore(scene);
            if (n > 0) EditorSceneManager.MarkSceneDirty(scene);
            Debug.Log($"[IKEA_EEG] {n} surround building(s) re-enabled." + (n > 0 ? " Save the scene to keep it." : ""));
        }

        /// <summary>Batch: measure and list what the rule would disable. Saves nothing.</summary>
        public static void InspectFromCommandLine()
        {
            var scene = EditorSceneManager.OpenScene(k_ScenePath, OpenSceneMode.Single);
            Debug.Log(Analyze(scene, "CURRENT STATE", out _));
        }

        /// <summary>Batch: disable the distant outliers, save the Area A scene ONLY, re-measure.</summary>
        public static void ApplyFromCommandLine()
        {
            var scene = EditorSceneManager.OpenScene(k_ScenePath, OpenSceneMode.Single);
            var sb = new StringBuilder();
            if (!Apply(scene, sb))
            {
                Debug.LogError(sb.ToString());
                EditorApplication.Exit(2);
                return;
            }

            // The scene ONLY. Never AssetDatabase.SaveAssets() (see SaveOwnAssets in the builder).
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            scene = EditorSceneManager.OpenScene(k_ScenePath, OpenSceneMode.Single);
            sb.Append(Analyze(scene, "AFTER CLEANUP (reloaded from disk)", out _));
            sb.Append(JapaneseCityEnvironmentBuilder.ValidateScene(scene));
            sb.AppendLine(AreaAEnvironmentBuilder.Validate());
            sb.AppendLine(ExperimentSceneBuilder.ValidateRoomScene(ExperimentArea.AreaA));
            Debug.Log(sb.ToString());
        }

        /// <summary>Batch: re-enable every surround building and save the Area A scene.</summary>
        public static void RestoreFromCommandLine()
        {
            var scene = EditorSceneManager.OpenScene(k_ScenePath, OpenSceneMode.Single);
            var n = Restore(scene);
            if (n > 0)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }
            Debug.Log($"[IKEA_EEG] {n} surround building(s) re-enabled.");
        }

        // =====================================================================================

        static GameObject FindRoot(Scene scene) =>
            scene.GetRootGameObjects().FirstOrDefault(g => g.name == JapaneseCityEnvironmentBuilder.RootName);

        static bool Apply(Scene scene, StringBuilder sb)
        {
            if (FindRoot(scene) == null)
            {
                sb.AppendLine("[IKEA_EEG] surround root not found. Nothing was changed.");
                return false;
            }

            // Decide from the COMPLETE surround, so re-running gives the same result.
            var restored = Restore(scene);
            if (restored > 0)
                sb.AppendLine($"[IKEA_EEG] re-enabled {restored} previously disabled building(s) before measuring.");

            sb.Append(Analyze(scene, "BEFORE CLEANUP (complete surround)", out var stats));
            var disable = stats.Where(s => !s.keep).ToList();
            foreach (var s in disable)
                s.renderer.gameObject.SetActive(false);

            sb.AppendLine($"[IKEA_EEG] disabled {disable.Count} of {stats.Count} active surround building(s); " +
                          $"{stats.Count - disable.Count} kept.");
            return true;
        }

        static int Restore(Scene scene)
        {
            var root = FindRoot(scene);
            if (root == null) return 0;
            var n = 0;
            foreach (var groupName in k_Groups)
            {
                var gt = root.transform.Find(groupName);
                if (gt == null || gt.childCount != 1) continue;
                foreach (Transform building in gt.GetChild(0))
                {
                    var go = building.gameObject;
                    var wasInactive = !go.activeSelf;

                    // Revert the prefab override rather than writing m_IsActive = 1 over it, so a
                    // restored building leaves no trace in the scene file.
                    var active = new SerializedObject(go).FindProperty("m_IsActive");
                    if (PrefabUtility.IsPartOfPrefabInstance(go) && active != null && active.prefabOverride)
                        PrefabUtility.RevertPropertyOverride(active, InteractionMode.AutomatedAction);
                    if (!go.activeSelf) go.SetActive(true);
                    if (wasInactive) n++;
                }
            }
            return n;
        }

        // =====================================================================================
        // Measurement
        // =====================================================================================

        class Stat
        {
            public string group, name;
            public MeshRenderer renderer;
            public float dist, height;
            public double spawn, envelopeMax;
            public bool keep;
        }

        static readonly RaycastHit[] s_Hits = new RaycastHit[256];

        static readonly string[] k_SectorNames =
            { "front", "front-right", "right", "back-right", "back", "back-left", "left", "front-left" };

        static string Analyze(Scene scene, string label, out List<Stat> statList)
        {
            var sb = new StringBuilder();
            statList = new List<Stat>();
            sb.AppendLine($"[IKEA_EEG] ===== ENCODING SURROUND VISIBILITY ({label}) =====");
            var root = FindRoot(scene);
            if (root == null) { sb.AppendLine("  surround root not found"); return sb.ToString(); }

            var stats = new Dictionary<MeshRenderer, Stat>();
            foreach (var groupName in k_Groups)
            {
                var gt = root.transform.Find(groupName);
                if (gt == null) continue;
                foreach (var r in gt.GetComponentsInChildren<MeshRenderer>(false))
                {
                    if (!r.enabled) continue;
                    var b = r.bounds;
                    var flat = new Vector2(Mathf.Max(b.min.x - k_Eye.x, 0f, k_Eye.x - b.max.x),
                                           Mathf.Max(b.min.z - k_Eye.z, 0f, k_Eye.z - b.max.z));
                    stats[r] = new Stat { group = groupName, name = r.name, renderer = r, dist = flat.magnitude, height = b.max.y };
                }
            }

            // Temporary colliders on the rendered, opaque geometry of the scene (memory only).
            var temp = new List<MeshCollider>();
            var owner = new Dictionary<Collider, MeshRenderer>();
            var skippedSeeThrough = 0;
            foreach (var g in scene.GetRootGameObjects())
            foreach (var r in g.GetComponentsInChildren<MeshRenderer>(false))
            {
                if (!r.enabled) continue;
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                if (!stats.ContainsKey(r) && IsSeeThrough(r)) { skippedSeeThrough++; continue; }
                var c = r.gameObject.AddComponent<MeshCollider>();
                c.sharedMesh = mf.sharedMesh;
                temp.Add(c);
                owner[c] = r;
            }
            Physics.SyncTransforms();
            var hitBackfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = false; // the building materials cull back faces

            var surround = new HashSet<MeshRenderer>(stats.Keys);
            var perEye = new List<Dictionary<MeshRenderer, double>>();
            double[,] spawnSectors = null;
            try
            {
                foreach (var eye in k_EyeEnvelope)
                {
                    perEye.Add(Cast(eye, owner, surround, out var sectors));
                    if (spawnSectors == null) spawnSectors = sectors;
                }
            }
            finally
            {
                foreach (var c in temp) Object.DestroyImmediate(c);
                Physics.queriesHitBackfaces = hitBackfaces;
            }

            foreach (var s in stats.Values)
            {
                s.spawn = perEye[0].TryGetValue(s.renderer, out var v) ? v : 0;
                s.envelopeMax = perEye.Max(d => d.TryGetValue(s.renderer, out var w) ? w : 0);
                s.keep = s.dist <= k_KeepWithinM ||
                         (s.dist <= k_OutlierM && (s.spawn >= k_MinSpawnDeg2 || s.envelopeMax >= k_MinEnvelopeDeg2));
            }
            statList = stats.Values.OrderBy(s => s.group).ThenBy(s => s.dist).ToList();

            // ---- report ---------------------------------------------------------------------
            sb.AppendLine($"  info  occluders: {temp.Count} rendered mesh(es); {skippedSeeThrough} transparent/cutout renderer(s) " +
                          "treated as see-through; scene colliders ignored");
            sb.AppendLine($"  info  {stats.Count} active surround building renderer(s); visible solid angle from Spawn_A " +
                          $"{stats.Values.Sum(s => s.spawn):0} deg^2 (elevation {k_ElevMin}..{k_ElevMax} deg, {k_StepDeg} deg rays, cos-weighted)");
            sb.AppendLine($"  info  rule: keep if within {k_KeepWithinM} m, or >= {k_MinSpawnDeg2} deg^2 from Spawn_A, " +
                          $"or >= {k_MinEnvelopeDeg2} deg^2 from any eye of the 1 m envelope; always drop beyond {k_OutlierM} m");
            sb.AppendLine("  group             n   visible(deg^2)  near..far(m)   rule keeps  rule drops  (dropped deg^2 spawn / envelope max)");
            foreach (var g in statList.GroupBy(s => s.group))
            {
                var drop = g.Where(s => !s.keep).ToList();
                sb.AppendLine($"  {g.Key,-16} {g.Count(),3}   {g.Sum(s => s.spawn),10:0}      {g.Min(s => s.dist),5:0}..{g.Max(s => s.dist),-5:0}" +
                              $"   {g.Count() - drop.Count,6}  {drop.Count,10}      ({drop.Sum(s => s.spawn):0.0} / {drop.Sum(s => s.envelopeMax):0.0})");
            }

            sb.AppendLine("  visible buildings (any envelope eye): dist m | top m | deg^2 spawn | deg^2 envelope max | keep");
            foreach (var s in statList.Where(s => s.envelopeMax > 0))
                sb.AppendLine($"    {s.dist,6:0.0} {s.height,6:0.0} {s.spawn,9:0.0} {s.envelopeMax,9:0.0}  {(s.keep ? "keep" : "DROP")}  {s.group}/{s.name}");

            sb.AppendLine("  rule drops (group: building @ distance m):");
            foreach (var g in statList.Where(s => !s.keep).GroupBy(s => s.group))
                sb.AppendLine($"    {g.Key} ({g.Count()}): " + string.Join(", ", g.Select(s => $"{s.name.Replace("005339_08932_", "")}@{s.dist:0}")));

            sb.AppendLine("  info  enclosure from Spawn_A, 0..30 deg elevation band, per 45-deg sector (share of the band):");
            sb.AppendLine("          sector        surround  other-geometry  sky");
            for (var i = 0; i < 8; i++)
            {
                var sum = spawnSectors[i, 0] + spawnSectors[i, 1] + spawnSectors[i, 2];
                sb.AppendLine($"          {k_SectorNames[i],-12} {spawnSectors[i, 0] / sum,8:P0}  {spawnSectors[i, 1] / sum,14:P0}  {spawnSectors[i, 2] / sum,5:P0}");
            }
            return sb.ToString();
        }

        static bool IsSeeThrough(Renderer r) =>
            r.sharedMaterials.Length > 0 &&
            r.sharedMaterials.All(m => m == null || m.renderQueue >= (int)UnityEngine.Rendering.RenderQueue.AlphaTest);

        /// <summary>
        /// deg^2 hit per surround renderer from one eye. sectors[i] = (surround, other geometry, sky)
        /// deg^2 in the 0..30 deg elevation band of 45-degree sector i (front = +Z).
        /// </summary>
        static Dictionary<MeshRenderer, double> Cast(Vector3 eye, Dictionary<Collider, MeshRenderer> owner,
            HashSet<MeshRenderer> surround, out double[,] sectors)
        {
            var result = new Dictionary<MeshRenderer, double>();
            sectors = new double[8, 3];
            for (var el = k_ElevMin + k_StepDeg / 2; el < k_ElevMax; el += k_StepDeg)
            {
                var w = Mathf.Cos(el * Mathf.Deg2Rad) * k_StepDeg * k_StepDeg;
                var band = el >= 0f && el < 30f;
                for (var az = k_StepDeg / 2; az < 360f; az += k_StepDeg)
                {
                    var dir = Quaternion.Euler(-el, az, 0f) * Vector3.forward;
                    var kind = 2;

                    // Nearest hit on a measured surface only (the scene's own colliders are ignored).
                    var n = Physics.RaycastNonAlloc(eye, dir, s_Hits, 1000f);
                    var best = -1;
                    for (var i = 0; i < n; i++)
                        if (owner.ContainsKey(s_Hits[i].collider) && (best < 0 || s_Hits[i].distance < s_Hits[best].distance))
                            best = i;

                    if (best >= 0)
                    {
                        var r = owner[s_Hits[best].collider];
                        if (surround.Contains(r))
                        {
                            result[r] = (result.TryGetValue(r, out var v) ? v : 0) + w;
                            kind = 0;
                        }
                        else kind = 1;
                    }
                    if (band) sectors[(int)(((az + 22.5f) % 360f) / 45f), kind] += w;
                }
            }
            return result;
        }
    }
}
