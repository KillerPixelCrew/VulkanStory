using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Optimum.Patcher;

/// <summary>
/// Injects new types, fields, and methods from the compiled assembly into the
/// vanilla assembly. Must run BEFORE method body transplants that reference
/// the injected members.
/// </summary>
public static class MemberInjector
{
    public static int InjectInterfaces(
        AssemblyDefinition vanilla,
        AssemblyDefinition compiled,
        Dictionary<string, List<string>> interfacesToInject)
    {
        int injected = 0;
        foreach (var (typeName, interfaceNames) in interfacesToInject)
        {
            var targetType = vanilla.MainModule.GetType(typeName)
                ?? throw new InvalidOperationException($"Required target type not found: {typeName}");
            var sourceType = compiled.MainModule.GetType(typeName);
            if (sourceType is null)
            {
                throw new InvalidOperationException($"Required donor interface type not found: {typeName}");
            }

            foreach (var interfaceName in interfaceNames)
            {
                var sourceInterface = sourceType.Interfaces.FirstOrDefault(item =>
                    item.InterfaceType.FullName == interfaceName);
                if (sourceInterface is null)
                {
                    throw new InvalidOperationException(
                        $"Required donor interface not found: {typeName} -> {interfaceName}");
                }
                if (targetType.Interfaces.Any(item => item.InterfaceType.FullName == interfaceName))
                {
                    continue;
                }

                targetType.Interfaces.Add(new InterfaceImplementation(
                    vanilla.MainModule.ImportReference(sourceInterface.InterfaceType)));
                injected++;
                Console.WriteLine($"  INJECTED INTERFACE: {typeName} -> {interfaceName}");
            }
        }

        return injected;
    }

    /// <summary>
    /// Inject entire types that exist in compiled but not in vanilla.
    /// </summary>
    public static int InjectTypes(AssemblyDefinition vanilla, AssemblyDefinition compiled, List<string> typeNames)
    {
        int injected = 0;
        foreach (var typeName in typeNames)
        {
            var existing = vanilla.MainModule.GetType(typeName);
            if (existing != null)
            {
                Console.WriteLine($"  TYPE EXISTS: {typeName}");
                continue;
            }

            var srcType = compiled.MainModule.GetType(typeName);
            if (srcType == null)
            {
                throw new InvalidOperationException($"Required compiled type not found: {typeName}");
            }

            var newType = CloneType(srcType, vanilla.MainModule);
            vanilla.MainModule.Types.Add(newType);
            injected++;
            Console.WriteLine($"  INJECTED TYPE: {typeName}");
        }
        return injected;
    }

    /// <summary>
    /// Inject static fields/properties into an existing type.
    /// </summary>
    public static int InjectStaticMembers(AssemblyDefinition vanilla, AssemblyDefinition compiled, string typeName, List<string> memberNames)
    {
        var vanillaType = vanilla.MainModule.GetType(typeName);
        var compiledType = compiled.MainModule.GetType(typeName);
        if (vanillaType == null || compiledType == null)
        {
            throw new InvalidOperationException($"Required injection type not found: {typeName}");
        }

        int injected = 0;
        foreach (var name in memberNames)
        {
            // Try as field first
            var srcField = compiledType.Fields.FirstOrDefault(f => f.Name == name);
            if (srcField != null)
            {
                if (vanillaType.Fields.Any(f => f.Name == name))
                {
                    Console.WriteLine($"  MEMBER EXISTS: {typeName}::{name}");
                    continue;
                }
                var newField = new FieldDefinition(
                    srcField.Name,
                    srcField.Attributes,
                    vanilla.MainModule.ImportReference(srcField.FieldType));
                if (srcField.HasConstant) newField.Constant = srcField.Constant;
                if (srcField.HasDefault) newField.Constant = srcField.Constant;
                CopyCustomAttributes(srcField.CustomAttributes, newField.CustomAttributes, vanilla.MainModule);
                vanillaType.Fields.Add(newField);
                injected++;
                Console.WriteLine($"  INJECTED FIELD: {typeName}::{name}");
                continue;
            }

            // Try as property (inject backing field + property + getter/setter)
            var srcProp = compiledType.Properties.FirstOrDefault(p => p.Name == name);
            if (srcProp != null)
            {
                if (vanillaType.Properties.Any(p => p.Name == name))
                {
                    Console.WriteLine($"  MEMBER EXISTS: {typeName}::{name}");
                    continue;
                }
                InjectProperty(vanillaType, srcProp, vanilla.MainModule, compiled);
                injected++;
                Console.WriteLine($"  INJECTED PROPERTY: {typeName}::{name}");
                continue;
            }

            // Try as method
            var sourceMethods = compiledType.Methods.Where(method => method.Name == name).ToArray();
            if (sourceMethods.Length > 0)
            {
                int methodCount = 0;
                foreach (var sourceMethod in sourceMethods)
                {
                    if (vanillaType.Methods.Any(method =>
                        MethodSignature.Matches(method, sourceMethod)))
                    {
                        continue;
                    }

                    InjectMethod(vanillaType, sourceMethod, vanilla.MainModule);
                    injected++;
                    methodCount++;
                    Console.WriteLine(
                        $"  INJECTED METHOD: {typeName}::{name}({sourceMethod.Parameters.Count} params)");
                    injected += InjectMethodDependencies(vanilla, compiled, sourceMethod);
                }
                if (methodCount == 0)
                {
                    Console.WriteLine($"  MEMBER EXISTS: {typeName}::{name}");
                }
                continue;
            }

            throw new InvalidOperationException($"Required donor member not found: {typeName}::{name}");
        }
        return injected;
    }

    /// <summary>
    /// Changes an existing vanilla field's declared type in place to match the
    /// donor's declared type for the same field. Unlike <see cref="InjectStaticMembers"/>
    /// (which only injects fields vanilla lacks and never compares types for
    /// fields it already has), this requires the field to exist on *both* sides
    /// and mutates the vanilla <see cref="FieldDefinition"/> instance in place -
    /// never removes and re-adds it. Cecil hands existing vanilla method bodies
    /// the FieldDefinition object itself as their instruction operand; replacing
    /// the definition would orphan those operands and write a FieldDef token for
    /// a field no longer in the type's field list.
    /// </summary>
    public static IReadOnlyList<FieldDefinition> RetypeFields(
        AssemblyDefinition vanilla, AssemblyDefinition compiled, string typeName, List<string> fieldNames)
    {
        var vanillaType = vanilla.MainModule.GetType(typeName)
            ?? throw new InvalidOperationException($"Required retype type not found in vanilla: {typeName}");
        var compiledType = compiled.MainModule.GetType(typeName)
            ?? throw new InvalidOperationException($"Required retype type not found in donor: {typeName}");

        var retyped = new List<FieldDefinition>();
        foreach (var name in fieldNames)
        {
            var vanillaField = vanillaType.Fields.FirstOrDefault(f => f.Name == name)
                ?? throw new InvalidOperationException($"Required field to retype not found in vanilla: {typeName}::{name}");
            var srcField = compiledType.Fields.FirstOrDefault(f => f.Name == name)
                ?? throw new InvalidOperationException($"Required field to retype not found in donor: {typeName}::{name}");

            var newType = vanilla.MainModule.ImportReference(srcField.FieldType);
            if (vanillaField.FieldType.FullName == newType.FullName)
            {
                Console.WriteLine($"  FIELD TYPE ALREADY MATCHES: {typeName}::{name}");
                retyped.Add(vanillaField);
                continue;
            }

            string oldTypeName = vanillaField.FieldType.FullName;
            vanillaField.FieldType = newType;
            vanillaField.Attributes = srcField.Attributes;
            retyped.Add(vanillaField);
            Console.WriteLine($"  RETYPED FIELD: {typeName}::{name} ({oldTypeName} -> {newType.FullName})");
        }
        return retyped;
    }

    /// <summary>
    /// Injects same-assembly fields and helper methods referenced by an injected
    /// method. Cecil copies the body of a helper after the main transplant pass,
    /// so a one-level scan leaves fields used only by that helper unresolved.
    /// Walk the dependency closure before the output reaches the verifier.
    /// </summary>
    private static int InjectMethodDependencies(
        AssemblyDefinition vanilla,
        AssemblyDefinition compiled,
        MethodDefinition rootMethod)
    {
        if (!rootMethod.HasBody) return 0;

        int injected = 0;
        var pending = new Queue<MethodDefinition>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        pending.Enqueue(rootMethod);

        while (pending.Count > 0)
        {
            var method = pending.Dequeue();
            string methodKey = MethodSignature.GetKey(method);
            if (!visited.Add(methodKey) || !method.HasBody) continue;

            var vanillaType = vanilla.MainModule.GetType(method.DeclaringType.FullName);
            var compiledType = compiled.MainModule.GetType(method.DeclaringType.FullName);
            if (vanillaType is null || compiledType is null) continue;

            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.Operand is FieldReference fieldReference)
                {
                    var fieldType = vanilla.MainModule.GetType(fieldReference.DeclaringType.FullName);
                    var sourceType = compiled.MainModule.GetType(fieldReference.DeclaringType.FullName);
                    if (fieldType is null || sourceType is null)
                    {
                        continue;
                    }

                    var existingField = fieldType.Fields.FirstOrDefault(field =>
                        field.Name == fieldReference.Name);
                    if (existingField is not null)
                    {
                        if (existingField.FieldType.FullName != fieldReference.FieldType.FullName)
                        {
                            throw new InvalidOperationException(
                                $"Field signature mismatch for {fieldReference.DeclaringType.FullName}::" +
                                $"{fieldReference.Name}: reference uses {fieldReference.FieldType.FullName}, " +
                                $"vanilla defines {existingField.FieldType.FullName}");
                        }
                        continue;
                    }

                    var sourceField = sourceType.Fields.FirstOrDefault(field =>
                        field.Name == fieldReference.Name &&
                        field.FieldType.FullName == fieldReference.FieldType.FullName);
                    if (sourceField is null)
                    {
                        throw new InvalidOperationException(
                            $"Required donor field not found: {fieldReference.DeclaringType.FullName}::" +
                            $"{fieldReference.Name} ({fieldReference.FieldType.FullName})");
                    }

                    var newField = new FieldDefinition(
                        sourceField.Name,
                        sourceField.Attributes,
                        vanilla.MainModule.ImportReference(sourceField.FieldType));
                    if (sourceField.HasConstant) newField.Constant = sourceField.Constant;
                    fieldType.Fields.Add(newField);
                    injected++;
                    Console.WriteLine(
                        $"    INJECTED DEPENDENCY FIELD: {fieldReference.DeclaringType.FullName}::{sourceField.Name}");
                    continue;
                }

                if (instruction.Operand is not MethodReference methodReference ||
                    methodReference.DeclaringType.FullName != method.DeclaringType.FullName)
                {
                    continue;
                }

                bool exists = vanillaType.Methods.Any(candidate =>
                    MethodSignature.Matches(candidate, methodReference));
                if (exists) continue;

                var sourceMethod = compiledType.Methods.FirstOrDefault(candidate =>
                    MethodSignature.Matches(candidate, methodReference));
                if (sourceMethod is null)
                {
                    throw new InvalidOperationException(
                        $"Required donor helper method not found: {method.DeclaringType.FullName}::" +
                        $"{methodReference.Name}");
                }

                InjectMethod(vanillaType, sourceMethod, vanilla.MainModule);
                injected++;
                Console.WriteLine(
                    $"    INJECTED DEPENDENCY METHOD: {method.DeclaringType.FullName}::{sourceMethod.Name}({sourceMethod.Parameters.Count} params)");
                pending.Enqueue(sourceMethod);
            }
        }

        return injected;
    }

    /// <summary>
    /// Copies custom attributes (e.g. [ThreadStatic]) onto a newly injected field.
    /// Field injection previously carried over only FieldAttributes (visibility/
    /// static/initonly flags) and silently dropped every real .NET attribute -
    /// for a plain data field this is harmless, but for something like
    /// [ThreadStatic] it changes the field's actual runtime semantics from a
    /// per-thread slot to one shared static, with no error or exception to
    /// signal it either at patch time or at runtime.
    /// </summary>
    private static void CopyCustomAttributes(
        Mono.Collections.Generic.Collection<CustomAttribute> source,
        Mono.Collections.Generic.Collection<CustomAttribute> target,
        ModuleDefinition targetModule)
    {
        foreach (var srcAttr in source)
        {
            var newAttr = new CustomAttribute(targetModule.ImportReference(srcAttr.Constructor));
            foreach (var arg in srcAttr.ConstructorArguments)
            {
                newAttr.ConstructorArguments.Add(new CustomAttributeArgument(
                    targetModule.ImportReference(arg.Type), arg.Value));
            }
            foreach (var namedArg in srcAttr.Fields)
            {
                newAttr.Fields.Add(new CustomAttributeNamedArgument(
                    namedArg.Name,
                    new CustomAttributeArgument(targetModule.ImportReference(namedArg.Argument.Type), namedArg.Argument.Value)));
            }
            foreach (var namedArg in srcAttr.Properties)
            {
                newAttr.Properties.Add(new CustomAttributeNamedArgument(
                    namedArg.Name,
                    new CustomAttributeArgument(targetModule.ImportReference(namedArg.Argument.Type), namedArg.Argument.Value)));
            }
            target.Add(newAttr);
        }
    }

    private static TypeDefinition CloneType(TypeDefinition src, ModuleDefinition targetModule)
    {
        var newType = new TypeDefinition(
            src.Namespace,
            src.Name,
            src.Attributes,
            src.BaseType != null ? targetModule.ImportReference(src.BaseType) : null);

        foreach (var implementedInterface in src.Interfaces)
        {
            newType.Interfaces.Add(new InterfaceImplementation(
                targetModule.ImportReference(implementedInterface.InterfaceType)));
        }

        foreach (var field in src.Fields)
        {
            var newField = new FieldDefinition(
                field.Name,
                field.Attributes,
                targetModule.ImportReference(field.FieldType));
            if (field.HasConstant) newField.Constant = field.Constant;
            newType.Fields.Add(newField);
        }

        foreach (var nestedType in src.NestedTypes)
        {
            newType.NestedTypes.Add(CloneType(nestedType, targetModule));
        }

        var methodMap = new Dictionary<MethodDefinition, MethodDefinition>();
        foreach (var method in src.Methods)
        {
            var newMethod = new MethodDefinition(
                method.Name,
                method.Attributes,
                targetModule.ImportReference(method.ReturnType));

            foreach (var param in method.Parameters)
            {
                newMethod.Parameters.Add(new ParameterDefinition(
                    param.Name,
                    param.Attributes,
                    targetModule.ImportReference(param.ParameterType)));
            }

            MethodBodyCloner.CopyPInvoke(method, newMethod, targetModule);

            newType.Methods.Add(newMethod);
            methodMap[method] = newMethod;
        }

        foreach (var (method, newMethod) in methodMap)
        {
            if (method.HasBody)
            {
                MethodBodyCloner.Copy(method, newMethod, targetModule);
            }
        }

        foreach (var property in src.Properties)
        {
            var newProperty = new PropertyDefinition(
                property.Name,
                property.Attributes,
                targetModule.ImportReference(property.PropertyType))
            {
                GetMethod = property.GetMethod != null ? methodMap.GetValueOrDefault(property.GetMethod) : null,
                SetMethod = property.SetMethod != null ? methodMap.GetValueOrDefault(property.SetMethod) : null,
            };
            foreach (var parameter in property.Parameters)
            {
                newProperty.Parameters.Add(new ParameterDefinition(
                    parameter.Name,
                    parameter.Attributes,
                    targetModule.ImportReference(parameter.ParameterType)));
            }
            newType.Properties.Add(newProperty);
        }

        return newType;
    }

    private static void InjectProperty(TypeDefinition target, PropertyDefinition src, ModuleDefinition targetModule, AssemblyDefinition compiledAsm)
    {
        var propType = targetModule.ImportReference(src.PropertyType);

        // Backing field (compiler-generated)
        var backingFieldName = $"<{src.Name}>k__BackingField";
        var srcBacking = src.DeclaringType.Fields.FirstOrDefault(f => f.Name == backingFieldName);

        if (srcBacking != null && !target.Fields.Any(f => f.Name == backingFieldName))
        {
            var newBacking = new FieldDefinition(backingFieldName, srcBacking.Attributes, propType);
            if (srcBacking.HasConstant) newBacking.Constant = srcBacking.Constant;
            target.Fields.Add(newBacking);
        }

        // Getter
        if (src.GetMethod != null && !target.Methods.Any(m => MethodSignature.Matches(m, src.GetMethod)))
        {
            InjectMethod(target, src.GetMethod, targetModule);
        }

        // Setter
        if (src.SetMethod != null && !target.Methods.Any(m => MethodSignature.Matches(m, src.SetMethod)))
        {
            InjectMethod(target, src.SetMethod, targetModule);
        }

        // Property definition
        var newProp = new PropertyDefinition(src.Name, src.Attributes, propType)
        {
            GetMethod = src.GetMethod == null
                ? null
                : target.Methods.FirstOrDefault(m => MethodSignature.Matches(m, src.GetMethod)),
            SetMethod = src.SetMethod == null
                ? null
                : target.Methods.FirstOrDefault(m => MethodSignature.Matches(m, src.SetMethod)),
        };
        target.Properties.Add(newProp);
    }

    private static void InjectMethod(TypeDefinition target, MethodDefinition src, ModuleDefinition targetModule)
    {
        var newMethod = new MethodDefinition(
            src.Name,
            src.Attributes,
            targetModule.ImportReference(src.ReturnType));

        foreach (var param in src.Parameters)
        {
            newMethod.Parameters.Add(new ParameterDefinition(
                param.Name,
                param.Attributes,
                targetModule.ImportReference(param.ParameterType)));
        }

        // The P/Invoke flag requires an import map in the destination module.
        bool pinvoke = MethodBodyCloner.CopyPInvoke(src, newMethod, targetModule);
        if (!pinvoke && src.HasBody)
        {
            MethodBodyCloner.Copy(src, newMethod, targetModule);
        }

        target.Methods.Add(newMethod);
    }
}
