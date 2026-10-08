#!/usr/bin/env bash
# One-time installation, never a game launcher.
# Usage: bash install-linux-runtime.sh <extracted-package> <existing-game> <fresh-backup>
# Requires Linux, bash and Python 3 (standard library only). Close game/server first.
# Validates official/payload hashes, loader policy, ELF inputs and ownership before
# writing. Keeps an external backup, writes dependencies before activation, then
# inserts one bounded tagged block before the unchanged official apphost command.
# Updates preserve loader.ini and retain ownership of omitted older payload files.
# Failure restores unchanged writes from backup; recovery conflicts are preserved.
set -eu
if [ "$#" -ne 3 ]; then
    echo 'usage: bash install-linux-runtime.sh <extracted-package> <existing-game> <fresh-external-backup>' >&2
    exit 2
fi
command -v python3 >/dev/null 2>&1 || { echo 'Python 3 is required for inventory validation.' >&2; exit 2; }
exec python3 - "$@" <<'PY'
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shlex
import shutil
import stat
import sys
import tempfile

PROFILE = 'vs-1.22.7-linux-x64'
BEGIN = b'# BEGIN VULKANSTORY STARTUP HOOK v1\n'
END = b'# END VULKANSTORY STARTUP HOOK v1\n'
INVOCATION = b'./Vintagestory "$@"\n'
RECEIPT = 'VulkanStory/install.json'

def digest(path):
    """Hash bytes without loading managed/native code."""
    result = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            result.update(chunk)
    return result.hexdigest().upper()

def byte_hash(data):
    return hashlib.sha256(data).hexdigest().upper()

def no_links(path):
    """Reject symlinks in existing ancestry before resolving an owned path."""
    for node in (path, *path.parents):
        if node.is_symlink():
            raise ValueError('Symlink traversal is not supported: ' + str(node))
    return path

def root(argument, exists=True):
    path = no_links(Path(os.path.abspath(argument)))
    if exists and not path.is_dir():
        raise ValueError('Missing directory: ' + str(path))
    return path

def child(directory, relative):
    part = PurePosixPath(relative)
    if not relative or part.is_absolute() or '\\' in relative or any(p in ('', '.', '..') for p in relative.split('/')):
        raise ValueError('Invalid inventory path: ' + relative)
    path = no_links(directory.joinpath(*part.parts))
    if path.exists() and not path.is_file():
        raise ValueError('Expected a regular file: ' + str(path))
    return path

def owned(relative):
    return relative.startswith(('VulkanStory/', 'Mods/vulkanstory/', 'Mods/vulkanstoryinput/'))

def unique_pairs(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError('Duplicate JSON key: ' + key)
        result[key] = value
    return result

def document(path, limit=1024 * 1024):
    if path.stat().st_size > limit:
        raise ValueError('Inventory exceeds size limit: ' + str(path))
    return json.loads(path.read_text(encoding='utf-8-sig'), object_pairs_hook=unique_pairs)

def inventory(data):
    if data.get('schema') != 1 or data.get('product') != 'VulkanStory' or data.get('profile') != PROFILE:
        raise ValueError('Unsupported Linux runtime inventory.')
    if data.get('rid', 'linux-x64') != 'linux-x64':
        raise ValueError('Runtime inventory RID/profile mismatch.')
    files = data.get('files')
    if not isinstance(files, dict) or not files:
        raise ValueError('Empty runtime inventory.')
    result = {}
    for relative, expected in files.items():
        if relative.startswith('optional-server/'):
            continue
        if relative == RECEIPT or not owned(relative) or not isinstance(expected, str) or not re.fullmatch('[0-9a-fA-F]{64}', expected):
            raise ValueError('Invalid ownership entry: ' + relative)
        result[relative] = expected.upper()
    return result

def loader_enabled(path):
    """Match the managed bootstrap's bounded [Bootstrap] Enabled integer policy."""
    if path.stat().st_size > 65536:
        raise ValueError('Loader configuration exceeds size limit.')
    section = False
    enabled = True
    for raw in path.read_text(encoding='utf-8-sig').splitlines():
        line = raw.strip()
        if not line or line.startswith((';', '#')):
            continue
        if line.startswith('[') and line.endswith(']'):
            section = line[1:-1].strip().lower() == 'bootstrap'
        elif section and '=' in line:
            key, value = line.split('=', 1)
            if key.strip().lower() == 'enabled':
                if not re.fullmatch('[+-]?[0-9]+', value.strip()) or not -2147483648 <= int(value) <= 2147483647:
                    raise ValueError('Bootstrap Enabled must be an integer (0 disables activation).')
                enabled = int(value) != 0
    return enabled

def require_closed(game):
    """Inspect Linux process metadata; never launch, signal or close an application."""
    for process in Path('/proc').iterdir():
        if not process.name.isdecimal():
            continue
        try:
            executable_name = os.readlink(process / 'exe')
            if executable_name.endswith(' (deleted)'):
                executable_name = executable_name[:-10]
            executable = Path(executable_name)
            if executable.parent == game and executable.name in ('Vintagestory', 'VintagestoryServer', 'VSCrashReporter'):
                raise ValueError('Close the game/server/crash reporter before installation.')
            if executable.name == 'dotnet':
                arguments = (process / 'cmdline').read_bytes().split(b'\0')
                for argument in arguments[1:]:
                    candidate = Path(os.fsdecode(argument))
                    if candidate.name in ('Vintagestory.dll', 'VintagestoryServer.dll', 'VSCrashReporter.dll'):
                        base = Path(os.readlink(process / 'cwd'))
                        if Path(os.path.abspath(base / candidate)).parent == game:
                            raise ValueError('Close the dotnet-hosted game/server before installation.')
        except (FileNotFoundError, ProcessLookupError, PermissionError):
            continue

def atomic_bytes(path, data, mode=0o644):
    """Publish a sibling temporary file without replacing through a symlink."""
    no_links(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary = tempfile.mkstemp(prefix='.vulkanstory-', dir=path.parent)
    try:
        with os.fdopen(descriptor, 'wb') as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.chmod(temporary, mode)
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)

def main():
    if not sys.platform.startswith('linux'):
        raise ValueError('This helper requires native Linux.')
    package, game, backup = root(sys.argv[1]), root(sys.argv[2]), root(sys.argv[3], False)
    for left, right in ((package, game), (game, package), (backup, game), (backup, package)):
        if left == right or right in left.parents:
            raise ValueError('Package, installation and external backup directories must be separate.')
    if backup.exists():
        raise ValueError('Choose a fresh external backup directory.')
    if any(character in str(game) for character in (':', '\n', '\r')):
        raise ValueError('Installation path cannot contain a startup-hook separator or newline.')
    package_path = child(package, 'VulkanStory/package.json')
    manifest = document(package_path)
    sources = inventory(manifest)
    required = ('VulkanStory/loader.ini', 'VulkanStory/managed/VulkanStory.Bootstrap.dll',
                'VulkanStory/managed/VulkanStory.Game.dll', 'VulkanStory/managed/profiles/' + PROFILE + '.json',
                'VulkanStory/native/linux-x64/libSDL3.so', 'VulkanStory/native/linux-x64/libshaderc_shared.so',
                'VulkanStory/native-inventory.json', 'VulkanStory/tools/remove-linux-runtime.sh',
                'Mods/vulkanstory/modinfo.json', 'Mods/vulkanstoryinput/modinfo.json',
                'Mods/vulkanstoryinput/VulkanStory.Input.Companion.dll', 'Mods/vulkanstoryinput/VulkanStory.Input.dll')
    if any(name not in sources for name in required) or RECEIPT in sources or 'VulkanStory/package.json' in sources:
        raise ValueError('Incomplete or invalid Linux client inventory.')
    for relative, expected in sources.items():
        if digest(child(package, relative)) != expected:
            raise ValueError('Package hash mismatch: ' + relative)
    profile = document(child(package, 'VulkanStory/managed/profiles/' + PROFILE + '.json'), 65536)
    if profile.get('schema') != 1 or profile.get('id') != PROFILE or profile.get('runtimeMajor') != 10:
        raise ValueError('Invalid official Linux game profile.')
    if not isinstance(profile.get('files'), dict) or not profile['files']:
        raise ValueError('Official Linux game profile has no file identities.')
    for relative, expected in profile['files'].items():
        if digest(child(game, relative)) != expected.upper():
            raise ValueError('Official game file mismatch: ' + relative)
    native = document(child(package, 'VulkanStory/native-inventory.json'), 65536)
    if native.get('schema') != 1 or native.get('rid') != 'linux-x64':
        raise ValueError('Native inventory RID/schema mismatch.')
    for relative in sources:
        if relative.startswith('VulkanStory/native/'):
            if not relative.startswith('VulkanStory/native/linux-x64/'):
                raise ValueError('Foreign native RID in Linux payload: ' + relative)
            with child(package, relative).open('rb') as stream:
                header = stream.read(20)
            if len(header) != 20 or header[:6] != b'\x7fELF\x02\x01' or int.from_bytes(header[18:20], 'little') != 62:
                raise ValueError('Native input is not Linux x64 ELF: ' + relative)
    for notice in native.get('requiredNotices', []):
        if 'VulkanStory/licenses/native/' + notice not in sources:
            raise ValueError('Missing native notice: ' + notice)
    sources['VulkanStory/package.json'] = digest(package_path)
    receipt_path = child(game, RECEIPT)
    previous = document(receipt_path) if receipt_path.exists() else None
    existing = inventory(previous) if previous else {}
    for relative in existing:
        child(game, relative)
    run = child(game, 'run.sh')
    current = run.read_bytes()
    if len(current) > 65536:
        raise ValueError('run.sh exceeds the activation size limit.')
    original = current
    if BEGIN in current or END in current:
        activation = previous.get('activation', {}) if previous else {}
        if current.count(BEGIN) != 1 or current.count(END) != 1 or byte_hash(current) != activation.get('installedSha256'):
            raise ValueError('Existing launch-script block is not unchanged owned activation.')
        start, end = current.index(BEGIN), current.index(END) + len(END)
        if end <= start or end - start > 4096 or byte_hash(current[start:end]) != activation.get('blockSha256'):
            raise ValueError('Invalid owned startup-hook block.')
        original = current[:start] + current[end:]
    elif previous:
        raise ValueError('Recorded startup-hook activation is missing; recover before updating.')
    if profile.get('launchScript') != 'run.sh' or byte_hash(original) != profile.get('launchScriptSha256'):
        raise ValueError('run.sh does not match the original official launch script.')
    if original.count(INVOCATION) != 1:
        raise ValueError('Official apphost invocation is missing or ambiguous.')
    hook = shlex.quote(str(child(game, 'VulkanStory/managed/VulkanStory.Bootstrap.dll')))
    block = BEGIN + (f'vulkanstory_startup_hook={hook}\n' + r'''vulkanstory_enabled=1
vulkanstory_section=0
vulkanstory_bytes=0
if [ -r "$SCRIPT_DIR/VulkanStory/loader.ini" ] && [ -r "$vulkanstory_startup_hook" ]; then
    while IFS= read -r vulkanstory_line || [ -n "$vulkanstory_line" ]; do
        vulkanstory_bytes=$((vulkanstory_bytes + ${#vulkanstory_line} + 1))
        if [ "$vulkanstory_bytes" -gt 65536 ]; then vulkanstory_enabled=0; break; fi
        vulkanstory_line=${vulkanstory_line#$'\xef\xbb\xbf'}
        vulkanstory_line=${vulkanstory_line%$'\r'}
        vulkanstory_line=${vulkanstory_line,,}
        if [[ "$vulkanstory_line" =~ ^[[:space:]]*\[[[:space:]]*bootstrap[[:space:]]*\][[:space:]]*$ ]]; then
            vulkanstory_section=1
        elif [[ "$vulkanstory_line" =~ ^[[:space:]]*\[.*\][[:space:]]*$ ]]; then
            vulkanstory_section=0
        elif [ "$vulkanstory_section" -eq 1 ] && [[ "$vulkanstory_line" =~ ^[[:space:]]*enabled[[:space:]]*= ]]; then
            if [[ "$vulkanstory_line" =~ ^[[:space:]]*enabled[[:space:]]*=[[:space:]]*([+-]?[0-9]+)[[:space:]]*$ ]]; then
                if [[ "${BASH_REMATCH[1]}" =~ ^[+-]?0+$ ]]; then vulkanstory_enabled=0; else vulkanstory_enabled=1; fi
            else
                vulkanstory_enabled=0
                break
            fi
        fi
    done < "$SCRIPT_DIR/VulkanStory/loader.ini"
    if [ "$vulkanstory_enabled" -eq 1 ]; then
        case ":${DOTNET_STARTUP_HOOKS-}:" in
            *":$vulkanstory_startup_hook:"*) ;;
            *) export DOTNET_STARTUP_HOOKS="${DOTNET_STARTUP_HOOKS:+$DOTNET_STARTUP_HOOKS:}$vulkanstory_startup_hook" ;;
        esac
    fi
fi
unset vulkanstory_startup_hook vulkanstory_enabled vulkanstory_section vulkanstory_bytes vulkanstory_line
''').encode() + END
    if len(block) > 4096:
        raise ValueError('Startup-hook block exceeds the size limit.')
    activated = original.replace(INVOCATION, block + INVOCATION, 1)
    writes = {}
    for relative, expected in sources.items():
        destination = child(game, relative)
        if destination.exists():
            if relative == 'VulkanStory/loader.ini' and relative in existing:
                continue
            actual = digest(destination)
            if relative not in existing or actual != existing[relative]:
                raise ValueError('Existing payload is not unchanged owned data: ' + relative)
            if actual == expected:
                continue
        writes[relative] = expected
    loader = child(game, 'VulkanStory/loader.ini')
    if 'VulkanStory/loader.ini' not in existing or not loader.exists():
        loader = child(package, 'VulkanStory/loader.ini')
    enabled = loader_enabled(loader)
    if not writes and current == activated:
        print('Linux runtime is already installed; the original script has one owned activation block.')
        return
    require_closed(game)
    backup.mkdir(parents=True)
    for relative in (*writes, RECEIPT, 'run.sh'):
        source = child(game, relative)
        if source.exists():
            destination = child(backup, relative)
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, destination)
    (backup / 'deployment.json').write_text(json.dumps({'schema': 1, 'gameDirectory': str(game),
        'status': 'prepared', 'plannedWrites': list(writes)}, indent=2) + '\n', encoding='utf-8')
    changed = {}
    try:
        for relative, expected in writes.items():
            source = child(package, relative)
            payload = source.read_bytes()
            if byte_hash(payload) != expected:
                raise ValueError('Package changed during installation: ' + relative)
            destination = child(game, relative)
            atomic_bytes(destination, payload, stat.S_IMODE(source.stat().st_mode))
            changed[relative] = expected
        if run.read_bytes() != current:
            raise ValueError('run.sh changed during installation.')
        atomic_bytes(run, activated, stat.S_IMODE(run.stat().st_mode))
        changed['run.sh'] = byte_hash(activated)
        installed = dict(existing)
        for relative, expected in sources.items():
            actual = digest(child(game, relative))
            if actual != expected and not (relative == 'VulkanStory/loader.ini' and relative in existing):
                raise ValueError('Installed payload changed: ' + relative)
            installed[relative] = actual
        record = {'schema': 1, 'product': 'VulkanStory', 'profile': PROFILE, 'rid': 'linux-x64',
            'acceptance': manifest.get('acceptance', 'unverified'), 'files': installed, 'backupDirectory': str(backup),
            'activation': {'path': 'run.sh', 'kind': 'startup-hook-block-v1', 'originalSha256': byte_hash(original),
                           'installedSha256': byte_hash(activated), 'blockSha256': byte_hash(block)}}
        data = (json.dumps(record, indent=2) + '\n').encode()
        atomic_bytes(receipt_path, data)
        changed[RECEIPT] = byte_hash(data)
    except Exception:
        # Detach new activation, restore dependencies, then restore prior activation.
        # Keep all concurrently modified files rather than overwriting a conflict.
        restore_activation = 'run.sh' in changed
        if restore_activation:
            if run.read_bytes() != activated:
                raise ValueError('Launch-script recovery conflict; backup retained at ' + str(backup))
            atomic_bytes(run, original, stat.S_IMODE(run.stat().st_mode))
            del changed['run.sh']
        for relative in reversed(list(changed)):
            destination = child(game, relative)
            if not destination.exists() or digest(destination) != changed[relative]:
                raise ValueError('Recovery conflict; backup retained at ' + str(backup) + ': ' + relative)
            saved = child(backup, relative)
            if saved.exists():
                atomic_bytes(destination, saved.read_bytes(), stat.S_IMODE(saved.stat().st_mode))
            else:
                destination.unlink()
            del changed[relative]
        if restore_activation and current != original:
            if run.read_bytes() != original:
                raise ValueError('Launch-script recovery conflict; backup retained at ' + str(backup))
            atomic_bytes(run, current, stat.S_IMODE(child(backup, 'run.sh').stat().st_mode))
        raise
    print('Installed Linux payload and one startup-hook block. Original apphost command/arguments are preserved.')
    print('Loader activation is ' + ('enabled' if enabled else 'disabled') + '; backup: ' + str(backup))
    print('No game launch or Linux renderer/provider acceptance was performed.')

try:
    main()
except (OSError, ValueError, KeyError, TypeError) as error:
    sys.exit('VulkanStory installation stopped: ' + str(error))
PY
