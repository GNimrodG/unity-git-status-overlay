using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace GitStatusOverlayShowcase
{
    /// <summary>
    /// Builds the README showcase: a small project with a committed baseline, then typical working-copy changes, then
    /// screenshots of the Project, Hierarchy and Inspector windows with Git Status Overlay installed.
    /// Run with build-showcase.sh (see README.md next to this file); each step is also in the Showcase menu.
    /// </summary>
    public static class Showcase
    {
        private const string ScenePath = "Assets/Scenes/Showcase.unity";
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        // ---------------------------------------------------------------- step 1: baseline (committed afterwards)

        [MenuItem("Showcase/1. Build baseline")]
        public static void Baseline()
        {
            foreach (var d in new[] { "Materials", "Prefabs", "Scenes", "Old" }) Directory.CreateDirectory("Assets/" + d);
            AssetDatabase.Refresh();
            var ground = Mat("Assets/Materials/Ground.mat", new Color(0.36f, 0.42f, 0.36f));
            var red = Mat("Assets/Materials/Red.mat", new Color(0.8f, 0.22f, 0.2f));
            var blue = Mat("Assets/Materials/Blue.mat", new Color(0.22f, 0.42f, 0.85f));
            Mat("Assets/Old/Legacy.mat", Color.gray);
            Mat("Assets/Old/Legacy Metal.mat", Color.white);
            File.WriteAllText("Assets/Old/Notes.txt", "Old notes.\n");
            AssetDatabase.ImportAsset("Assets/Old/Notes.txt");

            var player = new GameObject("Player");
            Prim(PrimitiveType.Capsule, "Body", player.transform, new Vector3(0, 1, 0), Vector3.one, red);
            Prim(PrimitiveType.Sphere, "Head", player.transform, new Vector3(0, 2.2f, 0), Vector3.one * 0.6f, red);
            Prim(PrimitiveType.Cube, "Weapon", player.transform, new Vector3(0.6f, 1.2f, 0.3f), new Vector3(0.1f, 0.1f, 0.8f), blue);
            var playerPrefab = PrefabUtility.SaveAsPrefabAsset(player, "Assets/Prefabs/Player.prefab");
            Object.DestroyImmediate(player);

            var crate = Prim(PrimitiveType.Cube, "Crate", null, Vector3.zero, Vector3.one, blue);
            var cratePrefab = PrefabUtility.SaveAsPrefabAsset(crate, "Assets/Prefabs/Crate.prefab");
            Object.DestroyImmediate(crate);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var env = new GameObject("Environment");
            Prim(PrimitiveType.Plane, "Ground", env.transform, Vector3.zero, Vector3.one * 3, ground);
            Prim(PrimitiveType.Cube, "Old Sign", env.transform, new Vector3(-3, 0.5f, 2), Vector3.one, blue);
            PrefabUtility.InstantiatePrefab(playerPrefab);
            var props = new GameObject("Props");
            for (int i = 1; i <= 3; i++)
            {
                var c = (GameObject)PrefabUtility.InstantiatePrefab(cratePrefab, props.transform);
                c.name = "Crate (" + i + ")";
                c.transform.localPosition = new Vector3(1.5f + i * 1.3f, 0.5f, 1.5f);
            }
            // An override that is already committed: shows the override glyph but no git letter.
            GameObject.Find("Props/Crate (2)").transform.localRotation = Quaternion.Euler(0, 25, 0);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
        }

        // ---------------------------------------------------------------- step 2: working-copy changes

        [MenuItem("Showcase/2. Apply changes")]
        public static void Changes()
        {
            var red = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/Red.mat");
            red.color = new Color(0.9f, 0.3f, 0.18f);
            red.SetFloat("_Glossiness", 0.8f);
            EditorUtility.SetDirty(red);
            var green = Mat("Assets/Materials/Green.mat", new Color(0.3f, 0.8f, 0.35f));
            FixRenamedMaterial();
            AssetDatabase.DeleteAsset("Assets/Old");

            // Prefab asset edit: a new child in Player.prefab.
            var contents = PrefabUtility.LoadPrefabContents("Assets/Prefabs/Player.prefab");
            Prim(PrimitiveType.Cube, "Backpack", contents.transform, new Vector3(0, 1.4f, -0.45f), new Vector3(0.6f, 0.7f, 0.3f),
                AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/Ground.mat"));
            PrefabUtility.SaveAsPrefabAsset(contents, "Assets/Prefabs/Player.prefab");
            PrefabUtility.UnloadPrefabContents(contents);

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var light = GameObject.Find("Directional Light").GetComponent<Light>();
            light.intensity = 1.35f;
            light.color = new Color(1f, 0.92f, 0.8f);
            var player = GameObject.Find("Player");
            player.transform.position = new Vector3(-1, 0, 0);
            var weapon = player.transform.Find("Weapon");
            weapon.localPosition = new Vector3(0.7f, 1.35f, 0.5f);
            weapon.localRotation = Quaternion.Euler(-20, 0, 0);
            player.transform.Find("Head").gameObject.AddComponent(typeof(Spinner));
            GameObject.Find("Props/Crate (3)").transform.localScale = Vector3.one * 1.4f;
            Object.DestroyImmediate(GameObject.Find("Environment/Old Sign"));
            Prim(PrimitiveType.Sphere, "Pickup", null, new Vector3(1.5f, 0.5f, -1.5f), Vector3.one * 0.5f, green);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
        }

        /// <summary>Blue.mat was renamed with git mv: give the material the new name too, as Unity's own rename would.</summary>
        public static void FixRenamedMaterial()
        {
            var renamed = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/Crate Blue.mat");
            if (renamed == null || renamed.name == "Crate Blue") return;
            renamed.name = "Crate Blue";
            EditorUtility.SetDirty(renamed);
            AssetDatabase.SaveAssets();
        }

        // ---------------------------------------------------------------- step 3: screenshots (GUI mode, exits Unity)

        private static readonly List<KeyValuePair<double, Action>> Steps = new List<KeyValuePair<double, Action>>();
        private static double _next;
        private static int _index;
        private static double _deadline;

        [MenuItem("Showcase/3. Capture screenshots")]
        public static void Capture() => Capture(false);

        public static void CaptureAndQuit() => Capture(true);

        private static void Capture(bool quit)
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var outDir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Screenshots");
            Directory.CreateDirectory(outDir);
            EditorWindow hierarchy = null, project = null, inspector = null;

            Steps.Clear();
            Add(0.5, () => _deadline = EditorApplication.timeSinceStartup + 90);
            Add(0.5, WaitForOverlay);
            Add(0.2, () =>
            {
                // An unsaved edit, to show the "saved state" hint on the scene row.
                var pickup = GameObject.Find("Pickup");
                Undo.RecordObject(pickup.transform, "Nudge");
                pickup.transform.position += new Vector3(0, 0.01f, 0);
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
                // Keep the Inspector shots short: fold everything except what changed.
                foreach (var path in new[] { "Player/Weapon", "Player/Head", "Props/Crate (3)", "Directional Light" })
                {
                    foreach (var c in GameObject.Find(path).GetComponents<Component>())
                    {
                        bool keep = c is Transform || c is Light || c.GetType().Name == "Spinner";
                        UnityEditorInternal.InternalEditorUtility.SetIsInspectorExpanded(c, keep);
                        var r = c as Renderer;
                        if (r != null)
                            foreach (var m in r.sharedMaterials) UnityEditorInternal.InternalEditorUtility.SetIsInspectorExpanded(m, false);
                    }
                }
            });

            Add(0.5, () => project = Float("UnityEditor.ProjectBrowser", new Rect(60, 60, 400, 300)));
            // Only once the window has initialized: switch to the one-column (tree) layout.
            Add(0.5, () => Call(project, "SetOneColumn"));
            Add(0.5, () => ExpandFolders(project, "Assets", "Assets/Materials", "Assets/Prefabs", "Assets/Scenes"));
            Add(2.0, () => GrabWhenDrawn(project, Path.Combine(outDir, "project.png")));
            Add(0.2, () => project.Close());

            Add(0.5, () => hierarchy = Float("UnityEditor.SceneHierarchyWindow", new Rect(60, 60, 400, 330)));
            Add(0.5, () =>
            {
                var expand = hierarchy.GetType().GetMethod("SetExpandedRecursive", Any, null, new[] { typeof(int), typeof(bool) }, null);
                foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
                    expand?.Invoke(hierarchy, new object[] { root.GetInstanceID(), true });
            });

            Add(0.5, () => inspector = Float("UnityEditor.InspectorWindow", new Rect(480, 60, 440, 420)));
            foreach (var shot in new[] { "Directional Light", "Player/Weapon", "Player/Head" })
            {
                var name = shot;
                var file = "inspector-" + name.Substring(name.LastIndexOf('/') + 1).Replace(" ", "-").Replace("(", "").Replace(")", "").ToLowerInvariant() + ".png";
                Add(0.3, () => Selection.activeGameObject = GameObject.Find(name));
                Add(2.0, () => GrabWhenDrawn(inspector, Path.Combine(outDir, file)));
            }
            Add(0.3, () => Selection.activeObject = null);
            Add(2.0, () => GrabWhenDrawn(hierarchy, Path.Combine(outDir, "hierarchy.png")));
            Add(0.5, () =>
            {
                Debug.Log("[Showcase] screenshots written to " + outDir);
                if (quit) EditorApplication.Exit(0);
            });

            _index = 0;
            _next = EditorApplication.timeSinceStartup + 3;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        private static void Add(double delay, Action a) => Steps.Add(new KeyValuePair<double, Action>(delay, a));

        private static void Tick()
        {
            if (EditorApplication.timeSinceStartup < _next) return;
            if (_index >= Steps.Count)
            {
                EditorApplication.update -= Tick;
                return;
            }
            var step = Steps[_index];
            try
            {
                step.Value();
            }
            catch (RetryException)
            {
                _next = EditorApplication.timeSinceStartup + 0.5;
                return;
            }
            catch (Exception e)
            {
                Debug.LogError("[Showcase] step " + _index + " failed: " + e);
            }
            _index++;
            _next = EditorApplication.timeSinceStartup + (_index < Steps.Count ? Steps[_index].Key : 0);
        }

        public sealed class RetryException : Exception
        {
        }

        /// <summary>Waits until Git Status Overlay has run git, diffed the scene and found the prefab overrides.</summary>
        private static void WaitForOverlay()
        {
            if (EditorApplication.timeSinceStartup > _deadline) throw new TimeoutException("overlay state not ready");
            Assembly asm = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == "GitStatusOverlay.Editor") asm = a;
            if (asm == null) throw new RetryException();
            var objects = asm.GetType("GitStatusOverlay.HierarchyGitState").GetField("Objects", Any).GetValue(null) as System.Collections.IDictionary;
            var overrides = asm.GetType("GitStatusOverlay.PrefabOverrideCache").GetField("Marks", Any).GetValue(null) as System.Collections.IDictionary;
            if (objects == null || objects.Count == 0 || overrides == null || overrides.Count == 0) throw new RetryException();
        }

        // ---------------------------------------------------------------- helpers

        private static Material Mat(string path, Color color)
        {
            var m = new Material(Shader.Find("Standard")) { color = color };
            m.SetFloat("_Glossiness", 0.3f);
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        private static GameObject Prim(PrimitiveType type, string name, Transform parent, Vector3 pos, Vector3 scale, Material mat)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            if (parent != null) go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            if (mat != null) go.GetComponent<Renderer>().sharedMaterial = mat;
            return go;
        }

        private static EditorWindow Float(string typeName, Rect rect)
        {
            var type = typeof(Editor).Assembly.GetType(typeName);
            var w = (EditorWindow)ScriptableObject.CreateInstance(type);
            w.Show();
            w.position = rect;
            return w;
        }

        /// <summary>Expands folders in the one-column Project tree (its tree view's data source keys rows by instance id).</summary>
        private static void ExpandFolders(EditorWindow project, params string[] folders)
        {
            var tree = project.GetType().GetField("m_AssetTree", Any)?.GetValue(project);
            var data = tree?.GetType().GetProperty("data", Any)?.GetValue(tree);
            if (data == null) return;
            foreach (var iface in data.GetType().GetInterfaces())
            {
                var setExpanded = iface.GetMethod("SetExpanded", new[] { typeof(int), typeof(bool) });
                if (setExpanded == null) continue;
                foreach (var f in folders)
                {
                    var folder = AssetDatabase.LoadMainAssetAtPath(f);
                    if (folder != null) setExpanded.Invoke(data, new object[] { folder.GetInstanceID(), true });
                }
                break;
            }
            project.Repaint();
        }

        private static int _grabAttempts;

        /// <summary>
        /// A freshly opened window can still be empty (UI built lazily): if the capture is one flat colour, rebuild
        /// and repaint, then try again on a later tick.
        /// </summary>
        private static void GrabWhenDrawn(EditorWindow w, string file)
        {
            if (Grab(w, file) || ++_grabAttempts > 10)
            {
                _grabAttempts = 0;
                return;
            }
            ActiveEditorTracker.sharedTracker.ForceRebuild();
            w.Repaint();
            throw new RetryException();
        }

        private static void Call(object target, string method)
        {
            var m = target.GetType().GetMethod(method, Any, null, Type.EmptyTypes, null);
            if (m != null) m.Invoke(target, null);
        }

        /// <summary>Renders the window's own backbuffer to a PNG (works even when other apps cover Unity).</summary>
        private static bool Grab(EditorWindow w, string file)
        {
            var parent = typeof(EditorWindow).GetField("m_Parent", Any).GetValue(w);
            var repaint = parent.GetType().GetMethod("RepaintImmediately", Any, null, Type.EmptyTypes, null);
            for (int i = 0; i < 3; i++) repaint.Invoke(parent, null);
            float ppp = EditorGUIUtility.pixelsPerPoint;
            int width = Mathf.RoundToInt(w.position.width * ppp), height = Mathf.RoundToInt(w.position.height * ppp);
            var rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
            parent.GetType().GetMethod("GrabPixels", Any).Invoke(parent, new object[] { rt, new Rect(0, 0, width, height) });
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            var px = tex.GetPixels32();
            bool drawn = false;
            for (int i = 1; i < px.Length && !drawn; i += 97) drawn = !px[i].Equals(px[0]);
            var flipped = new Color32[px.Length];
            for (int y = 0; y < height; y++) Array.Copy(px, y * width, flipped, (height - 1 - y) * width, width);
            tex.SetPixels32(flipped);
            File.WriteAllBytes(file, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            rt.Release();
            Object.DestroyImmediate(rt);
            return drawn;
        }
    }
}
