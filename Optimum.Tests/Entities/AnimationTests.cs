// Source: Optimum.Tests/AnimLodTests.cs
namespace Optimum.Tests
{
using System.Collections.Generic;
using Vintagestory.API.Config;
using Xunit;

public class AnimLodTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-9)]
    [InlineData(13)]
    public void PhaseCoversAllFourValuesAcrossFourConsecutivePositionsOnEachAxis(int baseCoord)
    {
        var xPhases = new HashSet<int>();
        var yPhases = new HashSet<int>();
        var zPhases = new HashSet<int>();

        for (int i = 0; i < 4; i++)
        {
            xPhases.Add(OptimumAnimLod.Phase(baseCoord + i, 0, 0));
            yPhases.Add(OptimumAnimLod.Phase(0, baseCoord + i, 0));
            zPhases.Add(OptimumAnimLod.Phase(0, 0, baseCoord + i));
        }

        Assert.Equal(4, xPhases.Count);
        Assert.Equal(4, yPhases.Count);
        Assert.Equal(4, zPhases.Count);
    }

    [Fact]
    public void PhaseIsAlwaysInRangeZeroToThree()
    {
        for (int x = -20; x <= 20; x++)
        {
            int phase = OptimumAnimLod.Phase(x, -x * 2, x + 7);
            Assert.InRange(phase, 0, 3);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MidTierDueFiresExactlyOncePerFourConsecutiveCounters(int phase)
    {
        for (int windowStart = -8; windowStart < 16; windowStart += 4)
        {
            int dueCount = 0;
            for (int frameCounter = windowStart; frameCounter < windowStart + 4; frameCounter++)
            {
                if (OptimumAnimLod.MidTierDue(frameCounter, phase)) dueCount++;
            }
            Assert.Equal(1, dueCount);
        }
    }

    [Fact]
    public void BudgetWindowConsumesBudgetWithinSameStamp()
    {
        long stamp = -1;
        int used = 0;

        Assert.True(OptimumAnimLod.BudgetWindow(100, ref stamp, ref used, 2));
        Assert.True(OptimumAnimLod.BudgetWindow(100, ref stamp, ref used, 2));
        Assert.False(OptimumAnimLod.BudgetWindow(100, ref stamp, ref used, 2));
    }

    [Fact]
    public void BudgetWindowResetsOnNewStamp()
    {
        long stamp = -1;
        int used = 0;

        Assert.True(OptimumAnimLod.BudgetWindow(100, ref stamp, ref used, 1));
        Assert.False(OptimumAnimLod.BudgetWindow(100, ref stamp, ref used, 1));
        Assert.True(OptimumAnimLod.BudgetWindow(101, ref stamp, ref used, 1));
        Assert.Equal(101, stamp);
    }

    [Fact]
    public void BudgetWindowZeroAlwaysRuns()
    {
        long stamp = -1;
        int used = 0;

        for (int i = 0; i < 5; i++)
        {
            Assert.True(OptimumAnimLod.BudgetWindow(100, ref stamp, ref used, 0));
        }
    }

    [Fact]
    public void BudgetWindowNegativeAlwaysRuns()
    {
        long stamp = -1;
        int used = 0;

        Assert.True(OptimumAnimLod.BudgetWindow(100, ref stamp, ref used, -1));
    }
}
}

// Source: Optimum.Tests/AnimatorAnimCodeComparerTests.cs
namespace Optimum.Tests
{
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Optimum.Patcher;
using Xunit;

/// <summary>
/// AnimatorBase.animsByCode uses a plain-ordinal dictionary in the vanilla assembly, so
/// GetAnimationState/OnFrame both allocate a lowercased string on every lookup. The source
/// patch (patches/VintagestoryApi/Common/Model/Animation/AnimatorBase.cs.patch) fixes this in
/// the decompiled tree, but the runtime API patcher leaves the shared API unchanged for server
/// use. These tests exercise the Cecil-level fix against a synthetic module shaped like the real
/// vanilla type.
/// </summary>
public sealed class AnimatorAnimCodeComparerTests
{
    private static (ModuleDefinition module, TypeDefinition animatorBase, FieldDefinition animsByCode,
        MethodDefinition ctor, MethodDefinition getAnimationState, MethodDefinition onFrame) BuildSyntheticAnimatorBase()
    {
        var module = ModuleDefinition.CreateModule("VintagestoryAPI", ModuleKind.Dll);

        var dictType = module.ImportReference(typeof(Dictionary<string, object>));
        var dictCtorInt = module.ImportReference(typeof(Dictionary<string, object>).GetConstructor(new[] { typeof(int) }));
        var dictTryGetValue = module.ImportReference(
            typeof(Dictionary<string, object>).GetMethod(nameof(Dictionary<string, object>.TryGetValue)));
        var toLowerInvariant = module.ImportReference(typeof(string).GetMethod(nameof(string.ToLowerInvariant), System.Type.EmptyTypes));

        var animatorBase = new TypeDefinition(
            "Vintagestory.API.Common",
            "AnimatorBase",
            TypeAttributes.Public | TypeAttributes.Abstract,
            module.TypeSystem.Object);
        module.Types.Add(animatorBase);

        var animsByCode = new FieldDefinition("animsByCode", FieldAttributes.Private | FieldAttributes.InitOnly, dictType);
        animatorBase.Fields.Add(animsByCode);

        var ctor = new MethodDefinition(
            ".ctor",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            module.TypeSystem.Void);
        ctor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
        ctor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
        ctor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
        animatorBase.Methods.Add(ctor);
        {
            var p = ctor.Body.GetILProcessor();
            p.Append(Instruction.Create(OpCodes.Ldarg_0));
            p.Append(Instruction.Create(OpCodes.Ldc_I4_0));
            p.Append(Instruction.Create(OpCodes.Newobj, dictCtorInt));
            p.Append(Instruction.Create(OpCodes.Stfld, animsByCode));
            p.Append(Instruction.Create(OpCodes.Ret));
        }

        var getAnimationState = new MethodDefinition(
            "GetAnimationState",
            MethodAttributes.Public | MethodAttributes.HideBySig,
            module.TypeSystem.Object);
        getAnimationState.Parameters.Add(new ParameterDefinition("code", ParameterAttributes.None, module.TypeSystem.String));
        var anim1 = new VariableDefinition(module.TypeSystem.Object);
        getAnimationState.Body.Variables.Add(anim1);
        animatorBase.Methods.Add(getAnimationState);
        {
            var p = getAnimationState.Body.GetILProcessor();
            p.Append(Instruction.Create(OpCodes.Ldarg_0));
            p.Append(Instruction.Create(OpCodes.Ldfld, animsByCode));
            p.Append(Instruction.Create(OpCodes.Ldarg_1));
            p.Append(Instruction.Create(OpCodes.Callvirt, toLowerInvariant));
            p.Append(Instruction.Create(OpCodes.Ldloca_S, anim1));
            p.Append(Instruction.Create(OpCodes.Callvirt, dictTryGetValue));
            p.Append(Instruction.Create(OpCodes.Pop));
            p.Append(Instruction.Create(OpCodes.Ldloc_0));
            p.Append(Instruction.Create(OpCodes.Ret));
        }

        var onFrame = new MethodDefinition(
            "OnFrame",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual,
            module.TypeSystem.Void);
        onFrame.Parameters.Add(new ParameterDefinition("activeAnimationsByAnimCode", ParameterAttributes.None, dictType));
        onFrame.Parameters.Add(new ParameterDefinition("dt", ParameterAttributes.None, module.TypeSystem.Single));
        var code = new VariableDefinition(module.TypeSystem.String);
        var anim2 = new VariableDefinition(module.TypeSystem.Object);
        onFrame.Body.Variables.Add(code);
        onFrame.Body.Variables.Add(anim2);
        animatorBase.Methods.Add(onFrame);
        {
            var p = onFrame.Body.GetILProcessor();
            p.Append(Instruction.Create(OpCodes.Ldarg_0));
            p.Append(Instruction.Create(OpCodes.Ldfld, animsByCode));
            p.Append(Instruction.Create(OpCodes.Ldloc_0));
            p.Append(Instruction.Create(OpCodes.Callvirt, toLowerInvariant));
            p.Append(Instruction.Create(OpCodes.Ldloca_S, anim2));
            p.Append(Instruction.Create(OpCodes.Callvirt, dictTryGetValue));
            p.Append(Instruction.Create(OpCodes.Pop));
            p.Append(Instruction.Create(OpCodes.Ret));
        }

        return (module, animatorBase, animsByCode, ctor, getAnimationState, onFrame);
    }

    [Fact]
    public void PatchAnimatorAnimCodeComparer_ReturnsThreeSites()
    {
        var (module, _, _, _, _, _) = BuildSyntheticAnimatorBase();

        int patched = ApiPatcher.PatchAnimatorAnimCodeComparer(module);

        Assert.Equal(3, patched);
    }

    [Fact]
    public void CtorConstructsDictionaryWithOrdinalIgnoreCaseComparer()
    {
        var (module, _, _, ctor, _, _) = BuildSyntheticAnimatorBase();

        ApiPatcher.PatchAnimatorAnimCodeComparer(module);

        var newobj = ctor.Body.Instructions.Single(i => i.OpCode == OpCodes.Newobj);
        var operandRef = (MethodReference)newobj.Operand;
        Assert.Equal(2, operandRef.Parameters.Count);
        Assert.Equal("System.Int32", operandRef.Parameters[0].ParameterType.FullName);
        Assert.Contains("IEqualityComparer", operandRef.Parameters[1].ParameterType.FullName);

        var before = newobj.Previous;
        Assert.Equal(OpCodes.Call, before.OpCode);
        Assert.Contains("OrdinalIgnoreCase", ((MethodReference)before.Operand).FullName);
    }

    [Fact]
    public void GetAnimationStateNoLongerCallsToLowerInvariant()
    {
        var (module, _, _, _, getAnimationState, _) = BuildSyntheticAnimatorBase();

        ApiPatcher.PatchAnimatorAnimCodeComparer(module);

        Assert.DoesNotContain(getAnimationState.Body.Instructions, i =>
            i.Operand is MethodReference m && m.Name == "ToLowerInvariant");
    }

    [Fact]
    public void OnFrameNoLongerCallsToLowerInvariant()
    {
        var (module, _, _, _, _, onFrame) = BuildSyntheticAnimatorBase();

        ApiPatcher.PatchAnimatorAnimCodeComparer(module);

        Assert.DoesNotContain(onFrame.Body.Instructions, i =>
            i.Operand is MethodReference m && m.Name == "ToLowerInvariant");
    }
}
}

// Source: Optimum.Tests/entity-animation-diagnostics-coverage-tests.cs
namespace Optimum.Tests
{
using System;
using System.IO;
using Vintagestory.API.Config;
using Xunit;

[Collection("EntityAnimationDiagnostics")]
public class EntityAnimationDiagnosticsCoverageTests
{
    [Fact]
    public void AnimationPathsExposeDistanceContextAndMeasurements()
    {
        string manager = Read("VintagestoryApi/Common/Model/Animation/AnimationManager.cs");
        string animator = Read("VintagestoryApi/Common/Model/Animation/ClientAnimator.cs");
        string renderer = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderEntities.cs");

        Assert.Contains("RecordEntityAnimationManagerCall", manager);
        Assert.Contains("RecordEntityAnimationPoseSkip", manager);
        Assert.Contains("RecordEntityAnimationMatrixBuild", animator);
        Assert.Contains("RecordEntityAnimationMatrixSkip", animator);
        Assert.Contains("BeginEntityAnimationContext", renderer);
        Assert.Contains("EndEntityAnimationContext", renderer);
    }

    [Fact]
    public void SummarySeparatesPlayerNearMidFarAndUnknownWork()
    {
        bool previous = OptimumDiagnostics.StutterWatchEnabled;
        try
        {
            OptimumDiagnostics.ResetEntityAnimation();
            OptimumDiagnostics.StutterWatchEnabled = true;

            OptimumDiagnostics.BeginEntityAnimationContext(isPlayer: true, distanceSq: 0);
            OptimumDiagnostics.RecordEntityAnimationManagerCall();
            OptimumDiagnostics.RecordEntityAnimationHeadUpdate();
            OptimumDiagnostics.RecordEntityAnimationPoseUpdate();
            OptimumDiagnostics.RecordEntityAnimationMatrixBuild();
            OptimumDiagnostics.RecordEntityAnimationMatrixTicks(1);
            OptimumDiagnostics.EndEntityAnimationContext();

            OptimumDiagnostics.BeginEntityAnimationContext(isPlayer: false, distanceSq: 30 * 30);
            OptimumDiagnostics.RecordEntityAnimationManagerCall();
            OptimumDiagnostics.RecordEntityAnimationPoseSkip();
            OptimumDiagnostics.RecordEntityAnimationMatrixSkip();
            OptimumDiagnostics.EndEntityAnimationContext();

            OptimumDiagnostics.RecordEntityAnimationManagerCall();

            string summary = OptimumDiagnostics.GetEntityAnimationSummary();
            Assert.Contains("managerCalls=3", summary);
            Assert.Contains("headUpdates=1", summary);
            Assert.Contains("poseUpdates=1", summary);
            Assert.Contains("poseSkips=1", summary);
            Assert.Contains("matrixBuilds=1", summary);
            Assert.Contains("matrixSkips=1", summary);
            Assert.Contains("player:1/1/0/1/0", summary);
            Assert.Contains("mid:1/0/1/0/1", summary);
            Assert.Contains("unknown:1/0/0/0/0", summary);
        }
        finally
        {
            OptimumDiagnostics.ResetEntityAnimation();
            OptimumDiagnostics.StutterWatchEnabled = previous;
        }
    }

    private static string Read(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {relativePath} from {AppContext.BaseDirectory}.");
    }
}
}
