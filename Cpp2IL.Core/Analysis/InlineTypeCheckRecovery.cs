using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 把IL2CPP内联的引用类型强制转换闭包恢复为托管CastClass。
/// </summary>
public static class InlineTypeCheckRecovery
{
    private const long TypeDepthOffset = 0x130;
    private const long TypeHierarchyOffset = 0xC8;
    private const long PointerSize = 8;

    public static void Run(MethodAnalysisContext method)
    {
        var cfg = method.ControlFlowGraph!;
        var definitions = BuildUniqueDefinitions(cfg.Instructions);
        var changed = false;

        foreach (var secondCheckBlock in cfg.Blocks.ToList())
        {
            if (!TryMatchSecondCheck(
                    secondCheckBlock,
                    definitions,
                    out var source,
                    out var targetType,
                    out var firstCheckBlock,
                    out var invalidCastBlock))
                continue;

            var castResult = new LocalVariable(
                $"cast_{source.Name}_{targetType.Name}",
                new Register(null, $"CAST_{source.Register.Name}_{targetType.Name}"),
                targetType);
            var insertAt = firstCheckBlock.Instructions.Count;
            if (insertAt > 0 && !firstCheckBlock.Instructions[^1].IsFallThrough)
                insertAt--;
            firstCheckBlock.Instructions.Insert(
                insertAt,
                new Instruction(-1, OpCode.CastClass, castResult, source, targetType));

            ReplaceSuccessRegionUses(secondCheckBlock, invalidCastBlock, source, castResult);
            RemoveFailureBranch(firstCheckBlock, invalidCastBlock);
            RemoveFailureBranch(secondCheckBlock, invalidCastBlock);
            changed = true;
        }

        if (!changed)
            return;

        cfg.RemoveUnreachableBlocks();
        DeadCodeEliminator.Run(method);
        LocalVariables.ResolveTypesAndFields(method);
    }

    private static bool TryMatchSecondCheck(
        Block secondCheckBlock,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions,
        out LocalVariable source,
        out TypeAnalysisContext targetType,
        out Block firstCheckBlock,
        out Block invalidCastBlock)
    {
        source = null!;
        targetType = null!;
        firstCheckBlock = null!;
        invalidCastBlock = null!;

        if (!TryGetFailureBranch(secondCheckBlock, definitions, out var secondCondition, out invalidCastBlock))
            return false;
        if (!IsInvalidCastOnly(invalidCastBlock))
            return false;
        if (!definitions.TryGetValue(secondCondition, out var secondConditionDefinition))
            return false;
        if (!TryUnwrapNot(secondConditionDefinition, definitions, out var equality))
            return false;
        if (equality.OpCode != OpCode.CheckEqual || equality.Operands.Count != 3)
            return false;
        var hierarchyEntryOperand = ResolveMoveSource(equality.Operands[1], definitions);
        var candidateTypeOperand = ResolveMoveSource(equality.Operands[2], definitions);
        if (hierarchyEntryOperand is not MemoryOperand hierarchyEntry
            || candidateTypeOperand is not TypeAnalysisContext candidateType)
            return false;
        if (hierarchyEntry.Addend != -PointerSize || hierarchyEntry.Base is not LocalVariable hierarchyEntryAddress)
            return false;
        if (!definitions.TryGetValue(hierarchyEntryAddress, out var hierarchyAddressDefinition)
            || hierarchyAddressDefinition.OpCode != OpCode.Add
            || hierarchyAddressDefinition.Operands.Count != 3)
            return false;
        var hierarchyOperand = ResolveMoveSource(hierarchyAddressDefinition.Operands[1], definitions);
        var scaledDepthOperand = ResolveLocalMoveCarrier(hierarchyAddressDefinition.Operands[2], definitions);
        if (hierarchyOperand is not MemoryOperand hierarchy || scaledDepthOperand is not LocalVariable scaledDepth)
            return false;
        if (hierarchy.Addend != TypeHierarchyOffset || hierarchy.Base is not LocalVariable sourceClass)
            return false;
        if (!definitions.TryGetValue(scaledDepth, out var scaledDepthDefinition)
            || scaledDepthDefinition.OpCode != OpCode.ShiftLeft
            || scaledDepthDefinition.Operands.Count != 3)
            return false;
        var targetDepthOperand = ResolveMoveSource(scaledDepthDefinition.Operands[1], definitions);
        var shiftOperand = ResolveMoveSource(scaledDepthDefinition.Operands[2], definitions);
        if (targetDepthOperand is not MemoryOperand targetDepth || shiftOperand is not Immediate { Value: 3 })
            return false;
        if (targetDepth.Addend != TypeDepthOffset || targetDepth.Base is not LocalVariable targetClass)
            return false;
        if (RuntimeClassRepresentedType(targetClass) is not { IsValueType: false } representedTarget)
            return false;
        if (!GenericCallRebinder.TypesEquivalent(candidateType, representedTarget))
            return false;
        if (!definitions.TryGetValue(sourceClass, out var sourceClassDefinition)
            || sourceClassDefinition.OpCode != OpCode.Move
            || sourceClassDefinition.Operands.Count != 2)
            return false;
        var sourceObjectOperand = ResolveMoveSource(sourceClassDefinition.Operands[1], definitions);
        if (sourceObjectOperand is not MemoryOperand { Addend: 0, Base: LocalVariable sourceObject })
            return false;
        if (sourceObject.Type == null)
            return false;

        var expectedFailureBlock = invalidCastBlock;
        var firstCandidates = secondCheckBlock.Predecessors
            .Where(block => TryGetFailureBranch(block, definitions, out _, out var failure)
                && ReferenceEquals(failure, expectedFailureBlock))
            .ToList();
        if (firstCandidates.Count != 1)
            return false;

        firstCheckBlock = firstCandidates[0];
        if (!TryGetFailureBranch(firstCheckBlock, definitions, out var firstCondition, out _)
            || !definitions.TryGetValue(firstCondition, out var firstDefinition)
            || firstDefinition.OpCode != OpCode.CheckLessUnsigned
            || firstDefinition.Operands.Count != 3)
            return false;
        var sourceDepthOperand = ResolveMoveSource(firstDefinition.Operands[1], definitions);
        var comparedTargetDepthOperand = ResolveMoveSource(firstDefinition.Operands[2], definitions);
        if (sourceDepthOperand is not MemoryOperand sourceDepth
            || comparedTargetDepthOperand is not MemoryOperand comparedTargetDepth
            || sourceDepth.Addend != TypeDepthOffset
            || !ReferenceEquals(sourceDepth.Base, sourceClass)
            || comparedTargetDepth.Addend != TypeDepthOffset
            || !ReferenceEquals(comparedTargetDepth.Base, targetClass))
            return false;

        source = sourceObject;
        targetType = representedTarget;
        return true;
    }

    private static IOperand ResolveMoveSource(
        IOperand operand,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions)
    {
        var visited = new HashSet<LocalVariable>();
        while (operand is LocalVariable local
            && visited.Add(local)
            && definitions.TryGetValue(local, out var definition)
            && definition is { OpCode: OpCode.Move, Operands.Count: 2 })
        {
            operand = definition.Operands[1];
        }

        return operand;
    }

    private static IOperand ResolveLocalMoveCarrier(
        IOperand operand,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions)
    {
        var visited = new HashSet<LocalVariable>();
        while (operand is LocalVariable local
            && visited.Add(local)
            && definitions.TryGetValue(local, out var definition)
            && definition is { OpCode: OpCode.Move, Operands.Count: 2 }
            && definition.Operands[1] is LocalVariable next)
        {
            operand = next;
        }

        return operand;
    }

    private static TypeAnalysisContext? RuntimeClassRepresentedType(LocalVariable local) =>
        local.Type is RuntimeClassTypeAnalysisContext { RepresentedType: var represented }
            ? represented
            : null;

    private static bool TryGetFailureBranch(
        Block block,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions,
        out LocalVariable condition,
        out Block failure)
    {
        condition = null!;
        failure = null!;
        if (block.Instructions.Count == 0
            || block.Instructions[^1] is not { OpCode: OpCode.ConditionalJump, Operands: [Block target, LocalVariable local] }
            || !definitions.ContainsKey(local))
            return false;

        condition = local;
        failure = target;
        return true;
    }

    private static bool TryUnwrapNot(
        Instruction instruction,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions,
        out Instruction equality)
    {
        if (instruction.OpCode == OpCode.CheckEqual)
        {
            equality = instruction;
            return true;
        }

        if (instruction is { OpCode: OpCode.Not, Operands: [_, LocalVariable source] }
            && definitions.TryGetValue(source, out var sourceDefinition)
            && sourceDefinition.OpCode == OpCode.CheckEqual)
        {
            equality = sourceDefinition;
            return true;
        }

        equality = null!;
        return false;
    }

    private static bool IsInvalidCastOnly(Block block) =>
        block.Instructions.Any(instruction => instruction is
        {
            OpCode: OpCode.Throw,
            Operands: [TypeAnalysisContext { FullName: "System.InvalidCastException" }],
        })
        && block.Instructions.All(instruction =>
            instruction.OpCode is OpCode.Nop or OpCode.Phi or OpCode.Throw or OpCode.Return);

    private static void RemoveFailureBranch(Block block, Block failure)
    {
        if (block.Instructions.Count > 0 && block.Instructions[^1].OpCode == OpCode.ConditionalJump)
        {
            block.Instructions[^1].OpCode = OpCode.Nop;
            block.Instructions[^1].SetOperands();
        }

        block.Successors.Remove(failure);
        failure.Predecessors.Remove(block);
        block.CalculateBlockType();
    }

    private static void ReplaceSuccessRegionUses(
        Block start,
        Block failure,
        LocalVariable source,
        LocalVariable replacement)
    {
        var visited = new HashSet<Block> { failure };
        var queue = new Queue<Block>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var block = queue.Dequeue();
            if (!visited.Add(block))
                continue;

            foreach (var instruction in block.Instructions)
            {
                for (var index = 0; index < instruction.Operands.Count; index++)
                {
                    if (ReferenceEquals(instruction.Operands[index], source))
                        instruction.SetOperand(index, replacement);
                    else if (instruction.Operands[index] is MemoryOperand memory
                        && ReferenceEquals(memory.Base, source))
                    {
                        memory.Base = replacement;
                        instruction.SetOperand(index, memory);
                    }
                }
            }

            foreach (var successor in block.Successors)
                queue.Enqueue(successor);
        }
    }

    private static IReadOnlyDictionary<LocalVariable, Instruction> BuildUniqueDefinitions(
        IEnumerable<Instruction> instructions) =>
        instructions
            .Where(instruction => instruction.Destination is LocalVariable)
            .GroupBy(instruction => (LocalVariable)instruction.Destination!)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());
}
