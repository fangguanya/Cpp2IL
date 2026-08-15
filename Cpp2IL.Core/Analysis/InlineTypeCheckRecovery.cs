using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 把IL2CPP内联的引用类型检查闭包恢复为托管CastClass或IsInst。
/// </summary>
public static class InlineTypeCheckRecovery
{
    private const long TypeDepthOffset = 0x130;
    private const long TypeHierarchyOffset = 0xC8;
    private const long PointerSize = 8;

    private enum FailureSemantics
    {
        CastClass,
        IsInstThenDereference,
    }

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
                    method.AppContext.SystemTypes.SystemObjectType,
                    out var source,
                    out var targetType,
                    out var firstCheckBlock,
                    out var failureBlock,
                    out var secondFailureEntry,
                    out var secondFailureUsesFallthrough,
                    out var recoveryOpCode))
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
                new Instruction(-1, recoveryOpCode, castResult, source, targetType));

            ReplaceSuccessRegionUses(secondCheckBlock, failureBlock, source, castResult);
            RemoveFailureBranch(firstCheckBlock);
            if (secondFailureUsesFallthrough)
                ReplaceFailureFallthroughWithSuccessJump(secondCheckBlock, secondFailureEntry);
            else
                RemoveFailureBranch(secondCheckBlock);
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
        TypeAnalysisContext objectType,
        out LocalVariable source,
        out TypeAnalysisContext targetType,
        out Block firstCheckBlock,
        out Block failureBlock,
        out Block secondFailureEntry,
        out bool secondFailureUsesFallthrough,
        out OpCode recoveryOpCode)
    {
        source = null!;
        targetType = null!;
        firstCheckBlock = null!;
        failureBlock = null!;
        secondFailureEntry = null!;
        secondFailureUsesFallthrough = false;
        recoveryOpCode = OpCode.Nop;

        if (!TryGetSecondCheckFailure(
                secondCheckBlock,
                definitions,
                out var secondCondition,
                out secondFailureEntry,
                out failureBlock,
                out var failureSemantics,
                out secondFailureUsesFallthrough))
            return false;
        if (!definitions.TryGetValue(secondCondition, out var secondConditionDefinition))
            return false;
        if (!TryUnwrapNot(secondConditionDefinition, definitions, out var equality))
            return false;
        if (secondFailureUsesFallthrough && secondConditionDefinition.OpCode != OpCode.CheckEqual)
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
        var inferredSourceType = sourceObject.Type
            ?? RuntimeClassRepresentedType(sourceClass)
            ?? objectType;
        if (inferredSourceType is not { IsValueType: false })
            return false;

        var expectedFailureBlock = failureBlock;
        var firstCandidates = secondCheckBlock.Predecessors
            .Where(block => TryGetFailureBranch(block, definitions, out _, out var failureEntry)
                && TryResolveFailureBlock(failureEntry, out var failure, out var candidateSemantics)
                && ReferenceEquals(failure, expectedFailureBlock)
                && candidateSemantics == failureSemantics)
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
        source.Type ??= inferredSourceType;
        targetType = representedTarget;
        recoveryOpCode = failureSemantics switch
        {
            FailureSemantics.CastClass => OpCode.CastClass,
            FailureSemantics.IsInstThenDereference when SuccessStartsWithTargetDereference(
                secondCheckBlock,
                secondFailureEntry,
                source,
                targetType,
                definitions) => OpCode.IsInst,
            _ => OpCode.Nop,
        };
        if (recoveryOpCode == OpCode.Nop)
            return false;

        return true;
    }

    /// <summary>
    /// 证明类层级检查成功后的首个有效操作会以目标类型实例调用解引用源对象。
    /// 这种形态来自“as T”结果的立即成员访问：类型不匹配与空对象都会在成员调用处保持空引用异常。
    /// </summary>
    private static bool SuccessStartsWithTargetDereference(
        Block secondCheckBlock,
        Block failureEntry,
        LocalVariable source,
        TypeAnalysisContext targetType,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions)
    {
        var successBlocks = secondCheckBlock.Successors
            .Where(block => !ReferenceEquals(block, failureEntry))
            .ToList();
        if (successBlocks.Count != 1)
            return false;

        foreach (var instruction in successBlocks[0].Instructions)
        {
            if (instruction.OpCode is OpCode.Nop or OpCode.Phi)
                continue;
            if (instruction.OpCode is not (OpCode.Call or OpCode.CallVoid)
                || instruction.Operands.FirstOrDefault() is not MethodAnalysisContext called
                || called.IsStatic
                || !GenericCallRebinder.TypesEquivalent(called.DeclaringType, targetType))
                return false;

            var receiverIndex = instruction.OpCode == OpCode.Call ? 2 : 1;
            return receiverIndex < instruction.Operands.Count
                && ReferenceEquals(
                    ResolveMoveSource(instruction.Operands[receiverIndex], definitions),
                    source);
        }

        return false;
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

    /// <summary>
    /// 解析类层级第二次比较的失败边。ARM64既会把“不相等”直接跳到失败块，
    /// 也会把“相等”跳到成功块并让顺序落空块纯跳到同一失败闭包；两者都必须由
    /// 唯一异常语义证明，避免按块顺序猜测成功与失败。
    /// </summary>
    private static bool TryGetSecondCheckFailure(
        Block block,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions,
        out LocalVariable condition,
        out Block failureEntry,
        out Block failureBlock,
        out FailureSemantics failureSemantics,
        out bool failureUsesFallthrough)
    {
        failureEntry = null!;
        failureBlock = null!;
        failureSemantics = default;
        failureUsesFallthrough = false;
        if (!TryGetFailureBranch(block, definitions, out condition, out var conditionalTarget))
            return false;

        if (TryResolveFailureBlock(conditionalTarget, out failureBlock, out failureSemantics))
        {
            failureEntry = conditionalTarget;
            return true;
        }

        var fallthroughCandidates = block.Successors
            .Where(successor => !ReferenceEquals(successor, conditionalTarget))
            .Select(successor => new
            {
                Entry = successor,
                Resolved = TryResolveFailureBlock(
                    successor,
                    out var resolvedFailure,
                    out var resolvedSemantics),
                Failure = resolvedFailure,
                Semantics = resolvedSemantics,
            })
            .Where(candidate => candidate.Resolved)
            .ToList();
        if (fallthroughCandidates.Count != 1)
            return false;

        var candidate = fallthroughCandidates[0];
        failureEntry = candidate.Entry;
        failureBlock = candidate.Failure;
        failureSemantics = candidate.Semantics;
        failureUsesFallthrough = true;
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

    private static bool TryGetFailureSemantics(Block block, out FailureSemantics semantics)
    {
        semantics = default;
        var throws = block.Instructions
            .Where(instruction => instruction.OpCode == OpCode.Throw)
            .ToList();
        if (throws.Count != 1
            || throws[0].Operands is not [TypeAnalysisContext exceptionType]
            || block.Instructions.Any(instruction =>
                instruction.OpCode is not (OpCode.Nop or OpCode.Phi or OpCode.Throw or OpCode.Return)))
            return false;

        semantics = exceptionType.FullName switch
        {
            "System.InvalidCastException" => FailureSemantics.CastClass,
            "System.NullReferenceException" => FailureSemantics.IsInstThenDereference,
            _ => default,
        };
        return exceptionType.FullName is "System.InvalidCastException" or "System.NullReferenceException";
    }

    /// <summary>
    /// 解析由控制流拆边产生的纯跳转失败入口。这里只跨越不携带业务计算的Jump块，
    /// 防止把带副作用或额外检查的路径误判成标准类型检查失败闭包。
    /// </summary>
    private static bool TryResolveFailureBlock(
        Block entry,
        out Block failureBlock,
        out FailureSemantics semantics)
    {
        var visited = new HashSet<Block>();
        var current = entry;
        while (visited.Add(current))
        {
            if (TryGetFailureSemantics(current, out semantics))
            {
                failureBlock = current;
                return true;
            }

            if (current.Successors.Count != 1
                || current.Instructions.Count == 0
                || current.Instructions[^1].OpCode != OpCode.Jump
                || current.Instructions.Any(instruction =>
                    instruction.OpCode is not (OpCode.Nop or OpCode.Phi or OpCode.Jump)))
            {
                break;
            }

            current = current.Successors.Single();
        }

        failureBlock = null!;
        semantics = default;
        return false;
    }

    private static void RemoveFailureBranch(Block block)
    {
        if (block.Instructions.Count == 0
            || block.Instructions[^1] is not
            {
                OpCode: OpCode.ConditionalJump,
                Operands: [Block failure, _],
            } branch)
            return;

        branch.OpCode = OpCode.Nop;
        branch.SetOperands();

        block.Successors.Remove(failure);
        failure.Predecessors.Remove(block);
        block.CalculateBlockType();
    }

    /// <summary>
    /// 当条件目标是成功块而顺序后继纯跳失败时，把条件跳转提升为无条件成功跳转，
    /// 并精确断开失败入口；随后统一不可达块清理会移除类型层级计算。
    /// </summary>
    private static void ReplaceFailureFallthroughWithSuccessJump(
        Block block,
        Block failureEntry)
    {
        if (block.Instructions.Count == 0
            || block.Instructions[^1] is not
            {
                OpCode: OpCode.ConditionalJump,
                Operands: [Block success, _],
            } branch
            || !block.Successors.Contains(failureEntry)
            || ReferenceEquals(success, failureEntry))
            return;

        branch.OpCode = OpCode.Jump;
        branch.SetOperands(success);
        block.Successors.Remove(failureEntry);
        failureEntry.Predecessors.Remove(block);
        block.CalculateBlockType();
    }

    internal static void ReplaceSuccessRegionUses(
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
                // 成功区若经循环回边重新到达新插入的CastClass定义，该定义是replacement
                // 的唯一生产者，绝不能把它自己的源对象重写成replacement形成自引用。
                if (ReferenceEquals(instruction.Destination, replacement))
                    continue;

                for (var index = 0; index < instruction.Operands.Count; index++)
                {
                    // 环形控制流可能从当前成功区重新到达更早的CastClass定义。定义目标不是
                    // 对旧值的读取；改写它会让两个不同目标类型共用一个局部量并永久振荡。
                    var operand = instruction.Operands[index];
                    if (ReferenceEquals(operand, source)
                        && !ReferenceEquals(instruction.Destination, operand))
                        instruction.SetOperand(index, replacement);
                    else if (operand is MemoryOperand memory
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
