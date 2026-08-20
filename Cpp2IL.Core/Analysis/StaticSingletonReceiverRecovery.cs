using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Extensions;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 恢复经 TypeInfo、static_fields 和自类型单例字段读取的实例调用接收者。
/// </summary>
public static class StaticSingletonReceiverRecovery
{
    public static int Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph == null)
            return 0;

        var instructions = method.ControlFlowGraph.Instructions;
        var definitions = instructions
            .Where(instruction => instruction.Destination is LocalVariable)
            .GroupBy(instruction => (LocalVariable)instruction.Destination!)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var ownerTypes = method.Locals
            .Select(local => local.Type)
            .OfType<TypeAnalysisContext>()
            .Where(type => type is not (RuntimeClassTypeAnalysisContext or StaticFieldStorageTypeAnalysisContext))
            .Distinct()
            .ToArray();
        var staticFieldsOffset = method.AppContext.Binary.is32Bit ? 0x5C : 0xB8;
        var recovered = 0;

        foreach (var move in instructions.Where(instruction => instruction.OpCode == OpCode.Move).ToArray())
        {
            if (move.Operands is not
                [LocalVariable { Type: { IsValueType: false } expectedType }, MemoryOperand receiverLoad]
                || !TryResolveReceiverLoad(
                    receiverLoad,
                    expectedType,
                    ownerTypes,
                    definitions,
                    staticFieldsOffset,
                    out var resolvedReceiver))
                continue;

            move.SetOperand(1, resolvedReceiver!);
            recovered++;
        }

        foreach (var call in instructions.Where(instruction => instruction.IsCall).ToArray())
        {
            if (call.Operands[0] is not MethodAnalysisContext { IsStatic: false, DeclaringType: { } targetType })
                continue;

            var receiverIndex = call.OpCode == OpCode.Call ? 2 : 1;
            if (receiverIndex >= call.Operands.Count
                || call.Operands[receiverIndex] is not MemoryOperand receiverLoad
                || !TryResolveReceiverLoad(
                    receiverLoad,
                    targetType,
                    ownerTypes,
                    definitions,
                    staticFieldsOffset,
                    out var resolvedReceiver))
                continue;

            call.SetOperand(receiverIndex, resolvedReceiver!);
            recovered++;
        }

        return recovered;
    }

    private static bool TryResolveReceiverLoad(
        MemoryOperand receiverLoad,
        TypeAnalysisContext expectedType,
        IReadOnlyList<TypeAnalysisContext> ownerTypes,
        IReadOnlyDictionary<LocalVariable, Instruction[]> definitions,
        long staticFieldsOffset,
        out FieldReference? resolvedReceiver)
    {
        resolvedReceiver = null;
        if (receiverLoad is not
            {
                Base: LocalVariable ownerLocal,
                Index: null,
                Scale: 0,
                Addend: >= 0,
            })
            return false;

        var instanceFields = ownerTypes
            .SelectMany(owner => owner.Fields)
            .Where(field => !field.IsStatic
                            && field.Offset == receiverLoad.Addend
                            && AreCompatible(field.FieldType, expectedType))
            .Distinct()
            .ToArray();
        if (instanceFields.Length != 1)
            return false;

        var instanceField = instanceFields[0];
        var ownerType = instanceField.DeclaringType;
        if (!TryResolveSingletonOwner(
                ownerLocal,
                ownerType,
                definitions,
                staticFieldsOffset,
                out _))
            return false;

        resolvedReceiver = new FieldReference(instanceField, ownerLocal, (int)receiverLoad.Addend);
        return true;
    }

    private static bool TryResolveSingletonOwner(
        LocalVariable ownerLocal,
        TypeAnalysisContext ownerType,
        IReadOnlyDictionary<LocalVariable, Instruction[]> definitions,
        long staticFieldsOffset,
        out FieldReference? singletonField)
    {
        singletonField = null;
        if (!TryGetUniqueMoveSource(definitions, ownerLocal, out var ownerDefinition, out var singletonLoad)
            || singletonLoad is not MemoryOperand
            {
                Base: LocalVariable staticStorage,
                Index: null,
                Scale: 0,
                Addend: >= 0,
            } singletonMemory
            || !TryGetUniqueMoveSource(definitions, staticStorage, out _, out var staticStorageLoad)
            || staticStorageLoad is not MemoryOperand
            {
                Base: LocalVariable runtimeClass,
                Index: null,
                Scale: 0,
            } staticStorageMemory
            || staticStorageMemory.Addend != staticFieldsOffset
            || !TryGetUniqueMoveSource(definitions, runtimeClass, out _, out var runtimeClassLoad)
            || runtimeClassLoad is not MemoryOperand
            {
                Base: LocalVariable tableBase,
                Index: null,
                Scale: 0,
                Addend: >= 0,
            }
            || !HasSingleAbsoluteRoot(tableBase, definitions))
            return false;

        var singletonCandidates = ownerType.Fields
            .Where(field => field.IsStatic
                            && field.Offset == singletonMemory.Addend
                            && AreCompatible(field.FieldType, ownerType))
            .ToArray();
        if (singletonCandidates.Length != 1)
            return false;

        runtimeClass.Type = new RuntimeClassTypeAnalysisContext(ownerType, ownerType.DeclaringAssembly);
        staticStorage.Type = new StaticFieldStorageTypeAnalysisContext(ownerType, ownerType.DeclaringAssembly);
        ownerLocal.Type = ownerType;
        singletonField = new FieldReference(singletonCandidates[0], staticStorage, (int)singletonMemory.Addend);
        ownerDefinition.SetOperand(1, singletonField);
        return true;
    }

    private static bool TryGetUniqueMoveSource(
        IReadOnlyDictionary<LocalVariable, Instruction[]> definitions,
        LocalVariable local,
        out Instruction definition,
        out IOperand source)
    {
        definition = null!;
        source = null!;
        if (!definitions.TryGetValue(local, out var localDefinitions)
            || localDefinitions.Length != 1
            || localDefinitions[0] is not
            {
                OpCode: OpCode.Move,
                Operands: [_, var moveSource],
            } move)
            return false;

        definition = move;
        source = moveSource;
        return true;
    }

    private static bool HasSingleAbsoluteRoot(
        LocalVariable local,
        IReadOnlyDictionary<LocalVariable, Instruction[]> definitions)
    {
        var roots = new HashSet<long>();
        var visited = new HashSet<LocalVariable>();
        return VisitAbsoluteRoots(local, definitions, visited, roots) && roots.Count == 1;
    }

    private static bool VisitAbsoluteRoots(
        LocalVariable local,
        IReadOnlyDictionary<LocalVariable, Instruction[]> definitions,
        ISet<LocalVariable> visited,
        ISet<long> roots)
    {
        if (!visited.Add(local))
            return true;
        if (!definitions.TryGetValue(local, out var localDefinitions) || localDefinitions.Length == 0)
            return false;

        foreach (var definition in localDefinitions)
        {
            if (definition is
                {
                    OpCode: OpCode.Move,
                    Operands: [_, MemoryOperand { Base: null, Index: null, Scale: 0, Addend: >= 0 } absolute],
                })
            {
                roots.Add(absolute.Addend);
                continue;
            }

            if (definition is
                {
                    Index: -1,
                    OpCode: OpCode.Move,
                    Operands: [_, LocalVariable source],
                })
            {
                if (!VisitAbsoluteRoots(source, definitions, visited, roots))
                    return false;
                continue;
            }

            if (definition is { Index: -1, OpCode: OpCode.Phi })
            {
                foreach (var phiSource in definition.Operands.Skip(1).OfType<LocalVariable>())
                {
                    if (!VisitAbsoluteRoots(phiSource, definitions, visited, roots))
                        return false;
                }
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool AreCompatible(TypeAnalysisContext left, TypeAnalysisContext right)
        => GenericCallRebinder.TypesEquivalent(left, right)
           || left.IsAssignableTo(right)
           || right.IsAssignableTo(left);
}
