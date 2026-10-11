#!/usr/bin/env bash
# Builds the mod and packs a Thunderstore-ready zip into dist/.
set -euo pipefail
cd "$(dirname "$0")"

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$HOME/.dotnet:$DOTNET_ROOT:$PATH"

dotnet build -c Release

VERSION=$(python3 -c "import json;print(json.load(open('manifest.json'))['version_number'])")
NAME=$(python3 -c "import json;print(json.load(open('manifest.json'))['name'])")
OUT="dist/${NAME}-${VERSION}.zip"
mkdir -p dist
rm -f "$OUT"

python3 - "$VERSION" "$NAME" "$OUT" <<'EOF'
import zipfile, sys
from pathlib import Path
version, name, out = sys.argv[1], sys.argv[2], sys.argv[3]
files = {
    'manifest.json': 'manifest.json',
    'README.md': 'README.md',
    'icon.png': 'icon.png',
    'RunItBack.dll': f'bin/Release/netstandard2.1/RunItBack.dll',
}
with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED) as z:
    for arc, src in files.items():
        z.write(src, arc)
    for path in Path('lang').rglob('*'):
        if path.is_file():
            z.write(path, path.as_posix())
print('wrote', out)
EOF
