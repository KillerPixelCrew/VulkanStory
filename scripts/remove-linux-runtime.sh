#!/usr/bin/env bash
# Usage: bash remove-linux-runtime.sh <existing-game> <fresh-external-backup>
# Requires Linux, bash and Python 3 (standard library only). Close game/server first.
# Removes only a verified owned run.sh block and unchanged receipted payload files.
# Keeps modified payloads, saves/settings, other hooks and all unrelated files.
# Backs up the activated script, moves payload into external recovery storage and
# records preserved files. Failure restores dependencies before reattaching the
# previous launch script; concurrent recovery conflicts stop without overwriting.
set -eu
if [ "$#" -ne 2 ]; then
    echo 'usage: bash remove-linux-runtime.sh <existing-game> <fresh-external-backup>' >&2
    exit 2
fi
command -v python3 >/dev/null 2>&1 || { echo 'Python 3 is required for inventory validation.' >&2; exit 2; }
exec python3 - "$@" <<'PY'
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import sys
import tempfile

PROFILE = 'vs-1.22.7-linux-x64'
BEGIN = b'# BEGIN VULKANSTORY STARTUP HOOK v1\n'
END = b'# END VULKANSTORY STARTUP HOOK v1\n'
ORIGINAL_HASH = '65647F3ADFB7C1A13F1CDE0E4450678E8F2141CA095FAC5DC53FF16A8BA2E337'

def digest(path):
    result = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            result.update(chunk)
    return result.hexdigest().upper()

def byte_hash(data):
    return hashlib.sha256(data).hexdigest().upper()

def no_links(path):
    for node in (path, *path.parents):
        if node.is_symlink():
            raise ValueError('Symlink traversal is not supported: ' + str(node))
    return path

def child(directory, relative):
    part = PurePosixPath(relative)
    if not relative or part.is_absolute() or '\\' in relative or any(p in ('', '.', '..') for p in relative.split('/')):
        raise ValueError('Invalid ownership path: ' + relative)
    path = no_links(directory.joinpath(*part.parts))
    if path.exists() and not path.is_file():
        raise ValueError('Expected a regular file: ' + str(path))
    return path

def unique_pairs(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError('Duplicate JSON key: ' + key)
        result[key] = value
    return result

def require_closed(game):
    for process in Path('/proc').iterdir():
        if not process.name.isdecimal():
            continue
        try:
            executable_name = os.readlink(process / 'exe')
            if executable_name.endswith(' (deleted)'):
                executable_name = executable_name[:-10]
            executable = Path(executable_name)
            if executable.parent == game and executable.name in ('Vintagestory', 'VintagestoryServer', 'VSCrashReporter'):
                raise ValueError('Close the game/server/crash reporter before removal.')
            if executable.name == 'dotnet':
                for argument in (process / 'cmdline').read_bytes().split(b'\0')[1:]:
                    candidate = Path(os.fsdecode(argument))
                    if candidate.name in ('Vintagestory.dll', 'VintagestoryServer.dll', 'VSCrashReporter.dll'):
                        base = Path(os.readlink(process / 'cwd'))
                        if Path(os.path.abspath(base / candidate)).parent == game:
                            raise ValueError('Close the dotnet-hosted game/server before removal.')
        except (FileNotFoundError, ProcessLookupError, PermissionError):
            continue

def atomic_bytes(path, data, mode):
    no_links(path)
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
    game = no_links(Path(os.path.abspath(sys.argv[1])))
    backup = no_links(Path(os.path.abspath(sys.argv[2])))
    if not game.is_dir() or backup.exists() or backup == game or game in backup.parents:
        raise ValueError('Use an existing game directory and a fresh external backup.')
    receipt = child(game, 'VulkanStory/install.json')
    if receipt.stat().st_size > 1024 * 1024:
        raise ValueError('Installation receipt exceeds size limit.')
    inventory = json.loads(receipt.read_text(encoding='utf-8-sig'), object_pairs_hook=unique_pairs)
    if inventory.get('schema') != 1 or inventory.get('product') != 'VulkanStory' or inventory.get('profile') != PROFILE:
        raise ValueError('Unknown Linux installation receipt.')
    if inventory.get('rid', 'linux-x64') != 'linux-x64':
        raise ValueError('Installation receipt RID/profile mismatch.')
    files = inventory.get('files')
    if not isinstance(files, dict) or not files or 'VulkanStory/install.json' in files:
        raise ValueError('Invalid installation ownership inventory.')
    run = child(game, 'run.sh')
    if run.stat().st_size > 65536:
        raise ValueError('run.sh exceeds the activation size limit.')
    activated = run.read_bytes()
    activation = inventory.get('activation', {})
    if activation.get('path') != 'run.sh' or activation.get('kind') != 'startup-hook-block-v1' or \
            byte_hash(activated) != activation.get('installedSha256') or activated.count(BEGIN) != 1 or activated.count(END) != 1:
        raise ValueError('Launch script is not unchanged recorded VulkanStory activation.')
    start, end = activated.index(BEGIN), activated.index(END) + len(END)
    if end <= start or end - start > 4096 or byte_hash(activated[start:end]) != activation.get('blockSha256'):
        raise ValueError('Owned startup-hook block does not match its receipt.')
    original = activated[:start] + activated[end:]
    if byte_hash(original) != ORIGINAL_HASH or activation.get('originalSha256') != ORIGINAL_HASH:
        raise ValueError('Removing the block would not restore the original official run.sh.')
    planned, preserved = {}, {}
    for relative, expected in files.items():
        if not relative.startswith(('VulkanStory/', 'Mods/vulkanstory/', 'Mods/vulkanstoryinput/')) or \
                not isinstance(expected, str) or not re.fullmatch('[0-9a-fA-F]{64}', expected):
            raise ValueError('Invalid ownership entry: ' + relative)
        source = child(game, relative)
        if not source.exists():
            continue
        actual = digest(source)
        (planned if actual == expected.upper() else preserved)[relative] = actual
    planned['VulkanStory/install.json'] = digest(receipt)
    require_closed(game)
    backup.mkdir(parents=True)
    shutil.copy2(run, child(backup, 'run.sh'))
    moved = {}
    def record(status):
        (backup / 'removal.json').write_text(json.dumps({'schema': 1, 'product': 'VulkanStory',
            'gameDirectory': str(game), 'status': status, 'files': moved, 'preserved': preserved,
            'restoredLaunchScriptSha256': ORIGINAL_HASH}, indent=2) + '\n', encoding='utf-8')
    record('prepared')
    script_mode = stat.S_IMODE(run.stat().st_mode)
    detached = False
    try:
        if run.read_bytes() != activated:
            raise ValueError('run.sh changed during removal.')
        atomic_bytes(run, original, script_mode)
        detached = True
        if digest(run) != ORIGINAL_HASH:
            raise ValueError('Official run.sh restoration hash mismatch.')
        for relative, expected in planned.items():
            source = child(game, relative)
            if digest(source) != expected:
                raise ValueError('Payload changed during removal: ' + relative)
            destination = child(backup, relative)
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.move(str(source), str(destination))
            moved[relative] = expected
            if digest(destination) != expected:
                raise ValueError('Recovery-backup hash mismatch: ' + relative)
            record('moving')
        record('complete')
    except Exception:
        for relative, expected in reversed(list(moved.items())):
            destination = child(game, relative)
            saved = child(backup, relative)
            if destination.exists() or digest(saved) != expected:
                raise ValueError('Recovery conflict; backup retained at ' + str(backup) + ': ' + relative)
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.move(str(saved), str(destination))
        if detached:
            if run.read_bytes() != original:
                raise ValueError('Launch-script recovery conflict; backup retained at ' + str(backup))
            atomic_bytes(run, activated, script_mode)
        record('rolled-back')
        raise
    print('Restored original official run.sh and detached Linux VulkanStory. Backup: ' + str(backup))
    print('Preserved ' + str(len(preserved)) + ' modified payload files; saves/settings and unrelated files are unchanged.')
    print('No game launch ran; empty directories remain.')

try:
    main()
except (OSError, ValueError, KeyError, TypeError) as error:
    sys.exit('VulkanStory removal stopped: ' + str(error))
PY
