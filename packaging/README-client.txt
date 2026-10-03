VulkanStory for Vintage Story 1.22.7 — Windows x64

Development package. Full release acceptance is still in progress.

INSTALL
Close the game. Extract the client ZIP into the existing folder containing
Vintagestory.exe, preserving the folder layout. Start with your normal shortcut.
The package uses hostfxr.dll, VulkanStory/ and Mods/vulkanstory plus
Mods/vulkanstoryinput. No second game folder, launcher, SDK or font installation.
This ZIP goes beside the game executable; putting it only in Mods is insufficient.

If hostfxr.dll already exists, use the ownership-aware updater instead of
overwriting it. The updater refuses unowned or modified files. It is supplied at
VulkanStory/tools/deploy-runtime.ps1 and accepts GameDirectory, PackageDirectory
and a fresh BackupDirectory outside the game/package folders.
Example after extracting a new ZIP to a temporary folder:
  powershell -File VulkanStory/tools/deploy-runtime.ps1 -GameDirectory "C:\Games\VintageStory" -PackageDirectory "C:\Temp\VulkanStory" -BackupDirectory "C:\Backups\VulkanStory-update"

SETTINGS
In a world, use .vulkanstory settings or .vulkanstory status.
Use .vulkanstory controller to open the connected controller's remapping panel.
Renderer/window options marked as requiring restart take effect next launch.
Controller glyphs are bundled; they do not change your system fonts.
The input companion supports the integrated single-player server. Remote servers
can optionally install the separate companion ZIP in their Mods directory.
Servers without it retain digital controller movement.

DISABLE
Disable VulkanStory in the game mod manager, then quit and restart.
Early renderer ownership changes require a restart.
For a startup problem, edit VulkanStory/loader.ini and set Enabled=0 under
[Bootstrap]. This skips managed startup on the next normal-shortcut launch.
Set Enabled=1 to allow early startup again, and re-enable the mod if necessary.

REMOVE
Close the game and use VulkanStory/tools/remove-runtime.ps1 with GameDirectory
and a fresh BackupDirectory outside the game folder. It moves unchanged owned
files to the backup, preserves modified files and leaves empty directories.
Saves, user settings and version.dll remain in place. Preview with -WhatIf.
Example:
  powershell -File VulkanStory/tools/remove-runtime.ps1 -GameDirectory "C:\Games\VintageStory" -BackupDirectory "C:\Backups\VulkanStory-remove" -WhatIf
Remove -WhatIf to perform the operation. Keep the backup for recovery.

EXISTING PROXIES
VulkanStory does not occupy version.dll. Coexistence with an MFG enabler has not
yet been accepted in this rewrite; the package does not replace that DLL.

DIAGNOSTICS
Keep %LOCALAPPDATA%\VulkanStory\Logs\bootstrap-*.jsonl and your game data
Logs/client-main.log when
reporting startup/rendering problems. Package hashes are in VulkanStory/package.json.
License/attribution notices, including PromptFont, are under VulkanStory/licenses.
