using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace GitStatusOverlay
{
    internal sealed class OverrideMark
    {
        public bool Properties;
        public bool AddedComponents;
        public bool RemovedComponents;
        public bool AddedGameObject;
        /// <summary>Instance root summary: number of objects with overrides inside this instance.</summary>
        public int OverriddenInInstance;
        public readonly List<string> Lines = new List<string>();
        public int ExtraLines;
        public string Tooltip;

        public bool HasOwn => Properties || AddedComponents || RemovedComponents || AddedGameObject;

        public void Line(string s)
        {
            if (Lines.Count < GitStatusSettings.MaxTooltipLines) Lines.Add(s);
            else ExtraLines++;
        }
    }

    /// <summary>Prefab overrides (vs the source prefab) of every prefab instance in the open scenes / prefab stage.</summary>
    internal static class PrefabOverrideCache
    {
        public static readonly Dictionary<int, OverrideMark> Marks = new Dictionary<int, OverrideMark>();
        private static bool _dirty = true;
        private static double _dirtySince;

        public static void SetDirty()
        {
            if (!_dirty) _dirtySince = EditorApplication.timeSinceStartup;
            _dirty = true;
        }

        public static void Tick()
        {
            // Debounce: rebuild 0.5 s after the last hierarchy/property change.
            if (!_dirty || EditorApplication.timeSinceStartup - _dirtySince < 0.5) return;
            _dirty = false;
            Rebuild();
            EditorApplication.RepaintHierarchyWindow();
        }

        private static void Rebuild()
        {
            Marks.Clear();
            if (!GitStatusSettings.PrefabOverrides) return;
            var scenes = new List<Scene>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.isLoaded) scenes.Add(s);
            }
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null) scenes.Add(stage.scene);

            foreach (var scene in scenes)
            {
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var go in root.GetComponentsInChildren<Transform>(true).Select(t => t.gameObject))
                    {
                        if (PrefabUtility.IsOutermostPrefabInstanceRoot(go)) CollectInstance(go);
                    }
                }
            }
            foreach (var m in Marks.Values) m.Tooltip = BuildTooltip(m);
        }

        private static OverrideMark Get(GameObject go)
        {
            OverrideMark m;
            int id = go.GetInstanceID();
            if (!Marks.TryGetValue(id, out m)) Marks[id] = m = new OverrideMark();
            return m;
        }

        private static void CollectInstance(GameObject root)
        {
            var source = PrefabUtility.GetCorrespondingObjectFromSource(root);
            var assetPath = source != null ? AssetDatabase.GetAssetPath(source) : null;
            var touched = new HashSet<int>();

            // Property overrides, grouped by the source object they target.
            var byTarget = new Dictionary<Object, List<string>>();
            var mods = PrefabUtility.GetPropertyModifications(root);
            if (mods != null)
            {
                foreach (var mod in mods)
                {
                    if (mod == null || mod.target == null || PrefabUtility.IsDefaultOverride(mod)) continue;
                    List<string> list;
                    if (!byTarget.TryGetValue(mod.target, out list)) byTarget[mod.target] = list = new List<string>();
                    if (!list.Contains(mod.propertyPath)) list.Add(mod.propertyPath);
                }
            }
            foreach (var obj in PrefabUtility.GetObjectOverrides(root, false).Select(ov => ov.instanceObject))
            {
                var go = AsGameObject(obj);
                if (go == null) continue;
                var m = Get(go);
                m.Properties = true;
                touched.Add(go.GetInstanceID());
                Object src = string.IsNullOrEmpty(assetPath) ? null : PrefabUtility.GetCorrespondingObjectFromSourceAtPath(obj, assetPath);
                List<string> props;
                if (src != null && byTarget.TryGetValue(src, out props))
                {
                    var shown = props.Take(6).ToList();
                    m.Line("◆ " + obj.GetType().Name + ": " + string.Join(", ", shown) + (props.Count > shown.Count ? ", … (+" + (props.Count - shown.Count) + ")" : ""));
                }
                else
                {
                    m.Line("◆ " + obj.GetType().Name + " overridden");
                }
            }
            foreach (var comp in PrefabUtility.GetAddedComponents(root).Select(ac => ac.instanceComponent).Where(c => c != null))
            {
                var m = Get(comp.gameObject);
                m.AddedComponents = true;
                m.Line("+ " + comp.GetType().Name + " (added component)");
                touched.Add(comp.gameObject.GetInstanceID());
            }
            foreach (var rc in PrefabUtility.GetRemovedComponents(root))
            {
                if (rc.containingInstanceGameObject == null) continue;
                var m = Get(rc.containingInstanceGameObject);
                m.RemovedComponents = true;
                m.Line("− " + (rc.assetComponent != null ? rc.assetComponent.GetType().Name : "component") + " (removed component)");
                touched.Add(rc.containingInstanceGameObject.GetInstanceID());
            }
            foreach (var added in PrefabUtility.GetAddedGameObjects(root).Select(ag => ag.instanceGameObject).Where(g => g != null))
            {
                var m = Get(added);
                m.AddedGameObject = true;
                m.Line("+ added GameObject (not in the source prefab)");
                touched.Add(added.GetInstanceID());
            }
            if (touched.Count > 0)
            {
                var rm = Get(root);
                rm.OverriddenInInstance = touched.Count;
            }
        }

        private static GameObject AsGameObject(Object o)
        {
            var go = o as GameObject;
            if (go != null) return go;
            var c = o as Component;
            return c != null ? c.gameObject : null;
        }

        private static string BuildTooltip(OverrideMark m)
        {
            var sb = new StringBuilder("Prefab overrides");
            if (m.OverriddenInInstance > 0) sb.Append(" (").Append(m.OverriddenInInstance).Append(" objects in this instance)");
            foreach (var l in m.Lines) sb.Append('\n').Append(l);
            if (m.ExtraLines > 0) sb.Append("\n… ").Append(m.ExtraLines).Append(" more");
            return sb.ToString();
        }
    }
}
