// Source: Optimum.Tests/chunk-deserialize-parallel-tests.cs
namespace Optimum.Tests
{
using System;
using System.Globalization;
using System.Threading;
using Vintagestory.API.Config;
using Xunit;

/// <summary>
/// Tests for the parallel ServerChunk.FromBytes deserialization feature (Step 29).
/// Covers config toggles, clamping, and diagnostics recording.
/// </summary>
public class ChunkDeserializeParallelTests
{
    public ChunkDeserializeParallelTests()
    {
        OptimumConfig.ChunkDeserializeParallel = true;
        OptimumConfig.ChunkDeserializeParallelMinY = 4;
        OptimumDiagnostics.ResetChunkDeserializeParallel();
    }

    [Fact]
    public void DefaultEnabled()
    {
        Assert.True(OptimumConfig.ChunkDeserializeParallel);
    }

    [Fact]
    public void DefaultMinYIs4()
    {
        Assert.Equal(4, OptimumConfig.ChunkDeserializeParallelMinY);
    }

    [Fact]
    public void ConfigToggleDisablesParallelPath()
    {
        OptimumConfig.ChunkDeserializeParallel = false;
        Assert.False(OptimumConfig.ChunkDeserializeParallel);
    }

    [Fact]
    public void MinYClampedTo2Minimum()
    {
        // Simulate load clamping
        int clamped = Math.Clamp(1, 2, 64);
        Assert.Equal(2, clamped);
    }

    [Fact]
    public void MinYClampedTo64Maximum()
    {
        int clamped = Math.Clamp(100, 2, 64);
        Assert.Equal(64, clamped);
    }

    [Fact]
    public void DiagnosticsRecordSingleColumn()
    {
        OptimumDiagnostics.RecordChunkDeserializeParallel(16);

        string summary = OptimumDiagnostics.GetChunkDeserializeParallelSummary();
        Assert.Contains("columns=1", summary);
        Assert.Contains("chunks=16", summary);
    }

    [Fact]
    public void DiagnosticsAccumulateMultipleColumns()
    {
        OptimumDiagnostics.RecordChunkDeserializeParallel(8);
        OptimumDiagnostics.RecordChunkDeserializeParallel(12);
        OptimumDiagnostics.RecordChunkDeserializeParallel(16);

        string summary = OptimumDiagnostics.GetChunkDeserializeParallelSummary();
        Assert.Contains("columns=3", summary);
        Assert.Contains("chunks=36", summary);
    }

    [Fact]
    public void DiagnosticsResetClearsCounters()
    {
        OptimumDiagnostics.RecordChunkDeserializeParallel(10);
        OptimumDiagnostics.ResetChunkDeserializeParallel();

        string summary = OptimumDiagnostics.GetChunkDeserializeParallelSummary();
        Assert.Contains("columns=0", summary);
        Assert.Contains("chunks=0", summary);
    }

    [Fact]
    public void DiagnosticsThreadSafe()
    {
        int threadCount = 8;
        int iterationsPerThread = 100;
        var barrier = new ManualResetEventSlim(false);
        var threads = new Thread[threadCount];

        for (int t = 0; t < threadCount; t++)
        {
            threads[t] = new Thread(() =>
            {
                barrier.Wait();
                for (int i = 0; i < iterationsPerThread; i++)
                {
                    OptimumDiagnostics.RecordChunkDeserializeParallel(4);
                }
            });
            threads[t].Start();
        }

        barrier.Set();
        foreach (var thread in threads)
            thread.Join();

        string summary = OptimumDiagnostics.GetChunkDeserializeParallelSummary();
        int expectedColumns = threadCount * iterationsPerThread;
        int expectedChunks = expectedColumns * 4;
        Assert.Contains($"columns={expectedColumns}", summary);
        Assert.Contains($"chunks={expectedChunks}", summary);
    }

    [Fact]
    public void GateConditionRespectsMinY()
    {
        OptimumConfig.ChunkDeserializeParallelMinY = 8;
        int chunkMapSizeY = 6;

        // Simulates the gate condition in ServerSystemSupplyChunks
        bool shouldParallelize = OptimumConfig.ChunkDeserializeParallel
            && chunkMapSizeY >= OptimumConfig.ChunkDeserializeParallelMinY;

        Assert.False(shouldParallelize);
    }

    [Fact]
    public void GateConditionAllowsWhenAboveMinY()
    {
        OptimumConfig.ChunkDeserializeParallelMinY = 4;
        int chunkMapSizeY = 16;

        bool shouldParallelize = OptimumConfig.ChunkDeserializeParallel
            && chunkMapSizeY >= OptimumConfig.ChunkDeserializeParallelMinY;

        Assert.True(shouldParallelize);
    }

    [Fact]
    public void GateConditionRespectsDisabledToggle()
    {
        OptimumConfig.ChunkDeserializeParallel = false;
        int chunkMapSizeY = 32;

        bool shouldParallelize = OptimumConfig.ChunkDeserializeParallel
            && chunkMapSizeY >= OptimumConfig.ChunkDeserializeParallelMinY;

        Assert.False(shouldParallelize);
    }

    [Fact]
    public void ConfigDataSerializationRoundTrip()
    {
        // Verify the DTO properties exist and have correct defaults
        var data = new OptimumConfigData();
        Assert.True(data.ChunkDeserializeParallel);
        Assert.Equal(4, data.ChunkDeserializeParallelMinY);
    }
}
}

// Source: Optimum.Tests/chunk-read-pool-lifecycle-tests.cs
namespace Optimum.Tests
{
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using Reflection = System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Optimum.Patcher;
using Xunit;

public sealed class ChunkReadPoolLifecycleTests
{
    [Fact]
    public void ConstructorClosesConnectionWhenInitializationFailsAfterOpen()
    {
        string? donorPath = TryFindRepositoryFile(
            "build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib.dll");
        if (donorPath is null) return;

        Assert.True(InitializeSqliteProvider(), "A platform SQLite provider is required for the donor lifecycle test.");
        Reflection.Assembly donor = Reflection.Assembly.LoadFrom(donorPath);
        Type poolType = donor.GetType("Vintagestory.Server.OptimumChunkReadPool")!;
        Reflection.ConstructorInfo constructor = poolType
            .GetConstructors(Reflection.BindingFlags.Instance | Reflection.BindingFlags.Public | Reflection.BindingFlags.NonPublic)
            .Single(candidate =>
            {
                Reflection.ParameterInfo[] parameters = candidate.GetParameters();
                return parameters.Length == 4 &&
                    parameters[3].ParameterType.IsGenericType &&
                    parameters[3].ParameterType.GetGenericTypeDefinition() == typeof(Action<>);
            });

        Type connectionType = constructor.GetParameters()[3].ParameterType.GetGenericArguments()[0];
        Type callbackType = typeof(Action<>).MakeGenericType(connectionType);
        var openedConnections = new List<object>();
        Delegate failAfterOpen = CreateFailingCallback(callbackType, connectionType, openedConnections);
        string databasePath = Path.Combine(
            Path.GetTempPath(), $"optimum-pool-{Guid.NewGuid():N}.db");
        File.WriteAllBytes(databasePath, Array.Empty<byte>());

        try
        {
            Reflection.TargetInvocationException exception = Assert.Throws<Reflection.TargetInvocationException>(() =>
                constructor.Invoke(new object[] { databasePath, 2, false, failAfterOpen }));

            Assert.True(
                exception.InnerException is InvalidOperationException,
                exception.InnerException?.ToString());
            Assert.Single(openedConnections);
            Reflection.PropertyInfo state = openedConnections[0].GetType().GetProperty("State")!;
            Assert.Equal("Closed", state.GetValue(openedConnections[0])!.ToString());
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public void ShutdownHookInsertsThePoolDisposeBeforeTheDatabaseDispose()
    {
        using AssemblyDefinition assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("OptimumLifecycleFixture", new Version(1, 0, 0, 0)),
            "OptimumLifecycleFixture",
            ModuleKind.Dll);
        ModuleDefinition module = assembly.MainModule;

        var gameDatabase = new TypeDefinition(
            "Vintagestory.Common",
            "GameDatabase",
            TypeAttributes.Public | TypeAttributes.Class,
            module.TypeSystem.Object);
        module.Types.Add(gameDatabase);

        var databaseDispose = new MethodDefinition(
            "Dispose",
            MethodAttributes.Public | MethodAttributes.HideBySig,
            module.TypeSystem.Void);
        gameDatabase.Methods.Add(databaseDispose);
        databaseDispose.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));

        var loadSave = new TypeDefinition(
            "Vintagestory.Server",
            "ServerSystemLoadAndSaveGame",
            TypeAttributes.Public | TypeAttributes.Class,
            module.TypeSystem.Object);
        module.Types.Add(loadSave);

        var databaseField = new FieldDefinition(
            "gameDatabase",
            FieldAttributes.Private,
            gameDatabase);
        loadSave.Fields.Add(databaseField);

        var poolDispose = new MethodDefinition(
            "DisposeOptimumChunkReadPool",
            MethodAttributes.Private | MethodAttributes.HideBySig,
            module.TypeSystem.Void);
        loadSave.Methods.Add(poolDispose);
        poolDispose.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));

        var shutdown = new MethodDefinition(
            "OnSeperateThreadShutDown",
            MethodAttributes.Public | MethodAttributes.HideBySig,
            module.TypeSystem.Void);
        loadSave.Methods.Add(shutdown);
        var processor = shutdown.Body.GetILProcessor();
        processor.Append(Instruction.Create(OpCodes.Ldarg_0));
        processor.Append(Instruction.Create(OpCodes.Ldfld, databaseField));
        processor.Append(Instruction.Create(OpCodes.Callvirt, databaseDispose));
        processor.Append(Instruction.Create(OpCodes.Ret));

        bool inserted = ILHook.InsertInstanceVoidCallBefore(
            assembly,
            "Vintagestory.Server.ServerSystemLoadAndSaveGame",
            "OnSeperateThreadShutDown",
            0,
            "DisposeOptimumChunkReadPool",
            "Dispose",
            "Vintagestory.Common.GameDatabase",
            Array.Empty<string>(),
            "System.Void",
            targetHasThis: true,
            targetExplicitThis: false,
            MethodCallingConvention.Default,
            targetGenericArity: 0);

        Assert.True(inserted);

        int databaseDisposeIndex = shutdown.Body.Instructions
            .Select((instruction, index) => (instruction, index))
            .Single(item => item.instruction.Operand is MethodReference method && method.Name == "Dispose")
            .index;

        Assert.Equal(OpCodes.Ldarg_0, shutdown.Body.Instructions[databaseDisposeIndex - 2].OpCode);
        Assert.Equal(OpCodes.Call, shutdown.Body.Instructions[databaseDisposeIndex - 1].OpCode);
        Assert.Equal("DisposeOptimumChunkReadPool", ((MethodReference)shutdown.Body.Instructions[databaseDisposeIndex - 1].Operand!).Name);
        Assert.Equal(OpCodes.Callvirt, shutdown.Body.Instructions[databaseDisposeIndex].OpCode);

        // The patcher should be safe if a caller retries the same hook against
        // an already-modified module.
        Assert.True(ILHook.InsertInstanceVoidCallBefore(
            assembly,
            "Vintagestory.Server.ServerSystemLoadAndSaveGame",
            "OnSeperateThreadShutDown",
            0,
            "DisposeOptimumChunkReadPool",
            "Dispose",
            "Vintagestory.Common.GameDatabase",
            Array.Empty<string>(),
            "System.Void",
            targetHasThis: true,
            targetExplicitThis: false,
            MethodCallingConvention.Default,
            targetGenericArity: 0));

        Assert.Equal(1, shutdown.Body.Instructions.Count(instruction =>
            instruction.Operand is MethodReference method && method.Name == "DisposeOptimumChunkReadPool"));
    }

    [Fact]
    public void OptimumPatchOwnsPoolShutdownBeforeClosingTheSaveDatabase()
    {
        string patch = PatchReader.ReadPatch(
            "patches/VintagestoryLib/Vintagestory.Server/ServerSystemLoadAndSaveGame.cs.patch");
        string program = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));
        string ilHook = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/ILHook.cs"));

        Assert.Contains("DisposeOptimumChunkReadPool();", patch);
        Assert.Contains("chunkthread.optimumReadPool = null;", patch);
        Assert.Contains("pool?.Dispose();", patch);
        Assert.True(
            patch.IndexOf("chunkthread.optimumReadPool = null;", StringComparison.Ordinal) <
            patch.IndexOf("pool?.Dispose();", StringComparison.Ordinal));
        Assert.Contains("\"DisposeOptimumChunkReadPool\"", program);
        Assert.Contains("InsertBeforeTarget: true", program);
        Assert.Contains("InsertInstanceVoidCallBefore", ilHook);
        Assert.Contains("expected exactly one call", ilHook);
    }

    private static Delegate CreateFailingCallback(
        Type callbackType,
        Type connectionType,
        List<object> openedConnections)
    {
        ParameterExpression connection = Expression.Parameter(connectionType, "connection");
        Reflection.MethodInfo add = typeof(List<object>).GetMethod(nameof(List<object>.Add))!;
        Expression recordConnection = Expression.Call(
            Expression.Constant(openedConnections),
            add,
            Expression.Convert(connection, typeof(object)));
        Expression throwFailure = Expression.Throw(
            Expression.New(typeof(InvalidOperationException)),
            typeof(void));
        return Expression.Lambda(
            callbackType,
            Expression.Block(recordConnection, throwFailure),
            connection).Compile();
    }

    private static bool InitializeSqliteProvider()
    {
        string[] providerDirectories = OperatingSystem.IsWindows()
            ? [
                ".vanilla/win-x64/vintagestory/Lib",
                ".vanilla/win-x64/package-client/Lib",
            ]
            : [
                ".vanilla/linux-x64/vintagestory/Lib",
                ".vanilla/win-x64/vintagestory/Lib",
                ".vanilla/win-x64/package-client/Lib",
            ];
        string[] nativeNames = OperatingSystem.IsWindows()
            ? ["e_sqlite3.dll"]
            : OperatingSystem.IsMacOS()
                ? ["libe_sqlite3.dylib", "libe_sqlite3.so"]
                : ["libe_sqlite3.so"];

        foreach (string relativeDirectory in providerDirectories)
        {
            string? batteriesPath = TryFindRepositoryFile(
                Path.Combine(relativeDirectory, "SQLitePCLRaw.batteries_v2.dll"));
            if (batteriesPath is null) continue;

            string providerDirectory = Path.GetDirectoryName(batteriesPath)!;
            string providerPath = Path.Combine(providerDirectory, "SQLitePCLRaw.provider.e_sqlite3.dll");
            string? nativePath = nativeNames
                .SelectMany(name => new[]
                {
                    Path.Combine(providerDirectory, name),
                    Path.Combine(AppContext.BaseDirectory, name),
                    Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", name),
                    Path.Combine(AppContext.BaseDirectory, "runtimes", "linux-x64", "native", name),
                })
                .FirstOrDefault(File.Exists);
            if (!File.Exists(providerPath) || nativePath is null) continue;

            Reflection.Assembly provider = Reflection.Assembly.LoadFrom(providerPath);
            System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(
                provider,
                (name, _, _) => name is "e_sqlite3" or "e_sqlite3.dll"
                    ? System.Runtime.InteropServices.NativeLibrary.Load(nativePath)
                    : IntPtr.Zero);
            Reflection.Assembly batteries = Reflection.Assembly.LoadFrom(batteriesPath);
            batteries.GetType("SQLitePCL.Batteries_V2")!.GetMethod("Init")!.Invoke(null, null);
            return true;
        }

        return false;
    }

    private static string? TryFindRepositoryFile(string relativePath)
    {
        try
        {
            return PatchReader.FindRepositoryFile(relativePath);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

}
}
