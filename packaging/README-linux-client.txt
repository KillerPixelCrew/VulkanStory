VulkanStory Linux x64 development package

Requires the existing official Vintage Story 1.22.7 Linux installation, .NET 10,
a working Vulkan driver/display session, bash and Python 3.

Install with the game and server closed:

  bash <package-dir>/VulkanStory/tools/install-linux-runtime.sh \
    <package-dir> <existing-game-dir> <fresh-external-backup-dir>

The installer verifies the declared inputs and records owned changes. It inserts
one tagged activation block into the original run.sh, preserving the original
executable and arguments. Start the game with its normal original shortcut.
Existing startup hooks remain chained; VulkanStory removes its own process hook
after initialization so it does not activate unrelated child processes.
Activation covers the normal desktop entry through run.sh. URI connect and
mod-install entries that launch the game executable directly bypass it.

Set Enabled=0 in VulkanStory/loader.ini to bypass VulkanStory at startup.

Remove using another fresh external backup directory:

  bash <game-dir>/VulkanStory/tools/remove-linux-runtime.sh \
    <game-dir> <fresh-external-backup-dir>

Removal verifies ownership before restoring/removing the tagged activation and
owned payload. Retain the backups; modified user files must not be overwritten.

Windows-only upscaler and frame-generation runtimes are not included; native TAA
is available. DLSS additionally needs the NVIDIA driver's NGX runtime and the
Linux VulkanStory NGX shim.
