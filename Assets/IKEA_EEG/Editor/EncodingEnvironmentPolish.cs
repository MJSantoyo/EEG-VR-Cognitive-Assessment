using System.Collections.Generic;
using System.IO;
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
    /// Visual polish for the Encoding environment (Area A) after the Japanese-city surround:
    ///
    /// 1. FACADE: re-tones the three project-owned storefront materials (blue cladding, alternate
    ///    blue bays, yellow sign) toward a muted urban palette. Colours only, edited in place:
    ///    no renderer, collider, transform or prefab changes. The materials are used by
    ///    AreaA_Visuals.prefab only (M_Facade / M_Accent, shared with Area C, are not touched).
    ///    NOTE: AreaAEnvironmentBuilder.BuildMaterials() writes the ORIGINAL colours back if the
    ///    Area A visuals are rebuilt; run step 1 again afterwards.
    ///
    ///    The same mechanism tones down M_AreaA_Pavement_Light (plaza, kerb, road markings).
    ///
    /// 2. SKY: a project-owned Skybox/Cubemap material that reuses the VR template's cloud
    ///    cubemap (Assets/VRTemplateAssets, tracked in git; read, never written). It is assigned
    ///    to the Bootstrap scene's Lighting settings because Bootstrap is the active scene at
    ///    runtime, so its RenderSettings decide the sky. Ambient is Flat there, so the sky change
    ///    does not change the scene's ambient lighting.
    ///
    /// No MonoBehaviour, collider, light or canvas is created. Never calls
    /// AssetDatabase.SaveAssets() (it re-serializes vendor materials); only own assets are saved.
    /// </summary>
    public static class EncodingEnvironmentPolish
    {
        const string k_AreaAScenePath = "Assets/IKEA_EEG/Scenes/IKEA_EEG_AreaA.unity";
        const string k_SkyMaterialPath = "Assets/IKEA_EEG/Art/JapaneseCity/Materials/JC_Sky_Day.mat";
        const string k_SkyCubemapPath = "Assets/VRTemplateAssets/Materials/Skybox/skybox01_openGL.png";
        const string k_SkyShaderName = "Skybox/Cubemap";
        const string k_LitShaderName = "Universal Render Pipeline/Lit";

        // Neutral grey tint: Skybox/Cubemap doubles the tint, so 0.5 leaves the texture as is.
        static readonly Color k_SkyTint = new Color(0.5f, 0.5f, 0.5f, 1f);
        const float k_SkyExposure = 1.15f;
        const float k_SkyRotation = 0f;

        /// <summary>Sign lettering must stay at least this readable against the panel behind it
        /// (WCAG-style luminance contrast; the original blue / yellow pair is ~4.7).</summary>
        const float k_MinSignContrast = 4.0f;

        struct FacadeTone
        {
            public string path;
            public Color original, harmonized;
            public float originalSmoothness, harmonizedSmoothness;
        }

        // Originals are AreaAEnvironmentBuilder.BuildMaterials() values, kept for the restore item.
        static readonly FacadeTone[] k_Facade =
        {
            new FacadeTone
            {
                path = "Assets/IKEA_EEG/Materials/M_AreaA_Facade_Blue.mat",
                original = new Color(0.086f, 0.325f, 0.620f), originalSmoothness = 0.28f,
                // Slate blue: still reads as a blue building, at under half the
                // saturation, closer to the concrete and tile of the surrounding blocks.
                harmonized = new Color(0.270f, 0.350f, 0.450f), harmonizedSmoothness = 0.22f,
            },
            new FacadeTone
            {
                path = "Assets/IKEA_EEG/Materials/M_AreaA_Facade_Blue_Alt.mat",
                original = new Color(0.070f, 0.290f, 0.565f), originalSmoothness = 0.34f,
                harmonized = new Color(0.240f, 0.318f, 0.410f), harmonizedSmoothness = 0.26f,
            },
            new FacadeTone
            {
                path = "Assets/IKEA_EEG/Materials/M_AreaA_Accent_Yellow.mat",
                original = new Color(0.980f, 0.770f, 0.050f), originalSmoothness = 0.32f,
                // Warm ochre-cream: less saturated, slightly lighter, so the lettering keeps
                // its contrast against the darker slate panel behind it.
                harmonized = new Color(0.880f, 0.760f, 0.400f), harmonizedSmoothness = 0.28f,
            },
            new FacadeTone
            {
                // Ground polish: plaza, kerb, Area A paving slabs and road markings. Near-white
                // concrete glared next to the city's 0.52 sidewalks; a warmer mid-light concrete
                // still keeps the markings well clear of the 0.205 asphalt.
                path = "Assets/IKEA_EEG/Materials/M_AreaA_Pavement_Light.mat",
                original = new Color(0.800f, 0.805f, 0.810f), originalSmoothness = 0.12f,
                harmonized = new Color(0.700f, 0.690f, 0.670f), harmonizedSmoothness = 0.12f,
            },
        };

        const int k_PanelIndex = 0;   // Panel_Above_Entrance, behind Sign_Text
        const int k_SignIndex = 2;    // Sign_Text, Sign_Band, Sign_Post_*

        // =================================================================================
        // Menu entry points
        // =================================================================================

        [MenuItem("IKEA_EEG/Visuals/Encoding Polish/1. Harmonize Central Facade Colours", false, 260)]
        public static void HarmonizeFacadeMenu()
        {
            var report = new StringBuilder();
            ApplyFacade(harmonized: true, report);
            Debug.Log(report.ToString());
        }

        [MenuItem("IKEA_EEG/Visuals/Encoding Polish/Restore Original Facade Colours", false, 280)]
        public static void RestoreFacadeMenu()
        {
            var report = new StringBuilder();
            ApplyFacade(harmonized: false, report);
            Debug.Log(report.ToString());
        }

        [MenuItem("IKEA_EEG/Visuals/Encoding Polish/2. Apply Day Sky to Open Bootstrap Scene", false, 261)]
        public static void ApplySkyMenu()
        {
            var bootstrap = SceneManager.GetSceneByPath(ExperimentSceneBuilder.BootstrapScenePath);
            if (!bootstrap.IsValid() || !bootstrap.isLoaded)
            {
                EditorUtility.DisplayDialog("IKEA_EEG",
                    $"Open {ExperimentSceneBuilder.BootstrapScenePath} first. Nothing was changed.", "OK");
                return;
            }

            var report = new StringBuilder();
            var sky = BuildSkyMaterial(report);
            if (sky == null) { Debug.LogError(report.ToString()); return; }

            // RenderSettings belong to the ACTIVE scene.
            var previousActive = SceneManager.GetActiveScene();
            SceneManager.SetActiveScene(bootstrap);
            report.AppendLine($"  ok    Bootstrap skybox: {Describe(RenderSettings.skybox)} -> {sky.name}");
            RenderSettings.skybox = sky;
            SceneManager.SetActiveScene(previousActive);

            EditorSceneManager.MarkSceneDirty(bootstrap);
            report.AppendLine("[IKEA_EEG] The Bootstrap scene is dirty — save it to keep the sky.");
            Debug.Log(report.ToString());
        }

        [MenuItem("IKEA_EEG/Visuals/Encoding Polish/Restore Default Sky in Open Bootstrap Scene", false, 281)]
        public static void RestoreSkyMenu()
        {
            var bootstrap = SceneManager.GetSceneByPath(ExperimentSceneBuilder.BootstrapScenePath);
            if (!bootstrap.IsValid() || !bootstrap.isLoaded)
            {
                Debug.Log($"[IKEA_EEG] {ExperimentSceneBuilder.BootstrapScenePath} is not open. Nothing was changed.");
                return;
            }

            var previousActive = SceneManager.GetActiveScene();
            SceneManager.SetActiveScene(bootstrap);
            RenderSettings.skybox = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Skybox.mat");
            SceneManager.SetActiveScene(previousActive);

            EditorSceneManager.MarkSceneDirty(bootstrap);
            Debug.Log("[IKEA_EEG] Bootstrap skybox set back to Unity's Default-Skybox. Save the scene to keep it.");
        }

        // =================================================================================
        // Batch entry points
        // =================================================================================

        /// <summary>
        /// Unity.exe -batchmode -quit -projectPath ... -executeMethod
        ///   IkeaEeg.EditorTools.EncodingEnvironmentPolish.ApplyFromCommandLine
        ///
        /// Re-tones the facade materials, writes the sky material, assigns it in Bootstrap and
        /// SAVES Bootstrap only. Area A is opened afterwards for validation and NOT saved.
        /// </summary>
        public static void ApplyFromCommandLine()
        {
            var report = new StringBuilder();
            report.AppendLine("[IKEA_EEG] ===== ENCODING POLISH: APPLY =====");

            if (!ApplyFacade(harmonized: true, report))
            {
                Debug.LogError(report.ToString());
                EditorApplication.Exit(2);
                return;
            }

            var sky = BuildSkyMaterial(report);
            if (sky == null)
            {
                Debug.LogError(report.ToString());
                EditorApplication.Exit(3);
                return;
            }

            var bootstrap = EditorSceneManager.OpenScene(ExperimentSceneBuilder.BootstrapScenePath, OpenSceneMode.Single);
            report.AppendLine($"  ok    Bootstrap skybox: {Describe(RenderSettings.skybox)} -> {sky.name}");
            RenderSettings.skybox = sky;
            EditorSceneManager.MarkSceneDirty(bootstrap);
            EditorSceneManager.SaveScene(bootstrap);

            report.Append(ValidateAll());
            Debug.Log(report.ToString());
        }

        /// <summary>Materials only (facade + pavement tones); opens and saves no scene.</summary>
        public static void FacadeFromCommandLine()
        {
            var report = new StringBuilder();
            var ok = ApplyFacade(harmonized: true, report);
            if (ok) Debug.Log(report.ToString()); else Debug.LogError(report.ToString());
            if (!ok) EditorApplication.Exit(2);
        }

        /// <summary>Validation only; saves nothing.</summary>
        public static void ValidateFromCommandLine() => Debug.Log(ValidateAll());

        /// <summary>
        /// Renders the participant's views of Bootstrap + Area A (Bootstrap active, as at
        /// runtime) to PNG. Saves nothing. Output folder: -captureOutput &lt;folder&gt;.
        /// Static renders only: world-space canvases are inactive in the authored scenes.
        /// </summary>
        public static void CaptureFromCommandLine()
        {
            var args = System.Environment.GetCommandLineArgs();
            var folder = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Temp", "EncodingPolish");
            for (var i = 0; i < args.Length - 1; i++)
                if (args[i] == "-captureOutput") folder = args[i + 1];

            var bootstrap = EditorSceneManager.OpenScene(ExperimentSceneBuilder.BootstrapScenePath, OpenSceneMode.Single);
            EditorSceneManager.OpenScene(k_AreaAScenePath, OpenSceneMode.Additive);
            SceneManager.SetActiveScene(bootstrap);

            Debug.Log(Capture(folder));
        }

        // =================================================================================
        // Facade
        // =================================================================================

        static bool ApplyFacade(bool harmonized, StringBuilder report)
        {
            var lit = Shader.Find(k_LitShaderName);
            var ok = true;

            foreach (var tone in k_Facade)
            {
                var material = AssetDatabase.LoadAssetAtPath<Material>(tone.path);
                if (material == null)
                {
                    report.AppendLine($"  FAIL  {tone.path} not found. Nothing else was changed for it.");
                    ok = false;
                    continue;
                }

                if (material.shader != lit)
                {
                    // Not expected: AreaAEnvironmentBuilder creates these with URP Lit. Leave the
                    // material alone rather than change its shader as a side effect.
                    report.AppendLine($"  FAIL  {material.name} uses '{material.shader?.name}', not {k_LitShaderName}; left unchanged.");
                    ok = false;
                    continue;
                }

                var color = harmonized ? tone.harmonized : tone.original;
                var smoothness = harmonized ? tone.harmonizedSmoothness : tone.originalSmoothness;

                if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
                if (material.HasProperty("_Color")) material.SetColor("_Color", color);
                if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);

                EditorUtility.SetDirty(material);
                AssetDatabase.SaveAssetIfDirty(material);

                report.AppendLine($"  ok    {material.name}: {(harmonized ? "harmonized" : "original")} colour " +
                                  $"{Hex(color)} (saturation {Saturation(color):0.00}), smoothness {smoothness:0.00}");
            }

            report.AppendLine($"  info  sign lettering / panel contrast: " +
                              $"{Contrast(Current(k_SignIndex), Current(k_PanelIndex)):0.0}:1 " +
                              $"(original palette {Contrast(k_Facade[k_SignIndex].original, k_Facade[k_PanelIndex].original):0.0}:1)");
            return ok;
        }

        static Color Current(int index)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(k_Facade[index].path);
            return material != null && material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor") : Color.black;
        }

        // =================================================================================
        // Sky
        // =================================================================================

        static Material BuildSkyMaterial(StringBuilder report)
        {
            var shader = Shader.Find(k_SkyShaderName);
            var cubemap = AssetDatabase.LoadAssetAtPath<Cubemap>(k_SkyCubemapPath);

            if (shader == null || cubemap == null)
            {
                report.AppendLine($"  FAIL  sky: shader '{k_SkyShaderName}' {(shader == null ? "missing" : "ok")}, " +
                                  $"cubemap {k_SkyCubemapPath} {(cubemap == null ? "missing / not a cubemap" : "ok")}. Nothing was written.");
                return null;
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(k_SkyMaterialPath);
            if (material == null)
            {
                material = new Material(shader) { name = Path.GetFileNameWithoutExtension(k_SkyMaterialPath) };
                AssetDatabase.CreateAsset(material, k_SkyMaterialPath);
            }
            else if (material.shader != shader)
            {
                material.shader = shader;
            }

            material.SetTexture("_Tex", cubemap);
            material.SetColor("_Tint", k_SkyTint);
            material.SetFloat("_Exposure", k_SkyExposure);
            material.SetFloat("_Rotation", k_SkyRotation);

            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssetIfDirty(material);

            report.AppendLine($"  ok    {material.name}: {k_SkyShaderName}, cubemap {cubemap.name} ({cubemap.width}px faces), " +
                              $"tint {k_SkyTint.r:0.00}, exposure {k_SkyExposure:0.00}");
            return material;
        }

        // =================================================================================
        // Validation (static only)
        // =================================================================================

        static string ValidateAll()
        {
            var sb = new StringBuilder();

            // Bootstrap: the persistent systems are intact and the saved sky is ours.
            EditorSceneManager.OpenScene(ExperimentSceneBuilder.BootstrapScenePath, OpenSceneMode.Single);
            sb.AppendLine(ExperimentSceneBuilder.ValidateBootstrapScene());
            var savedSky = RenderSettings.skybox;
            var ambientMode = RenderSettings.ambientMode;

            // Area A on its own, as the room validators expect.
            var areaA = EditorSceneManager.OpenScene(k_AreaAScenePath, OpenSceneMode.Single);
            sb.Append(JapaneseCityEnvironmentBuilder.ValidateScene(areaA));
            sb.AppendLine(AreaAEnvironmentBuilder.Validate());
            sb.AppendLine(ExperimentSceneBuilder.ValidateRoomScene(ExperimentArea.AreaA));

            sb.AppendLine("[IKEA_EEG] ===== ENCODING POLISH VALIDATION (static) =====");
            var problems = 0;

            var lit = Shader.Find(k_LitShaderName);
            foreach (var tone in k_Facade)
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>(tone.path);
                var good = m != null && m.shader == lit && m.shader.isSupported &&
                           ((Vector4)(m.GetColor("_BaseColor") - tone.harmonized)).magnitude < 0.002f;
                problems += Report(sb, good, $"{Path.GetFileNameWithoutExtension(tone.path)} is URP Lit with the harmonized colour" +
                                             (m == null ? " (missing)" : $" ({Hex(m.GetColor("_BaseColor"))})"));
            }

            var contrast = Contrast(Current(k_SignIndex), Current(k_PanelIndex));
            problems += Report(sb, contrast >= k_MinSignContrast,
                $"sign lettering contrast against the panel {contrast:0.0}:1 (minimum {k_MinSignContrast:0.0}:1)");

            // Facade renderers still point at the same three materials (nothing was reassigned).
            var visuals = areaA.GetRootGameObjects()
                .SelectMany(r => r.GetComponentsInChildren<MeshRenderer>(true))
                .Where(r => r.transform.root.name != JapaneseCityEnvironmentBuilder.RootName)
                .ToList();
            foreach (var (tone, expected) in new[] { (k_Facade[0], 5), (k_Facade[1], 4), (k_Facade[2], 4), (k_Facade[3], 25) })
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>(tone.path);
                var users = visuals.Count(r => r.sharedMaterials.Contains(m));
                problems += Report(sb, users == expected,
                    $"{Path.GetFileNameWithoutExtension(tone.path)} is on {users} Area A renderer(s) (expected {expected})");
            }

            var magenta = visuals.SelectMany(r => r.sharedMaterials)
                .Count(m => m == null || m.shader == null || !m.shader.isSupported ||
                            m.shader.name == "Hidden/InternalErrorShader");
            problems += Report(sb, magenta == 0, $"{magenta} Area A renderer slot(s) outside the surround would draw magenta");

            var sky = AssetDatabase.LoadAssetAtPath<Material>(k_SkyMaterialPath);
            problems += Report(sb, sky != null && sky.shader != null && sky.shader.name == k_SkyShaderName &&
                                   sky.shader.isSupported && sky.GetTexture("_Tex") != null,
                $"{Path.GetFileName(k_SkyMaterialPath)} is {k_SkyShaderName} with its cubemap assigned");
            problems += Report(sb, savedSky == sky,
                $"Bootstrap's saved skybox is {Describe(savedSky)}");
            problems += Report(sb, ambientMode == UnityEngine.Rendering.AmbientMode.Flat,
                $"Bootstrap ambient mode is {ambientMode} (Flat: the sky does not drive ambient light)");

            sb.AppendLine(problems == 0
                ? "[IKEA_EEG] ENCODING POLISH VALIDATION PASSED (static checks only — no Play Mode, no headset)."
                : $"[IKEA_EEG] ENCODING POLISH VALIDATION: {problems} problem(s).");
            return sb.ToString();
        }

        static int Report(StringBuilder sb, bool ok, string message)
        {
            sb.AppendLine($"  {(ok ? "ok  " : "FAIL")}  {message}");
            return ok ? 0 : 1;
        }

        // =================================================================================
        // Capture
        // =================================================================================

        struct View
        {
            public string name;
            public Vector3 position, lookAt;
            public float fieldOfView;
        }

        static readonly Vector3 k_Eye = new Vector3(0f, 1.60f, -0.70f);

        static IEnumerable<View> Views()
        {
            yield return new View { name = "01_Eye_Front", position = k_Eye, lookAt = new Vector3(0f, 3.0f, 2.0f), fieldOfView = 90f };
            yield return new View { name = "02_Eye_Front_Up", position = k_Eye, lookAt = new Vector3(0f, 9.0f, 8.0f), fieldOfView = 90f };
            yield return new View { name = "03_Eye_Back", position = k_Eye, lookAt = new Vector3(0f, 4.0f, -40f), fieldOfView = 90f };
            yield return new View { name = "04_Eye_BackLeft", position = k_Eye, lookAt = new Vector3(-30f, 4.0f, -40f), fieldOfView = 90f };
            yield return new View { name = "05_Eye_BackRight", position = k_Eye, lookAt = new Vector3(30f, 4.0f, -40f), fieldOfView = 90f };
            yield return new View { name = "06_Eye_Left", position = k_Eye, lookAt = new Vector3(-40f, 3.0f, -0.7f), fieldOfView = 90f };
            yield return new View { name = "07_Eye_Right", position = k_Eye, lookAt = new Vector3(40f, 3.0f, -0.7f), fieldOfView = 90f };
            yield return new View { name = "08_Sign_Close", position = new Vector3(0f, 2.30f, -0.20f), lookAt = new Vector3(0f, 3.32f, 1.95f), fieldOfView = 62f };
            yield return new View { name = "09_Plaza_ThreeQuarter", position = new Vector3(-11f, 2.10f, -9f), lookAt = new Vector3(0f, 2.6f, 1.9f), fieldOfView = 70f };
            yield return new View { name = "10_Road_Facade", position = new Vector3(0f, 1.75f, -19f), lookAt = new Vector3(0f, 3.5f, 1.9f), fieldOfView = 70f };
        }

        static string Capture(string folder)
        {
            Directory.CreateDirectory(folder);
            var host = new GameObject("~EncodingPolishCapture") { hideFlags = HideFlags.HideAndDontSave };
            var written = new List<string>();
            const int width = 1600, height = 900;

            try
            {
                var camera = host.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.Skybox;
                camera.nearClipPlane = 0.05f;
                camera.farClipPlane = 1000f;   // the XR camera's far clip
                camera.enabled = false;
                camera.allowMSAA = true;

                foreach (var view in Views())
                {
                    host.transform.SetPositionAndRotation(view.position,
                        Quaternion.LookRotation((view.lookAt - view.position).normalized, Vector3.up));
                    camera.fieldOfView = view.fieldOfView;

                    var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
                    var previous = RenderTexture.active;
                    camera.targetTexture = rt;
                    camera.Render();

                    RenderTexture.active = rt;
                    var image = new Texture2D(width, height, TextureFormat.RGB24, false);
                    image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    image.Apply();
                    RenderTexture.active = previous;
                    camera.targetTexture = null;

                    var path = Path.Combine(folder, view.name + ".png");
                    File.WriteAllBytes(path, image.EncodeToPNG());
                    written.Add(path);

                    Object.DestroyImmediate(image);
                    rt.Release();
                    Object.DestroyImmediate(rt);
                }
            }
            finally
            {
                Object.DestroyImmediate(host);
            }

            return $"[IKEA_EEG] Captured {written.Count} view(s) to {folder} (sky: {Describe(RenderSettings.skybox)})";
        }

        // =================================================================================
        // Colour helpers
        // =================================================================================

        static string Describe(Material m) => m == null ? "none" : m.name;

        static string Hex(Color c) => "#" + ColorUtility.ToHtmlStringRGB(c);

        static float Saturation(Color c)
        {
            Color.RGBToHSV(c, out _, out var s, out _);
            return s;
        }

        /// <summary>Relative-luminance contrast of two sRGB colours.</summary>
        static float Contrast(Color a, Color b)
        {
            float L(Color c) => 0.2126f * c.linear.r + 0.7152f * c.linear.g + 0.0722f * c.linear.b;
            var la = L(a);
            var lb = L(b);
            return (Mathf.Max(la, lb) + 0.05f) / (Mathf.Min(la, lb) + 0.05f);
        }
    }
}
