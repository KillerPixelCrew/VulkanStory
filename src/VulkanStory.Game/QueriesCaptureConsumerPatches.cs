using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using OpenTK.Windowing.Desktop;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.ClientNative;

namespace VulkanStory.Game;

internal static class QueriesCaptureConsumerPatches
{
    private const string Owner = "vulkanstory.routing.graphics-queries-capture";
    private static Func<bool>? enabled;
    private static readonly AccessTools.FieldRef<ClientSystem, ClientMain?> Origin =
        AccessTools.FieldRefAccess<ClientSystem, ClientMain?>("game");
    private static readonly AccessTools.FieldRef<SystemRenderSunMoon, int> MoonQuery =
        AccessTools.FieldRefAccess<SystemRenderSunMoon, int>("occlQueryId");
    private sealed record Call(MethodInfo Original, string Wrapper, int Count);
    private sealed record Body(MethodBase Target, Call[] Calls);
    private static MethodInfo Gl(string name, params Type[] types) => typeof(GL).GetMethod(name, types) ??
        throw new MissingMethodException("Original query/capture GL overload is missing: " + name);
    private static MethodInfo Method(Type type, string name, Type result, params Type[] types)
    {
        var method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
            binder: null, types: types, modifiers: null);
        if (method is null || method.ReturnType != result || method.GetMethodBody() is null)
            throw new MissingMethodException(type.FullName, name);
        return method;
    }
    private static Body[] Bodies()
    {
        var create = typeof(SystemRenderSunMoon).GetConstructor([typeof(ClientMain)]) ?? throw new MissingMethodException("Original sun constructor is missing.");
        var size = typeof(NativeWindow).GetProperty(nameof(NativeWindow.ClientSize))!.GetMethod!;
        return
        [
            new(create, [new(Gl("GenQueries", typeof(int), typeof(int).MakeByRefType()), nameof(Generate), 1)]),
            new(Method(typeof(SystemRenderSunMoon), "OnRenderFrame3DPost", typeof(void), typeof(float)),
            [
                new(Gl("GetQueryObject", typeof(int), typeof(GetQueryObjectParam), typeof(int).MakeByRefType()), nameof(Result), 2),
                new(Gl("BeginQuery", typeof(QueryTarget), typeof(int)), nameof(Begin), 1),
                new(Gl("EndQuery", typeof(QueryTarget)), nameof(End), 1),
                new(Gl("ColorMask", typeof(bool), typeof(bool), typeof(bool), typeof(bool)), nameof(ColorMask), 2),
            ]),
            new(Method(typeof(SystemRenderSunMoon), "Dispose", typeof(void), typeof(ClientMain)),
                [new(Gl("DeleteQuery", typeof(int)), nameof(Delete), 1)]),
            new(Method(typeof(Screenshot), "GrabScreenshot", typeof(SkiaSharp.SKBitmap), typeof(Size2i), typeof(bool), typeof(bool), typeof(bool)),
            [
                new(Gl("ReadPixels", typeof(int), typeof(int), typeof(int), typeof(int), typeof(PixelFormat), typeof(PixelType), typeof(IntPtr)), nameof(ReadPixels), 1),
                new(size, nameof(DisplaySize), 2),
            ]),
            new(Method(VideoWriterType(), "AddFrame", typeof(void)),
                [new(VideoReadMethod(), nameof(ReadVideoPixels), 1)]),
        ];
    }
    private static Type VideoWriterType()
    {
        Type type = Assembly.Load("xplatforminterface").GetType("VSPlatform.AviWriterImpl", throwOnError: true)!;
        if (!typeof(IAviWriter).IsAssignableFrom(type)) throw new InvalidOperationException("Official AVI writer contract changed.");
        return type;
    }
    private static MethodInfo VideoReadMethod()
    {
        Type[] parameters = [typeof(int), typeof(int), typeof(int), typeof(int), typeof(PixelFormat), typeof(PixelType), typeof(byte[])];
        MethodInfo? direct = typeof(GL).GetMethod("ReadPixels", parameters);
        if (direct != null) return direct;
        return typeof(GL).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == "ReadPixels" && method.IsGenericMethodDefinition && method.GetGenericArguments().Length == 1)
            .Select(method => method.MakeGenericMethod(typeof(byte)))
            .Single(method => method.ReturnType == typeof(void) &&
                method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(parameters));
    }
    private static void Check(Body body, IReadOnlyList<CodeInstruction> instructions)
    {
        foreach (var call in body.Calls)
            if (instructions.Count(instruction => instruction.Calls(call.Original)) != call.Count)
                throw new InvalidOperationException("Original query/capture anchors changed: " + body.Target.Name + "/" + call.Original.Name);
    }
    internal static StartupPatchGroup CreateSubset(Func<bool> routing)
    {
        ArgumentNullException.ThrowIfNull(routing);
        var harmony = new Harmony(Owner);
        (MethodInfo Target, string Prefix)[]? prefixes = null;
        Body[]? bodies = null;
        bool attempted = false;
        return new StartupPatchGroup("graphics-queries-capture", () =>
        {
            if (typeof(ClientPlatformWindows).GetField("screenshot", BindingFlags.Instance | BindingFlags.NonPublic)?.FieldType != typeof(Screenshot) ||
                typeof(ClientSystem).GetField("game", BindingFlags.Instance | BindingFlags.NonPublic)?.FieldType != typeof(ClientMain) ||
                typeof(SystemRenderSunMoon).GetField("occlQueryId", BindingFlags.Instance | BindingFlags.NonPublic)?.FieldType != typeof(int))
                throw new MissingFieldException("Original query/capture private metadata changed.");
            prefixes =
            [
                (Method(typeof(ClientPlatformWindows), "SaveScreenshot", typeof(string), typeof(string), typeof(string), typeof(bool), typeof(bool), typeof(string)), nameof(Save)),
                (Method(typeof(ClientPlatformWindows), "GrabScreenshot", typeof(BitmapRef), typeof(bool), typeof(bool)), nameof(Grab)),
                (Method(typeof(ClientPlatformWindows), "GrabScreenshot", typeof(BitmapRef), typeof(int), typeof(int), typeof(bool), typeof(bool), typeof(bool)), nameof(GrabSized)),
            ];
            bodies = Bodies();
            foreach (var body in bodies) Check(body, PatchProcessor.GetOriginalInstructions(body.Target));
        }, () =>
        {
            if (prefixes is null || bodies is null || enabled != null) throw new InvalidOperationException("Query/capture routing is unvalidated or already owned.");
            enabled = routing; attempted = true;
            foreach (var prefix in prefixes) harmony.Patch(prefix.Target,
                prefix: new HarmonyMethod(typeof(QueriesCaptureConsumerPatches), prefix.Prefix) { priority = Priority.First });
            foreach (var body in bodies) harmony.Patch(body.Target,
                transpiler: new HarmonyMethod(typeof(QueriesCaptureConsumerPatches), nameof(Transpiler)) { priority = Priority.First });
        }, () =>
        {
            if (!attempted) return;
            harmony.UnpatchAll(Owner);
            if (ReferenceEquals(enabled, routing)) enabled = null;
            attempted = false;
        });
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        Body body = Bodies().Single(candidate => candidate.Target.Equals(__originalMethod));
        var incoming = instructions.ToList();
        Check(body, incoming);
        foreach (var instruction in incoming)
        {
            Call? call = body.Calls.FirstOrDefault(candidate => instruction.Calls(candidate.Original));
            if (call != null)
            {
                var receiver = new CodeInstruction(OpCodes.Ldarg_0);
                receiver.labels.AddRange(instruction.labels);
                receiver.blocks.AddRange(instruction.blocks.Where(block => block.blockType != ExceptionBlockType.EndExceptionBlock));
                instruction.labels.Clear();
                instruction.blocks.RemoveAll(block => block.blockType != ExceptionBlockType.EndExceptionBlock);
                yield return receiver;
                instruction.opcode = OpCodes.Call;
                instruction.operand = typeof(QueriesCaptureConsumerPatches).GetMethod(call.Wrapper, BindingFlags.Static | BindingFlags.NonPublic)!;
            }
            yield return instruction;
        }
    }
    private static bool TryPlatform(ClientPlatformWindows platform, out GameGraphicsAdapter adapter)
    {
        adapter = null!;
        if (enabled?.Invoke() != true) return false;
        if (!GameGraphicsAdapter.TryGet(platform, out var found) || found is null) throw new InvalidOperationException("Active query/capture routing has no renderer adapter.");
        adapter = found; return true;
    }
    private static bool TryMoon(SystemRenderSunMoon moon, out GameGraphicsAdapter adapter)
    {
        adapter = null!;
        if (enabled?.Invoke() != true) return false;
        if (Origin(moon)?.Platform is not ClientPlatformWindows platform) throw new InvalidOperationException("Occlusion query has no originating platform.");
        return TryPlatform(platform, out adapter);
    }
    private static bool TryCapture(Screenshot shot, out GameGraphicsAdapter adapter)
    {
        adapter = null!;
        if (enabled?.Invoke() != true) return false;
        adapter = GameGraphicsAdapter.CaptureOwner(shot); return true;
    }
    private static bool Save(ClientPlatformWindows __instance, string __0, string __1, bool __2, bool __3, string __4, ref string __result)
    { if (!TryPlatform(__instance, out var adapter)) return true; __result = adapter.SaveScreenshot(__0, __1, __2, __3, __4); return false; }
    private static bool Grab(ClientPlatformWindows __instance, bool __0, bool __1, ref BitmapRef __result)
    { if (!TryPlatform(__instance, out var adapter)) return true; __result = adapter.GrabScreenshot(__0, __1); return false; }
    private static bool GrabSized(ClientPlatformWindows __instance, int __0, int __1, bool __2, bool __3, bool __4, ref BitmapRef __result)
    { if (!TryPlatform(__instance, out var adapter)) return true; __result = adapter.GrabScreenshot(__0, __1, __2, __3, __4); return false; }
    internal static void Generate(int count, ref int id, SystemRenderSunMoon moon)
    {
        if (!TryMoon(moon, out var adapter)) { GL.GenQueries(count, out id); return; }
        if (count != 1) throw new NotSupportedException("Sun-query generation requires one query.");
        id = adapter.CreateOcclusionQuery();
    }
    internal static void Result(int id, GetQueryObjectParam parameter, ref int value, SystemRenderSunMoon moon)
    {
        if (!TryMoon(moon, out var adapter)) { GL.GetQueryObject(id, parameter, out value); return; }
        if ((int)parameter is not (34918 or 34919)) throw new NotSupportedException("Unsupported occlusion result parameter.");
        value = adapter.ReadOcclusionQuery(id, (int)parameter == 34919);
    }
    internal static void Begin(QueryTarget target, int id, SystemRenderSunMoon moon)
    {
        if (!TryMoon(moon, out var adapter)) { GL.BeginQuery(target, id); return; }
        RequireSamples(target); adapter.BeginOcclusionQuery(id);
    }
    internal static void End(QueryTarget target, SystemRenderSunMoon moon)
    {
        if (!TryMoon(moon, out var adapter)) { GL.EndQuery(target); return; }
        RequireSamples(target); adapter.EndOcclusionQuery(MoonQuery(moon));
    }
    private static void RequireSamples(QueryTarget target)
    { if ((int)target != 35092) throw new NotSupportedException("Only SamplesPassed queries are routed."); }
    internal static void Delete(int id, SystemRenderSunMoon moon)
    { if (!TryMoon(moon, out var adapter)) { GL.DeleteQuery(id); return; } adapter.DeleteOcclusionQuery(id); }
    private static void ColorMask(bool r, bool g, bool b, bool a, SystemRenderSunMoon moon)
    { if (!TryMoon(moon, out var adapter)) { GL.ColorMask(r, g, b, a); return; } adapter.RequireStatedState().SetColorMask(r, g, b, a); }
    internal static void ReadPixels(int x, int y, int width, int height, PixelFormat format, PixelType type, IntPtr destination, Screenshot shot)
    {
        if (!TryCapture(shot, out var adapter)) { GL.ReadPixels(x, y, width, height, format, type, destination); return; }
        if ((int)format != 32993 || (int)type != 5121) throw new NotSupportedException("Screenshot readback requires unsigned-byte BGRA.");
        adapter.ReadCapturePixels(x, y, width, height, destination);
    }
    private static Vector2i DisplaySize(NativeWindow window, Screenshot shot)
    {
        if (!TryCapture(shot, out var adapter)) return window.ClientSize;
        var pixels = adapter.CaptureDisplaySize();
        return new Vector2i(pixels.Width, pixels.Height);
    }
    private static unsafe void ReadVideoPixels(int x, int y, int width, int height,
        PixelFormat format, PixelType type, byte[] destination, IAviWriter writer)
    {
        if (enabled?.Invoke() != true) { GL.ReadPixels(x, y, width, height, format, type, destination); return; }
        if ((int)format != 32993 || (int)type != 5121)
            throw new NotSupportedException("AVI readback requires unsigned-byte BGRA.");
        ArgumentNullException.ThrowIfNull(destination);
        if (width <= 0 || height <= 0 || destination.Length < checked(width * height * 4))
            throw new ArgumentException("AVI frame buffer is smaller than the requested capture.", nameof(destination));
        fixed (byte* pixels = destination)
            GameGraphicsAdapter.VideoOwner(writer).ReadCapturePixels(x, y, width, height, (IntPtr)pixels);
    }
}
