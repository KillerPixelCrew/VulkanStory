using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Optimum.Patcher;

/// <summary>Copies a donor body into a method in another module.</summary>
internal static class MethodBodyCloner
{
    internal static bool CopyPInvoke(
        MethodDefinition source,
        MethodDefinition target,
        ModuleDefinition targetModule)
    {
        if (!source.IsPInvokeImpl)
            return false;

        var pinvoke = source.PInvokeInfo
            ?? throw new InvalidOperationException($"P/Invoke method has no import map: {source.FullName}");
        var module = targetModule.ModuleReferences.FirstOrDefault(item =>
            item.Name == pinvoke.Module.Name);
        if (module is null)
        {
            module = new ModuleReference(pinvoke.Module.Name);
            targetModule.ModuleReferences.Add(module);
        }

        target.PInvokeInfo = new PInvokeInfo(pinvoke.Attributes, pinvoke.EntryPoint, module);
        target.ImplAttributes = source.ImplAttributes;
        return true;
    }

    internal static void Copy(
        MethodDefinition source,
        MethodDefinition target,
        ModuleDefinition targetModule)
    {
        if (!source.HasBody)
            throw new InvalidOperationException($"Method has no body: {source.FullName}");

        var sourceBody = source.Body;
        var targetBody = target.Body;
        targetBody.Instructions.Clear();
        targetBody.Variables.Clear();
        targetBody.ExceptionHandlers.Clear();
        targetBody.InitLocals = sourceBody.InitLocals;
        targetBody.MaxStackSize = sourceBody.MaxStackSize;

        var variables = new Dictionary<VariableDefinition, VariableDefinition>();
        foreach (var variable in sourceBody.Variables)
        {
            var cloned = new VariableDefinition(targetModule.ImportReference(variable.VariableType));
            targetBody.Variables.Add(cloned);
            variables.Add(variable, cloned);
        }

        var instructions = new Dictionary<Instruction, Instruction>();
        var il = targetBody.GetILProcessor();
        foreach (var instruction in sourceBody.Instructions)
        {
            var cloned = CloneInstruction(instruction, source, target, targetModule, variables);
            instructions.Add(instruction, cloned);
            il.Append(cloned);
        }

        foreach (var instruction in targetBody.Instructions)
        {
            if (instruction.Operand is Instruction branch)
                instruction.Operand = MapRequired(instructions, branch, source);
            else if (instruction.Operand is Instruction[] branches)
            {
                var mapped = new Instruction[branches.Length];
                for (int i = 0; i < branches.Length; i++)
                    mapped[i] = MapRequired(instructions, branches[i], source);
                instruction.Operand = mapped;
            }
        }

        foreach (var handler in sourceBody.ExceptionHandlers)
        {
            targetBody.ExceptionHandlers.Add(new ExceptionHandler(handler.HandlerType)
            {
                TryStart = MapOptional(instructions, handler.TryStart, source),
                TryEnd = MapOptional(instructions, handler.TryEnd, source),
                HandlerStart = MapOptional(instructions, handler.HandlerStart, source),
                HandlerEnd = MapOptional(instructions, handler.HandlerEnd, source),
                FilterStart = MapOptional(instructions, handler.FilterStart, source),
                CatchType = handler.CatchType is null
                    ? null
                    : targetModule.ImportReference(handler.CatchType),
            });
        }
    }

    private static Instruction? MapOptional(
        Dictionary<Instruction, Instruction> instructions,
        Instruction? instruction,
        MethodDefinition source) =>
        instruction is null ? null : MapRequired(instructions, instruction, source);

    private static Instruction MapRequired(
        Dictionary<Instruction, Instruction> instructions,
        Instruction instruction,
        MethodDefinition source) =>
        instructions.TryGetValue(instruction, out var mapped)
            ? mapped
            : throw new InvalidOperationException($"Branch leaves method body: {source.FullName}");

    private static Instruction CloneInstruction(
        Instruction sourceInstruction,
        MethodDefinition source,
        MethodDefinition target,
        ModuleDefinition targetModule,
        Dictionary<VariableDefinition, VariableDefinition> variables)
    {
        var opcode = sourceInstruction.OpCode;
        var operand = sourceInstruction.Operand;
        return operand switch
        {
            null => Instruction.Create(opcode),
            MethodReference method => Instruction.Create(opcode, targetModule.ImportReference(method)),
            TypeReference type => Instruction.Create(opcode, targetModule.ImportReference(type)),
            FieldReference field => Instruction.Create(opcode, targetModule.ImportReference(field)),
            string value => Instruction.Create(opcode, value),
            int value => Instruction.Create(opcode, value),
            long value => Instruction.Create(opcode, value),
            float value => Instruction.Create(opcode, value),
            double value => Instruction.Create(opcode, value),
            byte value => Instruction.Create(opcode, value),
            sbyte value => Instruction.Create(opcode, value),
            Instruction branch => Instruction.Create(opcode, branch),
            Instruction[] branches => Instruction.Create(opcode, branches),
            VariableDefinition variable => Instruction.Create(opcode,
                variables.TryGetValue(variable, out var mappedVariable)
                    ? mappedVariable
                    : throw new InvalidOperationException($"Unknown local in {source.FullName}")),
            ParameterDefinition parameter => Instruction.Create(opcode,
                MapParameter(parameter, source, target)),
            CallSite callSite => Instruction.Create(opcode, callSite),
            _ => throw new NotSupportedException(
                $"Unsupported operand {operand.GetType().FullName} in {source.FullName}"),
        };
    }

    private static ParameterDefinition MapParameter(
        ParameterDefinition parameter,
        MethodDefinition source,
        MethodDefinition target)
    {
        int index = source.Parameters.IndexOf(parameter);
        if (index < 0 || index >= target.Parameters.Count)
            throw new InvalidOperationException($"Unknown parameter in {source.FullName}");
        return target.Parameters[index];
    }
}
