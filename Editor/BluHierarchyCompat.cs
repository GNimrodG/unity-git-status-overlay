using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GitStatusOverlay
{
    /// <summary>
    /// Layout of BluWizard LABS' Enhanced Hierarchy (net.bluwizard.hierarchy), mirrored without referencing it: it draws a
    /// tag pill right after the name and, from the right edge, the active toggle, an optional layer pill and one icon per
    /// component, stopping where the name (and tag pill) ends. The markers are placed around those.
    /// Matches BluHierarchy 1.14 (settings in EditorPrefs "BluHierarchy_*").
    /// </summary>
    internal static class BluHierarchyCompat
    {
        private const string TypeName = "BluWizard.Hierarchy.BluHierarchy";
        private const float IconSize = 16f;
        private const float IconStep = 18f;
        private const float TagGap = 6f;
        private const float PillPadding = 6f;
        private const float ToggleWidth = 18f;
        private const float ToggleOffset = 22f;
        private const float NoToggleOffset = 4f;

        private static bool? _present;
        private static GUIStyle _pillStyle;
        private static readonly GUIContent Scratch = new GUIContent();
        private static readonly List<Component> Components = new List<Component>();

        public static bool Present
        {
            get
            {
                _present ??= FindType() != null;
                return _present.Value;
            }
        }

        private static Type FindType()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(TypeName, false);
                if (t != null) return t;
            }
            return null;
        }

        private static bool Pref(string key, bool defaultValue) => EditorPrefs.GetBool("BluHierarchy_" + key, defaultValue);

        private static float PillWidth(string text)
        {
            _pillStyle ??= new GUIStyle(EditorStyles.label) { fontSize = 10, padding = new RectOffset(0, 0, 0, 0) };
            Scratch.text = text;
            return _pillStyle.CalcSize(Scratch).x + PillPadding * 2f;
        }

        private static bool HasTag(GameObject go)
        {
            var tag = go.tag;
            return !string.IsNullOrEmpty(tag) && tag != "Untagged";
        }

        /// <summary>Width BluHierarchy's tag pill takes right after the name (0 when it isn't drawn).</summary>
        public static float TagPillWidth(GameObject go)
        {
            if (!Present || go == null || !Pref("ShowTagNames", true) || !HasTag(go)) return 0f;
            return TagGap + PillWidth(go.tag);
        }

        /// <summary>Left edge of BluHierarchy's right-hand content (toggle, layer pill, component icons) on this row.</summary>
        public static float RightContentStart(GameObject go, Rect row)
        {
            if (!Present || go == null) return row.xMax;
            bool toggle = Pref("ShowGameObjectToggle", true);
            float left = toggle ? row.xMax - ToggleWidth : row.xMax;
            float x = row.xMax - IconSize - (toggle ? ToggleOffset : NoToggleOffset);

            if (Pref("ShowLayerNames", false) && !EditorApplication.isPlaying && go.layer != 0)
            {
                var layerName = LayerMask.LayerToName(go.layer);
                if (!string.IsNullOrEmpty(layerName))
                {
                    float pillX = x + IconSize - PillWidth(layerName);
                    left = Mathf.Min(left, pillX);
                    x = pillX - IconSize - 2f;
                }
            }

            // Icons stop where the name (or the tag pill after it) ends.
            Scratch.text = go.name;
            float nameWidth = EditorStyles.label.CalcSize(Scratch).x;
            float minX = row.x + nameWidth + 8f;
            if (Pref("ShowTagNames", true) && HasTag(go))
                minX = Mathf.Max(minX, row.x + IconSize + nameWidth + TagGap + PillWidth(go.tag) + 8f);

            if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go) > 0 && x >= minX)
            {
                left = Mathf.Min(left, x);
                x -= IconStep;
            }
            if (EditorApplication.isPlaying && !Pref("ShowIconsInPlayMode", false)) return left;

            bool showHidden = Pref("ShowHiddenComponents", false);
            bool showTransform = Pref("ShowTransformIcon", false);
            go.GetComponents(Components);
            foreach (var c in Components)
            {
                if (c == null) continue;
                if (!showHidden && (c.hideFlags & HideFlags.HideInInspector) != 0) continue;
                if (c is Transform && !showTransform) continue;
                if (x < minX) break;
                left = Mathf.Min(left, x);
                x -= IconStep;
            }
            Components.Clear();
            return left;
        }
    }
}
