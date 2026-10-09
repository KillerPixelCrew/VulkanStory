# SDL boundary validation — 2026-09-29

Status: **targeted build and 5 focused tests passed; one analyzer warning remains.** This was one bounded validation batch. No source was changed and no second batch was run.

The command ran once from `D:\Coding\VulkanStory-Rewrite`:

```powershell
dotnet test tests\VulkanStory.Platform.Sdl.Tests\VulkanStory.Platform.Sdl.Tests.csproj -c Release `
  --results-directory artifacts\validation\sdl-20260929-01 `
  --logger 'trx;LogFileName=sdl-managed.trx'
```

The process exited 0. Its [console log](../../artifacts/validation/sdl-20260929-01/console.txt) and [TRX](../../artifacts/validation/sdl-20260929-01/sdl-managed.trx) are retained. `VulkanStory.Platform.Sdl` and its test project both built. All five tests passed, with zero failures and zero skips. They cover SDL event memory layouts, physical scancode/text separation, touch/display fields, coordinate scaling, and IME caret conversion.

The compiler emitted `CA2255` at `SdlNativeLibrary.cs:11`: module initializers are discouraged in library code. This does not fail the build, but the resolver registration should be moved to an explicit, idempotent path before release. No SDL native library was loaded, no SDL window or Vulkan surface was created, and no game process was launched in this batch. The backend-side `SdlVulkanWindowSurface`, game input adapter, controller stack, renderer, upscalers, and frame generation remain unbuilt and unverified in the rewrite.
