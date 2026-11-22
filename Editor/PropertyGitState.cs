using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GitStatusOverlay
{
    /// <summary>Changed properties of one object (component, GameObject or asset object) vs HEAD.</summary>
    internal sealed class PropertyMarks
    {
        public GitState State;
        public readonly Dictionary<string, PropChange> Props = new Dictionary<string, PropChange>(StringComparer.Ordinal);
        private string _tooltip;

        public void Add(GitState state, IEnumerable<PropChange> props)
        {
            if (GitStatusSnapshot.Priority(state) > GitStatusSnapshot.Priority(State)) State = state;
            foreach (var p in props)
            {
                if (string.IsNullOrEmpty(p.Path)) continue;
                PropChange existing;
                if (!Props.TryGetValue(p.Path, out existing)) Props[p.Path] = p;
                else
                {
                    foreach (var v in p.Old) existing.AddValue(false, v);
                    foreach (var v in p.New) existing.AddValue(true, v);
                }
            }
            _tooltip = null;
        }

        /// <summary>2: this property (or a whole parent value) changed; 1: something below it changed; 0: unchanged.</summary>
        public int Match(string propertyPath, out PropChange hit)
        {
            hit = null;
            int level = 0;
            foreach (var p in Props.Values)
            {
                var changed = p.Path;
                if (changed == propertyPath || propertyPath.StartsWith(changed + ".", StringComparison.Ordinal))
                {
                    hit = p;
                    return 2;
                }
                if (level == 0 && changed.StartsWith(propertyPath + ".", StringComparison.Ordinal))
                {
                    hit = p;
                    level = 1;
                }
            }
            return level;
        }

        public string Tooltip
        {
            get
            {
                if (_tooltip != null) return _tooltip;
                var sb = new StringBuilder();
                if (State == GitState.Untracked || State == GitState.Added) sb.Append("U  new object (not in HEAD)");
                else sb.Append("M  ").Append(Props.Count).Append(" changed propert").Append(Props.Count == 1 ? "y" : "ies").Append(" vs HEAD");
                int max = GitStatusSettings.MaxTooltipLines;
                foreach (var p in Props.Values.OrderBy(p => p.Display, StringComparer.Ordinal).Take(max))
                    sb.Append("\n  ").Append(Describe(p));
                if (Props.Count > max) sb.Append("\n  … ").Append(Props.Count - max).Append(" more");
                _tooltip = sb.ToString();
                return _tooltip;
            }
        }

        public static string Describe(PropChange p)
        {
            var name = string.IsNullOrEmpty(p.Display) ? p.Path : p.Display;
            if (p.Old.Count == 1 && p.New.Count == 1) return name + ":  " + ValueOf(p.Old[0]) + "  →  " + ValueOf(p.New[0]);
            if (p.Old.Count == 0 && p.New.Count > 0) return name + ":  + " + ValueOf(p.New[0]) + (p.New.Count > 1 ? " …" : "");
            if (p.New.Count == 0 && p.Old.Count > 0) return name + ":  − " + ValueOf(p.Old[0]) + (p.Old.Count > 1 ? " …" : "");
            return name + ":  changed";
        }

        /// <summary>"key: value" → "value"; "- item" → "item". Long values are shortened.</summary>
        private static string ValueOf(string line)
        {
            var v = line.StartsWith("- ", StringComparison.Ordinal) ? line.Substring(2) : line;
            int colon = v.IndexOf(": ", StringComparison.Ordinal);
            int brace = v.IndexOf('{');
            if (colon > 0 && (brace < 0 || brace > colon)) v = v.Substring(colon + 2);
            if (v.EndsWith(":", StringComparison.Ordinal)) v = v.Substring(0, v.Length - 1);
            if (v.Length == 0) v = "\"\"";
            v = AssetName(v) ?? v;
            return v.Length > 60 ? v.Substring(0, 57) + "…" : v;
        }

        /// <summary>"{fileID: 2100000, guid: …, type: 2}" → the referenced asset's file name.</summary>
        private static string AssetName(string reference)
        {
            int g = reference.IndexOf("guid: ", StringComparison.Ordinal);
            if (g < 0 || reference.Length < g + 6 + 32) return null;
            var path = AssetDatabase.GUIDToAssetPath(reference.Substring(g + 6, 32));
            return string.IsNullOrEmpty(path) ? null : Path.GetFileName(path);
        }
    }

    /// <summary>
    /// Property-level git changes for the Inspector: scene/prefab-stage objects (filled by <see cref="HierarchyGitState"/>)
    /// and modified text assets selected in the Project window (materials, prefabs, ScriptableObjects, controllers, …).
    /// </summary>
    internal static class PropertyGitState
    {
        private const ulong FileIdMask = 0x7FFFFFFFFFFFFFFFUL;

        private static readonly Dictionary<int, PropertyMarks> SceneMarks = new Dictionary<int, PropertyMarks>();
        private static readonly Dictionary<int, PropertyMarks> AssetMarks = new Dictionary<int, PropertyMarks>();
        private static readonly Dictionary<string, SceneFileDiff> AssetDiffs = new Dictionary<string, SceneFileDiff>(StringComparer.OrdinalIgnoreCase);
        private static Task<List<SceneFileDiff>> _pending;
        private static bool _updateRequested;

        public static int Version { get; private set; }

        public static bool TryGet(int instanceId, out PropertyMarks marks)
        {
            return SceneMarks.TryGetValue(instanceId, out marks) || AssetMarks.TryGetValue(instanceId, out marks);
        }

        // ---------------------------------------------------------------- scene objects

        public static void ClearScene()
        {
            SceneMarks.Clear();
            Version++;
        }

        public static void AddScene(Object obj, GitState state, IEnumerable<PropChange> props)
        {
            if (obj == null) return;
            int id = obj.GetInstanceID();
            PropertyMarks m;
            if (!SceneMarks.TryGetValue(id, out m)) SceneMarks[id] = m = new PropertyMarks();
            m.Add(state, props);
            Version++;
        }

        // ---------------------------------------------------------------- selected assets

        public static void RequestUpdate() => _updateRequested = true;

        /// <summary>Called from EditorApplication.update.</summary>
        public static void Tick()
        {
            if (_pending != null && _pending.IsCompleted)
            {
                var done = _pending;
                _pending = null;
                ReloadGuard.End();
                if (done.Status == TaskStatus.RanToCompletion)
                {
                    foreach (var d in done.Result) AssetDiffs[d.AssetPath] = d;
                }
                else if (done.Exception != null)
                {
                    Debug.LogWarning("[GitStatusOverlay] asset diff failed: " + done.Exception.GetBaseException().Message);
                }
                _updateRequested = true;
            }
            if (_updateRequested && _pending == null)
            {
                _updateRequested = false;
                UpdateAssets();
            }
        }

        private static List<string> SelectedAssetPaths()
        {
            var paths = new List<string>();
            foreach (var o in Selection.objects)
            {
                if (o == null || !EditorUtility.IsPersistent(o)) continue;
                var p = AssetDatabase.GetAssetPath(o);
                if (string.IsNullOrEmpty(p) || p.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) || AssetDatabase.IsValidFolder(p)) continue;
                if (!paths.Contains(p)) paths.Add(p);
            }
            return paths;
        }

        private static void UpdateAssets()
        {
            if (!GitStatusSettings.InspectorGit)
            {
                if (AssetMarks.Count > 0) { AssetMarks.Clear(); Version++; }
                return;
            }
            var snap = GitStatusService.Snapshot;
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var jobs = new List<KeyValuePair<string, string>>();
            var wanted = new List<KeyValuePair<string, FileStatus>>();
            foreach (var path in SelectedAssetPaths())
            {
                FileStatus st;
                if (!snap.TryGet(path, out st) || st.State == GitState.Deleted) continue;
                var fi = new FileInfo(Path.Combine(projectRoot, path));
                if (!fi.Exists) continue;
                wanted.Add(new KeyValuePair<string, FileStatus>(path, st));
                var key = string.Join("|", path, fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                    fi.Length.ToString(CultureInfo.InvariantCulture), snap.HeadSha, st.State.ToString());
                SceneFileDiff cached;
                if (AssetDiffs.TryGetValue(path, out cached) && cached.CacheKey == key) continue;
                jobs.Add(new KeyValuePair<string, string>(path, key));
            }
            if (jobs.Count > 0)
            {
                var repoRoot = snap.RepoRoot;
                var prefix = RepoPrefix(repoRoot, projectRoot);
                var untracked = new HashSet<string>(wanted.Where(w => w.Value.State == GitState.Untracked).Select(w => w.Key), StringComparer.OrdinalIgnoreCase);
                ReloadGuard.Begin();
                _pending = Task.Run(() =>
                {
                    var results = new List<SceneFileDiff>();
                    foreach (var j in jobs)
                    {
                        var d = UnityYamlDiff.Compute(repoRoot, prefix + j.Key, Path.Combine(projectRoot, j.Key), j.Key, untracked.Contains(j.Key));
                        d.CacheKey = j.Value;
                        results.Add(d);
                    }
                    return results;
                });
                return;
            }
            ResolveAssets(wanted.Select(w => w.Key));
        }

        private static string RepoPrefix(string repoRoot, string projectRoot)
        {
            var p = projectRoot.Replace('\\', '/').TrimEnd('/');
            if (string.IsNullOrEmpty(repoRoot) || p.Length <= repoRoot.Length) return "";
            return p.Substring(repoRoot.Length + 1) + "/";
        }

        private static void ResolveAssets(IEnumerable<string> paths)
        {
            AssetMarks.Clear();
            foreach (var path in paths)
            {
                SceneFileDiff diff;
                if (!AssetDiffs.TryGetValue(path, out diff) || diff.Error != null) continue;
                var byLocalId = new Dictionary<long, Object>();
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    string guid;
                    long local;
                    if (o != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out guid, out local)) byLocalId[local] = o;
                }
                foreach (var ch in diff.Changes.Values)
                {
                    if (ch.ClassId == 1001)
                    {
                        // Nested prefab instance inside a prefab asset: modification targets live at (source ^ instance) & mask.
                        foreach (var ic in ch.InstanceChanges.Where(c => c.Section == "m_Modifications"))
                        {
                            long local = unchecked((long)(((ulong)ic.Target ^ (ulong)ch.FileId) & FileIdMask));
                            Object target;
                            if (byLocalId.TryGetValue(local, out target)) AddAsset(target, GitState.Modified, new[] { ic.Values });
                        }
                        continue;
                    }
                    Object obj;
                    if (!byLocalId.TryGetValue(ch.FileId, out obj)) continue;
                    AddAsset(obj, ch.Added ? GitState.Untracked : GitState.Modified, ch.Props.Values);
                }
            }
            Version++;
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        }

        private static void AddAsset(Object obj, GitState state, IEnumerable<PropChange> props)
        {
            int id = obj.GetInstanceID();
            PropertyMarks m;
            if (!AssetMarks.TryGetValue(id, out m)) AssetMarks[id] = m = new PropertyMarks();
            m.Add(state, props);
        }
    }
}
