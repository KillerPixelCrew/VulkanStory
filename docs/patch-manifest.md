# Cecil patch selection

`Optimum.Patcher/PatchManifest.cs` declares the VintagestoryLib transplant policy.
`Program.cs` selects the CLI mode and inputs, then passes that manifest to
`ILPatcher`. The manifest groups injected types and members, field retypes,
method-body transplants, IL hooks and platform-virtualization changes. Its typed
operation report names the donor or vanilla source and the vanilla destination.

Method targets name a declaring type, member and parameter count. Targets with
overloads of the same arity also declare parameter types. `ILPatcher` resolves a
unique compiled donor method, then requires a matching vanilla signature before
transplanting. An ambiguous or missing required target fails the patch. Hook
targets additionally name the exact call signature and insertion side.

`patches/cecil-owned.list` is the checked-in report of *existing source patches*
whose declaring types the manifest changes. Regenerate it after changing patch
selection:

```bash
bash scripts/update-cecil-owned.sh
```

The main ownership
test and `scripts/check-patches.sh` compare the generated report with the list.
Source patches are checked against the donor tree; runtime patches target the
vanilla assemblies separately. The list does not turn an absent source patch
into a shipped change.
