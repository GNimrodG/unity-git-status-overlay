using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace GitStatusOverlay
{
    /// <summary>
    /// Maps one line of a Unity YAML document to the SerializedProperty path it belongs to, e.g.
    /// <c>m_LocalPosition</c>, <c>m_Materials.Array.data[0]</c>, <c>m_SavedProperties.m_Floats.Array.data[12].second</c>
    /// (shown as <c>m_SavedProperties.m_Floats[_Glossiness]</c>) or, for [SerializeReference] data such as VRCFury's,
    /// <c>content.sliderBitWidthOverride</c> instead of <c>references.RefIds.Array.data[0].data.sliderBitWidthOverride</c>.
    /// </summary>
    internal static class YamlPropertyPath
    {
        private const string RefIdsPrefix = "references.RefIds.Array.data[";
        private const int MaxRefDepth = 8;

        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
        private static readonly Regex KeyRx = new Regex(@"^([A-Za-z_]\w*):(?: (.*)|$)", RegexOptions.Compiled, RegexTimeout);

        /// <summary>std::map-style lists serialized as "- key: value" pairs (first/second).</summary>
        private static readonly HashSet<string> PairLists = new HashSet<string>(StringComparer.Ordinal)
        {
            "m_Floats", "m_Ints", "m_Colors", "m_TexEnvs",
        };

        /// <summary>Path segment plus how to show it ("Array.data[3]" is shown as "[3]", a pair as "[_Name]").</summary>
        private struct Seg
        {
            public string Path;
            public string Display;
            public bool Indexer;
        }

        public static bool TryGet(UnityYamlDiff.YamlFile file, UnityYamlDiff.Block block, int line,
            out string path, out string display, out string value)
        {
            path = display = value = null;
            if (!TryBasic(file, block, line, out var segs, out value)) return false;
            path = JoinPath(segs);
            display = JoinDisplay(segs);
            ResolveManagedReferences(file, block, ref path, ref display);
            // Materials: "m_SavedProperties.m_Floats[_Glossiness]" → "_Glossiness" (the shader property name).
            const string SavedProps = "m_SavedProperties.m_";
            int open = display.IndexOf('[');
            int close = display.IndexOf(']');
            if (display.StartsWith(SavedProps, StringComparison.Ordinal) && open > 0 && close > open)
                display = display.Substring(open + 1, close - open - 1) + display.Substring(close + 1);
            return true;
        }

        private static bool TryBasic(UnityYamlDiff.YamlFile file, UnityYamlDiff.Block block, int line, out List<Seg> segs, out string value)
        {
            segs = new List<Seg>();
            value = null;
            int first = block.Start + 2; // skip "--- !u!..." and "ClassName:"
            if (line < first || line >= block.End) return false;

            int cur = line;
            string text = file.Line(cur);
            int ind = UnityYamlDiff.Indent(text);
            string body = text.Substring(ind);
            value = body.Trim();
            if (body.Length == 0) return false;

            // A line without "key:" continues a wrapped value: the owner is the nearest line indented less.
            if (!body.StartsWith("- ", StringComparison.Ordinal) && !KeyRx.IsMatch(body))
            {
                cur = FindOwner(file, block, cur, ind);
                if (cur < 0) return false;
                text = file.Line(cur);
                ind = UnityYamlDiff.Indent(text);
                body = text.Substring(ind);
            }

            if (body.StartsWith("- ", StringComparison.Ordinal))
            {
                var m = KeyRx.Match(body.Substring(2));
                string listKey;
                int index;
                if (!ListOf(file, block, cur, ind, out listKey, out index)) return false;
                AddItem(segs, m.Success ? m.Groups[1].Value : null, listKey, index);
                if (!AddKey(segs, listKey)) return false;
                cur = FindListKeyLine(file, block, cur, ind);
                if (cur < 0) return false;
            }
            else
            {
                var m = KeyRx.Match(body);
                segs.Add(new Seg { Path = m.Groups[1].Value, Display = m.Groups[1].Value });
            }

            return Climb(file, block, segs, cur, UnityYamlDiff.Indent(file.Line(cur)));
        }

        /// <summary>Walks up to the top-level field (indent 2), adding parent keys and list indexes.</summary>
        private static bool Climb(UnityYamlDiff.YamlFile file, UnityYamlDiff.Block block, List<Seg> segs, int cur, int ind)
        {
            int first = block.Start + 2;
            while (ind > 2)
            {
                int found = -1;
                for (int j = cur - 1; j >= first; j--)
                {
                    var t = file.Line(j);
                    int ti = UnityYamlDiff.Indent(t);
                    if (ti >= ind || t.Length == ti) continue;
                    var tb = t.Substring(ti);
                    if (tb.StartsWith("- ", StringComparison.Ordinal))
                    {
                        // We're inside a list item. Deeper than its content column means we're under the item's inline key.
                        var m = KeyRx.Match(tb.Substring(2));
                        string listKey;
                        int index;
                        if (!ListOf(file, block, j, ti, out listKey, out index)) return false;
                        AddItem(segs, ind > ti + 2 && m.Success ? m.Groups[1].Value : null, listKey, index);
                        if (!AddKey(segs, listKey)) return false;
                        found = FindListKeyLine(file, block, j, ti);
                    }
                    else
                    {
                        var m = KeyRx.Match(tb);
                        if (!m.Success) return false;
                        // "key: {flow, wrapped" owns the continuation lines below it: they are not child fields.
                        if (m.Groups[2].Success && m.Groups[2].Value.Length > 0) segs.Clear();
                        segs.Add(new Seg { Path = m.Groups[1].Value, Display = m.Groups[1].Value });
                        found = j;
                    }
                    break;
                }
                if (found < 0) return false;
                cur = found;
                ind = UnityYamlDiff.Indent(file.Line(cur));
            }
            segs.Reverse();
            return segs.Count > 0;
        }

        private static int FindOwner(UnityYamlDiff.YamlFile file, UnityYamlDiff.Block block, int line, int ind)
        {
            for (int j = line - 1; j >= block.Start + 2; j--)
            {
                var t = file.Line(j);
                if (t.Length > 0 && UnityYamlDiff.Indent(t) < ind) return j;
            }
            return -1;
        }

        /// <summary>For a "- " item line: the list's key and the item's index (Unity puts items at the key's indent).</summary>
        private static bool ListOf(UnityYamlDiff.YamlFile file, UnityYamlDiff.Block block, int itemLine, int ind, out string listKey, out int index)
        {
            listKey = null;
            index = 0;
            int keyLine = FindListKeyLine(file, block, itemLine, ind);
            if (keyLine < 0) return false;
            for (int j = keyLine + 1; j < itemLine; j++)
            {
                var t = file.Line(j);
                if (UnityYamlDiff.Indent(t) == ind && t.Length > ind + 1 && t[ind] == '-' && t[ind + 1] == ' ') index++;
            }
            var kb = file.Line(keyLine);
            var m = KeyRx.Match(kb.Substring(UnityYamlDiff.Indent(kb)));
            if (!m.Success) return false;
            listKey = m.Groups[1].Value;
            return true;
        }

        private static int FindListKeyLine(UnityYamlDiff.YamlFile file, UnityYamlDiff.Block block, int itemLine, int ind)
        {
            for (int j = itemLine - 1; j >= block.Start + 2; j--)
            {
                var t = file.Line(j);
                int ti = UnityYamlDiff.Indent(t);
                if (t.Length == ti || ti > ind) continue;
                if (ti == ind && t.Length > ti + 1 && t[ti] == '-' && t[ti + 1] == ' ') continue;
                return j;
            }
            return -1;
        }

        private static void AddItem(List<Seg> segs, string innerKey, string listKey, int index)
        {
            var idx = index.ToString(CultureInfo.InvariantCulture);
            if (innerKey != null && PairLists.Contains(listKey))
            {
                // "- _Color: {r: 1, ...}" is a pair: the value is ".second", shown by its name.
                segs.Add(new Seg { Path = "Array.data[" + idx + "].second", Display = "[" + innerKey + "]", Indexer = true });
                return;
            }
            if (innerKey != null) segs.Add(new Seg { Path = innerKey, Display = innerKey });
            segs.Add(new Seg { Path = "Array.data[" + idx + "]", Display = "[" + idx + "]", Indexer = true });
        }

        private static bool AddKey(List<Seg> segs, string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            segs.Add(new Seg { Path = key, Display = key });
            return true;
        }

        private static string JoinPath(List<Seg> segs)
        {
            var parts = new string[segs.Count];
            for (int i = 0; i < segs.Count; i++) parts[i] = segs[i].Path;
            return string.Join(".", parts);
        }

        private static string JoinDisplay(List<Seg> segs)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var s in segs)
            {
                if (sb.Length > 0 && !s.Indexer) sb.Append('.');
                sb.Append(s.Display);
            }
            return sb.ToString();
        }

        /// <summary>
        /// [SerializeReference] values live under references.RefIds[k].data; the field that holds them only stores
        /// "rid: N". Rewrite the path to that field's path so it matches what the Inspector draws.
        /// </summary>
        private static void ResolveManagedReferences(UnityYamlDiff.YamlFile file, UnityYamlDiff.Block block, ref string path, ref string display)
        {
            for (int depth = 0; depth < MaxRefDepth && path.StartsWith(RefIdsPrefix, StringComparison.Ordinal); depth++)
            {
                int close = path.IndexOf(']', RefIdsPrefix.Length);
                if (close < 0) return;
                int k;
                if (!int.TryParse(path.Substring(RefIdsPrefix.Length, close - RefIdsPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out k)) return;
                var rest = path.Substring(close + 1);
                string rid = RefIdAt(file, block, k);
                if (rid == null) return;
                int owner = FindRidOwner(file, block, rid);
                if (owner < 0) return;
                List<Seg> segs;
                string unused;
                if (!TryBasic(file, block, owner, out segs, out unused)) return;
                var ownerPath = JoinPath(segs);
                var ownerDisplay = JoinDisplay(segs);
                if (ownerPath.EndsWith(".rid", StringComparison.Ordinal)) ownerPath = ownerPath.Substring(0, ownerPath.Length - 4);
                if (ownerDisplay.EndsWith(".rid", StringComparison.Ordinal)) ownerDisplay = ownerDisplay.Substring(0, ownerDisplay.Length - 4);
                // ".data.x" is a field of the referenced object; ".type"/".rid" means the reference itself changed.
                var tail = rest.StartsWith(".data", StringComparison.Ordinal) ? rest.Substring(5) : "";
                path = ownerPath + tail;
                display = ownerDisplay + tail.Replace(".Array.data[", "[");
            }
        }

        private static string RefIdAt(UnityYamlDiff.YamlFile file, UnityYamlDiff.Block block, int k)
        {
            int refIdsLine = -1, refIdsIndent = 0;
            for (int i = block.Start + 2; i < block.End; i++)
            {
                var t = file.Line(i);
                if (t.TrimStart().StartsWith("RefIds:", StringComparison.Ordinal))
                {
                    refIdsLine = i;
                    refIdsIndent = UnityYamlDiff.Indent(t);
                    break;
                }
            }
            if (refIdsLine < 0) return null;
            int n = 0;
            for (int i = refIdsLine + 1; i < block.End; i++)
            {
                var t = file.Line(i);
                int ti = UnityYamlDiff.Indent(t);
                if (t.Length == ti) continue;
                if (ti < refIdsIndent) break;
                if (ti != refIdsIndent || !t.Substring(ti).StartsWith("- rid: ", StringComparison.Ordinal)) continue;
                if (n++ == k) return t.Substring(ti + 7).Trim();
            }
            return null;
        }

        /// <summary>The "rid: N" line of the field that references N (not the RefIds entry itself).</summary>
        private static int FindRidOwner(UnityYamlDiff.YamlFile file, UnityYamlDiff.Block block, string rid)
        {
            for (int i = block.Start + 2; i < block.End; i++)
            {
                var t = file.Line(i);
                var tb = t.TrimStart();
                bool isRid = tb == "rid: " + rid || tb == "- rid: " + rid;
                if (!isRid) continue;
                // RefIds entries are "- rid: N" directly under "RefIds:" (same indent as that key).
                bool isEntry = tb.StartsWith("- ", StringComparison.Ordinal) && IsUnderRefIds(file, block, i);
                if (!isEntry) return i;
            }
            return -1;
        }

        private static bool IsUnderRefIds(UnityYamlDiff.YamlFile file, UnityYamlDiff.Block block, int line)
        {
            int ind = UnityYamlDiff.Indent(file.Line(line));
            int keyLine = FindListKeyLine(file, block, line, ind);
            return keyLine >= 0 && file.Line(keyLine).TrimStart().StartsWith("RefIds:", StringComparison.Ordinal);
        }
    }
}
