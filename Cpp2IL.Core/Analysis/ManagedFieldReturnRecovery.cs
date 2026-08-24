using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 恢复“对象字段地址/默认静态槽地址汇合后统一解引用”的托管引用返回值。
/// </summary>
/// <remarks>
/// ARM64 会在成功边计算 <c>对象 + 字段偏移</c>，在默认边装入一个静态槽地址，
/// 然后在共同尾声只解引用一次。退 SSA 后两个地址共用同一局部，普通字段解析器因定义
/// 不同而必须拒绝。本恢复器只接受恰好两个直接前驱、一个精确托管字段地址、一个绝对
/// 默认槽地址和一个独占零偏移返回解引用；任一条件不唯一都保持原图。
/// </remarks>
public static class ManagedFieldReturnRecovery
{
    public static int Run(MethodAnalysisContext method)
    {
        if (method == null)
            throw new ArgumentNullException(nameof(method));
        if (method.ReturnType.IsValueType
            || method.ReturnType is PointerTypeAnalysisContext or ByRefTypeAnalysisContext)
            return 0;

        return Rewrite(
            method.ControlFlowGraph!,
            method.ReturnType,
            method.Locals,
            method.DeclaringType);
    }

    /// <summary>
    /// 测试入口直接消费已完成退 SSA 的控制流图和权威返回类型。
    /// </summary>
    internal static int Rewrite(
        ISILControlFlowGraph graph,
        TypeAnalysisContext returnType,
        ICollection<LocalVariable> locals,
        TypeAnalysisContext? callerType = null)
    {
        var homeBlocks = graph.Blocks
            .SelectMany(block => block.Instructions.Select(instruction => (instruction, block)))
            .ToDictionary(pair => pair.instruction, pair => pair.block);
        var definitions = graph.Instructions
            .Where(instruction => instruction.Destination is LocalVariable)
            .GroupBy(instruction => (LocalVariable)instruction.Destination!)
            .ToDictionary(group => group.Key, group => group.ToArray());

        foreach (var returnBlock in graph.Blocks)
        {
            if (returnBlock.Instructions.LastOrDefault() is not
                {
                    OpCode: OpCode.Return,
                    Operands:
                    [
                        MemoryOperand
                        {
                            Base: LocalVariable carrier,
                            Index: null,
                            Addend: 0,
                            Scale: 0
                        }
                    ]
                } returnInstruction
                || !definitions.TryGetValue(carrier, out var carrierDefinitions)
                || carrierDefinitions.Length != 2
                || !TryPartitionDefinitions(
                    carrierDefinitions,
                    returnType,
                    out var fieldDefinition,
                    out var receiver,
                    out var field,
                    out var fieldOffset,
                    out var fallbackDefinition)
                || !HasExactMergeShape(
                    returnBlock,
                    fieldDefinition,
                    fallbackDefinition,
                    homeBlocks)
                || !HasExclusiveReturnUse(
                    graph.Instructions,
                    carrierDefinitions,
                    returnInstruction,
                    carrier))
                continue;

            var value = new LocalVariable(
                $"managedFieldReturn{Math.Max(returnInstruction.Index, 0)}",
                new Register(null, $"MANAGED_FIELD_RETURN_{Math.Max(returnInstruction.Index, 0)}"),
                returnType);
            locals.Add(value);

            // 中文注释：成功边不再传播原生字段地址，而是直接读取已经由布局唯一确定的字段值。
            var fieldReference = new FieldReference(
                field,
                receiver,
                checked((int)fieldOffset));
            if (callerType != null
                && PropertyBackingFieldRecovery.TryResolveGetter(
                    callerType,
                    fieldReference,
                    out var getter))
            {
                // 中文注释：晚期才出现的跨类型私有 backing field 直接绑定同一公开 getter，
                // 不重复运行整套属性恢复扫描。
                fieldDefinition.OpCode = OpCode.Call;
                fieldDefinition.SetOperands(getter, value, receiver);
            }
            else
            {
                fieldDefinition.OpCode = OpCode.Move;
                fieldDefinition.SetOperands(value, fieldReference);
            }

            // 中文注释：默认边先保留绝对槽装载，再在同一基本块内完成原生的第二次解引用。
            var fallbackBlock = homeBlocks[fallbackDefinition];
            var fallbackIndex = fallbackBlock.Instructions.IndexOf(fallbackDefinition);
            fallbackBlock.Instructions.Insert(
                fallbackIndex + 1,
                new Instruction(
                    fallbackDefinition.Index,
                    OpCode.Move,
                    value,
                    new MemoryOperand(carrier)));

            returnInstruction.SetOperand(0, value);
            return 1;
        }

        return 0;
    }

    private static bool TryPartitionDefinitions(
        IReadOnlyList<Instruction> definitions,
        TypeAnalysisContext returnType,
        out Instruction fieldDefinition,
        out LocalVariable receiver,
        out FieldAnalysisContext field,
        out long fieldOffset,
        out Instruction fallbackDefinition)
    {
        fieldDefinition = null!;
        receiver = null!;
        field = null!;
        fieldOffset = 0;
        fallbackDefinition = null!;

        foreach (var definition in definitions)
        {
            if (TryResolveFieldAddress(
                    definition,
                    returnType,
                    out var candidateReceiver,
                    out var candidateField,
                    out var candidateOffset))
            {
                if (fieldDefinition != null)
                    return false;
                fieldDefinition = definition;
                receiver = candidateReceiver;
                field = candidateField;
                fieldOffset = candidateOffset;
                continue;
            }

            if (IsAbsoluteFallbackAddress(definition))
            {
                if (fallbackDefinition != null)
                    return false;
                fallbackDefinition = definition;
                continue;
            }

            return false;
        }

        return fieldDefinition != null && fallbackDefinition != null;
    }

    private static bool TryResolveFieldAddress(
        Instruction definition,
        TypeAnalysisContext returnType,
        out LocalVariable receiver,
        out FieldAnalysisContext field,
        out long fieldOffset)
    {
        receiver = null!;
        field = null!;
        fieldOffset = 0;
        if (definition is not { OpCode: OpCode.Add, Operands.Count: 3 })
            return false;

        if (definition.Operands[1] is LocalVariable left
            && definition.Operands[2] is Immediate rightOffset)
        {
            receiver = left;
            fieldOffset = rightOffset.Value;
        }
        else if (definition.Operands[1] is Immediate leftOffset
                 && definition.Operands[2] is LocalVariable right)
        {
            receiver = right;
            fieldOffset = leftOffset.Value;
        }
        else
        {
            return false;
        }

        if (receiver.Type == null
            || receiver.Type.IsValueType
            || receiver.Type is PointerTypeAnalysisContext
                or ByRefTypeAnalysisContext
                or StaticFieldStorageTypeAnalysisContext)
            return false;

        field = FindFieldAtOffset(receiver.Type, fieldOffset)!;
        return field != null
               && !field.IsStatic
               && GenericCallRebinder.TypesEquivalent(field.FieldType, returnType);
    }

    private static FieldAnalysisContext? FindFieldAtOffset(
        TypeAnalysisContext owner,
        long fieldOffset)
    {
        if (owner is GenericInstanceTypeAnalysisContext generic)
            return GenericInstanceFieldLayout.FindConcreteFieldAtOffset(generic, fieldOffset);

        for (var candidateOwner = owner;
             candidateOwner != null;
             candidateOwner = candidateOwner.BaseType)
        {
            var matches = candidateOwner.Fields
                .Where(candidate => !candidate.IsStatic
                                    && candidate.Offset == fieldOffset)
                .Take(2)
                .ToArray();
            if (matches.Length == 1)
                return matches[0];
            if (matches.Length > 1)
                return null;
        }

        return null;
    }

    private static bool IsAbsoluteFallbackAddress(Instruction definition)
        => definition is
        {
            OpCode: OpCode.Move,
            Operands:
            [
                LocalVariable,
                MemoryOperand
                {
                    Base: null,
                    Index: null,
                    Addend: > 0,
                    Scale: 0
                }
            ]
        };

    private static bool HasExactMergeShape(
        Block returnBlock,
        Instruction fieldDefinition,
        Instruction fallbackDefinition,
        IReadOnlyDictionary<Instruction, Block> homeBlocks)
    {
        var fieldBlock = homeBlocks[fieldDefinition];
        var fallbackBlock = homeBlocks[fallbackDefinition];
        if (ReferenceEquals(fieldBlock, fallbackBlock)
            || returnBlock.Predecessors.Count != 2
            || !returnBlock.Predecessors.Contains(fieldBlock)
            || !returnBlock.Predecessors.Contains(fallbackBlock))
            return false;

        return fieldBlock.Successors.Count == 1
               && ReferenceEquals(fieldBlock.Successors[0], returnBlock)
               && fallbackBlock.Successors.Count == 1
               && ReferenceEquals(fallbackBlock.Successors[0], returnBlock);
    }

    private static bool HasExclusiveReturnUse(
        IReadOnlyList<Instruction> instructions,
        IReadOnlyCollection<Instruction> definitions,
        Instruction returnInstruction,
        LocalVariable carrier)
    {
        var definitionSet = new HashSet<Instruction>(definitions);
        foreach (var instruction in instructions)
        {
            for (var operandIndex = 0; operandIndex < instruction.Operands.Count; operandIndex++)
            {
                if (definitionSet.Contains(instruction)
                    && operandIndex == 0
                    && ReferenceEquals(instruction.Operands[0], carrier))
                    continue;

                var operand = instruction.Operands[operandIndex];
                if (ReferenceEquals(instruction, returnInstruction)
                    && operandIndex == 0
                    && operand is MemoryOperand
                    {
                        Base: LocalVariable returnCarrier,
                        Index: null,
                        Addend: 0,
                        Scale: 0
                    }
                    && ReferenceEquals(returnCarrier, carrier))
                    continue;

                if (ContainsLocal(operand, carrier))
                    return false;
            }
        }

        return true;
    }

    private static bool ContainsLocal(IOperand operand, LocalVariable expected)
        => operand switch
        {
            LocalVariable local => ReferenceEquals(local, expected),
            MemoryOperand memory => ReferenceEquals(memory.Base, expected)
                                    || ReferenceEquals(memory.Index, expected),
            FieldReference field => ReferenceEquals(field.Local, expected),
            AddressOf address => ReferenceEquals(address.Target, expected),
            ArrayAccess array => ReferenceEquals(array.Array, expected)
                                 || ReferenceEquals(array.Index, expected),
            _ => false,
        };
}
