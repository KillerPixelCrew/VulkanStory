using System.Reflection;
using System.Runtime.InteropServices;
using VulkanStory.Render.Vulkan;

namespace VulkanStory.Game;

internal sealed partial class ManagedGlBridge
{
    private sealed class GlQuery
    {
        internal int Target, Occlusion;
        internal VulkanDevice.GlTimestampQuery? Timer;
        internal bool Active, Deleted;
    }
    private int GenQuery()
    { int name = Name(); C.Queries.Add(name, new()); return name; }
    private GlQuery QueryObject(int name)
    { if (!C.Queries.TryGetValue(name, out GlQuery? query)) Error(0x0502); return query!; }
    private void BeginQuery(int target, int name)
    {
        if (target is not (0x8914 or 0x8C2F or 0x8D6A or 0x88BF)) Error(0x0500);
        GlQuery query = QueryObject(name);
        bool booleanAlias = (target is 0x8C2F or 0x8D6A) && (query.Target is 0x8C2F or 0x8D6A);
        if (query.Active || C.ActiveQueries.ContainsKey(target) || query.Target != 0 && query.Target != target && !booleanAlias) Error(0x0502);
        if (target == 0x88BF)
        {
            var next = Device.BeginGlTimestamp(elapsed: true);
            if (query.Timer is not null) Device.RetireGlTimestamp(query.Timer);
            query.Timer = next;
        }
        else
        {
            if (query.Occlusion == 0) query.Occlusion = Device.CreateOcclusionQuery();
            if (!Device.CanBeginGlOcclusion(query.Occlusion)) Error(0x0502);
            Device.BeginOcclusionQuery(query.Occlusion);
        }
        query.Target = target; query.Active = true;
        C.ActiveQueries.Add(target, query);
    }
    private void EndQuery(int target)
    {
        if (target is not (0x8914 or 0x8C2F or 0x8D6A or 0x88BF)) Error(0x0500);
        if (!C.ActiveQueries.TryGetValue(target, out GlQuery? query)) Error(0x0502);
        if (target == 0x88BF) Device.EndGlTimestamp(query!.Timer!); else Device.EndOcclusionQuery(query!.Occlusion);
        C.ActiveQueries.Remove(target);
        query.Active = false;
        if (query.Deleted) RetireQuery(query);
    }
    private void QueryCounter(int name, int target)
    {
        if (target != 0x8E28) Error(0x0500);
        GlQuery query = QueryObject(name);
        if (query.Active || query.Target != 0 && query.Target != target) Error(0x0502);
        var next = Device.BeginGlTimestamp(elapsed: false);
        if (query.Timer is not null) Device.RetireGlTimestamp(query.Timer);
        query.Timer = next; query.Target = target;
    }
    private object? GetQueryObject(MethodInfo method, object?[] a)
    {
        GlQuery query = QueryObject(I(a[0]));
        if (query.Active || query.Target == 0) Error(0x0502);
        int token = I(a[1]);
        if (token is not (0x8866 or 0x8867 or 0x9194)) Error(0x0500);
        bool wait = token == 0x8866;
        ulong result;
        bool available;
        if (query.Timer is not null) available = Device.ReadGlTimestamp(query.Timer, wait, out result);
        else
        {
            available = Device.IsQueryResultAvailable(query.Occlusion);
            result = wait || available ? Device.GetGlOcclusionResult(query.Occlusion, wait) : 0;
            if (wait) available = Device.IsQueryResultAvailable(query.Occlusion);
            if (query.Target is 0x8C2F or 0x8D6A) result = result == 0 ? 0u : 1u;
        }
        if (token == 0x8867) result = available ? 1u : 0u;
        else if (!available)
        { if (wait) throw new InvalidOperationException("The submitted GPU query did not become available."); return null; }
        WriteQueryScalar(method, a, 2, result);
        return null;
    }
    private static void WriteQueryScalar(MethodInfo method, object?[] a, int index, ulong value)
    {
        Type parameter = method.GetParameters()[index].ParameterType;
        Type element = parameter.GetElementType()!;
        object converted = element == typeof(long) ? (object)(long)Math.Min(value, (ulong)long.MaxValue) :
            element == typeof(ulong) ? value : element == typeof(uint) ? (object)(uint)Math.Min(value, uint.MaxValue) : (int)Math.Min(value, int.MaxValue);
        if (parameter.IsByRef) { a[index] = converted; return; }
        if (a[index] is System.Array array)
        { if (array.Length == 0) Error(0x0501); array.SetValue(converted, 0); return; }
        nint pointer = Pointer(a[index]); if (pointer == 0) Error(0x0501);
        if (element == typeof(long)) Marshal.WriteInt64(pointer, (long)converted);
        else if (element == typeof(ulong)) Marshal.WriteInt64(pointer, unchecked((long)(ulong)converted));
        else if (element == typeof(uint)) Marshal.WriteInt32(pointer, unchecked((int)(uint)converted));
        else Marshal.WriteInt32(pointer, (int)converted);
    }
    private void DeleteQuery(int name)
    {
        if (!C.Queries.Remove(name, out GlQuery? query)) return;
        query.Deleted = true;
        if (!query.Active) RetireQuery(query);
    }
    private void RetireQuery(GlQuery query)
    {
        if (query.Timer is not null) { session.Device.RetireGlTimestamp(query.Timer); query.Timer = null; }
        if (query.Occlusion != 0) { session.Device.DeleteQuery(query.Occlusion); query.Occlusion = 0; }
    }
    private void ReleaseContextQueries(Context context)
    {
        foreach (GlQuery query in context.Queries.Values.Concat(context.ActiveQueries.Values).Distinct()) RetireQuery(query);
        context.Queries.Clear(); context.ActiveQueries.Clear();
    }
}
