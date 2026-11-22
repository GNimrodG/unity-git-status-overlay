using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace GitStatusOverlay
{
    internal sealed class ObjectMark
    {
        public GitState State;
        public int ChangedDescendants;
        public readonly List<string> Reasons = new List<string>();
        public int ExtraReasons;

        public void Add(GitState s, string reason)
        {
            if (GitStatusSnapshot.Priority(s) > GitStatusSnapshot.Priority(State)) State = s;
            if (reason == null || Reasons.Contains(reason)) return;
            if (Reasons.Count < GitStatusSettings.MaxTooltipLines) Reasons.Add(reason);
            else ExtraReasons++;
        }
    }

    internal sealed class SceneMark
    {
        public string AssetPath;
        public FileStatus Status;
        public SceneFileDiff Diff;
        public bool IsDirty;
        public bool IsPrefabStage;
        public int ChangedObjects;
        public int Unresolved;
        public string Tooltip;
    }

    /// <summary>
    /// Maps git changes of the open scenes / prefab stage onto live GameObjects.
    /// Diffs are computed on a worker thread and cached per file state; resolving to objects runs on the main thread.
    /// </summary>
    internal static class HierarchyGitState
    {
        private const ulong FileIdMask = 0x7FFFFFFFFFFFFFFFUL;

        public static readonly Dictionary<int, ObjectMark> Objects = new Dictionary<int, ObjectMark>();
        public static readonly Dictionary<int, SceneMark> Scenes = new Dictionary<int, SceneMark>();
        /// <summary>Instance id of the prefab stage root, which carries the file summary in prefab mode.</summary>
        public static int PrefabStageRootId { get; private set; }
        public static SceneMark PrefabStageMark { get; private set; }

        private static readonly Dictionary<string, SceneFileDiff> DiffCache = new Dictionary<string, SceneFileDiff>(StringComparer.OrdinalIgnoreCase);
        private static GitStatusSnapshot _snapshot = GitStatusSnapshot.Empty;
        private static Task<List<SceneFileDiff>> _pending;
        private static bool _resolveRequested;

        public static void OnSnapshot(GitStatusSnapshot snap)
        {
            _snapshot = snap;
            Update();
        }

        public static void RequestResolve() => _resolveRequested = true;

        /// <summary>Called from EditorApplication.update.</summary>
        public static void Tick()
        {
            if (_pending != null && _pending.IsCompleted)
            {
                var task = _pending;
                _pending = null;
                ReloadGuard.End();
                if (task.Status == TaskStatus.RanToCompletion)
                {
                    foreach (var d in task.Result) DiffCache[d.AssetPath] = d;
                }
                else if (task.Exception != null)
                {
                    Debug.LogWarning("[GitStatusOverlay] scene diff failed: " + task.Exception.GetBaseException().Message);
                }
                // Files may have changed again while the worker ran.
                Update();
                return;
            }
            if (_resolveRequested && _pending == null)
            {
                _resolveRequested = false;
                Resolve();
            }
        }

        private struct Target
        {
            public string AssetPath;
            public Scene Scene;
            public PrefabStage Stage;
        }

        private static List<Target> OpenTargets()
        {
            var list = new List<Target>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.isLoaded && !string.IsNullOrEmpty(s.path)) list.Add(new Target { AssetPath = s.path, Scene = s });
            }
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && !string.IsNullOrEmpty(stage.assetPath))
                list.Add(new Target { AssetPath = stage.assetPath, Scene = stage.scene, Stage = stage });
            return list;
        }

        /// <summary>Starts background diffs for open files whose cached diff is stale, then resolves.</summary>
        public static void Update()
        {
            if (_pending != null) return; // Tick() calls Update() again when the worker finishes
            if (!GitStatusSettings.HierarchyGit && !GitStatusSettings.InspectorGit)
            {
                Clear();
                return;
            }
            var snap = _snapshot;
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var jobs = new List<KeyValuePair<string, string>>(); // asset path, cache key
            var untracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var assetPath in OpenTargets().Select(t => t.AssetPath))
            {
                FileStatus st;
                if (!snap.TryGet(assetPath, out st) || st.State == GitState.Deleted) continue;
                var abs = Path.Combine(projectRoot, assetPath);
                var fi = new FileInfo(abs);
                if (!fi.Exists) continue;
                var key = string.Join("|", assetPath, fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                    fi.Length.ToString(CultureInfo.InvariantCulture), snap.HeadSha, st.State.ToString());
                SceneFileDiff cached;
                if (DiffCache.TryGetValue(assetPath, out cached) && cached.CacheKey == key) continue;
                jobs.Add(new KeyValuePair<string, string>(assetPath, key));
                if (st.State == GitState.Untracked) untracked.Add(assetPath);
            }
            if (jobs.Count == 0)
            {
                Resolve();
                return;
            }
            var repoRoot = snap.RepoRoot;
            var projPrefix = RepoPrefix(repoRoot, projectRoot);
            ReloadGuard.Begin();
            _pending = Task.Run(() =>
            {
                var results = new List<SceneFileDiff>();
                foreach (var j in jobs)
                {
                    var d = UnityYamlDiff.Compute(repoRoot, projPrefix + j.Key, Path.Combine(projectRoot, j.Key), j.Key,
                        untracked.Contains(j.Key));
                    d.CacheKey = j.Value;
                    results.Add(d);
                }
                return results;
            });
        }

        private static string RepoPrefix(string repoRoot, string projectRoot)
        {
            var p = projectRoot.Replace('\\', '/').TrimEnd('/');
            if (string.IsNullOrEmpty(repoRoot) || p.Length <= repoRoot.Length) return "";
            return p.Substring(repoRoot.Length + 1) + "/";
        }

        public static void Clear()
        {
            Objects.Clear();
            Scenes.Clear();
            PrefabStageRootId = 0;
            PrefabStageMark = null;
        }

        // ---------------------------------------------------------------- resolving (main thread)

        private static void Resolve()
        {
            Clear();
            PropertyGitState.ClearScene();
            if (!GitStatusSettings.HierarchyGit && !GitStatusSettings.InspectorGit) return;
            var snap = _snapshot;
            foreach (var t in OpenTargets())
            {
                FileStatus st;
                if (!snap.TryGet(t.AssetPath, out st)) continue;
                var mark = new SceneMark
                {
                    AssetPath = t.AssetPath,
                    Status = st,
                    IsDirty = t.Scene.isDirty,
                    IsPrefabStage = t.Stage != null,
                };
                SceneFileDiff diff;
                if (DiffCache.TryGetValue(t.AssetPath, out diff))
                {
                    mark.Diff = diff;
                    IObjectResolver resolver = t.Stage != null
                        ? (IObjectResolver)new PrefabStageResolver(t.Stage)
                        : new SceneResolver(t.AssetPath);
                    ApplyDiff(mark, diff, resolver);
                }
                mark.Tooltip = BuildSceneTooltip(mark);
                if (t.Stage != null)
                {
                    PrefabStageMark = mark;
                    PrefabStageRootId = t.Stage.prefabContentsRoot != null ? t.Stage.prefabContentsRoot.GetInstanceID() : 0;
                }
                else
                {
                    Scenes[t.Scene.handle] = mark;
                }
            }
            MarkAncestors();
            EditorApplication.RepaintHierarchyWindow();
        }

        private static void ApplyDiff(SceneMark mark, SceneFileDiff diff, IObjectResolver resolver)
        {
            // Pass 1: everything we need to look up, resolved in one batch.
            var keys = new List<ObjKey>();
            foreach (var ch in diff.Changes.Values)
            {
                if (ch.Stripped) keys.Add(new ObjKey(ch.SourceId, ch.PrefabInstanceId));
                else if (ch.ClassId == 1001)
                {
                    keys.Add(new ObjKey(ch.SourcePrefabRootHint, ch.FileId));
                    foreach (var ic in ch.InstanceChanges) keys.Add(new ObjKey(ic.Target, ch.FileId));
                }
                else keys.Add(new ObjKey(ch.FileId, 0));
            }
            resolver.Prefetch(keys);

            var touched = new HashSet<int>();
            foreach (var ch in diff.Changes.Values)
            {
                if (ch.Stripped)
                {
                    if (Mark(resolver.Get(new ObjKey(ch.SourceId, ch.PrefabInstanceId)), GitState.Modified, "prefab instance stub changed", touched) == null)
                        mark.Unresolved++;
                }
                else if (ch.ClassId == 1001)
                {
                    ApplyPrefabInstance(mark, ch, resolver, touched);
                }
                else
                {
                    var obj = resolver.Get(new ObjKey(ch.FileId, 0));
                    if (obj == null)
                    {
                        mark.Unresolved++;
                        continue;
                    }
                    string typeName = obj.GetType().Name;
                    PropertyGitState.AddScene(obj, ch.Added ? GitState.Untracked : GitState.Modified, ch.Props.Values);
                    if (obj is GameObject)
                        Mark(obj, ch.Added ? GitState.Untracked : GitState.Modified, ch.Added ? "new GameObject" : "GameObject changed", touched);
                    else
                        Mark(obj, GitState.Modified, ch.Added ? "+ " + typeName + " added" : typeName + " changed", touched);
                }
            }
            mark.ChangedObjects = touched.Count;
        }

        private static void ApplyPrefabInstance(SceneMark mark, BlockChange ch, IObjectResolver resolver, HashSet<int> touched)
        {
            GameObject root = null;
            var hint = AsGameObject(resolver.Get(new ObjKey(ch.SourcePrefabRootHint, ch.FileId)));
            if (hint != null) root = PrefabUtility.GetOutermostPrefabInstanceRoot(hint) ?? hint;
            if (root == null)
            {
                mark.Unresolved++;
                return;
            }
            if (ch.Added)
            {
                foreach (var go in root.GetComponentsInChildren<Transform>(true).Select(t => t.gameObject))
                    Mark(go, GitState.Untracked, go == root ? "new prefab instance" : "part of a new prefab instance", touched);
                return;
            }
            if (ch.InstanceRootChanged) Mark(root, GitState.Modified, "prefab instance settings changed", touched);
            foreach (var ic in ch.InstanceChanges)
            {
                var target = resolver.Get(new ObjKey(ic.Target, ch.FileId));
                bool removal = ic.Section == "m_RemovedComponents" || ic.Section == "m_RemovedGameObjects";
                // Removed objects don't exist in the instance: name them from the source prefab asset.
                string what = DescribeInstanceChange(ic, target ?? (removal ? LoadFromSourcePrefab(ch.SourcePrefabGuid, ic.Target) : null));
                if (target != null && !removal)
                {
                    Mark(target, GitState.Modified, what, touched);
                    if (ic.Section == "m_Modifications") PropertyGitState.AddScene(target, GitState.Modified, new[] { ic.Values });
                    if (AsGameObject(target) != root) Mark(root, GitState.Modified, "changed overrides inside this prefab instance", touched);
                }
                else
                {
                    Mark(root, GitState.Modified, what, touched);
                }
            }
        }

        private static Object LoadFromSourcePrefab(string prefabGuid, long fileId)
        {
            if (string.IsNullOrEmpty(prefabGuid) || fileId == 0) return null;
            GlobalObjectId gid;
            var s = "GlobalObjectId_V1-1-" + prefabGuid + "-" + unchecked((ulong)fileId).ToString(CultureInfo.InvariantCulture) + "-0";
            return GlobalObjectId.TryParse(s, out gid) ? GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid) : null;
        }

        private static string DescribeInstanceChange(InstanceChange ic, Object target)
        {
            string typeName = target != null ? target.GetType().Name : "missing object";
            switch (ic.Section)
            {
                case "m_Modifications":
                    return "override " + typeName + "." + (ic.PropertyPath ?? "?");
                case "m_AddedComponents":
                    return "added component(s) on this object";
                case "m_AddedGameObjects":
                    return "added child GameObject(s)";
                case "m_RemovedComponents":
                    {
                        var comp = target as Component;
                        if (comp != null) return "removed component " + typeName + " on '" + comp.gameObject.name + "'";
                        return "removed component #" + ic.Target.ToString(CultureInfo.InvariantCulture);
                    }
                case "m_RemovedGameObjects":
                    return "removed child GameObject " + (target != null ? target.name : "#" + ic.Target.ToString(CultureInfo.InvariantCulture));
                default:
                    return ic.Section + " changed";
            }
        }

        private static GameObject AsGameObject(Object o)
        {
            var go = o as GameObject;
            if (go != null) return go;
            var c = o as Component;
            return c != null ? c.gameObject : null;
        }

        private static Object Mark(Object obj, GitState state, string reason, HashSet<int> touched)
        {
            var go = AsGameObject(obj);
            if (go == null) return null;
            int id = go.GetInstanceID();
            ObjectMark m;
            if (!Objects.TryGetValue(id, out m)) Objects[id] = m = new ObjectMark();
            m.Add(state, reason);
            touched.Add(id);
            return go;
        }

        private static void MarkAncestors()
        {
            var marked = Objects.Where(kv => kv.Value.State != GitState.None).Select(kv => kv.Key).ToList();
            foreach (var id in marked)
            {
                var go = EditorUtility.InstanceIDToObject(id) as GameObject;
                if (go == null) continue;
                for (var p = go.transform.parent; p != null; p = p.parent)
                {
                    ObjectMark m;
                    int pid = p.gameObject.GetInstanceID();
                    if (!Objects.TryGetValue(pid, out m)) Objects[pid] = m = new ObjectMark();
                    m.ChangedDescendants++;
                }
            }
        }

        private static string BuildSceneTooltip(SceneMark mark)
        {
            var sb = new StringBuilder();
            sb.Append(Path.GetFileName(mark.AssetPath)).Append(": ").Append(mark.Status.State).Append(" (").Append(mark.Status.Detail).Append(')');
            var d = mark.Diff;
            if (d == null) sb.Append("\n(diff pending)");
            else if (d.Error != null) sb.Append("\ndiff unavailable: ").Append(d.Error);
            else
            {
                sb.Append('\n').Append(mark.ChangedObjects).Append(" GameObjects with changes, ")
                  .Append(d.Changes.Count).Append(" changed YAML blocks");
                if (mark.Unresolved > 0) sb.Append(" (").Append(mark.Unresolved).Append(" not shown: scene settings or objects not loaded)");
                if (d.Deleted.Count > 0)
                {
                    var del = d.Deleted.Where(x => x.Description != null).Select(x => x.Description).ToList();
                    sb.Append("\nDeleted (").Append(del.Count).Append("):");
                    foreach (var s in del.Take(15)) sb.Append("\n  - ").Append(s);
                    if (del.Count > 15) sb.Append("\n  … ").Append(del.Count - 15).Append(" more");
                }
            }
            if (mark.IsDirty) sb.Append("\n\nUnsaved changes: markers show the last SAVED state vs HEAD.");
            return sb.ToString();
        }

        // ---------------------------------------------------------------- resolvers

        internal struct ObjKey : IEquatable<ObjKey>
        {
            public readonly long ObjectId;
            public readonly long PrefabId;

            public ObjKey(long objectId, long prefabId)
            {
                ObjectId = objectId;
                PrefabId = prefabId;
            }

            public bool Equals(ObjKey other) => ObjectId == other.ObjectId && PrefabId == other.PrefabId;
            public override bool Equals(object obj) => obj is ObjKey k && Equals(k);
            public override int GetHashCode() => ObjectId.GetHashCode() * 397 ^ PrefabId.GetHashCode();
        }

        private interface IObjectResolver
        {
            void Prefetch(List<ObjKey> keys);
            Object Get(ObjKey key);
        }

        /// <summary>Scene objects: GlobalObjectId (type 2 = scene object; prefab-instance objects use source fileID + instance id).</summary>
        private sealed class SceneResolver : IObjectResolver
        {
            private readonly string _guid;
            private readonly Dictionary<ObjKey, Object> _map = new Dictionary<ObjKey, Object>();

            public SceneResolver(string scenePath)
            {
                _guid = AssetDatabase.AssetPathToGUID(scenePath);
            }

            public void Prefetch(List<ObjKey> keys)
            {
                var unique = keys.Where(k => k.ObjectId != 0).Distinct().ToList();
                var ids = new List<GlobalObjectId>(unique.Count);
                var idKeys = new List<ObjKey>(unique.Count);
                foreach (var k in unique)
                {
                    GlobalObjectId gid;
                    var s = "GlobalObjectId_V1-2-" + _guid + "-" + unchecked((ulong)k.ObjectId).ToString(CultureInfo.InvariantCulture)
                            + "-" + unchecked((ulong)k.PrefabId).ToString(CultureInfo.InvariantCulture);
                    if (!GlobalObjectId.TryParse(s, out gid)) continue;
                    ids.Add(gid);
                    idKeys.Add(k);
                }
                if (ids.Count == 0) return;
                var objs = new Object[ids.Count];
                GlobalObjectId.GlobalObjectIdentifiersToObjectsSlow(ids.ToArray(), objs);
                for (int i = 0; i < objs.Length; i++) _map[idKeys[i]] = objs[i];
            }

            public Object Get(ObjKey key)
            {
                Object o;
                return _map.TryGetValue(key, out o) && o != null ? o : null;
            }
        }

        /// <summary>
        /// Prefab mode: stage objects are clones of the prefab asset, so map them by walking the saved asset and the
        /// stage in parallel. Objects of nested prefab instances have local id (sourceId ^ instanceId) &amp; mask.
        /// </summary>
        private sealed class PrefabStageResolver : IObjectResolver
        {
            private readonly Dictionary<long, Object> _map = new Dictionary<long, Object>();

            public PrefabStageResolver(PrefabStage stage)
            {
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(stage.assetPath);
                if (asset != null && stage.prefabContentsRoot != null) Walk(asset, stage.prefabContentsRoot);
            }

            private void Walk(GameObject a, GameObject s)
            {
                Add(a, s);
                var ac = a.GetComponents<Component>();
                var sc = s.GetComponents<Component>();
                for (int i = 0; i < ac.Length && i < sc.Length; i++)
                {
                    if (ac[i] != null && sc[i] != null && ac[i].GetType() == sc[i].GetType()) Add(ac[i], sc[i]);
                }
                var at = a.transform;
                var st = s.transform;
                bool sameShape = at.childCount == st.childCount;
                for (int i = 0; i < at.childCount; i++)
                {
                    var child = at.GetChild(i);
                    Transform match = sameShape ? st.GetChild(i) : st.Find(child.name);
                    if (match != null) Walk(child.gameObject, match.gameObject);
                }
            }

            private void Add(Object assetObj, Object stageObj)
            {
                string guid;
                long local;
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(assetObj, out guid, out local)) _map[local] = stageObj;
            }

            public void Prefetch(List<ObjKey> keys)
            {
                // everything was mapped in the constructor
            }

            public Object Get(ObjKey key)
            {
                long id = key.PrefabId == 0
                    ? key.ObjectId
                    : unchecked((long)(((ulong)key.ObjectId ^ (ulong)key.PrefabId) & FileIdMask));
                Object o;
                return _map.TryGetValue(id, out o) && o != null ? o : null;
            }
        }
    }
}
