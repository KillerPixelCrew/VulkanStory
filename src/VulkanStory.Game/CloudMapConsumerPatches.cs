using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

internal static class CloudMapConsumerPatches
{
    private const string Owner = "vulkanstory.routing.graphics-cloud-map";
    private static ProcessRuntime? runtime;
    private static FieldInfo? tileWidth;
    private static readonly Dictionary<string, string> Wrappers = new()
    {
        ["DeleteTexture"] = nameof(DeleteTexture), ["DeleteFramebuffer"] = nameof(DeleteFramebuffer),
        ["GetInteger"] = nameof(GetInteger), ["GetUniformLocation"] = nameof(UniformLocation), ["Uniform3"] = nameof(Uniform3),
        ["BindFramebuffer"] = nameof(BindFramebuffer), ["Viewport"] = nameof(Viewport),
        ["Enable"] = nameof(Enable), ["Disable"] = nameof(Disable), ["BindTexture"] = nameof(BindTexture),
        ["TexSubImage2D"] = nameof(Upload), ["GenFramebuffer"] = nameof(CreateFramebuffer),
        ["FramebufferTexture2D"] = nameof(Attach), ["DrawBuffers"] = nameof(DrawBuffers),
    };
    private static readonly MethodInfo Mesh = AccessTools.Method(typeof(IRenderAPI), "RenderMesh", [typeof(MeshRef)])!;
    private static MethodInfo Wrapper(MethodInfo original)
    {
        if (!Wrappers.TryGetValue(original.Name, out string? name)) throw new NotSupportedException("Uncovered cloud-map GL call: " + original.Name);
        Type[] parameters = original.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
        if (original.Name == "GenFramebuffer") parameters = [typeof(object)];
        MethodInfo? wrapper = typeof(CloudMapConsumerPatches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic, null, parameters, null);
        if (wrapper == null || wrapper.ReturnType != original.ReturnType) throw new MissingMethodException("Cloud-map GL signature changed: " + original);
        return wrapper;
    }
    private static void Check(IReadOnlyList<CodeInstruction> body, int glCount, int meshes)
    {
        var calls = body.Where(instruction => instruction.operand is MethodInfo method && method.DeclaringType == typeof(GL)).ToArray();
        if (calls.Length != glCount || body.Count(instruction => instruction.Calls(Mesh)) != meshes)
            throw new InvalidOperationException("Original cloud-map GL/draw count changed.");
        foreach (var call in calls) Wrapper((MethodInfo)call.operand);
    }
    internal static StartupPatchGroup CreateGroup(ProcessRuntime owner, Assembly essentials)
    {
        Type type = essentials.GetType("FluffyClouds.CloudRendererMap", true)!;
        FieldInfo width = AccessTools.Field(type, "CloudTileLength") ?? throw new MissingFieldException("Cloud tile width is missing.");
        MethodInfo make = AccessTools.Method(type, "makeTexture", [typeof(int), typeof(PixelInternalFormat), typeof(PixelFormat), typeof(PixelType)])!;
        var routes = new (MethodInfo Method, int Calls, int Meshes)[]
        {
            (AccessTools.Method(type, "FreeGlResources", [])!, 5, 0),
            (AccessTools.Method(type, "WriteTexture", [])!, 4, 0),
            (AccessTools.Method(type, "InitCloudTiles", [typeof(int)])!, 8, 0),
            (AccessTools.Method(type, "OnRenderFrame", [typeof(float), typeof(EnumRenderStage)])!, 14, 1),
        };
        var harmony = new Harmony(Owner); bool attempted = false;
        return new StartupPatchGroup("graphics-scene-cloud-map", () =>
        {
            if (width.FieldType != typeof(int) || make?.ReturnType != typeof(int)) throw new InvalidOperationException("Original cloud-map resource metadata changed.");
            foreach (var route in routes)
            {
                if (route.Method == null || route.Method.ReturnType != typeof(void) || route.Method.GetMethodBody() == null)
                    throw new MissingMethodException("Original cloud-map method changed.");
                Check(PatchProcessor.GetOriginalInstructions(route.Method), route.Calls, route.Meshes);
            }
        }, () =>
        {
            if (runtime != null) throw new InvalidOperationException("Cloud-map routing already has an owner.");
            runtime = owner; tileWidth = width; attempted = true;
            harmony.Patch(make, prefix: new HarmonyMethod(typeof(CloudMapConsumerPatches), nameof(MakeTexture)) { priority = Priority.First });
            foreach (var route in routes) harmony.Patch(route.Method, transpiler: new HarmonyMethod(typeof(CloudMapConsumerPatches), nameof(Transpiler)) { priority = Priority.First });
        }, () =>
        {
            if (!attempted) return;
            harmony.UnpatchAll(Owner); if (ReferenceEquals(runtime, owner)) { runtime = null; tileWidth = null; }
            attempted = false;
        });
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var body = instructions.ToList();
        (int gl, int meshes) = __originalMethod.Name switch
        { "FreeGlResources" => (5, 0), "WriteTexture" => (4, 0), "InitCloudTiles" => (8, 0), "OnRenderFrame" => (14, 1), _ => throw new InvalidOperationException() };
        Check(body, gl, meshes);
        foreach (var instruction in body)
        {
            if (instruction.operand is MethodInfo method && method.DeclaringType == typeof(GL))
            {
                if (method.Name == "GenFramebuffer")
                {
                    var receiver = new CodeInstruction(OpCodes.Ldarg_0); receiver.labels.AddRange(instruction.labels); instruction.labels.Clear();
                    receiver.blocks.AddRange(instruction.blocks.Where(block => block.blockType != ExceptionBlockType.EndExceptionBlock));
                    instruction.blocks.RemoveAll(block => block.blockType != ExceptionBlockType.EndExceptionBlock); yield return receiver;
                }
                instruction.opcode = OpCodes.Call; instruction.operand = Wrapper(method);
            }
            else if (instruction.Calls(Mesh)) { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(CloudMapConsumerPatches), nameof(DrawMap)); }
            yield return instruction;
        }
    }
    private static GameGraphicsAdapter? Adapter()
    {
        if (runtime?.Routing.RoutingEnabled != true) return null;
        if (ScreenManager.Platform is not ClientPlatformWindows platform || !runtime.TrySession(platform, out var session))
            throw new InvalidOperationException("Active cloud-map routing lost its session.");
        return session.Graphics;
    }
    private static bool MakeTexture(int __0, PixelInternalFormat __1, PixelFormat __2, PixelType __3, ref int __result)
    { if (Adapter() is not { } graphics) return true; __result = graphics.CreateCloudTexture(__0, (int)__1); return false; }
    private static int CreateFramebuffer(object instance) => Adapter() is { } graphics
        ? graphics.CreateCloudFramebuffer((int)tileWidth!.GetValue(instance)!) : GL.GenFramebuffer();
    private static void DeleteTexture(int texture) { if (Adapter() is { } graphics) graphics.DeleteCloudTexture(texture); else GL.DeleteTexture(texture); }
    private static void DeleteFramebuffer(int frame) { if (Adapter() is { } graphics) graphics.DeleteCloudFramebuffer(frame); else GL.DeleteFramebuffer(frame); }
    private static void GetInteger(GetPName name, out int value)
    { if (Adapter() is not { } graphics) { GL.GetInteger(name, out value); return; } if ((int)name != 36006) throw new NotSupportedException(); value = graphics.SaveCloudFramebuffer(); }
    private static void GetInteger(GetPName name, int[] values)
    { if (Adapter() is not { } graphics) { GL.GetInteger(name, values); return; } if ((int)name != 2978) throw new NotSupportedException(); graphics.GetCloudViewport(values); }
    private static int UniformLocation(int program, string name) => Adapter() is { } graphics ? graphics.CloudUniformLocation(program, name) : GL.GetUniformLocation(program, name);
    private static void Uniform3(int location, int count, float[] values) { if (Adapter() is { } graphics) graphics.CloudUniform3(location, count, values); else GL.Uniform3(location, count, values); }
    private static void BindFramebuffer(FramebufferTarget kind, int frame)
    { if (Adapter() is not { } graphics) { GL.BindFramebuffer(kind, frame); return; } if ((int)kind != 36160) throw new NotSupportedException(); graphics.BindCloudFramebuffer(frame); }
    private static void Viewport(int x, int y, int width, int height) { if (Adapter() is { } graphics) graphics.CloudViewport(x, y, width, height); else GL.Viewport(x, y, width, height); }
    private static void Enable(EnableCap cap) { if (Adapter() is not { } graphics) { GL.Enable(cap); return; } if ((int)cap == 2929) graphics.RequireStatedState().DepthTest = true; else if ((int)cap == 3042) graphics.RequireStatedState().SetBlendEnabled(true); else throw new NotSupportedException(); }
    private static void Disable(EnableCap cap) { if (Adapter() is not { } graphics) { GL.Disable(cap); return; } if ((int)cap == 2929) graphics.RequireStatedState().DepthTest = false; else if ((int)cap == 3042) graphics.RequireStatedState().SetBlendEnabled(false); else throw new NotSupportedException(); }
    private static void BindTexture(TextureTarget kind, int texture) { if (Adapter() is not { } graphics) { GL.BindTexture(kind, texture); return; } if ((int)kind != 3553) throw new NotSupportedException(); graphics.BindCloudTexture(texture); }
    private static void Upload(TextureTarget kind, int level, int x, int y, int width, int height, PixelFormat format, PixelType type, short[] values)
    { if (Adapter() is not { } graphics) { GL.TexSubImage2D(kind, level, x, y, width, height, format, type, values); return; } if ((int)kind != 3553 || (int)format != 6408 || (int)type != 5122) throw new NotSupportedException(); graphics.UploadCloudShorts(level, x, y, width, height, values); }
    private static void Attach(FramebufferTarget target, OpenTK.Graphics.OpenGL.FramebufferAttachment attachment, TextureTarget textureKind, int texture, int level)
    { if (Adapter() is not { } graphics) { GL.FramebufferTexture2D(target, attachment, textureKind, texture, level); return; } if ((int)target != 36160 || (int)textureKind != 3553 || level != 0) throw new NotSupportedException(); graphics.AttachCloudTexture((int)attachment, texture); }
    private static void DrawBuffers(int count, DrawBuffersEnum[] values) { if (Adapter() is { } graphics) graphics.SelectDrawBuffers(count, values); else GL.DrawBuffers(count, values); }
    private static void DrawMap(IRenderAPI render, MeshRef mesh) { if (Adapter() is { } graphics) graphics.RenderCloudMap(mesh); else render.RenderMesh(mesh); }
}
