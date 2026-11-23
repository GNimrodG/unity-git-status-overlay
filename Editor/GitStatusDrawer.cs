using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GitStatusOverlay
{
    /// <summary>Draws the git letters / override glyphs in the Project and Hierarchy windows. Only reads cached state.</summary>
    [InitializeOnLoad]
    internal static class GitStatusDrawer
    {
        private const float Slot = 16f;
        private const float SceneMenuWidth = 20f;

        private static GUIStyle _letterStyle;
        private static GUIStyle _hintStyle;
        private static readonly GUIContent Content = new GUIContent();
        private static readonly Dictionary<string, string> FolderTooltips = new Dictionary<string, string>();
        private static int _folderTooltipVersion = -1;

        static GitStatusDrawer()
        {
            EditorApplication.projectWindowItemOnGUI += OnProjectItem;
            EditorApplication.hierarchyWindowItemOnGUI += OnHierarchyItem;
            // Re-subscribe once every [InitializeOnLoad] class has run, so the markers draw after (on top of) other
            // hierarchy extensions such as BluHierarchy.
            EditorApplication.delayCall += () =>
            {
                EditorApplication.hierarchyWindowItemOnGUI -= OnHierarchyItem;
                EditorApplication.hierarchyWindowItemOnGUI += OnHierarchyItem;
            };
        }

        private static bool Dark => EditorGUIUtility.isProSkin;

        private static Color ColorFor(GitState s)
        {
            switch (s)
            {
                case GitState.Untracked:
                case GitState.Added:
                    return Dark ? new Color(0.45f, 0.82f, 0.38f) : new Color(0.15f, 0.55f, 0.1f);
                case GitState.Modified:
                    return Dark ? new Color(0.92f, 0.72f, 0.25f) : new Color(0.7f, 0.45f, 0f);
                case GitState.Deleted:
                    return Dark ? new Color(0.95f, 0.4f, 0.35f) : new Color(0.75f, 0.15f, 0.1f);
                case GitState.Renamed:
                    return Dark ? new Color(0.35f, 0.8f, 0.85f) : new Color(0f, 0.5f, 0.55f);
                case GitState.Conflict:
                    return Dark ? new Color(0.95f, 0.35f, 0.75f) : new Color(0.7f, 0.1f, 0.5f);
                default:
                    return Color.gray;
            }
        }

        private static Color OverrideColor => Dark ? new Color(0.33f, 0.62f, 1f) : new Color(0.05f, 0.38f, 0.85f);

        private static string LetterFor(GitState s)
        {
            switch (s)
            {
                case GitState.Untracked: return "U";
                case GitState.Added: return "A";
                case GitState.Modified: return "M";
                case GitState.Deleted: return "D";
                case GitState.Renamed: return "R";
                case GitState.Conflict: return "!";
                default: return "";
            }
        }

        private static GUIStyle LetterStyle
        {
            get
            {
                if (_letterStyle == null)
                {
                    _letterStyle = new GUIStyle(EditorStyles.boldLabel)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        padding = new RectOffset(0, 0, 0, 0),
                        margin = new RectOffset(0, 0, 0, 0),
                        fontSize = 11,
                    };
                }
                return _letterStyle;
            }
        }

        private static GUIStyle HintStyle
        {
            get
            {
                if (_hintStyle == null)
                {
                    _hintStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight, fontStyle = FontStyle.Italic };
                }
                return _hintStyle;
            }
        }

        private static void Draw(Rect r, string text, Color color, string tooltip)
        {
            var old = GUI.contentColor;
            LetterStyle.normal.textColor = color;
            GUI.contentColor = Color.white;
            Content.text = text;
            Content.tooltip = tooltip;
            GUI.Label(r, Content, LetterStyle);
            GUI.contentColor = old;
        }

        private static Color Dim(Color c) => new Color(c.r, c.g, c.b, 0.7f);

        /// <summary>Hands out marker slots either right after the item label or leftwards from the row's right edge.</summary>
        private struct RowLayout
        {
            private Rect _row;
            private bool _afterName;
            private bool _crowded;
            private float _cursor;

            public static RowLayout For(Rect row, string label, GUIStyle style, float rightMargin)
            {
                return For(row, label, style, rightMargin, 0f, float.MaxValue, 3 * Slot);
            }

            /// <param name="afterLabel">Space another extension draws right after the label (BluHierarchy's tag pill).</param>
            /// <param name="rightLimit">Where another extension's right-hand content starts (BluHierarchy's icons).</param>
            /// <param name="needed">Width of the markers this row will draw.</param>
            public static RowLayout For(Rect row, string label, GUIStyle style, float rightMargin, float afterLabel, float rightLimit, float needed)
            {
                float right = Mathf.Min(row.xMax - rightMargin, rightLimit);
                var l = new RowLayout { _row = row, _cursor = right };
                if (!GitStatusSettings.MarkersAfterName || string.IsNullOrEmpty(label)) return l;
                // Rows start at the item icon (16 px + 2 px gap), then the label.
                float x = row.x + 18f + style.CalcSize(new GUIContent(label)).x + 2f + afterLabel;
                bool othersOnRight = rightLimit < row.xMax;
                if (x + needed <= right || (!othersOnRight && x + 3 * Slot < row.xMax))
                {
                    l._afterName = true;
                    l._cursor = x;
                }
                else if (othersOnRight)
                {
                    // The other extension's icons reach the name: no free space. Stay after the name, on a backing chip.
                    l._afterName = true;
                    l._crowded = true;
                    l._cursor = x;
                }
                return l;
            }

            /// <summary>Next slot; on a crowded row it gets a backing chip so the marker stays readable over icons.</summary>
            public Rect Take(float width)
            {
                var r = Next(width);
                if (_crowded) DrawBacking(r);
                return r;
            }

            public Rect Next(float width)
            {
                Rect r;
                if (_afterName)
                {
                    r = new Rect(_cursor, _row.y, width, _row.height);
                    _cursor += width;
                }
                else
                {
                    _cursor -= width;
                    r = new Rect(_cursor, _row.y, width, _row.height);
                }
                return r;
            }

            public bool AfterName => _afterName;

            private static void DrawBacking(Rect r)
            {
                var color = Dark ? new Color(0.17f, 0.17f, 0.17f, 0.95f) : new Color(0.86f, 0.86f, 0.86f, 0.95f);
                GUI.DrawTexture(new Rect(r.x, r.y + 1f, r.width, r.height - 2f), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, color, 0f, 3f);
            }
        }


        // ---------------------------------------------------------------- Project window

        private static void OnProjectItem(string guid, Rect rect)
        {
            if (!GitStatusSettings.ProjectOverlay || Event.current.type != EventType.Repaint) return;
            var snap = GitStatusService.Snapshot;
            if (snap.Version == 0 || string.IsNullOrEmpty(guid)) return;
            var path = PathCache.Get(guid);
            if (string.IsNullOrEmpty(path)) return;

            FileStatus st;
            bool isFolder = AssetDatabase.IsValidFolder(path);
            if (snap.TryGet(path, out st))
            {
                var tip = LetterFor(st.State) + "  " + st.State + " (" + st.Detail + ")";
                if (isFolder) tip += FolderTooltip(snap, path, false);
                Draw(ProjectSlot(rect, path, isFolder), LetterFor(st.State), ColorFor(st.State), tip);
            }
            else if (isFolder && (snap.DirtyFolders.Contains(path) || snap.DeletedByFolder.ContainsKey(path)))
            {
                var color = snap.DeletedByFolder.ContainsKey(path) && !snap.DirtyFolders.Contains(path)
                    ? ColorFor(GitState.Deleted)
                    : ColorFor(GitState.Modified);
                Draw(ProjectSlot(rect, path, true), "•", Dim(color), FolderTooltip(snap, path, true));
            }
        }

        private static Rect ProjectSlot(Rect rect, string path, bool isFolder)
        {
            if (rect.height > 20f) return new Rect(rect.xMax - 14f, rect.y, 14f, 14f); // icon grid: thumbnail corner
            var label = isFolder ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
            return RowLayout.For(rect, label, EditorStyles.label, 0f).Next(Slot);
        }

        /// <summary>Folder tooltip: counts of changes inside plus the deleted files that can't be shown as items.</summary>
        private static string FolderTooltip(GitStatusSnapshot snap, string folder, bool header)
        {
            if (_folderTooltipVersion != snap.Version)
            {
                FolderTooltips.Clear();
                _folderTooltipVersion = snap.Version;
            }
            var key = (header ? "h:" : "n:") + folder;
            string cached;
            if (FolderTooltips.TryGetValue(key, out cached)) return cached;

            var counts = new Dictionary<GitState, int>();
            var prefix = folder + "/";
            foreach (var kv in snap.Files)
            {
                if (!kv.Key.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase)) continue;
                int n;
                counts.TryGetValue(kv.Value.State, out n);
                counts[kv.Value.State] = n + 1;
            }
            var sb = new StringBuilder();
            if (header) sb.Append("Changes inside this folder");
            if (counts.Count > 0)
            {
                sb.Append(header ? ": " : "\nInside: ");
                bool first = true;
                foreach (var kv in counts)
                {
                    if (!first) sb.Append(", ");
                    sb.Append(kv.Value).Append(' ').Append(LetterFor(kv.Key));
                    first = false;
                }
            }
            SortedDictionary<string, int> deleted;
            if (snap.DeletedByFolder.TryGetValue(folder, out deleted))
            {
                sb.Append("\nDeleted here:");
                int shown = 0;
                foreach (var kv in deleted)
                {
                    if (shown++ >= GitStatusSettings.MaxTooltipLines)
                    {
                        sb.Append("\n  … ").Append(deleted.Count - GitStatusSettings.MaxTooltipLines).Append(" more");
                        break;
                    }
                    sb.Append("\n  D ").Append(kv.Key);
                    if (kv.Value > 1) sb.Append("  (").Append(kv.Value).Append(" files)");
                }
            }
            var s = sb.ToString();
            FolderTooltips[key] = s;
            return s;
        }

        // ---------------------------------------------------------------- Hierarchy window

        private static void OnHierarchyItem(int instanceId, Rect rect)
        {
            if (Event.current.type != EventType.Repaint) return;

            SceneMark sceneMark;
            if (GitStatusSettings.HierarchyGit && HierarchyGitState.Scenes.TryGetValue(instanceId, out sceneMark)
                && EditorUtility.InstanceIDToObject(instanceId) == null)
            {
                DrawSceneRow(rect, sceneMark, instanceId);
                return;
            }

            ObjectMark om = null;
            OverrideMark ov = null;
            bool isStageRoot = GitStatusSettings.HierarchyGit && instanceId == HierarchyGitState.PrefabStageRootId
                               && HierarchyGitState.PrefabStageMark != null;
            bool hasGit = GitStatusSettings.HierarchyGit && HierarchyGitState.Objects.TryGetValue(instanceId, out om);
            bool hasOv = GitStatusSettings.PrefabOverrides && PrefabOverrideCache.Marks.TryGetValue(instanceId, out ov);
            if (!hasGit && !hasOv && !isStageRoot) return;

            var go = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
            float needed = (hasGit ? Slot : 0f) + (hasOv ? Slot : 0f) + (isStageRoot ? Slot + 6f : 0f);
            float afterLabel = 0f, rightLimit = float.MaxValue;
            if (go != null && BluHierarchyCompat.Present)
            {
                float pill = BluHierarchyCompat.TagPillWidth(go);
                afterLabel = pill > 0f ? pill + 4f : 0f;
                rightLimit = BluHierarchyCompat.RightContentStart(go, rect) - 2f;
            }
            var layout = RowLayout.For(rect, go != null ? go.name : null, EditorStyles.label, GitStatusSettings.HierarchyRightOffset,
                afterLabel, rightLimit, needed);

            if (hasGit)
            {
                var r = layout.Take(Slot);
                if (om.State != GitState.None)
                    Draw(r, LetterFor(om.State), ColorFor(om.State), ObjectTooltip(om));
                else if (om.ChangedDescendants > 0)
                    Draw(r, "•", Dim(ColorFor(GitState.Modified)), om.ChangedDescendants + " changed object(s) inside");
            }
            if (hasOv)
            {
                string glyph;
                if (ov.AddedGameObject || (ov.AddedComponents && !ov.Properties && !ov.RemovedComponents)) glyph = "+";
                else if (ov.RemovedComponents && !ov.Properties && !ov.AddedComponents) glyph = "−";
                else if (ov.HasOwn) glyph = "◆";
                else glyph = "◇";
                Draw(layout.Take(Slot), glyph, OverrideColor, ov.Tooltip);
            }
            if (isStageRoot)
            {
                // Prefab mode has no scene header row: the root carries the file status.
                var stage = PrefabStageUtility.GetCurrentPrefabStage();
                bool dirty = stage != null && stage.scene.isDirty;
                DrawFileMark(layout.Take(Slot + 6f), HierarchyGitState.PrefabStageMark, dirty, true);
                if (dirty) DrawUnsavedHint(layout.Next(UnsavedHintWidth), layout.AfterName);
            }
        }

        private const float UnsavedHintWidth = 68f;

        private static void DrawSceneRow(Rect rect, SceneMark mark, int handle)
        {
            bool dirty = false;
            string name = null;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.handle != handle) continue;
                dirty = s.isDirty;
                name = s.name + (dirty ? "*" : "");
                break;
            }
            var layout = RowLayout.For(rect, name, EditorStyles.boldLabel, GitStatusSettings.HierarchyRightOffset + SceneMenuWidth);
            DrawFileMark(layout.Next(Slot), mark, dirty, false);
            if (dirty) DrawUnsavedHint(layout.Next(UnsavedHintWidth), layout.AfterName);
        }

        private static void DrawFileMark(Rect r, SceneMark mark, bool dirty, bool fileLabel)
        {
            var tip = mark.Tooltip;
            if (dirty && !mark.IsDirty) tip += "\n\nUnsaved changes: markers show the last SAVED state vs HEAD.";
            // In prefab mode the file letter shares the row with the root object's own letter: bracket it.
            var text = fileLabel ? "[" + LetterFor(mark.Status.State) + "]" : LetterFor(mark.Status.State);
            Draw(r, text, ColorFor(mark.Status.State), tip);
        }

        private static void DrawUnsavedHint(Rect r, bool leftAligned)
        {
            var old = HintStyle.normal.textColor;
            var oldAlign = HintStyle.alignment;
            HintStyle.normal.textColor = Color.gray;
            HintStyle.alignment = leftAligned ? TextAnchor.MiddleLeft : TextAnchor.MiddleRight;
            Content.text = "saved state";
            Content.tooltip = "This file has unsaved changes. Git markers compare the last saved file with HEAD.";
            GUI.Label(r, Content, HintStyle);
            HintStyle.normal.textColor = old;
            HintStyle.alignment = oldAlign;
        }

        private static string ObjectTooltip(ObjectMark m)
        {
            var sb = new StringBuilder();
            sb.Append(LetterFor(m.State)).Append("  ").Append((m.State == GitState.Untracked || m.State == GitState.Added) ? "new in working copy (not in HEAD)" : "changed vs HEAD");
            foreach (var r in m.Reasons) sb.Append("\n  ").Append(r);
            if (m.ExtraReasons > 0) sb.Append("\n  … ").Append(m.ExtraReasons).Append(" more");
            if (m.ChangedDescendants > 0) sb.Append('\n').Append(m.ChangedDescendants).Append(" changed object(s) inside");
            return sb.ToString();
        }
    }
}
