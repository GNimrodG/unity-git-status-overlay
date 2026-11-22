using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace GitStatusOverlay
{
    /// <summary>
    /// Marks properties that changed vs HEAD in the Inspector: an amber bar in the left gutter (tooltip: old → new) and
    /// a git letter in each changed component's header (tooltip: all changed properties).
    /// IMGUI editors are covered through Unity's internal EditorGUIUtility.beginProperty callback (fired by every
    /// EditorGUI.BeginProperty / PropertyField); UI Toolkit editors (e.g. VRCFury) through their bound PropertyFields.
    /// </summary>
    [InitializeOnLoad]
    internal static class InspectorGitOverlay
    {
        private const string HeaderMarkName = "git-status-header-mark";
        private const string FieldMarkName = "git-status-field-mark";
        private const float GutterX = 3f;
        private const float GutterWidth = 2f;
        private const double ScanInterval = 0.3;
        private const double WindowListInterval = 2.0;

        private static readonly Type InspectorWindowType = typeof(EditorWindow).Assembly.GetType("UnityEditor.InspectorWindow");
        private static readonly GUIContent TipContent = new GUIContent();
        private static readonly List<EditorWindow> Inspectors = new List<EditorWindow>();
        private static PropertyInfo _editorElementEditor;
        private static double _nextScan;
        private static double _nextWindowList;
        private static int _paintedVersion = -1;

        /// <summary>False when this Unity version has no beginProperty hook (UI Toolkit fields and headers still work).</summary>
        public static bool ImguiHooked { get; private set; }

        static InspectorGitOverlay()
        {
            HookBeginProperty();
            EditorApplication.update += Scan;
        }

        private static void HookBeginProperty()
        {
            try
            {
                var ev = typeof(EditorGUIUtility).GetEvent("beginProperty", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var add = ev != null ? ev.GetAddMethod(true) : null;
                if (add == null) return;
                add.Invoke(null, new object[] { new Action<Rect, SerializedProperty>(OnBeginProperty) });
                ImguiHooked = true;
            }
            catch (TargetInvocationException e)
            {
                Debug.LogWarning("[GitStatusOverlay] Inspector property hook unavailable: " + e.GetBaseException().Message);
            }
            catch (ArgumentException e)
            {
                Debug.LogWarning("[GitStatusOverlay] Inspector property hook unavailable: " + e.Message);
            }
        }

        private static bool Dark => EditorGUIUtility.isProSkin;
        private static Color Modified => Dark ? new Color(0.92f, 0.72f, 0.25f) : new Color(0.75f, 0.5f, 0f);
        private static Color ModifiedInside => new Color(Modified.r, Modified.g, Modified.b, 0.45f);
        private static Color New => Dark ? new Color(0.45f, 0.82f, 0.38f) : new Color(0.15f, 0.55f, 0.1f);

        private static bool IsNew(PropertyMarks m) => m.State == GitState.Untracked || m.State == GitState.Added;

        // ---------------------------------------------------------------- IMGUI properties

        private static void OnBeginProperty(Rect rect, SerializedProperty prop)
        {
            if (!GitStatusSettings.InspectorGit || Event.current == null || Event.current.type != EventType.Repaint) return;
            var so = prop?.serializedObject;
            var target = so != null ? so.targetObject : null;
            if (target == null) return;
            PropertyMarks marks;
            if (!PropertyGitState.TryGet(target.GetInstanceID(), out marks)) return;

            Color color;
            string tip;
            if (IsNew(marks))
            {
                if (prop.depth > 0) return;
                color = New;
                tip = "U  new object (not in HEAD)";
            }
            else
            {
                PropChange hit;
                int level = marks.Match(prop.propertyPath, out hit);
                if (level == 0) return;
                color = level == 2 ? Modified : ModifiedInside;
                tip = (level == 2 ? "M  " : "M  changed inside: ") + PropertyMarks.Describe(hit);
            }
            EditorGUI.DrawRect(new Rect(GutterX, rect.y, GutterWidth, rect.height), color);
            TipContent.tooltip = tip;
            GUI.Label(new Rect(0f, rect.y, GutterX + GutterWidth + 3f, rect.height), TipContent);
        }

        // ---------------------------------------------------------------- headers + UI Toolkit fields

        private static void Scan()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now < _nextScan || InspectorWindowType == null) return;
            _nextScan = now + ScanInterval;
            if (now >= _nextWindowList)
            {
                _nextWindowList = now + WindowListInterval;
                Inspectors.Clear();
                foreach (var o in Resources.FindObjectsOfTypeAll(InspectorWindowType))
                {
                    var w = o as EditorWindow;
                    if (w != null) Inspectors.Add(w);
                }
            }
            bool repaint = _paintedVersion != PropertyGitState.Version;
            _paintedVersion = PropertyGitState.Version;
            foreach (var w in Inspectors)
            {
                if (w == null) continue;
                foreach (var ie in w.rootVisualElement.Query<InspectorElement>().ToList())
                {
                    var target = TargetOf(ie.parent);
                    PropertyMarks marks = null;
                    bool has = GitStatusSettings.InspectorGit && target != null && PropertyGitState.TryGet(target.GetInstanceID(), out marks);
                    UpdateHeader(ie.parent, target, has ? marks : null);
                    UpdateFields(ie, has ? marks : null);
                }
                if (repaint) w.Repaint();
            }
        }

        /// <summary>The inspected object of an EditorElement (internal type; its "editor" property via reflection).</summary>
        private static Object TargetOf(VisualElement editorElement)
        {
            if (editorElement == null) return null;
            if (_editorElementEditor == null || _editorElementEditor.DeclaringType != editorElement.GetType())
                _editorElementEditor = editorElement.GetType().GetProperty("editor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var editor = _editorElementEditor != null ? _editorElementEditor.GetValue(editorElement) as Editor : null;
            return editor != null ? editor.target : null;
        }

        private static void UpdateHeader(VisualElement editorElement, Object target, PropertyMarks marks)
        {
            if (editorElement == null) return;
            var label = editorElement.Q<Label>(HeaderMarkName);
            bool show = marks != null && (IsNew(marks) || marks.Props.Count > 0);
            if (!show)
            {
                label?.RemoveFromHierarchy();
                return;
            }
            if (label == null)
            {
                label = new Label { name = HeaderMarkName, pickingMode = PickingMode.Position };
                label.style.position = Position.Absolute;
                if (target is GameObject)
                {
                    // GameObject header: name field and Static toggle fill the top row; use the space under the icon.
                    label.style.left = 2;
                    label.style.top = 36;
                }
                else
                {
                    // Component / asset header: left of the help, preset and menu icons.
                    label.style.right = 64;
                    label.style.top = 3;
                }
                label.style.width = 16;
                label.style.height = 16;
                label.style.unityTextAlign = TextAnchor.MiddleCenter;
                label.style.unityFontStyleAndWeight = FontStyle.Bold;
                label.style.fontSize = 12;
                editorElement.Add(label);
            }
            label.text = IsNew(marks) ? "U" : "M";
            label.style.color = IsNew(marks) ? New : Modified;
            label.tooltip = marks.Tooltip;
        }

        private static void UpdateFields(InspectorElement inspector, PropertyMarks marks)
        {
            foreach (var pf in inspector.Query<PropertyField>().ToList())
            {
                var existing = pf.Q<VisualElement>(FieldMarkName);
                PropChange hit = null;
                bool mark = marks != null && !IsNew(marks) && !string.IsNullOrEmpty(pf.bindingPath)
                            && marks.Match(pf.bindingPath, out hit) == 2 && !HasMarkedAncestor(pf);
                if (!mark)
                {
                    existing?.RemoveFromHierarchy();
                    continue;
                }
                if (existing == null)
                {
                    existing = new VisualElement { name = FieldMarkName, pickingMode = PickingMode.Position };
                    existing.style.position = Position.Absolute;
                    existing.style.left = -6;
                    existing.style.top = 1;
                    existing.style.bottom = 1;
                    existing.style.width = GutterWidth;
                    pf.Add(existing);
                }
                existing.style.backgroundColor = Modified;
                existing.tooltip = "M  " + PropertyMarks.Describe(hit);
            }
        }

        private static bool HasMarkedAncestor(VisualElement element)
        {
            for (var p = element.parent; p != null; p = p.parent)
            {
                if (p is PropertyField && p.Q<VisualElement>(FieldMarkName) is VisualElement m && m.parent == p) return true;
            }
            return false;
        }
    }
}
