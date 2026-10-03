# Screenshot and recording route review — 2026-10-01

Implementation only; no builds, tests, runtime probes, packages or game launches.
The preceding goal turn made progress with a clean accumulated compile batch.

## Source evidence

The original SystemScreenshot registers captures after final composition or in
Done, binds Primary/default, and calls platform.SaveScreenshot. The new capture
group routes SaveScreenshot and both GrabScreenshot overloads through the
original Screenshot service. Its ReadPixels and window ClientSize call sites
are checked and replaced, using the active Vulkan target, BGRA8 decoding and SDL
display pixels. PNG save, original scaling/flipping and metadata behavior remain
in the original service. The isolated harness exercises separate scheduled
capture code; that evidence does not prove all interactive screenshot options.

Original SystemCinematicCamera still creates an IAviWriter from the platform and
calls avi.AddFrame in Done. SdlXPlatformInterface forwards codec enumeration and
GetAviWriter to the original OS service. The available snapshot includes no
concrete AVI writer implementation: its frame-acquisition behavior therefore
remains unproven. Do not infer that it uses the patched Screenshot service from
the donor's comments alone. The concrete game-shipped OS implementation must be
inspected before claiming recording parity; encoded AVI output is not verified.

## Concrete cleanup fix

CaptureService associates the original Screenshot object with its graphics
sidecar in a ConditionalWeakTable. DetachCaptureService existed but had no caller.
Graphics detachment now invokes it while the original platform is still present,
after routing has stopped and before platform/device references are dropped.
This removes a stale association that could retain a disposed renderer when
the original platform/capture service stays alive.

The cleanup addition is unbuilt. Existing capture/native builds remain previous
evidence; no rerun occurred. Full interactive screenshot/video acceptance stays
open alongside the broader renderer/SDL/provider port.

## Concrete AVI acquisition port — source continuation

Inspected the official installed Lib/xplatforminterface.dll with the existing
ILSpy decompiler, without loading or running game code. VSPlatform.AviWriterImpl
implements IAviWriter. Open creates the retained MJPEG Skia stream with flip=true,
starts the original background writer task and allocates its ScreenFramePool.
AddFrame requests a byte array, directly calls OpenTK GL.ReadPixels with unsigned
byte BGRA, and enqueues the array. The worker encodes and returns arrays to the
pool. Thus the donor comment suggesting Screenshot coverage was insufficient:
AVI acquisition was a separate, uncovered OpenGL call.

The existing query/capture patch group now binds the original AddFrame method and
requires exactly one matching ReadPixels byte-array call. It replaces only that
call with renderer-owned BGRA8 readback, passing the original writer as an
IAviWriter receiver. Generic OpenTK byte-array overload resolution is supported.
Original pooling, codec setup, asynchronous encoding and flip remain intact.
This is a call-site port; no official implementation or encoder DLL is copied
into the product. The profile now pins the official xplatforminterface DLL hash.

SDL's OS service wrapper associates returned AVI writers with the graphics owner.
Acquisition requires that association, owner-thread routing and sufficient buffer
capacity; it cannot silently fall through to OpenGL during active Vulkan routing.
Weak writer bookkeeping removes ownership associations on graphics disposal.

Source-only. Compilation, Harmony anchor installation, recording output and
timing/resize behavior remain unverified. No tests, builds, packages or game run.
