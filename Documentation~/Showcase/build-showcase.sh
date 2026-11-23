#!/usr/bin/env bash
# Rebuilds the README screenshots (Documentation~/images) from a throwaway sample project.
# Needs Python with Pillow for the last step (trimming and the composed showcase.png).
#
# usage: build-showcase.sh <empty Unity 2022.3 project dir> [<Unity.exe>]
#   Create the project first, e.g.: Unity.exe -batchmode -quit -createProject <dir>
#
# Steps: copy the showcase scripts in, reference this package from disk, build the baseline (batch mode),
# commit it, stage a rename, apply the working-copy changes (batch mode), then open the editor (GUI mode) to capture
# the Project, Hierarchy and Inspector windows. The editor quits by itself when the screenshots are written.
set -euo pipefail

proj=$(cd "$1" && pwd)
unity=${2:-"/c/Program Files/Unity/Hub/Editor/2022.3.22f1/Editor/Unity.exe"}
here=$(cd "$(dirname "$0")" && pwd)
pkg=$(cd "$here/../.." && pwd)
images="$pkg/Documentation~/images"

winpath() { if command -v cygpath >/dev/null; then cygpath -m "$1"; else echo "$1"; fi; }

mkdir -p "$proj/Assets/Editor" "$proj/Assets/Scripts"
cp "$here/Editor/Showcase.cs" "$proj/Assets/Editor/"
cp "$here/Spinner.cs" "$proj/Assets/Scripts/"
python - "$proj/Packages/manifest.json" "file:$(winpath "$pkg")" <<'EOF'
import json, sys
path, ref = sys.argv[1], sys.argv[2]
m = json.load(open(path, encoding='utf-8'))
m['dependencies']['com.data-unknown.git-status-overlay'] = ref
json.dump(m, open(path, 'w', encoding='utf-8'), indent=2)
EOF
cat > "$proj/.gitignore" <<'EOF'
/[Ll]ibrary/
/[Tt]emp/
/[Oo]bj/
/[Bb]uild/
/[Ll]ogs/
/[Uu]ser[Ss]ettings/
/Screenshots/
*.csproj
*.sln
EOF

run() { "$unity" -batchmode -quit -projectPath "$(winpath "$proj")" -logFile "$(winpath "$proj")/Logs/showcase-$1.log" -executeMethod "GitStatusOverlayShowcase.Showcase.$1"; }

run Baseline
cd "$proj"
git init -q -b main
git add -A
git -c user.name=Showcase -c user.email=showcase@example.invalid commit -q -m "Baseline"
git mv "Assets/Materials/Blue.mat" "Assets/Materials/Crate Blue.mat"
git mv "Assets/Materials/Blue.mat.meta" "Assets/Materials/Crate Blue.mat.meta"
run Changes
git status --short

"$unity" -projectPath "$(winpath "$proj")" -logFile "$(winpath "$proj")/Logs/showcase-capture.log" -executeMethod GitStatusOverlayShowcase.Showcase.CaptureAndQuit
python "$(winpath "$here/compose.py")" "$(winpath "$proj/Screenshots")" "$(winpath "$images")"
ls -la "$images"
