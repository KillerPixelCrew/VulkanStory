# Runtime delivery batch — 2026-10-01

One bounded batch, run once. No implementation source changes, reruns, builds,
tests, native SDK execution, installation or game launch.

| Operation | Result |
| --- | --- |
| Native bundle preparation | Passed; selected bridges/core/vendor DLLs and notices, matched Streamline release version and DLL hashes |
| Runtime staging | Passed; 362 inventory files, full shader program coverage and referenced SPIR-V hashes checked |
| Client/server archives | Passed; staged file hashes checked and separate product inventories written |

Batch logs, result JSON, native bundle, stage and archives:
`artifacts/validation/runtime-delivery-20261001-074023/`.
All operation logs were read. Archive metadata and manifest locations were inspected.

| Archive | Bytes | Entries | SHA256 |
| --- | ---: | ---: | --- |
| VulkanStory-win-x64.zip | 142,619,374 | 357 | 992D60155A0F7BBA31C9FDE2E7250029EE0378E190EBF8A86DDBC9FA214DD237 |
| VulkanStory-Input-Companion.zip | 24,269 | 7 | 34CF7782A1CD43B6BDD45A4B8B602FEA001244E449FA0CE84CAC25B584A95092 |

Client archive has hostfxr.dll at root, Mods/vulkanstory/modinfo.json and
VulkanStory/package.json. Companion has vulkanstoryinput/modinfo.json and its
own package.json, suitable for extraction into server Mods. Its contents do not
appear in the client archive. These archives keep the product's existing-game
directory layout and contain no copied game installation.

**Acceptance remains unverified**, as recorded in the manifests. This batch
establishes assembly of the selected files, not native import completeness,
runtime Harmony guard/installation success, SDL startup, rendered menus/worlds,
provider evaluation, resize/shutdown behavior, update/removal or final release
redistribution acceptance. Next action is installation/startup integration.
