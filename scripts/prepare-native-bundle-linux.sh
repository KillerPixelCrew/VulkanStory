#!/usr/bin/env bash
# prepare-native-bundle-linux.sh <output directory> [Configuration] [SdlDirectory] [ShadercDirectory] [VmaDirectory] [NgxShim]
# Collects the declared Linux native runtimes, the required VMA allocator and the
# redistribution notices into a fresh bundle.
# Maps packaging/native-linux-x64.json to explicit core sources, verifies every
# pinned input identity against that inventory, then copies native/ and licenses/
# into a fresh tree with bundle.json hashes. Optional provider components are
# copied only when their complete input set exists. Missing inputs or identity
# mismatches stop the run; no provider execution, builds, tests, installation or
# game launch occur. This is the Linux counterpart of prepare-native-bundle.ps1,
# which collects the Windows runtimes and provider bridges.
# Requires bash and Python 3 (standard library only).
set -euo pipefail
if [ "$#" -gt 6 ]; then
    echo 'usage: bash prepare-native-bundle-linux.sh <output directory> [Configuration] [SdlDirectory] [ShadercDirectory] [VmaDirectory] [NgxShim]' >&2
    exit 2
fi
out="${1:?usage: bash prepare-native-bundle-linux.sh <output directory> [Configuration] [SdlDirectory] [ShadercDirectory] [VmaDirectory] [NgxShim]}"
configuration="${2:-Release}"
sdl_directory="${3:-}"
shaderc_directory="${4:-}"
vma_directory="${5:-}"
ngx_shim="${6:-}"
command -v python3 >/dev/null 2>&1 || { echo 'Python 3 is required for inventory validation.' >&2; exit 2; }
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec python3 - "$here/.." "$out" "$configuration" "$sdl_directory" "$shaderc_directory" "$vma_directory" "$ngx_shim" <<'PY'
import hashlib
import json
import os
from pathlib import Path
import shutil
import sys

def digest(path):
    result = hashlib.sha256()
    with open(path, 'rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            result.update(chunk)
    return result.hexdigest().upper()

def unique_pairs(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError('Duplicate JSON key: ' + key)
        result[key] = value
    return result

def assert_identity(path, expected):
    if not Path(path).is_file():
        raise ValueError('Missing native input: ' + str(path))
    actual = digest(path)
    if actual != expected.upper():
        raise ValueError('Native input identity mismatch: ' + str(path) + ' (' + actual + ')')
    return actual

def main():
    project = Path(os.path.abspath(sys.argv[1]))
    output = Path(os.path.abspath(sys.argv[2]))
    configuration = sys.argv[3]
    sdl_directory = sys.argv[4]
    shaderc_directory = sys.argv[5]
    vma_directory = sys.argv[6]
    ngx_shim = sys.argv[7]
    if configuration not in ('Debug', 'Release'):
        raise ValueError('Configuration must be Debug or Release.')
    if output.exists():
        raise ValueError('Choose a fresh output directory; nothing is merged or deleted.')
    nuget = os.environ.get('NUGET_PACKAGES') or str(Path.home() / '.nuget' / 'packages')
    if not sdl_directory:
        sdl_directory = str(Path(nuget) / 'ppy.sdl3-cs/2026.722.0/runtimes/linux-x64/native')
    if not shaderc_directory:
        shaderc_directory = str(Path(nuget) / 'silk.net.shaderc.native/2.23.0/runtimes/linux-x64/native')
    if not vma_directory:
        vma_directory = str(project / 'artifacts/native-vma' / configuration / 'linux-x64')

    inventory = json.loads((project / 'packaging/native-linux-x64.json').read_text(encoding='utf-8-sig'),
                           object_pairs_hook=unique_pairs)
    if not isinstance(inventory, dict) or inventory.get('schema') != 1 or inventory.get('rid') != 'linux-x64':
        raise ValueError('Native inventory RID/schema mismatch.')

    native = output / 'native'
    licenses = output / 'licenses/native'
    native.mkdir(parents=True)
    licenses.mkdir(parents=True)

    # Pinned inputs: identity-checked against the inventory, then copied.
    resolved = {
        'libSDL3.so': str(Path(sdl_directory) / 'libSDL3.so'),
        'libshaderc_shared.so': str(Path(shaderc_directory) / 'libshaderc_shared.so'),
    }
    pinned = inventory.get('sha256', {})
    hashes = {}
    for name, source in resolved.items():
        expected = pinned.get(name)
        if not expected:
            raise ValueError('No pinned hash for ' + name + '.')
        hashes[name] = assert_identity(source, expected)
        shutil.copy2(source, native / name)

    # Required allocator bridge, built from the pinned sdk/vma submodule (not hash-pinned in the
    # inventory, so only its presence is required here).
    vma_binary = Path(vma_directory) / 'libVulkanStoryVma.so'
    if not vma_binary.is_file():
        raise ValueError('Missing required native input: ' + str(vma_binary))
    hashes['libVulkanStoryVma.so'] = digest(vma_binary)
    shutil.copy2(vma_binary, native / 'libVulkanStoryVma.so')

    # Optional provider shim: copied only when the caller supplies it. The inventory
    # pins no hash for it, so only its presence is verified here.
    if ngx_shim:
        optional = inventory.get('optionalComponents', {}).get('dlss', [])
        missing = [entry for entry in optional if entry != 'libVulkanStoryNgx.so']
        if missing:
            raise ValueError('Unhandled optional component entries: ' + ', '.join(missing))
        hashes['libVulkanStoryNgx.so'] = assert_identity(ngx_shim, digest(ngx_shim))
        shutil.copy2(ngx_shim, native / 'libVulkanStoryNgx.so')

    # License tree exactly matching the inventory's requiredNotices.
    notice_root = project / 'packaging/notices/native'
    notices = {
        'core/SDL-LICENSE.txt': notice_root / 'SDL-LICENSE.txt',
        'core/shaderc/AUTHORS': notice_root / 'shaderc/AUTHORS',
        'core/shaderc/LICENSE': notice_root / 'shaderc/LICENSE',
        'core/glslang/LICENSE.txt': notice_root / 'glslang/LICENSE.txt',
        'core/SPIRV-Tools/LICENSE': notice_root / 'SPIRV-Tools/LICENSE',
        'core/SPIRV-Headers/LICENSE': notice_root / 'SPIRV-Headers/LICENSE',
        'vma/LICENSE.txt': Path(vma_directory) / 'licenses/vma/LICENSE.txt',
    }
    for required in inventory.get('requiredNotices', []):
        if required not in notices:
            raise ValueError('Inventory requires an unmapped notice: ' + required)
    notice_hashes = {}
    for relative, source in notices.items():
        if not source.is_file():
            raise ValueError('Missing required native notice: ' + relative)
        destination = licenses / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, destination)
        notice_hashes[relative] = digest(destination)

    bundle = {'schema': 1, 'rid': 'linux-x64', 'acceptance': 'unverified', 'files': hashes, 'notices': notice_hashes}
    (output / 'bundle.json').write_text(json.dumps(bundle, indent=2) + '\n', encoding='utf-8')
    print('Collected Linux native bundle at %s (%d runtime(s), %d notice(s)). No installation or game launch ran.'
          % (output, len(hashes), len(notices)))

try:
    main()
except (OSError, ValueError, KeyError, TypeError) as error:
    sys.exit('VulkanStory native bundle stopped: ' + str(error))
PY
