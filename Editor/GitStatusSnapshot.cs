using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GitStatusOverlay
{
    internal enum GitState
    {
        None = 0,
        Modified = 1,
        Renamed = 2,
        Deleted = 3,
        Untracked = 4,
        Added = 5,
        Conflict = 6,
    }

    internal struct FileStatus
    {
        public GitState State;
        public string Detail;
    }

    /// <summary>Immutable result of one <c>git status</c> run, keyed by Unity asset path (".meta" folded into its asset).</summary>
    internal sealed class GitStatusSnapshot
    {
        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
        private static readonly char[] Space = { ' ' };

        public readonly Dictionary<string, FileStatus> Files = new Dictionary<string, FileStatus>(PathComparer);
        public readonly List<string> UntrackedDirs = new List<string>();
        public readonly HashSet<string> DirtyFolders = new HashSet<string>(PathComparer);
        public readonly Dictionary<string, SortedDictionary<string, int>> DeletedByFolder =
            new Dictionary<string, SortedDictionary<string, int>>(PathComparer);

        public string HeadSha = "";
        public string RepoRoot = "";
        public string Error;
        public int Version;

        public static readonly GitStatusSnapshot Empty = new GitStatusSnapshot();

        public bool TryGet(string assetPath, out FileStatus status)
        {
            if (Files.TryGetValue(assetPath, out status)) return true;
            bool inUntrackedDir = UntrackedDirs.Exists(dir =>
                assetPath.StartsWith(dir, StringComparison.OrdinalIgnoreCase) || PathComparer.Equals(assetPath + "/", dir));
            if (!inUntrackedDir) return false;
            status = new FileStatus { State = GitState.Untracked, Detail = "untracked" };
            return true;
        }

        public static int Priority(GitState s)
        {
            switch (s)
            {
                case GitState.Conflict: return 6;
                case GitState.Untracked: return 5;
                case GitState.Added: return 5;
                case GitState.Deleted: return 4;
                case GitState.Renamed: return 3;
                case GitState.Modified: return 2;
                default: return 0;
            }
        }

        // ---------------------------------------------------------------- building (background thread)

        /// <summary>Runs git status (and nested status for dirty submodules). Safe to call off the main thread.</summary>
        public static GitStatusSnapshot Build(string projectRoot, int version)
        {
            var snap = new GitStatusSnapshot { Version = version };
            if (!GitRunner.Available)
            {
                snap.Error = "git was not found on PATH";
                return snap;
            }
            var top = GitRunner.Run(projectRoot, "rev-parse --show-toplevel");
            if (top == null)
            {
                snap.Error = "not a git repository";
                return snap;
            }
            snap.RepoRoot = Normalize(Path.GetFullPath(top.Trim()));
            var projRoot = Normalize(Path.GetFullPath(projectRoot));
            // Project path relative to the repo root ("" when they are the same folder).
            string projPrefix = "";
            if (projRoot.Length > snap.RepoRoot.Length
                && projRoot.StartsWith(snap.RepoRoot + "/", StringComparison.OrdinalIgnoreCase))
            {
                projPrefix = projRoot.Substring(snap.RepoRoot.Length + 1) + "/";
            }

            var raw = new List<KeyValuePair<string, FileStatus>>();
            var untrackedDirs = new List<string>();
            if (!CollectStatus(snap, snap.RepoRoot, "", raw, untrackedDirs, 0))
            {
                snap.Error = "git status failed";
                return snap;
            }

            foreach (var kv in raw) AddEntry(snap, StripPrefix(kv.Key, projPrefix), kv.Value);
            foreach (var d in untrackedDirs)
            {
                var p = StripPrefix(d, projPrefix);
                if (p == null) continue;
                snap.UntrackedDirs.Add(p);
                MarkParents(snap, p.TrimEnd('/'));
            }
            GroupDeleted(snap, projRoot);
            return snap;
        }

        private static string StripPrefix(string repoPath, string prefix)
        {
            if (repoPath == null) return null;
            if (prefix.Length == 0) return repoPath;
            return repoPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? repoPath.Substring(prefix.Length) : null;
        }

        private static string Normalize(string p) => p.Replace('\\', '/').TrimEnd('/');

        private static bool CollectStatus(GitStatusSnapshot snap, string dir, string pathPrefix,
            List<KeyValuePair<string, FileStatus>> raw, List<string> untrackedDirs, int depth)
        {
            var output = GitRunner.Run(dir, "status --porcelain=v2 -z --branch --untracked-files=normal --ignore-submodules=none");
            if (output == null) return false;
            var tokens = output.Split('\0');
            int i = 0;
            while (i < tokens.Length)
            {
                var t = tokens[i++];
                if (t.Length < 2) continue;
                switch (t[0])
                {
                    case '#':
                        if (depth == 0 && t.StartsWith("# branch.oid ", StringComparison.Ordinal)) snap.HeadSha = t.Substring(13);
                        break;
                    case '?':
                        {
                            var p = pathPrefix + t.Substring(2);
                            if (p.EndsWith('/')) untrackedDirs.Add(p);
                            else raw.Add(Entry(p, GitState.Untracked, "untracked"));
                            break;
                        }
                    case '1':
                        {
                            var parts = t.Split(Space, 9);
                            if (parts.Length < 9) break;
                            var p = pathPrefix + parts[8];
                            var sub = parts[2];
                            if (sub[0] == 'S')
                                AddSubmodule(snap, p, sub, raw, untrackedDirs, depth);
                            else
                                raw.Add(Entry(p, FromXY(parts[1]), Describe(parts[1])));
                            break;
                        }
                    case '2':
                        {
                            var parts = t.Split(Space, 10);
                            if (parts.Length < 10) break;
                            var p = pathPrefix + parts[9];
                            var orig = i < tokens.Length ? tokens[i++] : ""; // -z: the original path is the next token
                            var renamed = parts[8][0] == 'R';
                            raw.Add(Entry(p, renamed ? GitState.Renamed : GitState.Added,
                                (renamed ? "renamed from " : "copied from ") + pathPrefix + orig));
                            break;
                        }
                    case 'u':
                        {
                            var parts = t.Split(Space, 11);
                            if (parts.Length < 11) break;
                            raw.Add(Entry(pathPrefix + parts[10], GitState.Conflict, "merge conflict"));
                            break;
                        }
                }
            }
            return true;
        }

        private static void AddSubmodule(GitStatusSnapshot snap, string p, string sub,
            List<KeyValuePair<string, FileStatus>> raw, List<string> untrackedDirs, int depth)
        {
            var notes = new List<string>();
            if (sub[1] == 'C') notes.Add("new commits");
            if (sub[2] == 'M') notes.Add("modified content");
            if (sub[3] == 'U') notes.Add("untracked content");
            raw.Add(Entry(p, GitState.Modified, "submodule: " + (notes.Count > 0 ? string.Join(", ", notes) : "changed")));
            // Look inside dirty submodules so their files get markers too.
            if (depth < 2 && (sub[2] == 'M' || sub[3] == 'U'))
            {
                CollectStatus(snap, snap.RepoRoot + "/" + p, p + "/", raw, untrackedDirs, depth + 1);
            }
        }

        private static KeyValuePair<string, FileStatus> Entry(string path, GitState s, string detail)
        {
            return new KeyValuePair<string, FileStatus>(path, new FileStatus { State = s, Detail = detail });
        }

        private static GitState FromXY(string xy)
        {
            char x = xy[0], y = xy[1];
            if (x == 'A' || y == 'A') return GitState.Added;
            if (x == 'D' || y == 'D') return GitState.Deleted;
            if (x == 'R' || y == 'R') return GitState.Renamed;
            return GitState.Modified;
        }

        private static string Describe(string xy)
        {
            char x = xy[0], y = xy[1];
            if (x != '.' && y != '.') return "staged + unstaged changes";
            return x != '.' ? "staged" : "unstaged";
        }

        private static void AddEntry(GitStatusSnapshot snap, string path, FileStatus st)
        {
            if (string.IsNullOrEmpty(path)) return;
            bool isMeta = path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);
            var asset = isMeta ? path.Substring(0, path.Length - 5) : path;
            if (isMeta) st.Detail = ".meta " + st.Detail;
            FileStatus existing;
            if (snap.Files.TryGetValue(asset, out existing))
            {
                bool existingWins = Priority(existing.State) >= Priority(st.State);
                var keep = existingWins ? existing : st;
                var other = existingWins ? st : existing;
                keep.Detail = keep.Detail + "; " + other.Detail;
                snap.Files[asset] = keep;
            }
            else
            {
                snap.Files[asset] = st;
            }
            MarkParents(snap, asset);
        }

        private static void MarkParents(GitStatusSnapshot snap, string path)
        {
            int idx = path.LastIndexOf('/');
            while (idx > 0)
            {
                path = path.Substring(0, idx);
                if (!snap.DirtyFolders.Add(path)) break;
                idx = path.LastIndexOf('/');
            }
        }

        /// <summary>Deleted assets can't be drawn; list them under the nearest folder that still exists.</summary>
        private static void GroupDeleted(GitStatusSnapshot snap, string projRoot)
        {
            var deletedPaths = snap.Files.Where(f => f.Value.State == GitState.Deleted).Select(f => f.Key).ToList();
            var deletedParents = new HashSet<string>(PathComparer);
            foreach (var p in deletedPaths)
            {
                for (int idx = p.LastIndexOf('/'); idx > 0; idx = p.LastIndexOf('/', idx - 1))
                {
                    if (!deletedParents.Add(p.Substring(0, idx))) break;
                }
            }
            foreach (var path in deletedPaths)
            {
                var parent = path;
                string child = path;
                while (true)
                {
                    int idx = parent.LastIndexOf('/');
                    if (idx <= 0) { parent = ""; break; }
                    child = parent;
                    parent = parent.Substring(0, idx);
                    if (Directory.Exists(projRoot + "/" + parent)) break;
                }
                if (parent.Length == 0) continue;
                var label = child == path ? Path.GetFileName(path) : child.Substring(parent.Length + 1) + "/";
                // A deleted folder's own .meta lists as "name"; fold it into "name/".
                if (child == path && deletedParents.Contains(path)) label += "/";
                SortedDictionary<string, int> list;
                if (!snap.DeletedByFolder.TryGetValue(parent, out list))
                    snap.DeletedByFolder[parent] = list = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                int n;
                list.TryGetValue(label, out n);
                list[label] = n + 1;
            }
        }
    }
}
