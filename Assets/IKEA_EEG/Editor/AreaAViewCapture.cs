using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace IkeaEeg.EditorTools
{
    /// <summary>
    /// Renders still images of Area A from fixed viewpoints, for visual review without a
    /// headset.
    ///
    /// READ-ONLY. It creates a throwaway camera, renders to a RenderTexture, writes PNGs
    /// outside the Assets folder and destroys the camera again. It never marks the scene
    /// dirty and never saves it, so running it cannot change the project.
    ///
    /// It captures the EDIT-MODE scene. The Area A UI text is written by ExperimentManager at
    /// run time, so these images show the environment and the UI panel as authored — they are
    /// not a substitute for verifying the live protocol in the headset.
    /// </summary>
    public static class AreaAViewCapture
    {
        // The participant's eye at Spawn_A: the spawn mark is (0, 0, -0.7) and the rig places
        // the head above it at the participant's own standing height. 1.60 m is the value the
        // scene builder's own reach checks assume.
        static readonly Vector3 k_SpawnEye = new Vector3(0f, 1.60f, -0.70f);

        struct View
        {
            public string name;
            public Vector3 position;
            public Vector3 lookAt;
            public float fieldOfView;
            public int width, height;
        }

        static IEnumerable<View> Views()
        {
            yield return new View
            {
                // Framed to approximate what the headset actually shows from the spawn mark:
                // a ~90 deg horizontal field, which reaches the sign band and the sky above it.
                name = "01_AreaA_FromSpawn_Eye",
                position = k_SpawnEye,
                lookAt = new Vector3(0f, 1.90f, 2.0f),
                fieldOfView = 84f, width = 1600, height = 900,
            };

            yield return new View
            {
                name = "02_AreaA_FromSpawn_Wide",
                position = k_SpawnEye,
                lookAt = new Vector3(0f, 1.75f, 2.0f),
                fieldOfView = 96f, width = 1600, height = 900,
            };

            yield return new View
            {
                name = "03_AreaA_FromSpawn_LookingUp",
                position = k_SpawnEye,
                lookAt = new Vector3(0f, 3.30f, 2.0f),
                fieldOfView = 80f, width = 1600, height = 900,
            };

            yield return new View
            {
                name = "04_AreaA_ThreeQuarter",
                position = new Vector3(-1.70f, 1.60f, -1.60f),
                lookAt = new Vector3(0.20f, 1.40f, 1.95f),
                fieldOfView = 70f, width = 1600, height = 900,
            };

            yield return new View
            {
                name = "05_AreaA_DoorDetail",
                position = new Vector3(0f, 1.35f, 0.55f),
                lookAt = new Vector3(0f, 1.30f, 2.0f),
                fieldOfView = 62f, width = 1600, height = 1100,
            };

            yield return new View
            {
                name = "06_AreaA_SignDetail",
                position = new Vector3(0f, 2.30f, -0.20f),
                lookAt = new Vector3(0f, 3.32f, 1.95f),
                fieldOfView = 62f, width = 1800, height = 800,
            };

            // Scene-view style overview, from outside and above the vestibule.
            yield return new View
            {
                name = "07_AreaA_Overview",
                position = new Vector3(-4.60f, 4.40f, -4.60f),
                lookAt = new Vector3(0f, 1.40f, 1.90f),
                fieldOfView = 55f, width = 1600, height = 1000,
            };

            // Straight down the UI plane, to show the panel standing clear in front of the doors.
            yield return new View
            {
                name = "08_AreaA_UIClearance",
                position = new Vector3(-2.05f, 1.60f, 0.30f),
                lookAt = new Vector3(0.30f, 1.55f, 1.60f),
                fieldOfView = 68f, width = 1600, height = 900,
            };

            // The two sides the graybox walls used to close off. Taken from the spawn eye so
            // they show what the participant actually sees when they turn their head — and
            // therefore whether the other experiment areas are still visible on the horizon.
            yield return new View
            {
                name = "09_AreaA_LateralLeft_Open",
                position = k_SpawnEye,
                lookAt = new Vector3(-7.0f, 1.55f, -1.60f),
                fieldOfView = 80f, width = 1600, height = 900,
            };

            yield return new View
            {
                name = "10_AreaA_LateralRight_Open",
                position = k_SpawnEye,
                lookAt = new Vector3(7.0f, 1.55f, -1.60f),
                fieldOfView = 80f, width = 1600, height = 900,
            };

            // The whole 28 m storefront, from far enough back that it fits in frame.
            yield return new View
            {
                name = "12_AreaA_FullStorefront",
                position = new Vector3(0f, 1.75f, -19.0f),
                lookAt = new Vector3(0f, 2.60f, 1.9f),
                fieldOfView = 52f, width = 1920, height = 1080,
            };

            // The zone sequence: plaza, kerb, gutter, asphalt and its markings.
            yield return new View
            {
                name = "13_AreaA_PlazaKerbRoad",
                position = new Vector3(-4.60f, 2.55f, -13.5f),
                lookAt = new Vector3(1.20f, 0.35f, -3.0f),
                fieldOfView = 58f, width = 1920, height = 1080,
            };

            // The two facade sample panels side by side: current flat blue on the LEFT,
            // the new textured metal-panel material on the RIGHT, under identical light.
            yield return new View
            {
                name = "21_AreaA_MaterialSample_Comparison",
                position = new Vector3(6.35f, 1.80f, -5.40f),
                lookAt = new Vector3(6.35f, 1.80f, 1.80f),
                fieldOfView = 62f, width = 1920, height = 1080,
            };

            // Closer, at a grazing angle, so the normal-map relief and the gloss response are
            // actually judgeable rather than flattened by a head-on view.
            yield return new View
            {
                name = "22_AreaA_MaterialSample_Grazing",
                position = new Vector3(2.60f, 1.70f, -2.20f),
                lookAt = new Vector3(8.60f, 1.60f, 1.80f),
                fieldOfView = 55f, width = 1920, height = 1080,
            };

            // Three-quarter pair, from outside the plaza looking back at the entrance.
            yield return new View
            {
                name = "14_AreaA_ThreeQuarter_Left",
                position = new Vector3(-11.0f, 2.10f, -9.0f),
                lookAt = new Vector3(0f, 2.20f, 1.9f),
                fieldOfView = 56f, width = 1920, height = 1080,
            };

            yield return new View
            {
                name = "15_AreaA_ThreeQuarter_Right",
                position = new Vector3(11.0f, 2.10f, -9.0f),
                lookAt = new Vector3(0f, 2.20f, 1.9f),
                fieldOfView = 56f, width = 1920, height = 1080,
            };
        }

        [MenuItem("IKEA_EEG/Visuals/Capture Area A Views", false, 202)]
        public static void CaptureMenu()
        {
            var folder = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".",
                "Presentation_Evidence", "AreaA_Static");
            Debug.Log(Capture(folder));
        }

        /// <summary>
        /// Batch entry point. Opens the experiment scene, renders every view, quits WITHOUT
        /// saving. An output folder may be passed as the last command-line argument.
        /// </summary>
        public static void CaptureFromCommandLine()
        {
            EditorSceneManager.OpenScene(ExperimentSceneBuilder.ScenePath, OpenSceneMode.Single);

            var args = System.Environment.GetCommandLineArgs();
            var folder = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".",
                "Presentation_Evidence", "AreaA_Static");

            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-captureOutput")
                    folder = args[i + 1];
            }

            Debug.Log(Capture(folder));
        }

        public static string Capture(string outputFolder)
        {
            Directory.CreateDirectory(outputFolder);

            var host = new GameObject("~AreaAViewCapture")
            {
                hideFlags = HideFlags.HideAndDontSave,
            };

            var written = new List<string>();

            try
            {
                var camera = host.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.Skybox;
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = 200f;
                camera.enabled = false;          // rendered explicitly, never per-frame
                camera.allowMSAA = true;

                foreach (var view in Views())
                {
                    host.transform.position = view.position;
                    host.transform.rotation = Quaternion.LookRotation(
                        (view.lookAt - view.position).normalized, Vector3.up);

                    camera.fieldOfView = view.fieldOfView;

                    var rt = new RenderTexture(view.width, view.height, 24,
                        RenderTextureFormat.ARGB32)
                    {
                        antiAliasing = 4,
                    };

                    var previous = RenderTexture.active;
                    camera.targetTexture = rt;
                    camera.Render();

                    RenderTexture.active = rt;
                    var image = new Texture2D(view.width, view.height, TextureFormat.RGB24, false);
                    image.ReadPixels(new Rect(0, 0, view.width, view.height), 0, 0);
                    image.Apply();

                    RenderTexture.active = previous;
                    camera.targetTexture = null;

                    var path = Path.Combine(outputFolder, view.name + ".png");
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

            return $"[IKEA_EEG] Captured {written.Count} Area A view(s) to {outputFolder}\n" +
                   string.Join("\n", written.Select(p => "  " + Path.GetFileName(p)));
        }
    }
}
