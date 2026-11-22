using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;

namespace GitStatusOverlay
{
    /// <summary>Schedules background <c>git status</c> runs and wires editor events. Never runs git on the main thread.</summary>
    [InitializeOnLoad]
    internal static class GitStatusService
    {
        public static GitStatusSnapshot Snapshot { get; private set; } = GitStatusSnapshot.Empty;

        public static string StatusLine
        {
            get
            {
                if (_task != null) return "Refreshing…";
                if (Snapshot.Error != null) return "Error: " + Snapshot.Error;
                if (Snapshot.Version == 0) return "Not refreshed yet";
                return Snapshot.Files.Count + " changed paths, HEAD " + (Snapshot.HeadSha.Length > 8 ? Snapshot.HeadSha.Substring(0, 8) : Snapshot.HeadSha)
                       + ", git: " + GitRunner.GitExe;
            }
        }

        private static readonly string ProjectRoot = Path.GetDirectoryName(Application.dataPath);
        private static Task<GitStatusSnapshot> _task;
        private static bool _requested = true;
        private static double _lastStart = -1000;
        private static double _hierarchyChangedAt = -1;
        private static int _version;

        static GitStatusService()
        {
            EditorApplication.update += Update;
            EditorApplication.focusChanged += focused => { if (focused) RequestRefresh(); };
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
            EditorApplication.projectChanged += () => RequestRefresh();
            Undo.undoRedoPerformed += PrefabOverrideCache.SetDirty;
            ObjectChangeEvents.changesPublished += (ref ObjectChangeEventStream _) => PrefabOverrideCache.SetDirty();
            EditorSceneManager.sceneSaved += _ => RequestRefresh();
            EditorSceneManager.sceneOpened += (s, m) => SceneSetChanged();
            EditorSceneManager.sceneClosed += _ => SceneSetChanged();
            PrefabStage.prefabStageOpened += _ => SceneSetChanged();
            PrefabStage.prefabStageClosing += _ => SceneSetChanged();
            PrefabStage.prefabSaved += _ => RequestRefresh();
            Selection.selectionChanged += PropertyGitState.RequestUpdate;
        }

        public static void RequestRefresh() => RequestRefresh(false);

        public static void RequestRefresh(bool immediate)
        {
            _requested = true;
            if (immediate) _lastStart = -1000;
        }

        private static void SceneSetChanged()
        {
            PrefabOverrideCache.SetDirty();
            // Run on the next tick: during these callbacks the stage/scene isn't fully set up yet.
            EditorApplication.delayCall += HierarchyGitState.Update;
        }

        private static void OnHierarchyChanged()
        {
            PrefabOverrideCache.SetDirty();
            _hierarchyChangedAt = EditorApplication.timeSinceStartup;
        }

        private static void Update()
        {
            double now = EditorApplication.timeSinceStartup;

            if (_task != null && _task.IsCompleted)
            {
                var done = _task;
                _task = null;
                ReloadGuard.End();
                if (done.Status == TaskStatus.RanToCompletion)
                {
                    Snapshot = done.Result;
                    PathCache.Clear();
                    HierarchyGitState.OnSnapshot(Snapshot);
                    PropertyGitState.RequestUpdate();
                    EditorApplication.RepaintProjectWindow();
                    EditorApplication.RepaintHierarchyWindow();
                }
                else if (done.Exception != null)
                {
                    Debug.LogWarning("[GitStatusOverlay] git status failed: " + done.Exception.GetBaseException().Message);
                }
            }

            bool anyOverlay = GitStatusSettings.ProjectOverlay || GitStatusSettings.HierarchyGit || GitStatusSettings.InspectorGit;
            if (anyOverlay && now - _lastStart > GitStatusSettings.RefreshInterval && InternalEditorUtility.isApplicationActive)
                _requested = true;

            // At most one git run at a time, and not more often than once a second.
            if (_requested && anyOverlay && _task == null && now - _lastStart > 1.0)
            {
                _requested = false;
                _lastStart = now;
                int version = ++_version;
                var root = ProjectRoot;
                ReloadGuard.Begin();
                _task = Task.Run(() => GitStatusSnapshot.Build(root, version));
            }

            // New objects (undo, instantiate, reload) get new instance ids: re-resolve 2 s after the last hierarchy change.
            if (_hierarchyChangedAt > 0 && now - _hierarchyChangedAt > 2.0)
            {
                _hierarchyChangedAt = -1;
                HierarchyGitState.RequestResolve();
            }

            HierarchyGitState.Tick();
            PrefabOverrideCache.Tick();
            PropertyGitState.Tick();
        }
    }

    /// <summary>
    /// Holds off script reloads while a git job runs on a worker thread: a domain reload that starts while a thread is
    /// inside System.Diagnostics.Process can hang the editor. Jobs finish in well under a second (git has a kill timer).
    /// </summary>
    internal static class ReloadGuard
    {
        public static void Begin() => EditorApplication.LockReloadAssemblies();

        public static void End() => EditorApplication.UnlockReloadAssemblies();
    }

    internal sealed class GitStatusAssetPostprocessor : AssetPostprocessor
    {
        // Unity calls this by name with this exact signature; the arguments aren't needed (any change triggers a refresh).
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            PathCache.Clear();
            GitStatusService.RequestRefresh();
        }
    }

    /// <summary>GUID → asset path cache for the Project window callback.</summary>
    internal static class PathCache
    {
        private static readonly System.Collections.Generic.Dictionary<string, string> Map =
            new System.Collections.Generic.Dictionary<string, string>();

        public static void Clear() => Map.Clear();

        public static string Get(string guid)
        {
            string p;
            if (!Map.TryGetValue(guid, out p))
            {
                p = AssetDatabase.GUIDToAssetPath(guid);
                Map[guid] = p;
            }
            return p;
        }
    }
}
