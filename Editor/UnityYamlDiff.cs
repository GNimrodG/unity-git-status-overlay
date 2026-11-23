using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace GitStatusOverlay
{
    /// <summary>One changed serialized property: its SerializedProperty path plus the removed and added YAML values.</summary>
    internal sealed class PropChange
    {
        private const int MaxValues = 4;

        public string Path;
        public string Display;
        public readonly List<string> Old = new List<string>();
        public readonly List<string> New = new List<string>();

        public void AddValue(bool isNew, string value)
        {
            var list = isNew ? New : Old;
            if (list.Count < MaxValues && value != null) list.Add(value);
        }
    }

    /// <summary>One change inside a PrefabInstance block (an m_Modifications / m_Added* / m_Removed* entry).</summary>
    internal sealed class InstanceChange
    {
        public string Section;
        public long Target;
        public string PropertyPath;
        public readonly PropChange Values = new PropChange();
    }

    internal sealed class BlockChange
    {
        public long FileId;
        public int ClassId;
        public bool Added;
        public bool Stripped;
        public long SourceId;          // stripped blocks: m_CorrespondingSourceObject
        public long PrefabInstanceId;  // stripped blocks: m_PrefabInstance
        public string SourcePrefabGuid; // PrefabInstance blocks: m_SourcePrefab guid
        public long SourcePrefabRootHint; // PrefabInstance blocks: a target fileID usable to find the instance
        public bool InstanceRootChanged;
        public readonly List<InstanceChange> InstanceChanges = new List<InstanceChange>();
        /// <summary>Changed properties of a modified (not new) block, keyed by property path.</summary>
        public readonly Dictionary<string, PropChange> Props = new Dictionary<string, PropChange>();
    }

    internal sealed class DeletedBlock
    {
        public long FileId;
        public int ClassId;
        public string Description;
        /// <summary>MonoBehaviours: guid of m_Script, so the main thread can name the script.</summary>
        public string ScriptGuid;
        public long ScriptFileId;
    }

    /// <summary>Which YAML documents of a .unity/.prefab file differ between HEAD and the working file.</summary>
    internal sealed class SceneFileDiff
    {
        public string AssetPath;
        public string CacheKey;
        public bool WholeFileNew;
        public string Error;
        public readonly Dictionary<long, BlockChange> Changes = new Dictionary<long, BlockChange>();
        public readonly List<DeletedBlock> Deleted = new List<DeletedBlock>();
    }

    internal static class UnityYamlDiff
    {
        private const int PrefabInstanceClass = 1001;
        private const int GameObjectClass = 1;
        private const int MonoBehaviourClass = 114;

        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
        private static readonly Regex HeaderRx = new Regex(@"^--- !u!(\d+) &(-?\d+)( stripped)?", RegexOptions.Compiled, RegexTimeout);
        private static readonly Regex HunkRx = new Regex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.Compiled, RegexTimeout);
        private static readonly Regex FileIdRx = new Regex(@"fileID: (-?\d+)", RegexOptions.Compiled, RegexTimeout);
        private static readonly Regex GuidRx = new Regex(@"guid: ([0-9a-fA-F]{32})", RegexOptions.Compiled, RegexTimeout);
        private static readonly Regex SectionRx = new Regex(@"^    (m_\w+):", RegexOptions.Compiled, RegexTimeout);

        internal sealed class Block
        {
            public long FileId;
            public int ClassId;
            public bool Stripped;
            public int Start; // header line, 0-based
            public int End;   // exclusive
        }

        internal sealed class YamlFile
        {
            public string[] Lines;
            public readonly List<Block> Blocks = new List<Block>();
            public readonly Dictionary<long, Block> ById = new Dictionary<long, Block>();

            public static YamlFile Parse(string text)
            {
                var f = new YamlFile { Lines = text.Split('\n') };
                for (int i = 0; i < f.Lines.Length; i++)
                {
                    var l = f.Lines[i];
                    if (l.Length < 8 || l[0] != '-' || !l.StartsWith("--- !u!", StringComparison.Ordinal)) continue;
                    var m = HeaderRx.Match(l);
                    if (!m.Success) continue;
                    if (f.Blocks.Count > 0) f.Blocks[f.Blocks.Count - 1].End = i;
                    var b = new Block
                    {
                        ClassId = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                        FileId = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                        Stripped = m.Groups[3].Success,
                        Start = i,
                        End = f.Lines.Length,
                    };
                    f.Blocks.Add(b);
                    f.ById[b.FileId] = b;
                }
                return f;
            }

            public string Line(int i) => Lines[i].TrimEnd('\r');

            public Block BlockAt(int line)
            {
                int lo = 0, hi = Blocks.Count - 1, found = -1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (Blocks[mid].Start <= line) { found = mid; lo = mid + 1; }
                    else hi = mid - 1;
                }
                return found < 0 ? null : Blocks[found];
            }

            /// <summary>Value of a top-level field ("  m_Name: x") of a block, or null.</summary>
            public string Field(Block b, string name)
            {
                var prefix = "  " + name + ":";
                for (int i = b.Start + 2; i < b.End; i++)
                {
                    var l = Line(i);
                    if (l.StartsWith(prefix, StringComparison.Ordinal)) return l.Substring(prefix.Length).Trim();
                }
                return null;
            }
        }

        // ---------------------------------------------------------------- entry point (background thread)

        public static SceneFileDiff Compute(string repoRoot, string repoRelPath, string absPath, string assetPath, bool untracked)
        {
            var result = new SceneFileDiff { AssetPath = assetPath };
            string newText;
            try
            {
                newText = File.ReadAllText(absPath);
            }
            catch (IOException e)
            {
                result.Error = e.Message;
                return result;
            }
            catch (UnauthorizedAccessException e)
            {
                result.Error = e.Message;
                return result;
            }
            if (!newText.StartsWith("%YAML", StringComparison.Ordinal))
            {
                result.Error = "not a text-serialized asset";
                return result;
            }
            var newFile = YamlFile.Parse(newText);

            string oldText = untracked ? null : GitRunner.Run(repoRoot, "show " + GitRunner.Quote("HEAD:" + repoRelPath));
            if (oldText == null)
            {
                result.WholeFileNew = true;
                foreach (var b in newFile.Blocks) GetChange(result, newFile, b, true);
                return result;
            }
            var oldFile = YamlFile.Parse(oldText);

            var diff = GitRunner.Run(repoRoot, "diff --no-ext-diff --text -U0 HEAD -- " + GitRunner.Quote(repoRelPath));
            if (diff == null)
            {
                result.Error = "git diff failed";
                return result;
            }

            var deletedIds = new HashSet<long>();
            foreach (var raw in diff.Split('\n'))
            {
                if (!raw.StartsWith("@@", StringComparison.Ordinal)) continue;
                var m = HunkRx.Match(raw);
                if (!m.Success) continue;
                int oldStart = ParseInt(m.Groups[1].Value);
                int oldCount = m.Groups[2].Success ? ParseInt(m.Groups[2].Value) : 1;
                int newStart = ParseInt(m.Groups[3].Value);
                int newCount = m.Groups[4].Success ? ParseInt(m.Groups[4].Value) : 1;

                for (int k = 0; k < newCount; k++)
                {
                    int line = newStart - 1 + k;
                    var b = newFile.BlockAt(line);
                    if (b == null) continue;
                    bool added = !oldFile.ById.ContainsKey(b.FileId);
                    var ch = GetChange(result, newFile, b, added);
                    if (added) continue;
                    if (b.ClassId == PrefabInstanceClass) AddInstanceChange(ch, newFile, b, line, true);
                    else RecordProp(ch, newFile, b, line, true);
                }
                for (int k = 0; k < oldCount; k++)
                {
                    int line = oldStart - 1 + k;
                    var b = oldFile.BlockAt(line);
                    if (b == null) continue;
                    Block nb;
                    if (newFile.ById.TryGetValue(b.FileId, out nb))
                    {
                        var ch = GetChange(result, newFile, nb, false);
                        if (b.ClassId == PrefabInstanceClass) AddInstanceChange(ch, oldFile, b, line, false);
                        else RecordProp(ch, oldFile, b, line, false);
                    }
                    else if (deletedIds.Add(b.FileId))
                    {
                        var script = b.ClassId == MonoBehaviourClass && !b.Stripped ? oldFile.Field(b, "m_Script") : null;
                        var guid = script == null ? null : GuidRx.Match(script);
                        result.Deleted.Add(new DeletedBlock
                        {
                            FileId = b.FileId,
                            ClassId = b.ClassId,
                            Description = DescribeDeleted(oldFile, b),
                            ScriptGuid = guid != null && guid.Success ? guid.Groups[1].Value : null,
                            ScriptFileId = FirstFileId(script),
                        });
                    }
                }
            }
            return result;
        }

        private static int ParseInt(string s) => int.Parse(s, CultureInfo.InvariantCulture);

        private static BlockChange GetChange(SceneFileDiff result, YamlFile file, Block b, bool added)
        {
            BlockChange ch;
            if (result.Changes.TryGetValue(b.FileId, out ch)) return ch;
            ch = new BlockChange { FileId = b.FileId, ClassId = b.ClassId, Added = added, Stripped = b.Stripped };
            if (b.Stripped)
            {
                ch.SourceId = FirstFileId(file.Field(b, "m_CorrespondingSourceObject"));
                ch.PrefabInstanceId = FirstFileId(file.Field(b, "m_PrefabInstance"));
            }
            if (b.ClassId == PrefabInstanceClass)
            {
                var src = file.Field(b, "m_SourcePrefab");
                var gm = src == null ? null : GuidRx.Match(src);
                ch.SourcePrefabGuid = gm != null && gm.Success ? gm.Groups[1].Value : null;
                ch.SourcePrefabRootHint = FirstModificationTarget(file, b);
                if (added) ch.InstanceRootChanged = true;
            }
            result.Changes[b.FileId] = ch;
            return ch;
        }

        private static long FirstFileId(string s)
        {
            if (s == null) return 0;
            var m = FileIdRx.Match(s);
            return m.Success ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }

        private static long FirstModificationTarget(YamlFile file, Block b)
        {
            for (int i = b.Start + 1; i < b.End; i++)
            {
                var l = file.Line(i);
                if (l.StartsWith("    - target:", StringComparison.Ordinal)) return FirstFileId(l);
            }
            return 0;
        }

        /// <summary>Attributes one changed line of a PrefabInstance block to the list entry (and its target) it belongs to.</summary>
        private static void AddInstanceChange(BlockChange ch, YamlFile file, Block b, int line, bool isNew)
        {
            if (line <= b.Start + 1)
            {
                ch.InstanceRootChanged = true;
                return;
            }
            int item = -1;
            string section = null;
            for (int i = line; i > b.Start; i--)
            {
                var l = file.Line(i);
                if (l.StartsWith("    - ", StringComparison.Ordinal)) { item = i; break; }
                var sm = SectionRx.Match(l);
                if (sm.Success) { section = sm.Groups[1].Value; break; }
                if (Indent(l) <= 2 && l.Trim().Length > 0)
                {
                    ch.InstanceRootChanged = true;
                    return;
                }
            }
            if (item < 0)
            {
                // A section-level line (e.g. "m_TransformParent", "m_RemovedComponents: []") belongs to the root.
                if (section != "m_Modifications") ch.InstanceRootChanged = true;
                return;
            }
            for (int i = item - 1; i > b.Start; i--)
            {
                var sm = SectionRx.Match(file.Line(i));
                if (sm.Success) { section = sm.Groups[1].Value; break; }
            }
            string propertyPath = null;
            for (int i = item + 1; i < b.End; i++)
            {
                var l = file.Line(i);
                if (Indent(l) <= 4) break;
                var t = l.TrimStart();
                if (t.StartsWith("propertyPath:", StringComparison.Ordinal))
                {
                    propertyPath = t.Substring(13).Trim();
                    break;
                }
            }
            section = section ?? "?";
            long target = FirstFileId(file.Line(item));
            var change = ch.InstanceChanges.Find(c => c.Target == target && c.Section == section && c.PropertyPath == propertyPath);
            if (change == null)
            {
                change = new InstanceChange { Section = section, Target = target, PropertyPath = propertyPath };
                change.Values.Path = propertyPath;
                change.Values.Display = propertyPath;
                ch.InstanceChanges.Add(change);
            }
            // Only the value lines are interesting in a tooltip (not target/propertyPath lines).
            var text = file.Line(line).Trim();
            bool isValue = text.StartsWith("value:", StringComparison.Ordinal) && text.Length > "value:".Length;
            bool isReference = text.StartsWith("objectReference:", StringComparison.Ordinal) && !text.EndsWith("{fileID: 0}", StringComparison.Ordinal);
            if (isValue || isReference) change.Values.AddValue(isNew, text);
        }

        private static void RecordProp(BlockChange ch, YamlFile file, Block b, int line, bool isNew)
        {
            string path, display, value;
            if (!YamlPropertyPath.TryGet(file, b, line, out path, out display, out value)) return;
            PropChange pc;
            if (!ch.Props.TryGetValue(path, out pc))
            {
                pc = new PropChange { Path = path, Display = display };
                ch.Props[path] = pc;
            }
            pc.AddValue(isNew, value);
        }

        internal static int Indent(string l)
        {
            int n = 0;
            while (n < l.Length && l[n] == ' ') n++;
            return n;
        }

        private static string DescribeDeleted(YamlFile oldFile, Block b)
        {
            // Stripped blocks are only reference stubs for objects inside a prefab instance (no name, no data). Unity
            // drops them when nothing references the object any more, or when it reserializes the file (e.g. on an
            // editor upgrade), so their removal isn't a deleted object.
            if (b.Stripped) return null;
            if (b.ClassId == GameObjectClass) return "GameObject '" + Unquote(oldFile.Field(b, "m_Name")) + "'";
            if (b.ClassId == PrefabInstanceClass) return "Prefab instance '" + PrefabInstanceName(oldFile, b) + "'";
            var name = ClassName(b.ClassId);
            var go = FirstFileId(oldFile.Field(b, "m_GameObject"));
            Block gob;
            if (go != 0 && oldFile.ById.TryGetValue(go, out gob))
                return name + " on '" + GameObjectName(oldFile, gob) + "'";
            return name + " &" + b.FileId.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>A GameObject block's name; for a stripped one (inside a prefab instance), the instance's name.</summary>
        private static string GameObjectName(YamlFile file, Block gameObject)
        {
            if (!gameObject.Stripped) return Unquote(file.Field(gameObject, "m_Name"));
            Block instance;
            var instanceId = FirstFileId(file.Field(gameObject, "m_PrefabInstance"));
            return instanceId != 0 && file.ById.TryGetValue(instanceId, out instance)
                ? PrefabInstanceName(file, instance) + " (prefab instance)"
                : "?";
        }

        private static string PrefabInstanceName(YamlFile file, Block b)
        {
            for (int i = b.Start + 1; i < b.End - 1; i++)
            {
                if (file.Line(i).Trim() == "propertyPath: m_Name")
                {
                    var v = file.Line(i + 1).Trim();
                    if (v.StartsWith("value:", StringComparison.Ordinal)) return Unquote(v.Substring(6).Trim());
                }
            }
            return "?";
        }

        private static string Unquote(string s)
        {
            if (string.IsNullOrEmpty(s)) return "?";
            if (s.Length >= 2 && (s[0] == '\'' || s[0] == '"') && s[s.Length - 1] == s[0])
                return s.Substring(1, s.Length - 2).Replace("''", "'");
            return s;
        }

        public static string ClassName(int classId)
        {
            switch (classId)
            {
                case 1: return "GameObject";
                case 4: return "Transform";
                case 20: return "Camera";
                case 23: return "MeshRenderer";
                case 33: return "MeshFilter";
                case 54: return "Rigidbody";
                case 64: return "MeshCollider";
                case 65: return "BoxCollider";
                case 82: return "AudioSource";
                case 95: return "Animator";
                case 108: return "Light";
                case 114: return "MonoBehaviour";
                case 135: return "SphereCollider";
                case 136: return "CapsuleCollider";
                case 137: return "SkinnedMeshRenderer";
                case 198: return "ParticleSystem";
                case 199: return "ParticleSystemRenderer";
                case 224: return "RectTransform";
                case 1001: return "PrefabInstance";
                default: return "Object(class " + classId.ToString(CultureInfo.InvariantCulture) + ")";
            }
        }
    }
}
