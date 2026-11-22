using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GitStatusOverlay
{
    /// <summary>Per-user settings (EditorPrefs), a Preferences page and the Tools/Git Status menu.</summary>
    internal static class GitStatusSettings
    {
        private const string Prefix = "GitStatusOverlay.";
        private const string MenuRoot = "Tools/Git Status/";
        private const string MenuProject = MenuRoot + "Project Window Overlay";
        private const string MenuHierarchy = MenuRoot + "Hierarchy Git Overlay";
        private const string MenuOverrides = MenuRoot + "Prefab Override Overlay";
        private const string MenuInspector = MenuRoot + "Inspector Changed Properties";

        public static bool ProjectOverlay
        {
            get => EditorPrefs.GetBool(Prefix + "Project", true);
            set { EditorPrefs.SetBool(Prefix + "Project", value); Changed(); }
        }

        public static bool HierarchyGit
        {
            get => EditorPrefs.GetBool(Prefix + "HierarchyGit", true);
            set { EditorPrefs.SetBool(Prefix + "HierarchyGit", value); Changed(); }
        }

        public static bool PrefabOverrides
        {
            get => EditorPrefs.GetBool(Prefix + "PrefabOverrides", true);
            set { EditorPrefs.SetBool(Prefix + "PrefabOverrides", value); Changed(); }
        }

        public static bool InspectorGit
        {
            get => EditorPrefs.GetBool(Prefix + "InspectorGit", true);
            set { EditorPrefs.SetBool(Prefix + "InspectorGit", value); Changed(); PropertyGitState.RequestUpdate(); }
        }

        /// <summary>Seconds between background refreshes while the editor has focus.</summary>
        public static float RefreshInterval
        {
            get => EditorPrefs.GetFloat(Prefix + "Interval", 10f);
            set => EditorPrefs.SetFloat(Prefix + "Interval", Mathf.Max(2f, value));
        }

        /// <summary>Pixels to keep free at the right edge of Hierarchy rows (for other hierarchy extensions).</summary>
        public static float HierarchyRightOffset
        {
            get => EditorPrefs.GetFloat(Prefix + "HierarchyOffset", 0f);
            set { EditorPrefs.SetFloat(Prefix + "HierarchyOffset", Mathf.Max(0f, value)); EditorApplication.RepaintHierarchyWindow(); }
        }

        /// <summary>
        /// Draw markers right after the item name (default; avoids other hierarchy extensions that use the right edge)
        /// instead of at the right edge of the row.
        /// </summary>
        public static bool MarkersAfterName
        {
            get => EditorPrefs.GetBool(Prefix + "AfterName", true);
            set { EditorPrefs.SetBool(Prefix + "AfterName", value); EditorApplication.RepaintHierarchyWindow(); EditorApplication.RepaintProjectWindow(); }
        }

        public static int MaxTooltipLines
        {
            get => EditorPrefs.GetInt(Prefix + "MaxLines", 15);
            set => EditorPrefs.SetInt(Prefix + "MaxLines", Mathf.Clamp(value, 3, 100));
        }

        private static void Changed()
        {
            GitStatusService.RequestRefresh();
            PrefabOverrideCache.SetDirty();
            EditorApplication.RepaintProjectWindow();
            EditorApplication.RepaintHierarchyWindow();
        }

        [MenuItem(MenuRoot + "Refresh Now", false, 0)]
        private static void RefreshNow() => GitStatusService.RequestRefresh(true);

        [MenuItem(MenuProject, false, 20)]
        private static void ToggleProject() => ProjectOverlay = !ProjectOverlay;

        [MenuItem(MenuProject, true)]
        private static bool ToggleProjectValidate()
        {
            Menu.SetChecked(MenuProject, ProjectOverlay);
            return true;
        }

        [MenuItem(MenuHierarchy, false, 21)]
        private static void ToggleHierarchy() => HierarchyGit = !HierarchyGit;

        [MenuItem(MenuHierarchy, true)]
        private static bool ToggleHierarchyValidate()
        {
            Menu.SetChecked(MenuHierarchy, HierarchyGit);
            return true;
        }

        [MenuItem(MenuOverrides, false, 22)]
        private static void ToggleOverrides() => PrefabOverrides = !PrefabOverrides;

        [MenuItem(MenuOverrides, true)]
        private static bool ToggleOverridesValidate()
        {
            Menu.SetChecked(MenuOverrides, PrefabOverrides);
            return true;
        }

        [MenuItem(MenuInspector, false, 23)]
        private static void ToggleInspector() => InspectorGit = !InspectorGit;

        [MenuItem(MenuInspector, true)]
        private static bool ToggleInspectorValidate()
        {
            Menu.SetChecked(MenuInspector, InspectorGit);
            return true;
        }

        [MenuItem(MenuRoot + "Preferences…", false, 40)]
        private static void OpenPrefs() => SettingsService.OpenUserPreferences("Preferences/Git Status Overlay");

        [SettingsProvider]
        private static SettingsProvider CreateProvider()
        {
            return new SettingsProvider("Preferences/Git Status Overlay", SettingsScope.User)
            {
                keywords = new HashSet<string>(new[] { "git", "status", "overlay", "prefab", "override", "hierarchy" }),
                guiHandler = _ =>
                {
                    EditorGUIUtility.labelWidth = 230f;
                    bool v = EditorGUILayout.Toggle("Project window: git letters", ProjectOverlay);
                    if (v != ProjectOverlay) ProjectOverlay = v;
                    v = EditorGUILayout.Toggle("Hierarchy: git changes in scenes", HierarchyGit);
                    if (v != HierarchyGit) HierarchyGit = v;
                    v = EditorGUILayout.Toggle("Hierarchy: prefab overrides", PrefabOverrides);
                    if (v != PrefabOverrides) PrefabOverrides = v;
                    v = EditorGUILayout.Toggle("Inspector: changed properties", InspectorGit);
                    if (v != InspectorGit) InspectorGit = v;
                    EditorGUILayout.Space();
                    var interval = EditorGUILayout.Slider("Refresh interval (s)", RefreshInterval, 2f, 120f);
                    if (!Mathf.Approximately(interval, RefreshInterval)) RefreshInterval = interval;
                    v = EditorGUILayout.Toggle("Markers right after the name", MarkersAfterName);
                    if (v != MarkersAfterName) MarkersAfterName = v;
                    var offset = EditorGUILayout.Slider("Right-edge mode: margin (px)", HierarchyRightOffset, 0f, 120f);
                    if (!Mathf.Approximately(offset, HierarchyRightOffset)) HierarchyRightOffset = offset;
                    var lines = EditorGUILayout.IntSlider("Max tooltip lines", MaxTooltipLines, 3, 100);
                    if (lines != MaxTooltipLines) { MaxTooltipLines = lines; PrefabOverrideCache.SetDirty(); }
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Status", GitStatusService.StatusLine, EditorStyles.wordWrappedLabel);
                    if (GUILayout.Button("Refresh now", GUILayout.Width(120))) GitStatusService.RequestRefresh(true);
                },
            };
        }
    }
}
