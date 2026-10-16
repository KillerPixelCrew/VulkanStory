using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using OpenTK.Mathematics;
using Silk.NET.Vulkan;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan;
using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Game;

/// <summary>GL command/resource semantics for managed callers using an owned Vulkan window context.</summary>
/// <remarks>Names are shared by the session. VAOs, bindings, error flags and fixed-function state belong to a context.</remarks>
internal sealed partial class ManagedGlBridge : IDisposable
{
    [ThreadStatic] private static ManagedGlBridge? current;
    internal static ManagedGlBridge? Current => current;
    private readonly GameRenderSession session;
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private readonly Dictionary<nint, Context> contexts = new();
    private readonly Dictionary<int, BufferObject> buffers = new();
    private readonly Dictionary<int, ShaderObject> shaders = new();
    private readonly Dictionary<int, ProgramObject> programs = new();
    private readonly Dictionary<int, TextureObject> textures = new();
    private int nextName = 0x40000000;
    private Context? selected;
    private bool disposed;
    private string? drawTracePath;
    private int tracedMainDraws;
    private int tracedAuxiliaryDraws;
    private int tracedErrors;
    private bool TracePending => drawTracePath is not null && (C.Primary ? tracedMainDraws : tracedAuxiliaryDraws) < 8;
    private VulkanDevice Device => session.Graphics.ManagedGlDevice;
    private Context C => selected ?? throw new InvalidOperationException("No managed GL context is current.");
    private StatedRenderState State => C.Primary ? session.Graphics.RequireStatedState() : C.State;
    private int Target => C.Primary ? session.Graphics.CurrentTargetId : C.DrawFramebuffer == 0 ? C.Target : C.DrawFramebuffer;
    private int CurrentProgram => C.Primary ? session.Graphics.StatedProgram : C.Program;
    private int ActiveUnit { get => C.Primary ? State.ActiveTextureUnit : C.ActiveTexture; set { if (C.Primary) State.ActiveTextureUnit = value; else C.ActiveTexture = value; } }

    internal ManagedGlBridge(GameRenderSession session)
    {
        this.session = session;
        ManagedGlCallPatches.ActivateNativeEntries();
        Device.ProgramDeleted += ForgetBorrowedProgram;
        Device.FramebufferDeleted += ForgetFramebuffer;
        string? trace = Environment.GetEnvironmentVariable("VULKANSTORY_MANAGED_GL_TRACE");
        if (!string.IsNullOrEmpty(trace) && System.IO.Path.IsPathFullyQualified(trace)) drawTracePath = trace;
    }
    internal void SelectContext(nint contextId, int targetId, int width, int height)
    {
        RequireOwner();
        if (contextId == 0) { selected = null; if (ReferenceEquals(current, this)) current = null; return; }
        if (!contexts.TryGetValue(contextId, out Context? context))
        {
            context = new Context { Id = contextId, Primary = targetId <= 0, Target = targetId, Width = width, Height = height };
            context.State.Viewport = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)Math.Max(0, width), (uint)Math.Max(0, height)));
            context.State.SetSlotBlend(0, 0x8006, 1, 0, 1, 0);
            context.State.ColorRoutesChanged = Device.SetFramebufferColorRoutes;
            contexts.Add(contextId, context);
        }
        if (context.Primary != (targetId <= 0)) throw new InvalidOperationException("A managed GL context cannot change primary-window ownership.");
        context.Target = targetId; context.Width = width; context.Height = height;
        selected = context; current = this;
    }
    internal void ReleaseContext(nint contextId)
    {
        RequireOwner();
        if (!contexts.Remove(contextId, out Context? context)) return;
        foreach (int framebuffer in framebuffers.Where(entry => ReferenceEquals(entry.Value, context)).Select(entry => entry.Key).ToArray()) Device.DeleteFramebuffer(framebuffer);
        if (ReferenceEquals(selected, context)) { selected = null; if (ReferenceEquals(current, this)) current = null; }
        CollectDeletedPrograms(); CollectDeletedTextures();
    }
    private void RequireOwner()
    {
        if (disposed) throw new ObjectDisposedException(nameof(ManagedGlBridge));
        if (Environment.CurrentManagedThreadId != ownerThread) throw new InvalidOperationException("Managed GL contexts belong to the session owner thread.");
    }
    private int Name()
    {
        while (buffers.ContainsKey(nextName) || shaders.ContainsKey(nextName) || programs.ContainsKey(nextName) ||
            contexts.Values.Any(context => context.Arrays.ContainsKey(nextName))) nextName = checked(nextName + 1);
        int name = nextName; nextName = checked(nextName + 1); return name;
    }
    private sealed class Context
    {
        internal nint Id;
        internal bool Primary;
        internal int Target, Width, Height, Program, ActiveTexture, DrawFramebuffer, ReadFramebuffer;
        internal readonly Dictionary<int, int> ReadBuffers = new();
        internal readonly Dictionary<int, int> PixelStorage = new();
        internal BufferObject? ArrayBuffer;
        internal int VertexArray;
        internal readonly Dictionary<int, VertexArrayObject> Arrays = new() { [0] = new() };
        internal readonly StatedRenderState State = new();
        internal readonly Queue<int> Errors = new();
        internal int PolygonFront = 0x1B02, PolygonBack = 0x1B02;
        internal float[] ClearColor = [0, 0, 0, 0];
    }
    private sealed class BufferObject(int name)
    {
        internal readonly int Name = name;
        internal byte[] Data = [];
    }
    private sealed class VertexArrayObject
    {
        internal BufferObject? Elements;
        internal readonly Dictionary<int, Attribute> Attributes = new();
        internal readonly HashSet<int> Enabled = new();
    }
    private sealed record Attribute(BufferObject Buffer, int Components, int Type, bool Normalized, bool Integer, int Stride, int Offset)
    { internal bool Enabled; }
    private sealed class ShaderObject(int type)
    {
        internal readonly int Type = type;
        internal string Source = "", Log = "";
        internal bool Compiled, Deleted;
    }
    private sealed class ProgramObject
    {
        internal bool Borrowed;
        internal readonly Dictionary<int, ShaderObject> Stages = new();
        internal readonly Dictionary<string, int> Attributes = new(StringComparer.Ordinal);
        // Renderer locations encode sampler bindings as negative values. GL
        // exposes an independent nonnegative namespace, with -1 only for missing.
        internal readonly Dictionary<string, int> NamedLocations = new(StringComparer.Ordinal);
        internal readonly Dictionary<int, int> Locations = new();
        internal readonly Dictionary<int, int> PublicLocations = new();
        internal readonly Dictionary<int, object> TracedUniformValues = new();
        internal int Backend;
        internal bool Linked, Deleted;
        internal string Log = "";
    }
    private sealed class TextureObject(int name)
    {
        internal readonly int Name = name;
        internal int Width, Height, Depth = 1, Levels, Format, Target;
        internal bool Immutable, Deleted, Created, Borrowed;
        internal readonly Dictionary<int, TextureLevel> Images = new();
    }
    private readonly record struct TextureLevel(int Width, int Height, int Depth, int Format);
    private sealed class GlError(int code) : Exception { internal readonly int Code = code; }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Error(int code) => throw new GlError(code);
    private static int I(object? value) => Convert.ToInt32(value);
    private static long L(object? value) => value is IntPtr pointer ? pointer.ToInt64() : value is UIntPtr unsigned ? checked((long)unsigned.ToUInt64()) : Convert.ToInt64(value);
    private static IntPtr Pointer(object? value) => value is IntPtr pointer ? pointer : new IntPtr(L(value));
    private void QueueError(int code) { if (!C.Errors.Contains(code)) C.Errors.Enqueue(code); }

    internal void RecordNativeFailure(string detail, bool log)
    {
        QueueError(0x0502);
        if (!log) return;
        if (drawTracePath is not null) WriteTrace(new { @event = "native-gl-failure", detail, context = C.Id.ToInt64() });
        session.ReportNativeGlFailure(detail);
    }

    /// <summary>Executes one exact reflected OpenTK overload; array and by-ref outputs remain in args.</summary>
    internal object? Dispatch(MethodInfo method, object?[] args)
    {
        RequireOwner();
        object? result = method.ReturnType == typeof(void) ? null : method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
        if (method.Name is "GetUniformLocation" or "GetAttribLocation") result = -1;
        try
        {
            result = Command(method, args);
            if (method.ReturnType.IsEnum && result is not null && !method.ReturnType.IsInstanceOfType(result)) result = Enum.ToObject(method.ReturnType, result);
            else if (result is not null && method.ReturnType.IsPrimitive && !method.ReturnType.IsInstanceOfType(result))
                result = Convert.ChangeType(result, method.ReturnType, System.Globalization.CultureInfo.InvariantCulture);
            return result;
        }
        catch (GlError failure) { QueueError(failure.Code); TraceGlError(method.Name, failure.Code); return result; }
        catch (OverflowException) { QueueError(0x0501); TraceGlError(method.Name, 0x0501); return result; }
        catch (OutOfMemoryException) { QueueError(0x0505); TraceGlError(method.Name, 0x0505); return result; }
    }
    private object? Command(MethodInfo method, object?[] a)
    {
        switch (method.Name)
        {
            case "GetError": return C.Errors.TryDequeue(out int error) ? error : 0;
            case "LoadBindings": if (a[0] is null) throw new ArgumentNullException("context"); ManagedGlCallPatches.ActivateNativeEntries(); return null;
            case "GetInteger": return Query(method, a, false);
            case "GetBoolean": return Query(method, a, true);
            case "GenFramebuffer": return GenFramebuffer();
            case "GenFramebuffers": return GenerateMany(method, a, GenFramebuffer);
            case "DeleteFramebuffer": DeleteFramebuffer(I(a[0])); return null;
            case "DeleteFramebuffers": DeleteMany(a, DeleteFramebuffer); return null;
            case "BindFramebuffer": BindFramebuffer(I(a[0]), I(a[1])); return null;
            case "FramebufferTexture2D": FramebufferTexture(I(a[0]), I(a[1]), I(a[2]), I(a[3]), I(a[4])); return null;
            case "CheckFramebufferStatus": { int framebuffer = FramebufferFor(I(a[0])); int status = Device.FramebufferStatus(framebuffer); session.Graphics.RefreshManagedFramebuffer(framebuffer); return status; }
            case "GetFramebufferAttachmentParameter": return FramebufferAttachmentQuery(method, a);
            case "DrawBuffer": SelectDrawBuffers([I(a[0])]); return null;
            case "DrawBuffers": { int count = I(a[0]); if (count < 0 || count > StatedRenderState.MaxColorAttachments) Error(0x0501); SelectDrawBuffers(ReadInts(a[1], count)); return null; }
            case "ReadBuffer": SelectReadBuffer(I(a[0])); return null;
            case "BlitFramebuffer": BlitFramebuffer(a); return null;
            case "CopyTexSubImage2D": CopyTexSubImage(a); return null;
            case "PixelStore": PixelStore(I(a[0]), I(a[1])); return null;
            case "GetTexImage": GetTexImage(a); return null;
            case "GetTextureSubImage": GetTextureSubImage(a); return null;
            case "ReadPixels": ReadPixels(a); return null;
            case "GetTexLevelParameter": return TextureLevelQuery(method, a);
            case "IsTexture": return textures.TryGetValue(I(a[0]), out TextureObject? queriedTexture) ? queriedTexture.Created && !queriedTexture.Deleted : Device.IsTexture(I(a[0]));
            case "GenBuffer": { int name = Name(); buffers.Add(name, new(name)); return name; }
            case "GenBuffers": return GenerateMany(method, a, () => { int name = Name(); buffers.Add(name, new(name)); return name; });
            case "DeleteBuffer": DeleteBuffer(I(a[0])); return null;
            case "DeleteBuffers": DeleteMany(a, DeleteBuffer); return null;
            case "BindBuffer": BindBuffer(I(a[0]), I(a[1])); return null;
            case "BufferData": BufferData(I(a[0]), L(a[1]), a[2], I(a[3])); return null;
            case "BufferSubData": BufferSubData(I(a[0]), L(a[1]), L(a[2]), a[3]); return null;
            case "GenVertexArray": { int name = Name(); C.Arrays.Add(name, new()); return name; }
            case "GenVertexArrays": return GenerateMany(method, a, () => { int name = Name(); C.Arrays.Add(name, new()); return name; });
            case "BindVertexArray": if (!C.Arrays.ContainsKey(I(a[0]))) Error(0x0502); C.VertexArray = I(a[0]); return null;
            case "DeleteVertexArray": DeleteVertexArray(I(a[0])); return null;
            case "DeleteVertexArrays": DeleteMany(a, DeleteVertexArray); return null;
            case "VertexAttribPointer": SetAttribute(I(a[0]), I(a[1]), I(a[2]), Convert.ToBoolean(a[3]), I(a[4]), L(a[5]), false); return null;
            case "VertexAttribIPointer": SetAttribute(I(a[0]), I(a[1]), I(a[2]), false, I(a[3]), L(a[4]), true); return null;
            case "EnableVertexAttribArray": AttributeEnabled(I(a[0]), true); return null;
            case "DisableVertexAttribArray": AttributeEnabled(I(a[0]), false); return null;
            case "DrawElements": Draw(I(a[0]), I(a[1]), I(a[2]), L(a[3]), 0); return null;
            case "DrawElementsBaseVertex": Draw(I(a[0]), I(a[1]), I(a[2]), L(a[3]), I(a[4])); return null;
            case "DrawArrays": DrawArrays(I(a[0]), I(a[1]), I(a[2])); return null;
            case "CreateShader": { int type = I(a[0]); if (type is not (0x8B31 or 0x8B30 or 0x8DD9)) Error(0x0500); int name = Name(); shaders.Add(name, new(type)); return name; }
            case "ShaderSource": ShaderSource(a); return null;
            case "CompileShader": CompileShader(I(a[0])); return null;
            case "GetShader": return ShaderQuery(method, a);
            case "GetShaderInfoLog": return InfoLog(method, a, Shader(I(a[0])).Log);
            case "DeleteShader": DeleteShader(I(a[0])); return null;
            case "CreateProgram": { int name = Name(); programs.Add(name, new()); return name; }
            case "AttachShader": AttachShader(I(a[0]), I(a[1])); return null;
            case "DetachShader": DetachShader(I(a[0]), I(a[1])); return null;
            case "LinkProgram": LinkProgram(I(a[0])); return null;
            case "GetProgram": return ProgramQuery(method, a);
            case "GetProgramInfoLog": return InfoLog(method, a, Program(I(a[0])).Log);
            case "DeleteProgram": DeleteProgram(I(a[0])); return null;
            case "UseProgram": UseProgram(I(a[0])); return null;
            case "GetUniformLocation": return UniformLocation(I(a[0]), (string)a[1]!);
            case "GetAttribLocation": { var program = Program(I(a[0])); if (!program.Linked) Error(0x0502); return Device.GetAttributeLocation(program.Backend, (string)a[1]!); }
            case "BindAttribLocation": { var program = Program(I(a[0])); int index = I(a[1]); if (index < 0 || index >= 16) Error(0x0501); program.Attributes[(string)a[2]!] = index; return null; }
            case "Uniform1": Uniform1(method, a); return null;
            case "Uniform2": case "Uniform3": case "Uniform4": UniformVector(method, a); return null;
            case "UniformMatrix4": UniformMatrix4(a); return null;
            case "GenTexture": { int name = Device.ReserveTextureName(); textures.Add(name, new(name)); return name; }
            case "GenTextures": return GenerateMany(method, a, () => { int name = Device.ReserveTextureName(); textures.Add(name, new(name)); return name; });
            case "DeleteTexture": DeleteTexture(I(a[0])); return null;
            case "DeleteTextures": DeleteMany(a, DeleteTexture); return null;
            case "ActiveTexture": { int unit = I(a[0]) - 0x84C0; if ((uint)unit >= StatedRenderState.MaxTextureUnits) Error(0x0500); ActiveUnit = unit; return null; }
            case "BindTexture": BindTexture(I(a[0]), I(a[1])); return null;
            case "TexStorage2D": TextureStorage(I(a[0]), I(a[1]), I(a[2]), I(a[3]), I(a[4])); return null;
            case "TexImage2D": TextureImage(a, false); return null;
            case "TexImage3D": TextureImage(a, true); return null;
            case "TexSubImage2D": TextureSubImage(a, false); return null;
            case "TexSubImage3D": TextureSubImage(a, true); return null;
            case "GenerateMipmap": GenerateTextureMips(I(a[0])); return null;
            case "TexParameter": TextureParameter(a); return null;
            case "Enable": SetEnabled(I(a[0]), true); return null;
            case "Disable": SetEnabled(I(a[0]), false); return null;
            case "IsEnabled": return Enabled(I(a[0]));
            case "DepthMask": State.DepthWrite = Convert.ToBoolean(a[0]); return null;
            case "DepthFunc": { int function = I(a[0]); if (function < 0x0200 || function > 0x0207) Error(0x0500); State.DepthCompare = GlEnums.CompareOpFrom(function); return null; }
            case "ColorMask": State.SetColorMask(Convert.ToBoolean(a[0]), Convert.ToBoolean(a[1]), Convert.ToBoolean(a[2]), Convert.ToBoolean(a[3])); return null;
            case "CullFace": State.CullFaces = I(a[0]) switch { 0x0404 => CullModeFlags.FrontBit, 0x0405 => CullModeFlags.BackBit, 0x0408 => CullModeFlags.FrontAndBack, _ => throw new GlError(0x0500) }; return null;
            case "BlendEquation": BlendEquation(I(a[0]), I(a[0])); return null;
            case "BlendEquationSeparate": BlendEquation(I(a[0]), I(a[1])); return null;
            case "BlendFunc": BlendFunc(I(a[0]), I(a[1]), I(a[0]), I(a[1])); return null;
            case "BlendFuncSeparate": BlendFunc(I(a[0]), I(a[1]), I(a[2]), I(a[3])); return null;
            case "PolygonMode": PolygonMode(I(a[0]), I(a[1])); return null;
            case "Scissor": Rectangle(a, false); return null;
            case "Viewport": Rectangle(a, true); return null;
            case "ClearColor": C.ClearColor = a.Length == 1 && a[0] is Color4 color
                ? [color.R, color.G, color.B, color.A]
                : [Convert.ToSingle(a[0]), Convert.ToSingle(a[1]), Convert.ToSingle(a[2]), Convert.ToSingle(a[3])]; return null;
            case "Clear": { int mask = I(a[0]); if ((mask & ~0x4500) != 0) Error(0x0501); if (Device.FramebufferStatus(Target) != 0x8CD5) Error(0x0506); session.Graphics.ClearManagedGlTarget(Target, State, mask, C.ClearColor); return null; }
            default: throw new NotSupportedException("Managed GL does not implement " + method + ".");
        }
    }

    private object? GenerateMany(MethodInfo method, object?[] a, System.Func<int> generate)
    {
        int count = I(a[0]); if (count < 0) Error(0x0501);
        int[] values = Enumerable.Range(0, count).Select(_ => generate()).ToArray();
        WriteOutput(method, a, 1, values, false); return null;
    }
    private static void DeleteMany(object?[] a, Action<int> delete)
    {
        int count = I(a[0]); if (count < 0) Error(0x0501);
        int[] names = ReadInts(a[1], count);
        foreach (int name in names) delete(name);
    }
    private VertexArrayObject Array => C.Arrays[C.VertexArray];
    private BufferObject BoundBuffer(int target)
    {
        BufferObject? buffer = target switch { 0x8892 => C.ArrayBuffer, 0x8893 => Array.Elements, _ => null };
        if (target is not (0x8892 or 0x8893)) Error(0x0500);
        if (buffer is null) Error(0x0502);
        return buffer!;
    }
    private void BindBuffer(int target, int name)
    {
        if (target is not (0x8892 or 0x8893)) Error(0x0500);
        if (name < 0) Error(0x0501);
        BufferObject? buffer = null;
        if (name != 0 && !buffers.TryGetValue(name, out buffer)) { buffer = new(name); buffers.Add(name, buffer); }
        if (target == 0x8892) C.ArrayBuffer = buffer; else Array.Elements = buffer;
    }
    private void DeleteBuffer(int name)
    {
        if (!buffers.Remove(name, out BufferObject? buffer)) return;
        if (ReferenceEquals(C.ArrayBuffer, buffer)) C.ArrayBuffer = null;
        // Delete detaches from the bound VAO only; other VAOs retain the object
        // (not a lookup through the now-deleted public name).
        if (ReferenceEquals(Array.Elements, buffer)) Array.Elements = null;
        foreach (int index in Array.Attributes.Where(entry => ReferenceEquals(entry.Value.Buffer, buffer)).Select(entry => entry.Key).ToArray()) Array.Attributes.Remove(index);
    }
    private void BufferData(int target, long size, object? data, int usage)
    {
        if (size < 0 || size > int.MaxValue) Error(0x0501);
        if (usage is not (0x88E0 or 0x88E1 or 0x88E2 or 0x88E4 or 0x88E5 or 0x88E6 or 0x88E8 or 0x88E9 or 0x88EA)) Error(0x0500);
        BoundBuffer(target).Data = CopyBytes(data, (int)size, allowNull: true);
    }
    private void BufferSubData(int target, long offset, long size, object? data)
    {
        BufferObject buffer = BoundBuffer(target);
        if (offset < 0 || size < 0 || size > int.MaxValue || offset > buffer.Data.LongLength - size) Error(0x0501);
        if (size == 0) return;
        CopyBytes(data, (int)size, false).CopyTo(buffer.Data, (int)offset);
    }
    private void DeleteVertexArray(int name)
    {
        if (name == 0) return;
        C.Arrays.Remove(name);
        if (C.VertexArray == name) C.VertexArray = 0;
    }
    private void SetAttribute(int index, int components, int type, bool normalized, int stride, long offset, bool integer)
    {
        if (index < 0 || index >= 16 || components < 1 || components > 4 || stride < 0 || offset < 0 || offset > int.MaxValue) Error(0x0501);
        if (type is not (0x1400 or 0x1401 or 0x1402 or 0x1403 or 0x1404 or 0x1405 or 0x1406)) Error(0x0500);
        if (integer && type == 0x1406) Error(0x0500);
        if (C.VertexArray == 0 || C.ArrayBuffer is null) Error(0x0502);
        bool enabled = Array.Enabled.Contains(index);
        Array.Attributes[index] = new(C.ArrayBuffer!, components, type, normalized, integer, stride, (int)offset) { Enabled = enabled };
    }
    private void AttributeEnabled(int index, bool enabled)
    {
        if (index < 0 || index >= 16) Error(0x0501);
        if (C.VertexArray == 0) Error(0x0502);
        if (enabled) Array.Enabled.Add(index); else Array.Enabled.Remove(index);
        if (Array.Attributes.TryGetValue(index, out Attribute? attribute)) attribute.Enabled = enabled;
    }
    private void DrawArrays(int mode, int first, int count)
    {
        if (first < 0 || count < 0 || (long)first + count > int.MaxValue) Error(0x0501);
        var indices = new int[count];
        for (int i = 0; i < count; i++) indices[i] = first + i;
        Draw(mode, count, 0x1405, 0, 0, indices);
    }

    private void Draw(int mode, int count, int indexType, long offset, int baseVertex, int[]? suppliedIndices = null)
    {
        if (count < 0 || offset < 0) Error(0x0501);
        if (Device.FramebufferStatus(Target) != 0x8CD5) Error(0x0506);
        MeshDrawMode topology = mode switch { 4 => MeshDrawMode.Triangles, 1 => MeshDrawMode.Lines, 3 => MeshDrawMode.LineStrip, _ => throw new NotSupportedException("Managed GL primitive topology " + mode + " has no backend mapping.") };
        int size = indexType switch { 0x1401 => 1, 0x1403 => 2, 0x1405 => 4, _ => 0 };
        if (size == 0) Error(0x0500);
        if (offset % size != 0) Error(0x0502);
        if (count == 0) return;
        if (State.Viewport.Extent.Width == 0 || State.Viewport.Extent.Height == 0 ||
            State.ScissorEnabled && (State.Scissor.Extent.Width == 0 || State.Scissor.Extent.Height == 0)) return;
        if (C.PolygonFront != C.PolygonBack)
            throw new NotSupportedException("Managed GL drawing with distinct front/back polygon modes needs per-face rasterization capability.");
        if (C.PolygonFront == 0x1B00)
            throw new NotSupportedException("Managed GL point polygon mode needs a point-polygon pipeline capability.");
        if (C.VertexArray == 0 || suppliedIndices is null && Array.Elements is null || CurrentProgram == 0) Error(0x0502);
        if (Array.Enabled.Any(index => !Array.Attributes.ContainsKey(index)))
            throw new NotSupportedException("Managed GL drawing a constant/default vertex attribute needs a constant-attribute backend capability.");
        byte[] elements = Array.Elements?.Data ?? [];
        if (suppliedIndices is null && offset > elements.LongLength - (long)count * size) Error(0x0502);
        int[] indices = suppliedIndices ?? new int[count];
        long maximum = 0;
        for (int index = 0; index < count; index++)
        {
            long raw;
            if (suppliedIndices is not null) raw = unchecked((uint)suppliedIndices[index]);
            else
            {
                ReadOnlySpan<byte> value = elements.AsSpan(checked((int)offset + index * size), size);
                raw = size == 1 ? value[0] : size == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(value) : BinaryPrimitives.ReadUInt32LittleEndian(value);
            }
            long adjusted = raw + baseVertex;
            if (adjusted < 0 || adjusted > uint.MaxValue) Error(0x0502);
            indices[index] = unchecked((int)(uint)adjusted);
            maximum = Math.Max(maximum, adjusted);
        }
        var streams = new List<RawVertexStream>();
        foreach (var entry in Array.Attributes.OrderBy(entry => entry.Key))
        {
            Attribute attribute = entry.Value; if (!attribute.Enabled) continue;
            int scalar = attribute.Type is 0x1400 or 0x1401 ? 1 : attribute.Type is 0x1402 or 0x1403 ? 2 : 4;
            int bytes = scalar * attribute.Components, stride = attribute.Stride == 0 ? bytes : attribute.Stride;
            if ((long)attribute.Offset + maximum * stride + bytes > attribute.Buffer.Data.LongLength) Error(0x0502);
            streams.Add(new(entry.Key, attribute.Components, (RawVertexElementType)attribute.Type,
                attribute.Normalized, attribute.Integer, attribute.Stride, attribute.Offset, attribute.Buffer.Data));
        }
        int mesh = Device.CreateRawMesh(new() { VertexStreams = streams.ToArray(), Indices = indices, Mode = topology });
        try
        {
            session.Graphics.RecordManagedGlDraw(mesh, CurrentProgram, State, Target, C.Primary);
            TraceDraw(indices, streams, mode, indexType, offset, baseVertex);
        }
        finally { Device.DeleteMesh(mesh); }
    }

    private ShaderObject Shader(int name) { if (!shaders.TryGetValue(name, out ShaderObject? shader)) Error(0x0501); return shader!; }
    private ProgramObject Program(int name)
    {
        if (programs.TryGetValue(name, out ProgramObject? program)) return program;
        // RenderAPI-created programs retain their ordinary renderer name. Only
        // the GL uniform-location view belongs to this bridge, never the program.
        if (!Device.IsProgram(name)) Error(0x0501);
        program = new ProgramObject { Backend = name, Linked = true, Borrowed = true };
        programs.Add(name, program);
        return program;
    }
    private void ForgetBorrowedProgram(int name)
    {
        if (programs.TryGetValue(name, out ProgramObject? program) && program.Borrowed) programs.Remove(name);
    }
    private void ShaderSource(object?[] a)
    {
        ShaderObject shader = Shader(I(a[0]));
        if (a.Length == 2 && a[1] is string source) shader.Source = source;
        else if (a.Length >= 3 && a[2] is string[] sources)
        {
            int count = I(a[1]); if (count < 0 || count > sources.Length) Error(0x0501);
            int[]? lengths = a.Length > 3 && a[3] is not null && !(a[3] is IntPtr ptr && ptr == IntPtr.Zero) ? ReadInts(a[3], count) : null;
            shader.Source = string.Concat(sources.Take(count).Select((text, index) => lengths is null || lengths[index] < 0 ? text : text[..Math.Min(text.Length, lengths[index])]));
        }
        else throw new NotSupportedException("Managed GL ShaderSource requires the string or string-array OpenTK overload.");
        shader.Compiled = false; shader.Log = "";
    }
    private void CompileShader(int name)
    {
        ShaderObject shader = Shader(name);
        shader.Compiled = Device.ValidateGlShader((ShaderStageType)shader.Type, shader.Source, out shader.Log);
    }
    private void AttachShader(int programName, int shaderName)
    {
        ProgramObject program = Program(programName); ShaderObject shader = Shader(shaderName);
        if (program.Stages.ContainsKey(shaderName)) Error(0x0502);
        program.Stages.Add(shaderName, shader);
    }
    private void DetachShader(int programName, int shaderName)
    {
        if (!Program(programName).Stages.Remove(shaderName)) Error(0x0502);
        CollectDeletedShaders();
    }
    private void DeleteShader(int name) { if (name == 0) return; Shader(name).Deleted = true; CollectDeletedShaders(); }
    private void CollectDeletedShaders()
    {
        foreach (int name in shaders.Where(entry => entry.Value.Deleted && !programs.Values.Any(program => program.Stages.ContainsKey(entry.Key))).Select(entry => entry.Key).ToArray()) shaders.Remove(name);
    }
    private void LinkProgram(int name)
    {
        ProgramObject program = Program(name);
        program.Linked = false; program.NamedLocations.Clear(); program.Locations.Clear();
        program.PublicLocations.Clear(); program.TracedUniformValues.Clear();
        if (program.Stages.Values.Any(stage => !stage.Compiled)) { program.Log = "An attached shader has not compiled successfully."; return; }
        if (program.Stages.Values.GroupBy(stage => stage.Type).Any(group => group.Count() > 1)) { program.Log = "Multiple source objects for one stage require a source-linker capability."; return; }
        ShaderStageDefinition? Stage(int type)
        {
            ShaderObject? shader = program.Stages.Values.FirstOrDefault(stage => stage.Type == type);
            if (shader is null) return null;
            // The renderer retains GL image orientation throughout rendering;
            // presentation performs the single vertical flip for every window.
            var definition = new ShaderStageDefinition { Type = (ShaderStageType)type, Code = shader.Source };
            if (!Device.CompileShader(definition))
                throw new InvalidOperationException("Managed GL shader staging failed: " + Device.GetError());
            return definition;
        }
        int backend = Device.LinkProgram(new ShaderProgramDefinition("managed-gl-" + name,
            Stage(0x8B31), Stage(0x8B30), Stage(0x8DD9), null, program.Attributes));
        if (backend == 0) { program.Log = Device.GetError() ?? "The shader translator/linker rejected the program."; return; }
        int previous = program.Backend;
        program.Backend = backend; program.Linked = true; program.Log = "";
        if (previous != 0)
        {
            foreach (Context context in contexts.Values)
                if (!context.Primary && context.Program == previous) context.Program = backend;
            if (session.Graphics.StatedProgram == previous) session.Graphics.UseShaderProgram(backend);
            Device.DeleteProgram(previous);
        }
    }

    private object? ShaderQuery(MethodInfo method, object?[] a)
    {
        ShaderObject shader = Shader(I(a[0]));
        int value = I(a[1]) switch { 0x8B81 => shader.Compiled ? 1 : 0, 0x8B80 => shader.Deleted ? 1 : 0, 0x8B4F => shader.Type, 0x8B84 => shader.Log.Length == 0 ? 0 : shader.Log.Length + 1, 0x8B88 => shader.Source.Length + 1, _ => -1 };
        if (value < 0) Error(0x0500);
        if (a.Length > 2) WriteOutput(method, a, 2, [value], false);
        return method.ReturnType == typeof(void) ? null : value;
    }
    private object? ProgramQuery(MethodInfo method, object?[] a)
    {
        ProgramObject program = Program(I(a[0]));
        int value = I(a[1]) switch { 0x8B82 => program.Linked ? 1 : 0, 0x8B80 => program.Deleted ? 1 : 0, 0x8B84 => program.Log.Length == 0 ? 0 : program.Log.Length + 1, 0x8B85 => program.Stages.Count, _ => -1 };
        if (value < 0) Error(0x0500);
        if (a.Length > 2) WriteOutput(method, a, 2, [value], false);
        return method.ReturnType == typeof(void) ? null : value;
    }
    private static object? InfoLog(MethodInfo method, object?[] a, string log)
    {
        if (method.ReturnType == typeof(string)) return log;
        if (a.Length < 4) throw new NotSupportedException("Managed GL info-log output overload " + method + " is unsupported.");
        int capacity = I(a[1]); if (capacity < 0) Error(0x0501);
        string text = log[..Math.Min(log.Length, Math.Max(0, capacity - 1))];
        WriteOutput(method, a, 2, [text.Length], false);
        if (a[3] is System.Text.StringBuilder builder) { builder.Clear(); builder.Append(text); }
        else if (method.GetParameters()[3].ParameterType.GetElementType() == typeof(string)) a[3] = text;
        else { IntPtr pointer = Pointer(a[3]); byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text + (capacity > 0 ? "\0" : "")); if (bytes.Length > 0) Marshal.Copy(bytes, 0, pointer, bytes.Length); }
        return null;
    }
    private void UseProgram(int name)
    {
        int backend = name;
        if (name != 0 && programs.TryGetValue(name, out ProgramObject? program))
        { if (!program.Linked) Error(0x0502); backend = program.Backend; }
        else if (name != 0 && !Device.IsProgram(name)) Error(0x0501);
        // Original game programs are already the renderer's IDs. This permits
        // SaveGLState to restore the real current game program after a raw draw.
        if (C.Primary) session.Graphics.UseShaderProgram(backend); else C.Program = backend;
        CollectDeletedPrograms();
    }
    private void DeleteProgram(int name) { if (name == 0) return; Program(name).Deleted = true; CollectDeletedPrograms(); }
    private void CollectDeletedPrograms()
    {
        foreach (int name in programs.Where(entry => entry.Value.Deleted && (entry.Value.Backend == 0 || !contexts.Values.Any(context =>
            (context.Primary ? session.Graphics.StatedProgram : context.Program) == entry.Value.Backend))).Select(entry => entry.Key).ToArray())
        { ProgramObject program = programs[name]; if (program.Backend != 0) session.Graphics.DeleteProgram(program.Backend); programs.Remove(name); }
        CollectDeletedShaders();
    }
    private int UniformLocation(int name, string uniformName)
    {
        ProgramObject program = Program(name);
        if (!program.Linked) Error(0x0502);
        if (program.NamedLocations.TryGetValue(uniformName, out int location)) return location;
        int backendLocation = Device.GetUniformLocation(program.Backend, uniformName);
        if (backendLocation == -1) return -1;
        if (!program.PublicLocations.TryGetValue(backendLocation, out location))
        {
            location = program.Locations.Count;
            program.Locations.Add(location, backendLocation);
            program.PublicLocations.Add(backendLocation, location);
        }
        program.NamedLocations.Add(uniformName, location);
        return location;
    }
    private ProgramObject UniformProgram(int location, out int backendLocation)
    {
        backendLocation = -1;
        ProgramObject? program = programs.Values.FirstOrDefault(program => program.Backend == CurrentProgram && program.Linked);
        if (program is null || !program.Locations.TryGetValue(location, out backendLocation)) Error(0x0502);
        return program!;
    }
    private void Uniform1(MethodInfo method, object?[] a)
    {
        int location = I(a[0]); if (location == -1) return;
        ProgramObject program = UniformProgram(location, out int backendLocation);
        if (a.Length == 2)
        {
            if (a[1] is float value) session.Graphics.Uniform(program.Backend, backendLocation, value);
            else if (a[1] is int integer) session.Graphics.Uniform(program.Backend, backendLocation, integer);
            else throw new NotSupportedException("Managed GL Uniform1 requires a 32-bit float or signed integer uniform.");
            TraceUniform(program, location, a[1]!);
        }
        else if (a.Length == 3)
        {
            int count = I(a[1]); if (count < 0) Error(0x0501);
            Type element = method.GetParameters()[2].ParameterType.GetElementType()!;
            if (element != typeof(float)) throw new NotSupportedException("Managed GL integer uniform arrays require an integer-array uniform backend capability.");
            float[] values;
            if (a[2] is float[] array) { if (count > array.Length) Error(0x0501); values = array; }
            else { values = new float[count]; if (count > 0) { IntPtr pointer = Pointer(a[2]); if (pointer == IntPtr.Zero) Error(0x0501); Marshal.Copy(pointer, values, 0, count); } }
            session.Graphics.UniformArray(program.Backend, backendLocation, count, values, 1);
            if (count > 0 && TracePending) TraceUniform(program, location, new { count, values = values.Take(Math.Min(count, 64)).ToArray() });
        }
        else throw new NotSupportedException("Managed GL Uniform1 array/pointer form requires a matching scalar backend capability.");
    }
    private void UniformVector(MethodInfo method, object?[] a)
    {
        int location = I(a[0]); if (location == -1) return;
        ProgramObject program = UniformProgram(location, out int backendLocation);
        int components = method.Name[^1] - '0';
        if (a.Length == components + 1 && method.GetParameters()[1].ParameterType == typeof(float))
        {
            float x = Convert.ToSingle(a[1]), y = Convert.ToSingle(a[2]);
            if (components == 2) session.Graphics.Uniform(program.Backend, backendLocation, x, y);
            else if (components == 3) session.Graphics.Uniform(program.Backend, backendLocation, x, y, Convert.ToSingle(a[3]));
            else session.Graphics.Uniform(program.Backend, backendLocation, x, y, Convert.ToSingle(a[3]), Convert.ToSingle(a[4]));
        }
        else
        {
            int count = I(a[1]); if (count < 0 || count > int.MaxValue / components) Error(0x0501);
            int length = count * components;
            float[] values;
            if (a[2] is float[] array) { if (array.Length < length) Error(0x0501); values = array; }
            else { values = new float[length]; if (length > 0) { IntPtr pointer = Pointer(a[2]); if (pointer == IntPtr.Zero) Error(0x0501); Marshal.Copy(pointer, values, 0, length); } }
            session.Graphics.UniformArray(program.Backend, backendLocation, count, values, components);
        }
    }
    private void UniformMatrix4(object?[] a)
    {
        int location = I(a[0]); if (location == -1) return;
        ProgramObject program = UniformProgram(location, out int backendLocation);
        int count; bool transpose; float[] values;
        if (a.Length == 3 && a[2] is Matrix4 matrix)
        {
            count = 1; transpose = Convert.ToBoolean(a[1]);
            values = [matrix.M11, matrix.M12, matrix.M13, matrix.M14, matrix.M21, matrix.M22, matrix.M23, matrix.M24,
                matrix.M31, matrix.M32, matrix.M33, matrix.M34, matrix.M41, matrix.M42, matrix.M43, matrix.M44];
        }
        else if (a.Length == 4)
        {
            count = I(a[1]); if (count < 0 || count > int.MaxValue / 16) Error(0x0501);
            transpose = Convert.ToBoolean(a[2]);
            if (a[3] is float[] data) { if (data.Length < count * 16) Error(0x0501); values = data[..(count * 16)]; }
            else { values = new float[count * 16]; if (values.Length > 0) { IntPtr pointer = Pointer(a[3]); if (pointer == IntPtr.Zero) Error(0x0501); Marshal.Copy(pointer, values, 0, values.Length); } }
        }
        else throw new NotSupportedException("Managed GL UniformMatrix4 overload requires float matrices.");
        if (transpose)
            for (int matrixIndex = 0; matrixIndex < count; matrixIndex++)
                for (int row = 0; row < 4; row++) for (int column = row + 1; column < 4; column++)
                    (values[matrixIndex * 16 + row * 4 + column], values[matrixIndex * 16 + column * 4 + row]) =
                        (values[matrixIndex * 16 + column * 4 + row], values[matrixIndex * 16 + row * 4 + column]);
        session.Graphics.UniformMatrices(program.Backend, backendLocation, count, values);
        if (count > 0 && TracePending) TraceUniform(program, location, new { count, values = values.Take(64).ToArray() });
    }

    private void TraceUniform(ProgramObject program, int location, object value)
    {
        if (TracePending) program.TracedUniformValues[location] = value;
    }
    private void TraceDraw(int[] indices, List<RawVertexStream> streams,
        int mode, int indexType, long offset, int baseVertex)
    {
        if (!TracePending) return;
        // This opt-in producer snapshot is bounded per primary/auxiliary class,
        // independent of world reloads; it never consumes the GL error queue.
        int ordinal = C.Primary ? ++tracedMainDraws : ++tracedAuxiliaryDraws;
        try
        {
            var managedProgram = programs.FirstOrDefault(entry => entry.Value.Linked && entry.Value.Backend == CurrentProgram);
            object[] uniforms = managedProgram.Value is { } program
                ? program.NamedLocations.Take(64).Select(entry => (object)new
                {
                    name = entry.Key, location = entry.Value, backendLocation = program.Locations[entry.Value],
                    value = program.TracedUniformValues.GetValueOrDefault(entry.Value)
                }).ToArray() : [];
            object[] attributes = streams.Select(stream => (object)new
            {
                location = stream.Location, components = stream.Components, type = (int)stream.ElementType,
                normalized = stream.Normalized, integer = stream.Integer, stride = stream.Stride, offset = stream.Offset,
                vertices = indices.Take(6).Distinct().Select(index =>
                {
                    int scalarBytes = stream.ElementType is RawVertexElementType.Byte or RawVertexElementType.UnsignedByte ? 1 :
                        stream.ElementType is RawVertexElementType.Short or RawVertexElementType.UnsignedShort ? 2 : 4;
                    int bytes = scalarBytes * stream.Components;
                    int start = checked(stream.Offset + index * (stream.Stride == 0 ? bytes : stream.Stride));
                    byte[] raw = stream.Data.AsSpan(start, bytes).ToArray();
                    return new { index, bytes = Convert.ToHexString(raw), values = Enumerable.Range(0, stream.Components)
                        .Select(component => TraceVertexScalar(raw, component * scalarBytes, stream.ElementType)).ToArray() };
                }).ToArray()
            }).ToArray();
            var snapshot = new
            {
                @event = "draw-recorded", primary = C.Primary, recorded = true,
                draw = ordinal, frameId = Device.LatencyFrameId, target = Target, context = C.Id.ToInt64(),
                width = C.Width, height = C.Height, program = managedProgram.Key, backendProgram = CurrentProgram,
                viewport = new[] { State.Viewport.Offset.X, State.Viewport.Offset.Y, (int)State.Viewport.Extent.Width, (int)State.Viewport.Extent.Height },
                scissorEnabled = State.ScissorEnabled,
                scissor = new[] { State.Scissor.Offset.X, State.Scissor.Offset.Y, (int)State.Scissor.Extent.Width, (int)State.Scissor.Extent.Height },
                mode, indexType, indexByteOffset = offset, baseVertex, indexCount = indices.Length, indices = indices.Take(12).ToArray(),
                uniforms, attributes, activeTextureUnit = ActiveUnit, boundTexture = BoundTexture(), texture0 = State.TextureAt(0),
                boundTextures = Enumerable.Range(0, StatedRenderState.MaxTextureUnits).Select(State.TextureAt).ToArray(),
                errors = C.Errors.ToArray()
            };
            WriteTrace(snapshot);
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { drawTracePath = null; }
        if (tracedMainDraws == 8 && tracedAuxiliaryDraws == 8)
            foreach (ProgramObject program in programs.Values) program.TracedUniformValues.Clear();
    }
    private void TraceGlError(string command, int error)
    {
        if (drawTracePath is null || tracedErrors >= 8) return;
        tracedErrors++;
        WriteTrace(new { @event = "gl-error", command, error, primary = C.Primary, frameId = Device.LatencyFrameId,
            target = Target, context = C.Id.ToInt64(), backendProgram = CurrentProgram });
    }
    private void WriteTrace(object snapshot)
    {
        try
        {
            System.IO.File.AppendAllText(drawTracePath!, System.Text.Json.JsonSerializer.Serialize(snapshot,
                new System.Text.Json.JsonSerializerOptions
                { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }) + Environment.NewLine);
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { drawTracePath = null; }
    }
    private static double TraceVertexScalar(byte[] bytes, int at, RawVertexElementType type) => type switch
    {
        RawVertexElementType.Byte => unchecked((sbyte)bytes[at]),
        RawVertexElementType.UnsignedByte => bytes[at],
        RawVertexElementType.Short => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(at)),
        RawVertexElementType.UnsignedShort => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at)),
        RawVertexElementType.Int => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at)),
        RawVertexElementType.UnsignedInt => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at)),
        RawVertexElementType.Float => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at))),
        _ => throw new InvalidOperationException("Unrecognized managed GL vertex scalar type.")
    };

    private int BoundTexture(int target = 0x0DE1) => State.TextureAt(ActiveUnit, target == 0x806F ? TextureKind.Texture3D : TextureKind.Texture2D);
    private void BindTexture(int target, int name)
    {
        if (target is not (0x0DE1 or 0x806F)) Error(0x0500);
        if (name < 0) Error(0x0501);
        if (name != 0)
        {
            if (textures.TryGetValue(name, out TextureObject? texture))
            {
                if (texture.Deleted || texture.Target != 0 && texture.Target != target) Error(0x0502);
                if (!texture.Created)
                {
                    Device.SetTextureParameter(name, 0x2800, 0x2601);
                    Device.SetTextureParameter(name, 0x2801, 0x2702);
                    if (target == 0x806F) Device.SetTextureParameter(name, 0x8072, 0x2901);
                }
                texture.Created = true; texture.Target = target;
            }
            else if (!Device.IsTexture(name) || Device.TextureOf(name)!.Volume != (target == 0x806F)) Error(0x0502);
        }
        State.BindTextureTarget(ActiveUnit, name, target);
        CollectDeletedTextures();
    }
    private void TextureStorage(int target, int levels, int format, int width, int height)
    {
        if (target != 0x0DE1) Error(0x0500);
        if (width <= 0 || height <= 0 || levels <= 0 || levels > 1 + (int)Math.Floor(Math.Log2(Math.Max(width, height)))) Error(0x0501);
        TextureObject? texture = null;
        if (BoundTexture() == 0 || !textures.TryGetValue(BoundTexture(), out texture)) Error(0x0502);
        if (texture!.Immutable) Error(0x0502);
        RequireImageFormat(format);
        if (format is 0x1902 or 0x1903 or 0x1907 or 0x1908 or 0x8227) Error(0x0500);
        Device.DefineMutableTexture(texture.Name, width, height, 1, format, levels, false, -1);
        texture.Width = width; texture.Height = height; texture.Levels = levels; texture.Format = format; texture.Immutable = true;
        texture.Images.Clear();
        for (int level = 0; level < levels; level++) texture.Images[level] = new(Math.Max(1, width >> level), Math.Max(1, height >> level), 1, format);
    }
    private void TextureParameter(object?[] a)
    {
        int target = I(a[0]); if (target is not (0x0DE1 or 0x806F)) Error(0x0500);
        int name = BoundTexture(target); if (name == 0) Error(0x0502);
        int parameter = I(a[1]);
        if (parameter is not (0x2800 or 0x2801 or 0x2802 or 0x2803 or 0x8072 or 0x813C or 0x813D or 0x8501 or 0x84FE or 0x884C))
            throw new NotSupportedException("Managed GL texture parameter 0x" + parameter.ToString("X") + " is unsupported.");
        float scalar = Convert.ToSingle(a[2]);
        if (!float.IsFinite(scalar)) Error(0x0501);
        int integer = (int)scalar;
        if (parameter == 0x813C && integer != 0) throw new NotSupportedException("Nonzero texture base levels require sampler-view offsets.");
        if (parameter == 0x2800 && integer is not (0x2600 or 0x2601) ||
            parameter == 0x2801 && integer is not (0x2600 or 0x2601 or 0x2700 or 0x2701 or 0x2702 or 0x2703) ||
            parameter is 0x2802 or 0x2803 or 0x8072 && integer is not (0x2901 or 0x812F or 0x8370 or 0x812D) ||
            parameter == 0x884C && integer is not (0 or 0x884E)) Error(0x0500);
        if (parameter is 0x813C or 0x813D && integer < 0 || parameter == 0x84FE && scalar < 1) Error(0x0501);
        if (a[2] is float value) Device.SetTextureParameter(name, parameter, value);
        else Device.SetTextureParameter(name, parameter, I(a[2]));
    }
    private void DeleteTexture(int name)
    {
        if (name == 0) return;
        if (!textures.TryGetValue(name, out TextureObject? texture))
        { session.Graphics.DeleteTexture(name); State.ForgetTexture(name); return; }
        texture.Deleted = true;
        State.ForgetTexture(name);
        CollectDeletedTextures();
    }
    private void CollectDeletedTextures()
    {
        foreach (int name in textures.Where(entry => entry.Value.Deleted && !contexts.Values.Any(context =>
            (context.Primary ? session.Graphics.Stated : context.State).HasTexture(entry.Key))).Select(entry => entry.Key).ToArray())
        { Device.DeleteTexture(name); textures.Remove(name); }
    }

    private bool Enabled(int cap) => cap switch
    { 0x0BE2 => State.BlendEnabled, 0x0B44 => State.CullEnabled, 0x0B71 => State.DepthTest, 0x0C11 => State.ScissorEnabled, _ => throw new GlError(0x0500) };
    private void SetEnabled(int cap, bool enabled)
    {
        switch (cap)
        { case 0x0BE2: State.SetBlendEnabled(enabled); break; case 0x0B44: State.CullEnabled = enabled; break; case 0x0B71: State.DepthTest = enabled; break; case 0x0C11: State.ScissorEnabled = enabled; break; default: Error(0x0500); break; }
    }
    private static int BlendFactorToken(BlendFactor factor) => factor switch
    { BlendFactor.Zero => 0, BlendFactor.One => 1, BlendFactor.SrcColor => 0x0300, BlendFactor.OneMinusSrcColor => 0x0301,
        BlendFactor.SrcAlpha => 0x0302, BlendFactor.OneMinusSrcAlpha => 0x0303, BlendFactor.DstAlpha => 0x0304,
        BlendFactor.OneMinusDstAlpha => 0x0305, BlendFactor.DstColor => 0x0306, BlendFactor.OneMinusDstColor => 0x0307,
        BlendFactor.SrcAlphaSaturate => 0x0308, _ => throw new NotSupportedException("Managed GL blend factor has no token mapping: " + factor) };
    private static int BlendEquationToken(BlendOp equation) => equation switch
    { BlendOp.Add => 0x8006, BlendOp.Subtract => 0x800A, BlendOp.ReverseSubtract => 0x800B, BlendOp.Min => 0x8007, BlendOp.Max => 0x8008,
        _ => throw new NotSupportedException("Managed GL blend equation has no token mapping: " + equation) };
    private void BlendEquation(int rgb, int alpha)
    {
        if (rgb is not (0x8006 or 0x800A or 0x800B or 0x8007 or 0x8008) || alpha is not (0x8006 or 0x800A or 0x800B or 0x8007 or 0x8008)) Error(0x0500);
        for (int slot = 0; slot < StatedRenderState.MaxColorAttachments; slot++)
        { AttachmentBlend blend = State.RawBlend(slot); State.SetSlotBlendSeparate(slot, rgb, alpha, BlendFactorToken(blend.SrcColor), BlendFactorToken(blend.DstColor), BlendFactorToken(blend.SrcAlpha), BlendFactorToken(blend.DstAlpha)); }
    }
    private void BlendFunc(int srcRgb, int dstRgb, int srcAlpha, int dstAlpha)
    {
        if (!ValidFactor(srcRgb) || !ValidFactor(dstRgb) || !ValidFactor(srcAlpha) || !ValidFactor(dstAlpha) || dstRgb == 0x0308 || dstAlpha == 0x0308) Error(0x0500);
        for (int slot = 0; slot < StatedRenderState.MaxColorAttachments; slot++)
        { AttachmentBlend blend = State.RawBlend(slot); State.SetSlotBlendSeparate(slot, BlendEquationToken(blend.ColorOp), BlendEquationToken(blend.AlphaOp), srcRgb, dstRgb, srcAlpha, dstAlpha); }
    }
    private static bool ValidFactor(int token) => token is 0 or 1 || token >= 0x0300 && token <= 0x0308;
    private void PolygonMode(int face, int mode)
    {
        if (face is not (0x0404 or 0x0405 or 0x0408) || mode is not (0x1B00 or 0x1B01 or 0x1B02)) Error(0x0500);
        if (face == 0x0404 || face == 0x0408) C.PolygonFront = mode;
        if (face == 0x0405 || face == 0x0408) C.PolygonBack = mode;
        if (C.PolygonFront == C.PolygonBack) State.Wireframe = mode == 0x1B01;
    }
    private void Rectangle(object?[] a, bool viewport)
    {
        int width = I(a[2]), height = I(a[3]); if (width < 0 || height < 0) Error(0x0501);
        var rectangle = new Rect2D(new Offset2D(I(a[0]), I(a[1])), new Extent2D((uint)width, (uint)height));
        if (viewport) State.Viewport = rectangle; else State.Scissor = rectangle;
    }
    private object? Query(MethodInfo method, object?[] a, bool boolean)
    {
        AttachmentBlend blend = State.RawBlend(0);
        int name = I(a[0]);
        int[] values = name switch
        {
            0x821B => [3], 0x821C => [3], 0x9126 => [1],
            0x85B5 => [C.VertexArray], 0x8894 => [C.ArrayBuffer?.Name ?? 0], 0x8895 => [Array.Elements?.Name ?? 0],
            0x8B8D => [CurrentProgram == 0 ? 0 : programs.FirstOrDefault(entry => entry.Value.Backend == CurrentProgram).Key is int owned && owned != 0 ? owned : CurrentProgram],
            0x84E0 => [0x84C0 + ActiveUnit], 0x8069 => [BoundTexture()],
            0x806A => [BoundTexture(0x806F)], 0x8073 => [Device.MaxVolumeSize],
            0x0BE2 => [State.BlendEnabled ? 1 : 0], 0x0C11 => [State.ScissorEnabled ? 1 : 0], 0x0B44 => [State.CullEnabled ? 1 : 0], 0x0B71 => [State.DepthTest ? 1 : 0],
            0x0B72 => [State.DepthWrite ? 1 : 0], 0x0B74 => [0x0200 + (int)State.DepthCompare],
            0x0B45 => [State.CullFaces switch { CullModeFlags.FrontBit => 0x0404, CullModeFlags.BackBit => 0x0405, _ => 0x0408 }],
            0x0C23 => [(State.ColorMask & ColorComponentFlags.RBit) != 0 ? 1 : 0, (State.ColorMask & ColorComponentFlags.GBit) != 0 ? 1 : 0,
                (State.ColorMask & ColorComponentFlags.BBit) != 0 ? 1 : 0, (State.ColorMask & ColorComponentFlags.ABit) != 0 ? 1 : 0],
            0x8009 => [BlendEquationToken(blend.ColorOp)], 0x883D => [BlendEquationToken(blend.AlphaOp)],
            0x80C9 => [BlendFactorToken(blend.SrcColor)], 0x80C8 => [BlendFactorToken(blend.DstColor)], 0x80CB => [BlendFactorToken(blend.SrcAlpha)], 0x80CA => [BlendFactorToken(blend.DstAlpha)],
            0x0C10 => [State.Scissor.Offset.X, State.Scissor.Offset.Y, (int)State.Scissor.Extent.Width, (int)State.Scissor.Extent.Height],
            0x0BA2 => [State.Viewport.Offset.X, State.Viewport.Offset.Y, (int)State.Viewport.Extent.Width, (int)State.Viewport.Extent.Height],
            0x0B40 => C.PolygonFront != C.PolygonBack ? [C.PolygonFront, C.PolygonBack] :
                [State.Wireframe ? 0x1B01 : 0x1B02, State.Wireframe ? 0x1B01 : 0x1B02],
            0x0D33 => [Device.MaxTextureSize], 0x8872 => [StatedRenderState.MaxTextureUnits],
            0x8CDF => [StatedRenderState.MaxColorAttachments], 0x8824 => [StatedRenderState.MaxColorAttachments],
            0x8CA6 => [PublicFramebuffer(Target)], 0x8CAA => [PublicFramebuffer(ReadTarget)],
            0x0C01 => [DrawBufferToken(0)], 0x0C02 => [ReadBuffer],
            >= 0x0CF0 and <= 0x0CF5 or >= 0x0D00 and <= 0x0D05 or 0x806B or 0x806C or 0x806D or 0x806E => [PixelStoreValue(name)],
            >= 0x8825 and < 0x8825 + StatedRenderState.MaxColorAttachments => [DrawBufferToken(name - 0x8825)],
            _ => throw new GlError(0x0500),
        };
        if (a.Length > 1) WriteOutput(method, a, 1, values, boolean);
        return method.ReturnType == typeof(void) ? null : boolean ? values[0] != 0 : values[0];
    }
    private static void WriteOutput(MethodInfo method, object?[] a, int index, int[] values, bool boolean)
    {
        Type type = method.GetParameters()[index].ParameterType;
        if (type.IsByRef && a[index] is not IntPtr)
        {
            if (values.Length != 1) throw new NotSupportedException("Managed GL multi-value queries require an array or pointer destination.");
            Type element = type.GetElementType()!;
            a[index] = element == typeof(bool) ? values[0] != 0 : Convert.ChangeType(values[0], element, System.Globalization.CultureInfo.InvariantCulture); return;
        }
        if (a[index] is Array array)
        {
            if (array.Length < values.Length) Error(0x0501);
            Type element = array.GetType().GetElementType()!;
            for (int i = 0; i < values.Length; i++)
                array.SetValue(element == typeof(bool) ? values[i] != 0 : Convert.ChangeType(values[i], element, System.Globalization.CultureInfo.InvariantCulture), i);
            return;
        }
        IntPtr pointer = Pointer(a[index]); if (pointer == IntPtr.Zero) Error(0x0501);
        if (boolean) for (int i = 0; i < values.Length; i++) Marshal.WriteByte(pointer, i, values[i] != 0 ? (byte)1 : (byte)0);
        else Marshal.Copy(values, 0, pointer, values.Length);
    }
    private static int[] ReadInts(object? value, int count)
    {
        if (value is Array enumArray && enumArray.GetType().GetElementType()!.IsEnum)
        {
            if (enumArray.Length < count) Error(0x0501);
            var tokens = new int[count];
            for (int i = 0; i < count; i++) tokens[i] = Convert.ToInt32(enumArray.GetValue(i));
            return tokens;
        }
        if (value is int[] ints) { if (ints.Length < count) Error(0x0501); return ints[..count]; }
        if (value is uint[] unsigned) { if (unsigned.Length < count) Error(0x0501); return unsigned.Take(count).Select(word => unchecked((int)word)).ToArray(); }
        if (value is int scalar && count == 1) return [scalar];
        if (value is uint word && count == 1) return [unchecked((int)word)];
        var result = new int[count]; if (count > 0) { IntPtr pointer = Pointer(value); if (pointer == IntPtr.Zero) Error(0x0501); Marshal.Copy(pointer, result, 0, count); } return result;
    }
    private static byte[] CopyBytes(object? value, int size, bool allowNull)
    {
        var result = new byte[size]; if (size == 0) return result;
        if (value is Array array)
        {
            if (System.Buffer.ByteLength(array) < size) Error(0x0501);
            System.Buffer.BlockCopy(array, 0, result, 0, size); return result;
        }
        IntPtr pointer = Pointer(value);
        if (pointer == IntPtr.Zero) { if (!allowNull) Error(0x0501); return result; }
        Marshal.Copy(pointer, result, 0, size); return result;
    }
    public void Dispose()
    {
        if (disposed) return;
        RequireOwner();
        VulkanDevice device = session.Device;
        device.ProgramDeleted -= ForgetBorrowedProgram;
        device.FramebufferDeleted -= ForgetFramebuffer;
        foreach (int framebuffer in framebuffers.Keys) device.DeleteFramebuffer(framebuffer);
        framebuffers.Clear();
        foreach (ProgramObject program in programs.Values) if (!program.Borrowed && program.Backend != 0) device.DeleteProgram(program.Backend);
        foreach (TextureObject texture in textures.Values) if (!texture.Borrowed) device.DeleteTexture(texture.Name);
        programs.Clear(); shaders.Clear(); buffers.Clear(); textures.Clear(); contexts.Clear();
        selected = null; if (ReferenceEquals(current, this)) current = null;
        disposed = true;
    }
}
