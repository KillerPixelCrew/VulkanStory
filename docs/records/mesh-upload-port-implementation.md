# Neutral mesh upload facade

Date: 2026-09-30. Implementation only; no builds, tests, probes, packages or game runs.

`MeshUploads` extracts the retained create/update/packed-face write bodies from the device resource partial and joins backend compilation. Allocation still follows vertex counts except flags capacity; updates retain every byte offset and part count, signed custom-short creation, SSBO omission of ordinary streams/indices and custom-int pair pruning. The scratch array remains owned by the upload helper. Source baseline: `386e0d05386d0b228b439d09aeca851428f7bbf3`.

`MeshUploadData`/`MeshCustomUpload` carry only scalar metadata and borrowed arrays. `GameMeshLayout.Capture` snapshots the official game's counts/layout/offsets while retaining array references for synchronous upload. The retained manager immediately copies bytes to mapped or owned staging storage; no game mesh object crosses into the backend. Neutral buffer slots replace the previous injected `EnumMeshBufferPart`, which exists only in the old optimization API and is not an official game type. Neutral topology translation preserves the retained mapping.

The staged device facade delegates create/update operations to this helper and initializes it alongside the retained mesh manager. Its mapped-pointer and topology entry points now use neutral types. The rest of the resource/device facade is still excluded, with texture/state/provider dependencies remaining.

A new game-boundary fixture checks borrowed arrays, flags capacity versus write count, custom-part data and all destination offsets. `--mesh-buffers` now allocates/uploads through `MeshUploads`, reads the original position bytes, updates one vertex at a nonzero byte offset and checks neighbouring vertices remain intact, then retains its SSBO/index checks. This increment is unvalidated. Next bounded validation should run backend/game tests, build the tool and run the buffer check once.

This does not establish the actual game mesh-reference/resource registry, persistent mapped chunk hooks, all custom streams or SSBO updates, indirect/instanced game draws, full facade compilation, startup/menu/world routing or providers. Full port acceptance remains open.

The [first bounded validation](validation-p0-mesh-upload-2026-09-30-01.md) passed 51 backend tests, 28 game tests, clean compilation and actual helper-driven native allocation/offset-update checks. The listed full-facade/game/provider boundaries remain open.
