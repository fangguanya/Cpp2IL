using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 将 IL2CPP 展开的 <c>List&lt;T&gt;.Add</c> 容量分支重新闭合为公开的托管调用。
/// </summary>
/// <remarks>
/// IL2CPP 会把快速路径展开为 <c>_items/_size/_version</c> 读写，只在容量不足时调用
/// <c>AddWithResize</c>。这些成员属于运行库私有实现，直接输出会生成既不可访问又带原生
/// 地址算术的 C#。本恢复器只接受完整的标准菱形：版本递增、容量判断、同值元素写入、
/// 大小递增以及同值慢路径必须同时成立，避免把业务分支误认成集合内联。
/// </remarks>
public static class ListAddRecovery
{
    public static int Run(MethodAnalysisContext method)
    {
        var graph = method.ControlFlowGraph!;
        var recovered = 0;
        // 共享快尾会在逐项改写后只剩一个前驱，因此必须在任何图修改之前冻结其身份。
        var originalSharedFastTails = graph.Blocks
            .Where(block => block.Predecessors.Count >= 2)
            .ToHashSet();

        foreach (var slowBlock in graph.Blocks.ToList())
        {
            if (TryRecoverSharedFastTail(graph, slowBlock, originalSharedFastTails))
            {
                recovered++;
                continue;
            }

            if (!TryMatchSlowPath(
                    slowBlock,
                    out var slowCall,
                    out var addWithResize,
                    out var receiver,
                    out var value,
                    out var slowTail))
                continue;

            if (slowBlock.Predecessors is not [var head])
                continue;
            if (!TryGetMergeBlock(graph, slowBlock, out var merge))
                continue;
            if (!TryMatchHead(
                    head,
                    slowBlock,
                    receiver,
                    out var rewriteHead,
                    out var headPath,
                    out var rewriteStart,
                    out var items,
                    out var sizeState,
                    out var versionResult,
                    out var preservedHeadBusiness))
                continue;
            if (!TryGetFastBlock(head, slowBlock, merge, out var fastBlock))
                continue;
            if (!TryMatchFastPath(
                    graph,
                    fastBlock,
                    merge,
                    receiver,
                    items,
                    sizeState,
                    versionResult,
                    value,
                    slowTail,
                    addWithResize.AppContext,
                    out var publicValue,
                    out var preservedSlowTail))
                continue;
            if (!TryCreatePublicAddTarget(addWithResize, out var addTarget))
                continue;

            Rewrite(
                graph,
                rewriteHead,
                headPath,
                fastBlock,
                slowBlock,
                merge,
                rewriteStart,
                slowCall,
                preservedSlowTail,
                preservedHeadBusiness,
                addTarget,
                receiver,
                publicValue);
            recovered++;
        }

        return recovered;
    }

    /// <summary>
    /// 恢复多个容量分支把元素写入汇聚到同一个原生快速尾块的 ARM64 形态。
    /// </summary>
    /// <remarks>
    /// 该形态先在每条快边写入元素载体，再由共享块统一执行数组地址计算、大小递增和元素写入；
    /// 慢边则直接调用 <c>AddWithResize</c>。恢复必须同时证明公开接收者别名、两级空检查、
    /// 版本递增、容量判断、共享尾的原生布局以及快慢值完全等价，任一条件漂移都保留原图。
    /// </remarks>
    private static bool TryRecoverSharedFastTail(
        ISILControlFlowGraph graph,
        Block slowBlock,
        HashSet<Block> originalSharedFastTails)
    {
        if (!TryMatchSharedSlowPath(
                slowBlock,
                out var slowCall,
                out var addWithResize,
                out var publicReceiver,
                out var slowValue,
                out var slowCarrierTail))
            return false;
        if (slowBlock.Predecessors is not [var capacityHead])
            return false;
        if (!TryGetMergeBlock(graph, slowBlock, out var merge))
            return false;
        if (!TryGetSharedFastBlocks(
                capacityHead,
                slowBlock,
                merge,
                originalSharedFastTails,
                out var stagingBlock,
                out var sharedFastTail))
            return false;
        if (!TryMatchSharedFastTail(
                sharedFastTail,
                merge,
                out var sharedPattern))
            return false;
        if (!TryMatchSharedCapacityHead(
                capacityHead,
                slowBlock,
                publicReceiver,
                sharedPattern))
            return false;
        if (!TryMatchSharedStatePrefix(
                capacityHead,
                publicReceiver,
                sharedPattern.Items,
                out var rewriteHead,
                out var headPath,
                out var rewriteStart))
            return false;
        if (!TryMatchSharedStagingPath(
                graph,
                stagingBlock,
                sharedFastTail,
                merge,
                sharedPattern.StagedValue,
                slowValue,
                slowCarrierTail,
                addWithResize.AppContext,
                out var publicValue,
                out var preservedCarrierTail))
            return false;
        if (!TryCreatePublicAddTarget(addWithResize, out var addTarget))
            return false;

        RewriteSharedFastTail(
            graph,
            rewriteHead,
            headPath,
            stagingBlock,
            slowBlock,
            sharedFastTail,
            merge,
            rewriteStart,
            slowCall,
            preservedCarrierTail,
            addTarget,
            publicReceiver,
            publicValue);
        return true;
    }

    private static bool TryMatchSharedSlowPath(
        Block block,
        out Instruction call,
        out ConcreteGenericMethodAnalysisContext target,
        out IOperand receiver,
        out IOperand value,
        out List<Instruction> carrierTail)
    {
        call = null!;
        target = null!;
        receiver = null!;
        value = null!;
        carrierTail = [];

        var instructions = PatternInstructions(block);
        if (instructions.Count < 2
            || instructions[0] is not
            {
                OpCode: OpCode.CallVoid,
                Operands:
                [
                    ConcreteGenericMethodAnalysisContext method,
                    var candidateReceiver,
                    var candidateValue,
                ],
            } candidateCall
            || method.Name != "AddWithResize"
            || method.BaseMethodContext.DeclaringType?.FullName != "System.Collections.Generic.List`1"
            || candidateReceiver is not (LocalVariable or FieldReference)
            || instructions[^1] is not { OpCode: OpCode.Jump, Operands: [Block] }
            || instructions.Skip(1).Take(instructions.Count - 2).Any(instruction =>
                instruction is not { OpCode: OpCode.Move }))
            return false;

        call = candidateCall;
        target = method;
        receiver = candidateReceiver;
        value = candidateValue;
        carrierTail = instructions.Skip(1).Take(instructions.Count - 2).ToList();
        return true;
    }

    private static bool TryGetSharedFastBlocks(
        Block capacityHead,
        Block slowBlock,
        Block merge,
        HashSet<Block> originalSharedFastTails,
        out Block stagingBlock,
        out Block sharedFastTail)
    {
        stagingBlock = null!;
        sharedFastTail = null!;
        if (capacityHead.Successors.Count != 2)
            return false;

        stagingBlock = capacityHead.Successors.SingleOrDefault(block =>
            !ReferenceEquals(block, slowBlock))!;
        if (stagingBlock == null
            || stagingBlock.Predecessors is not [var stagingPredecessor]
            || !ReferenceEquals(stagingPredecessor, capacityHead)
            || stagingBlock.Successors is not [var candidateTail]
            || !originalSharedFastTails.Contains(candidateTail)
            || candidateTail.Successors is not [var candidateMerge]
            || !ReferenceEquals(candidateMerge, merge))
            return false;

        sharedFastTail = candidateTail;
        return true;
    }

    private static bool TryMatchSharedFastTail(
        Block block,
        Block merge,
        out SharedFastTailPattern pattern)
    {
        pattern = null!;
        var instructions = PatternInstructions(block);
        if (instructions.Count != 9
            || instructions[0] is not
            {
                OpCode: OpCode.And,
                Operands: [LocalVariable masked, var sizeState, Immediate { Value: 0xFFFFFFFFL }],
            }
            || instructions[1] is not
            {
                OpCode: OpCode.Xor,
                Operands: [LocalVariable biased, var maskSource, Immediate { Value: 0x80000000L }],
            }
            || instructions[2] is not
            {
                OpCode: OpCode.Subtract,
                Operands: [LocalVariable normalized, var biasedSource, Immediate { Value: 0x80000000L }],
            }
            || instructions[3] is not
            {
                OpCode: OpCode.ShiftLeft,
                Operands: [LocalVariable elementOffset, var normalizedSource, Immediate scale],
            }
            || instructions[4] is not
            {
                OpCode: OpCode.Add,
                Operands: [LocalVariable elementAddress, var addressLeft, var addressRight],
            }
            || instructions[5] is not
            {
                OpCode: OpCode.Add,
                Operands: [LocalVariable newSize, var sizeAddSource, Immediate { Value: 1 }],
            }
            || instructions[6] is not
            {
                OpCode: OpCode.Move,
                Operands: [MemoryOperand sizeMemory, var sizeWriteSource],
            }
            || instructions[7] is not
            {
                OpCode: OpCode.Move,
                Operands: [MemoryOperand elementMemory, LocalVariable stagedValue],
            }
            || instructions[8] is not { OpCode: OpCode.Jump, Operands: [Block target] }
            || !ReferenceEquals(maskSource, masked)
            || !ReferenceEquals(biasedSource, biased)
            || !ReferenceEquals(normalizedSource, normalized)
            || scale.Value is not (1 or 2 or 3 or 4)
            || !ReferenceEquals(sizeAddSource, sizeState)
            || !ReferenceEquals(sizeWriteSource, newSize)
            || sizeMemory is not { Base: LocalVariable sizeAddress, Index: null, Addend: 0 }
            || elementMemory is not { Base: var storedElementAddress, Index: null, Addend: 0x20 }
            || !ReferenceEquals(storedElementAddress, elementAddress)
            || !ReferenceEquals(target, merge))
            return false;

        LocalVariable items;
        if (ReferenceEquals(addressLeft, elementOffset) && addressRight is LocalVariable rightItems)
            items = rightItems;
        else if (ReferenceEquals(addressRight, elementOffset) && addressLeft is LocalVariable leftItems)
            items = leftItems;
        else
            return false;

        pattern = new SharedFastTailPattern(items, sizeState, sizeAddress, sizeMemory, stagedValue);
        return true;
    }

    private static bool TryMatchSharedCapacityHead(
        Block block,
        Block slowBlock,
        IOperand publicReceiver,
        SharedFastTailPattern pattern)
    {
        var instructions = PatternInstructions(block);
        if (instructions is not
            [
                {
                    OpCode: OpCode.Add,
                    Operands: [var sizeAddress, var addressBase, Immediate { Value: 24 }],
                },
                {
                    OpCode: OpCode.Move,
                    Operands: [var sizeState, MemoryOperand sizeLoad],
                },
                {
                    OpCode: OpCode.CheckGreaterOrEqualUnsigned,
                    Operands: [LocalVariable condition, var checkedSize, ArrayLength length],
                },
                {
                    OpCode: OpCode.ConditionalJump,
                    Operands: [Block target, var branchCondition],
                }
            ]
            || !ReferenceEquals(sizeAddress, pattern.SizeAddress)
            || !AreEquivalentValue(addressBase, publicReceiver)
            || !ReferenceEquals(sizeState, pattern.SizeState)
            || !AreSameMemoryOperand(sizeLoad, pattern.SizeMemory)
            || !(ReferenceEquals(checkedSize, pattern.SizeState)
                 || checkedSize is MemoryOperand checkedMemory
                 && AreSameMemoryOperand(checkedMemory, pattern.SizeMemory))
            || !ReferenceEquals(length.Array, pattern.Items)
            || !ReferenceEquals(condition, branchCondition)
            || !ReferenceEquals(target, slowBlock))
            return false;

        return true;
    }

    private static bool TryMatchSharedStatePrefix(
        Block capacityHead,
        IOperand publicReceiver,
        LocalVariable items,
        out Block rewriteHead,
        out List<Block> headPath,
        out Instruction rewriteStart)
    {
        rewriteHead = null!;
        headPath = [];
        rewriteStart = null!;
        if (capacityHead.Predecessors is not [var itemsBlock]
            || itemsBlock.Predecessors is not [var aliasBlock])
            return false;

        var aliasInstructions = PatternInstructions(aliasBlock);
        var itemsInstructions = PatternInstructions(itemsBlock);
        if (aliasInstructions is not
            [
                { OpCode: OpCode.Move, Operands: [LocalVariable stateReceiver, var receiverSource] } aliasMove,
                { OpCode: OpCode.CheckEqual, Operands: [LocalVariable receiverNull, var checkedReceiver, Immediate { Value: 0 }] },
                { OpCode: OpCode.ConditionalJump, Operands: [Block receiverNullTarget, var receiverNullCondition] },
            ]
            || itemsInstructions is not
            [
                { OpCode: OpCode.Move, Operands: [var loadedItems, FieldReference itemsField] },
                { OpCode: OpCode.Add, Operands: [LocalVariable version, FieldReference versionSource, Immediate { Value: 1 }] },
                { OpCode: OpCode.Move, Operands: [FieldReference versionDestination, var writtenVersion] },
                { OpCode: OpCode.CheckEqual, Operands: [LocalVariable itemsNull, FieldReference checkedItems, Immediate { Value: 0 }] },
                { OpCode: OpCode.ConditionalJump, Operands: [Block itemsNullTarget, var itemsNullCondition] },
            ]
            || !AreEquivalentValue(receiverSource, publicReceiver)
            || !AreEquivalentValue(checkedReceiver, publicReceiver)
            || !ReferenceEquals(receiverNull, receiverNullCondition)
            || !ReferenceEquals(loadedItems, items)
            || !IsField(itemsField, stateReceiver, "_items")
            || !IsField(versionSource, stateReceiver, "_version")
            || !IsField(versionDestination, stateReceiver, "_version")
            || !ReferenceEquals(version, writtenVersion)
            || !IsField(checkedItems, stateReceiver, "_items")
            || !ReferenceEquals(itemsNull, itemsNullCondition)
            || !ReferenceEquals(receiverNullTarget, itemsNullTarget)
            || !ContainsNullReferenceThrow(receiverNullTarget)
            || aliasBlock.Successors.Count != 2
            || !aliasBlock.Successors.Contains(itemsBlock)
            || !aliasBlock.Successors.Contains(receiverNullTarget)
            || itemsBlock.Successors.Count != 2
            || !itemsBlock.Successors.Contains(capacityHead)
            || !itemsBlock.Successors.Contains(itemsNullTarget))
            return false;

        rewriteHead = aliasBlock;
        headPath = [aliasBlock, itemsBlock, capacityHead];
        rewriteStart = aliasMove;
        return true;
    }

    private static bool ContainsNullReferenceThrow(Block block)
        => SemanticInstructions(block).Any(instruction =>
            instruction is { OpCode: OpCode.Throw, Operands: [TypeAnalysisContext type] }
            && type.FullName == "System.NullReferenceException");

    private static bool TryMatchSharedStagingPath(
        ISILControlFlowGraph graph,
        Block stagingBlock,
        Block sharedFastTail,
        Block merge,
        LocalVariable stagedValue,
        IOperand slowValue,
        IReadOnlyList<Instruction> slowCarrierTail,
        ApplicationAnalysisContext appContext,
        out IOperand publicValue,
        out List<Instruction> preservedCarrierTail)
    {
        publicValue = null!;
        preservedCarrierTail = [];
        var instructions = PatternInstructions(stagingBlock);
        var hasExplicitJump = instructions.LastOrDefault() is
            { OpCode: OpCode.Jump, Operands: [Block target] }
            && ReferenceEquals(target, sharedFastTail);
        var stagingMoves = hasExplicitJump
            ? instructions.Take(instructions.Count - 1).ToList()
            : instructions;
        if (stagingMoves.Count < 1
            || stagingMoves.Any(instruction => instruction is not { OpCode: OpCode.Move }))
            return false;

        var valueMoves = stagingMoves.Where(instruction =>
            instruction is { OpCode: OpCode.Move, Operands: [var destination, _] }
            && ReferenceEquals(destination, stagedValue)).ToList();
        if (valueMoves.Count != 1
            || !TryReconcileElementValue(
                graph,
                valueMoves[0].Operands[1],
                slowValue,
                appContext,
                out publicValue))
            return false;

        var fastCarriers = stagingMoves
            .Where(instruction => !ReferenceEquals(instruction, valueMoves[0]))
            .ToList();
        return TryMatchRequiredSharedCarriers(
            fastCarriers,
            slowCarrierTail,
            merge,
            out preservedCarrierTail);
    }

    private static bool TryMatchRequiredSharedCarriers(
        IReadOnlyList<Instruction> fastCarriers,
        IReadOnlyList<Instruction> slowCarriers,
        Block merge,
        out List<Instruction> preservedSlowCarriers)
    {
        preservedSlowCarriers = [];
        if (fastCarriers.Concat(slowCarriers).Any(instruction =>
                instruction is not { OpCode: OpCode.Move, Operands: [LocalVariable, _] }))
            return false;

        var requiredDestinations = fastCarriers.Concat(slowCarriers)
            .Select(instruction => (LocalVariable)instruction.Operands[0])
            .Where(destination => IsReadBeforeDefinitionFromMerge(merge, destination))
            .Distinct()
            .ToList();
        foreach (var destination in requiredDestinations)
        {
            var fast = fastCarriers.Where(instruction =>
                ReferenceEquals(instruction.Operands[0], destination)).ToList();
            var slow = slowCarriers.Where(instruction =>
                ReferenceEquals(instruction.Operands[0], destination)).ToList();
            if (fast.Count != 1
                || slow.Count != 1
                || !AreEquivalentValue(fast[0].Operands[1], slow[0].Operands[1]))
                return false;
            preservedSlowCarriers.Add(slow[0]);
        }

        return true;
    }

    /// <summary>
    /// 判断汇合点之后是否存在“先读后写”路径；后继中只有覆盖写而没有先读时，边载体已经死亡。
    /// </summary>
    private static bool IsReadBeforeDefinitionFromMerge(Block merge, LocalVariable local)
    {
        var pending = new Stack<Block>();
        var visited = new HashSet<Block>();
        pending.Push(merge);

        while (pending.Count > 0)
        {
            var block = pending.Pop();
            if (!visited.Add(block))
                continue;

            var overwritten = false;
            foreach (var instruction in SemanticInstructions(block))
            {
                var destination = instruction.Destination;
                if (instruction.Operands.Any(operand =>
                        !(ReferenceEquals(operand, destination) && ReferenceEquals(operand, local))
                        && ReferencesLocal(operand, local)))
                    return true;

                if (ReferenceEquals(destination, local))
                {
                    overwritten = true;
                    break;
                }

                // Throw/Return 之后附着的原生异常辅助调用不属于同一条托管可达路径。
                if (instruction.OpCode is OpCode.Throw or OpCode.Return)
                {
                    overwritten = true;
                    break;
                }
            }

            if (overwritten)
                continue;
            foreach (var successor in block.Successors)
                pending.Push(successor);
        }

        return false;
    }

    private static bool AreEquivalentValue(IOperand left, IOperand right)
        => ReferenceEquals(left, right)
           || left is Immediate leftImmediate
           && right is Immediate rightImmediate
           && leftImmediate.Value == rightImmediate.Value
           || left is FieldReference leftField
           && right is FieldReference rightField
           && AreSameFieldRead(leftField, rightField)
           || left is MemoryOperand leftMemory
           && right is MemoryOperand rightMemory
           && AreSameMemoryOperand(leftMemory, rightMemory);

    private static bool AreSameMemoryOperand(MemoryOperand left, MemoryOperand right)
        => left.Addend == right.Addend
           && AreEquivalentNullableOperand(left.Base, right.Base)
           && AreEquivalentNullableOperand(left.Index, right.Index);

    private static bool AreEquivalentNullableOperand(IOperand? left, IOperand? right)
        => left == null && right == null
           || left != null && right != null && AreEquivalentValue(left, right);

    private static void RewriteSharedFastTail(
        ISILControlFlowGraph graph,
        Block rewriteHead,
        IReadOnlyList<Block> headPath,
        Block stagingBlock,
        Block slowBlock,
        Block sharedFastTail,
        Block merge,
        Instruction rewriteStart,
        Instruction slowCall,
        IReadOnlyList<Instruction> preservedCarrierTail,
        ConcreteGenericMethodAnalysisContext addTarget,
        IOperand receiver,
        IOperand value)
    {
        var firstRemovedIndex = rewriteHead.Instructions.IndexOf(rewriteStart);
        rewriteHead.Instructions.RemoveRange(firstRemovedIndex, rewriteHead.Instructions.Count - firstRemovedIndex);
        rewriteHead.Instructions.Add(new Instruction(slowCall.Index, OpCode.CallVoid, addTarget, receiver, value));
        rewriteHead.Instructions.AddRange(preservedCarrierTail);

        foreach (var pathBlock in headPath.Skip(1).ToList())
            Detach(graph, pathBlock);
        Detach(graph, stagingBlock);
        Detach(graph, slowBlock);
        if (sharedFastTail.Predecessors.Count == 0)
            Detach(graph, sharedFastTail);

        foreach (var successor in rewriteHead.Successors.ToList())
            successor.Predecessors.Remove(rewriteHead);
        rewriteHead.Successors.Clear();
        rewriteHead.Successors.Add(merge);
        if (!merge.Predecessors.Contains(rewriteHead))
            merge.Predecessors.Add(rewriteHead);
        rewriteHead.CalculateBlockType();
    }

    private sealed record SharedFastTailPattern(
        LocalVariable Items,
        IOperand SizeState,
        LocalVariable SizeAddress,
        MemoryOperand SizeMemory,
        LocalVariable StagedValue);

    /// <summary>
    /// 容量不足分支有时会直接以AddWithResize结束方法，而快速分支跳到同一Return块。
    /// 此时慢块的图后继是Exit，但它的唯一托管Return仍是公开Add应接回的汇合块。
    /// </summary>
    private static bool TryGetMergeBlock(
        ISILControlFlowGraph graph,
        Block slowBlock,
        out Block merge)
    {
        merge = null!;
        if (slowBlock.Successors is not [var successor])
            return false;
        if (!ReferenceEquals(successor, graph.ExitBlock))
        {
            merge = successor;
            return true;
        }

        var slowInstructions = SemanticInstructions(slowBlock);
        if (slowInstructions is not
            [
                { OpCode: OpCode.CallVoid, Operands: [MethodAnalysisContext { Name: "AddWithResize" }, ..] },
                { OpCode: OpCode.Return, Operands.Count: 0 }
            ])
            return false;

        var candidates = graph.ExitBlock.Predecessors.Where(block =>
            !ReferenceEquals(block, slowBlock)
            && SemanticInstructions(block) is
            [{ OpCode: OpCode.Return, Operands.Count: 0 }]).ToList();
        if (candidates.Count != 1)
            return false;

        merge = candidates[0];
        return true;
    }

    private static bool TryMatchSlowPath(
        Block block,
        out Instruction call,
        out ConcreteGenericMethodAnalysisContext target,
        out LocalVariable receiver,
        out IOperand value,
        out List<Instruction> tail)
    {
        call = null!;
        target = null!;
        receiver = null!;
        value = null!;
        tail = [];

        var instructions = SemanticInstructions(block);
        var callIndex = instructions.FindIndex(instruction =>
            instruction is { OpCode: OpCode.CallVoid, Operands.Count: 3 }
            && instruction.Operands[0] is MethodAnalysisContext { Name: "AddWithResize" });
        var trailingInstructions = callIndex < 0
            ? []
            : instructions
                .Skip(callIndex + 1)
                .Where(instruction => !IsIgnorableRuntimeMetadataMove(instruction))
                .ToList();
        if (callIndex < 0
            || instructions.Count(instruction =>
                instruction.IsCall
                && instruction.Operands.FirstOrDefault() is MethodAnalysisContext { Name: "AddWithResize" }) != 1
            || instructions[callIndex] is not { OpCode: OpCode.CallVoid } candidate
            || candidate.Operands.Count != 3
            || candidate.Operands[0] is not ConcreteGenericMethodAnalysisContext method
            || candidate.Operands[1] is not LocalVariable list
            || method.Name != "AddWithResize"
            || method.BaseMethodContext.DeclaringType?.FullName != "System.Collections.Generic.List`1"
            || instructions.Take(callIndex).Any(instruction => !IsIgnorableRuntimeMetadataMove(instruction))
            || !IsValidSlowTail(trailingInstructions))
            return false;

        call = candidate;
        target = method;
        receiver = list;
        value = candidate.Operands[2];
        tail = trailingInstructions
            .Where(instruction => instruction.OpCode != OpCode.Return)
            .ToList();
        return true;
    }

    private static bool IsValidSlowTail(IReadOnlyList<Instruction> instructions)
    {
        if (instructions.All(IsPotentialSlowTailInstruction))
            return true;

        return instructions.Count > 0
               && instructions[^1] is { OpCode: OpCode.Return, Operands.Count: 0 }
               && instructions.Take(instructions.Count - 1)
                   .All(IsPotentialSlowTailInstruction);
    }

    /// <summary>
    /// 慢边预筛选只额外放行下一项版本递增的固定加法；接收者、字段回写和快边等价性稍后统一验证。
    /// </summary>
    private static bool IsPotentialSlowTailInstruction(Instruction instruction)
        => instruction.OpCode == OpCode.Move
           || instruction is
           {
               OpCode: OpCode.Add,
               Operands:
               [
                   LocalVariable,
                   FieldReference { Field.Name: "_version" },
                   Immediate { Value: 1 },
               ],
           };

    private static bool TryMatchHead(
        Block head,
        Block slowBlock,
        LocalVariable receiver,
        out Block rewriteHead,
        out List<Block> headPath,
        out Instruction rewriteStart,
        out LocalVariable items,
        out IOperand sizeState,
        out LocalVariable versionResult,
        out List<Instruction> preservedHeadBusiness)
    {
        rewriteHead = null!;
        headPath = [];
        rewriteStart = null!;
        items = null!;
        sizeState = null!;
        versionResult = null!;
        preservedHeadBusiness = [];

        // 首个 Add 直接读取字段，连续 Add 则会复用上一容量菱形在汇合边写入的
        // size/version 局部载体；ARM64 还允许把 items 读取排在版本加法之后。
        // 逐个扩大后缀窗口，只在全部指令都属于该标准状态机时接受。
        for (var instructionCount = 5; instructionCount <= 32; instructionCount++)
        {
            if (!TryCollectHeadSuffix(head, instructionCount, out var suffix, out var candidatePath))
                continue;
            if (!TryMatchHeadSuffix(
                    suffix,
                    slowBlock,
                    receiver,
                    out _,
                    out var loadedItems,
                    out var candidateSizeState,
                    out var candidateVersionResult,
                    out var candidateBusiness))
                continue;

            rewriteHead = suffix[0].Block;
            var rewriteBlockIndex = candidatePath.IndexOf(rewriteHead);
            headPath = candidatePath.GetRange(
                rewriteBlockIndex,
                candidatePath.Count - rewriteBlockIndex);
            rewriteStart = suffix[0].Instruction;
            items = loadedItems;
            sizeState = candidateSizeState;
            versionResult = candidateVersionResult;
            preservedHeadBusiness = candidateBusiness;
            return true;
        }

        return false;
    }

    private static bool TryMatchHeadSuffix(
        IReadOnlyList<(Block Block, Instruction Instruction)> suffix,
        Block slowBlock,
        LocalVariable receiver,
        out Instruction itemsLoad,
        out LocalVariable items,
        out IOperand sizeState,
        out LocalVariable versionResult,
        out List<Instruction> preservedHeadBusiness)
    {
        itemsLoad = null!;
        items = null!;
        sizeState = null!;
        versionResult = null!;
        preservedHeadBusiness = [];

        if (suffix.Count < 5
            || suffix[^1].Instruction is not
            {
                OpCode: OpCode.ConditionalJump,
                Operands: [Block target, var branchCondition]
            }
            || suffix[^2].Instruction is not
            {
                OpCode: OpCode.CheckGreaterOrEqualUnsigned,
                Operands: [LocalVariable condition, var checkedSize, ArrayLength length]
            }
            || !ReferenceEquals(target, slowBlock)
            || !ReferenceEquals(condition, branchCondition))
            return false;

        var prefix = suffix.Take(suffix.Count - 2).Select(entry => entry.Instruction).ToList();
        var itemLoads = prefix.Where(instruction =>
            instruction is { OpCode: OpCode.Move, Operands: [LocalVariable, FieldReference field] }
            && IsField(field, receiver, "_items")).ToList();
        var versionAdds = prefix.Where(instruction =>
            instruction is
            {
                OpCode: OpCode.Add,
                Operands: [LocalVariable, var versionSource, Immediate { Value: 1 }]
            }
            && IsStateSource(prefix, instruction, versionSource, receiver, "_version")).ToList();
        if (itemLoads.Count != 1
            || versionAdds.Count != 1
            || itemLoads[0].Operands[0] is not LocalVariable loadedItems
            || versionAdds[0].Operands[0] is not LocalVariable version)
            return false;
        var versionSource = versionAdds[0].Operands[1];

        var versionWrites = prefix.Where(instruction =>
            instruction is { OpCode: OpCode.Move, Operands: [FieldReference field, var source] }
            && IsField(field, receiver, "_version")
            && ReferenceEquals(source, version)).ToList();
        if (versionWrites.Count != 1
            || prefix.IndexOf(versionAdds[0]) >= prefix.IndexOf(versionWrites[0])
            || !ReferenceEquals(loadedItems, length.Array)
            || !IsStateSource(prefix, suffix[^2].Instruction, checkedSize, receiver, "_size"))
            return false;

        var allowed = new List<Instruction> { itemLoads[0], versionAdds[0], versionWrites[0] };
        if (!CollectStateLoad(prefix, versionAdds[0], versionSource, receiver, "_version", allowed)
            || !CollectStateLoad(prefix, suffix[^2].Instruction, checkedSize, receiver, "_size", allowed)
            )
            return false;

        var itemsLoadIndex = prefix.IndexOf(itemLoads[0]);
        var versionAddIndex = prefix.IndexOf(versionAdds[0]);
        var business = prefix.Where(instruction => !allowed.Contains(instruction)).ToList();
        if (business.Any(instruction =>
                prefix.IndexOf(instruction) <= itemsLoadIndex
                || prefix.IndexOf(instruction) >= versionAddIndex
                || !IsPreservableHeadBusinessInstruction(
                    instruction,
                    receiver,
                    loadedItems,
                    version,
                    condition)))
            return false;

        itemsLoad = itemLoads[0];
        items = loadedItems;
        sizeState = checkedSize;
        versionResult = version;
        preservedHeadBusiness = business;
        return true;
    }

    /// <summary>
    /// 只搬运发生在 List 状态变更之前的业务计算；它不得读取或写入集合接收者、
    /// 容量载体和分支条件，也不得包含调用或控制流。这样公开 Add 仍位于原业务指令之后，
    /// 同时不会把可能观察集合状态的操作跨过版本与大小更新。
    /// </summary>
    private static bool IsPreservableHeadBusinessInstruction(
        Instruction instruction,
        LocalVariable receiver,
        LocalVariable items,
        LocalVariable version,
        LocalVariable condition)
    {
        if (instruction.IsCall
            || instruction.OpCode is OpCode.Return
                or OpCode.Jump
                or OpCode.IndirectJump
                or OpCode.ConditionalJump
                or OpCode.Throw)
            return false;

        return instruction.Operands.All(operand =>
            !ReferencesLocal(operand, receiver)
            && !ReferencesLocal(operand, items)
            && !ReferencesLocal(operand, version)
            && !ReferencesLocal(operand, condition));
    }

    private static bool ReferencesLocal(IOperand operand, LocalVariable local)
        => ReferenceEquals(operand, local)
           || operand is FieldReference field && ReferenceEquals(field.Local, local)
           || operand is MemoryOperand memory
           && (memory.Base is not null && ReferencesLocal(memory.Base, local)
               || memory.Index is not null && ReferencesLocal(memory.Index, local))
           || operand is ArrayLength length && ReferencesLocal(length.Array, local)
           || operand is ArrayAccess access && ReferencesLocal(access.Array, local);

    private static bool IsStateSource(
        IReadOnlyList<Instruction> instructions,
        Instruction use,
        IOperand source,
        LocalVariable receiver,
        string fieldName)
    {
        if (IsPublicListSize(source, receiver, fieldName))
            return true;
        if (source is FieldReference direct)
            return IsField(direct, receiver, fieldName);
        if (source is not LocalVariable local)
            return false;

        return instructions.Count(instruction =>
            instruction is { OpCode: OpCode.Move, Operands: [var destination, FieldReference field] }
            && ReferenceEquals(destination, local)
            && IsField(field, receiver, fieldName)
            && IsBefore(instructions, instruction, use)) == 1;
    }

    private static bool CollectStateLoad(
        IReadOnlyList<Instruction> instructions,
        Instruction use,
        IOperand source,
        LocalVariable receiver,
        string fieldName,
        ICollection<Instruction> allowed)
    {
        if (IsPublicListSize(source, receiver, fieldName))
            return true;
        if (source is FieldReference direct)
            return IsField(direct, receiver, fieldName);
        if (source is not LocalVariable local)
            return false;

        var loads = instructions.Where(instruction =>
            instruction is { OpCode: OpCode.Move, Operands: [var destination, FieldReference field] }
            && ReferenceEquals(destination, local)
            && IsField(field, receiver, fieldName)
            && IsBefore(instructions, instruction, use)).ToList();
        if (loads.Count != 1)
            return false;
        allowed.Add(loads[0]);
        return true;
    }

    private static bool TryCollectHeadSuffix(
        Block head,
        int instructionCount,
        out List<(Block Block, Instruction Instruction)> suffix,
        out List<Block> path)
    {
        var reversePath = new List<Block>();
        var semanticCount = 0;
        var current = head;

        while (true)
        {
            reversePath.Add(current);
            semanticCount += PatternInstructions(current).Count;
            if (semanticCount >= instructionCount)
                break;

            if (current.Predecessors is not [var predecessor]
                || predecessor.Successors is not [var successor]
                || !ReferenceEquals(successor, current)
                || predecessor.BlockType is BlockType.Entry or BlockType.Exit)
            {
                suffix = [];
                path = [];
                return false;
            }

            current = predecessor;
        }

        reversePath.Reverse();
        path = reversePath;
        var entries = path
            .SelectMany(block => PatternInstructions(block).Select(instruction => (block, instruction)))
            .ToList();
        suffix = entries.GetRange(entries.Count - instructionCount, instructionCount);
        return true;
    }

    private static bool TryGetFastBlock(
        Block head,
        Block slowBlock,
        Block merge,
        out Block fastBlock)
    {
        fastBlock = null!;
        if (head.Successors.Count != 2)
            return false;

        fastBlock = head.Successors.SingleOrDefault(block => !ReferenceEquals(block, slowBlock))!;
        return fastBlock != null
               && fastBlock.Predecessors is [var predecessor]
               && ReferenceEquals(predecessor, head)
               && fastBlock.Successors is [var successor]
               && ReferenceEquals(successor, merge);
    }

    private static bool TryMatchFastPath(
        ISILControlFlowGraph graph,
        Block block,
        Block merge,
        LocalVariable receiver,
        LocalVariable items,
        IOperand sizeState,
        LocalVariable versionResult,
        IOperand value,
        IReadOnlyList<Instruction> slowTail,
        ApplicationAnalysisContext appContext,
        out IOperand publicValue,
        out List<Instruction> preservedSlowTail)
    {
        publicValue = null!;
        preservedSlowTail = [];
        var instructions = PatternInstructions(block);
        if (instructions.Count < 6
            || instructions[^1] is not { OpCode: OpCode.Jump, Operands: [Block target] }
            || !ReferenceEquals(target, merge))
            return false;

        var stores = instructions.Where(instruction =>
            instruction is { OpCode: OpCode.Move, Operands: [MemoryOperand, _] }).ToList();
        var sizeAdds = instructions.Where(instruction =>
            instruction is { OpCode: OpCode.Add, Operands: [LocalVariable, var source, Immediate { Value: 1 }] }
            && IsSameStateOperand(source, sizeState, receiver, "_size")).ToList();
        if (stores.Count != 1
            || sizeAdds.Count != 1
            || stores[0].Operands[0] is not MemoryOperand memory
            || sizeAdds[0].Operands[0] is not LocalVariable newSize)
            return false;
        if (!TryReconcileElementValue(
                graph,
                stores[0].Operands[1],
                value,
                appContext,
                out publicValue))
            return false;

        var sizeWrites = instructions.Where(instruction =>
            instruction is { OpCode: OpCode.Move, Operands: [FieldReference field, var source] }
            && IsField(field, receiver, "_size")
            && ReferenceEquals(source, newSize)).ToList();
        var scales = instructions.Where(instruction =>
            instruction is
            {
                OpCode: OpCode.ShiftLeft or OpCode.Multiply,
                Operands: [LocalVariable, _, Immediate]
            }).ToList();
        if (sizeWrites.Count != 1
            || scales.Count != 1
            || scales[0].Operands[0] is not LocalVariable elementOffset)
            return false;
        var scaleSource = scales[0].Operands[1];

        var addresses = instructions.Where(instruction =>
            instruction is { OpCode: OpCode.Add, Operands: [LocalVariable destination, var left, var right] }
            && ReferenceEquals(destination, memory.Base)
            && ((IsItemsAddressBase(left, receiver, items) && ReferenceEquals(right, elementOffset))
                || (IsItemsAddressBase(right, receiver, items) && ReferenceEquals(left, elementOffset)))).ToList();
        if (addresses.Count != 1)
            return false;

        var allowed = new List<Instruction>
        {
            stores[0],
            sizeAdds[0],
            sizeWrites[0],
            scales[0],
            addresses[0],
            instructions[^1],
        };
        if (!IsSameStateOperand(scaleSource, sizeState, receiver, "_size")
            && !CollectUInt32IndexNormalization(
                instructions,
                scales[0],
                receiver,
                sizeState,
                scaleSource,
                allowed))
            return false;

        var slowStateRefreshes = slowTail.Where(instruction =>
            IsStateRefresh(instruction, receiver, "_size", newSize)
            || IsStateRefresh(instruction, receiver, "_version", versionResult)).ToList();
        if (slowTail.Any(instruction =>
                instruction is { OpCode: OpCode.Move, Operands: [_, FieldReference field] }
                && IsField(field, receiver, "_size")
                && !IsStateRefresh(instruction, receiver, "_size", newSize)
                || instruction is { OpCode: OpCode.Move, Operands: [_, FieldReference versionField] }
                && IsField(versionField, receiver, "_version")
                && !IsStateRefresh(instruction, receiver, "_version", versionResult)))
            return false;

        var fastTail = instructions.Where(instruction => !allowed.Contains(instruction)).ToList();
        // 连续内联 Add 会在汇合块继续消费上一菱形的 size/version 载体。公开 Add 已经完成
        // 集合状态更新，因此只有在后续仍引用载体时，才把慢路径的字段回读搬到公开调用之后；
        // 最后一项追加没有后续引用，仍删除全部私有状态刷新。
        var requiredStateRefreshes = slowStateRefreshes.Where(instruction =>
            instruction.Destination is LocalVariable destination
            && IsReferencedFromMerge(merge, destination)).ToHashSet();
        preservedSlowTail = slowTail.Where(instruction =>
            !slowStateRefreshes.Contains(instruction)
            || requiredStateRefreshes.Contains(instruction)).ToList();
        var callClobberRefreshes = preservedSlowTail.Where(instruction =>
            IsStateRefresh(instruction, receiver, "_items", items)
            || IsRedundantCallClobberRefresh(graph, instruction)).ToList();
        var comparableSlowTail = preservedSlowTail.Where(instruction =>
            !requiredStateRefreshes.Contains(instruction)
            && !callClobberRefreshes.Contains(instruction)).ToList();
        if (!TryCollectNextVersionAdvance(fastTail, receiver, out var fastVersionAdvance)
            || !TryCollectNextVersionAdvance(comparableSlowTail, receiver, out var slowVersionAdvance)
            || fastVersionAdvance.Count != slowVersionAdvance.Count)
            return false;

        // 连续内联 Add 还会把下一项的 _version++ 分别排到当前快慢边末尾。两边的 SSA
        // 结果局部不同，但都严格读取并回写同一集合版本字段；保留慢边的一份供下一菱形匹配，
        // 当前尾部比较则排除这组等价预更新。下一项提升为公开 Add 时会一并删除这份预更新。
        var comparableFastTail = fastTail.Where(instruction =>
            !fastVersionAdvance.Contains(instruction)
            && !IsDeadMistypedRuntimeMethodCarrier(instruction, merge)).ToList();
        comparableSlowTail = comparableSlowTail.Where(instruction =>
            !slowVersionAdvance.Contains(instruction)
            && !IsDeadMistypedRuntimeMethodCarrier(instruction, merge)).ToList();
        return HaveIdenticalCarrierMoves(comparableFastTail, comparableSlowTail);
    }

    /// <summary>
    /// ARM64 隐藏方法参数可能被类型传播误挂到通用调用寄存器局部；仅当该值在汇合点后
    /// 被覆盖前从未读取时，才把它视为死亡的原生方法句柄载体。
    /// </summary>
    private static bool IsDeadMistypedRuntimeMethodCarrier(Instruction instruction, Block merge)
        => instruction is
           {
               OpCode: OpCode.Move,
               Operands: [LocalVariable destination, RuntimeMethodInfoAnalysisContext],
           }
           && destination.Type is not RuntimeMethodInfoAnalysisContext
           && !IsReadBeforeDefinitionFromMerge(merge, destination);

    /// <summary>
    /// 收集快慢边末尾为下一次 List.Add 预排的唯一版本递增；没有该尾部也属于有效终项。
    /// </summary>
    private static bool TryCollectNextVersionAdvance(
        IReadOnlyList<Instruction> instructions,
        LocalVariable receiver,
        out List<Instruction> advance)
    {
        advance = [];
        for (var index = 0; index + 1 < instructions.Count; index++)
        {
            if (instructions[index] is not
                {
                    OpCode: OpCode.Add,
                    Operands:
                    [
                        LocalVariable version,
                        FieldReference source,
                        Immediate { Value: 1 },
                    ],
                }
                || !IsField(source, receiver, "_version")
                || instructions[index + 1] is not
                {
                    OpCode: OpCode.Move,
                    Operands: [FieldReference destination, var writtenVersion],
                }
                || !IsField(destination, receiver, "_version")
                || !ReferenceEquals(version, writtenVersion))
                continue;

            if (advance.Count != 0)
                return false;
            advance.Add(instructions[index]);
            advance.Add(instructions[index + 1]);
        }

        return true;
    }

    /// <summary>
    /// 判断容量菱形的汇合点及其后继是否继续使用指定状态载体。
    /// </summary>
    private static bool IsReferencedFromMerge(Block merge, LocalVariable local)
    {
        var pending = new Stack<Block>();
        var visited = new HashSet<Block>();
        pending.Push(merge);

        while (pending.Count > 0)
        {
            var block = pending.Pop();
            if (!visited.Add(block))
                continue;
            if (SemanticInstructions(block).Any(instruction =>
                    instruction.Operands.Any(operand => ReferencesLocal(operand, local))))
                return true;

            foreach (var successor in block.Successors)
                pending.Push(successor);
        }

        return false;
    }

    /// <summary>
    /// 统一快速路径的整元素常量写入与慢路径的 HFA 参数。
    /// </summary>
    /// <remarks>
    /// ARM64 会把 <c>Vector2</c> 等 HFA 在快速路径打包成一次 D 寄存器常量写入，慢路径则按
    /// V0/V1 分量调用 <c>AddWithResize</c>。后期死码清理可能只留下未定义的 ABI 分量局部变量；
    /// 此时必须从同一快速路径的只读常量恢复完整值，不能把两个默认浮点数写入公开 Add。
    /// </remarks>
    private static bool TryReconcileElementValue(
        ISILControlFlowGraph graph,
        IOperand fastValue,
        IOperand slowValue,
        ApplicationAnalysisContext appContext,
        out IOperand publicValue)
    {
        publicValue = null!;
        if (ReferenceEquals(fastValue, slowValue))
        {
            publicValue = slowValue;
            return true;
        }

        // ARM64 会为快速路径的数组写入和慢路径的 AddWithResize 实参分别创建 Immediate。
        // 两个装箱实例的引用身份不同，但数值位完全相同时仍是同一个托管常量；数值不同则保留原菱形。
        if (fastValue is Immediate fastImmediate
            && slowValue is Immediate slowImmediate
            && fastImmediate.Value == slowImmediate.Value)
        {
            publicValue = slowValue;
            return true;
        }

        // 字段解析会为快路径数组写入与慢路径AddWithResize实参分别构造FieldReference。
        // 只有字段元数据、接收者局部量与原生偏移三者完全相同，才把它们视为同一次托管字段读取。
        if (fastValue is FieldReference fastField
            && slowValue is FieldReference slowField
            && AreSameFieldRead(fastField, slowField))
        {
            publicValue = slowValue;
            return true;
        }

        if (fastValue is not MemoryOperand { IsConstant: true, Addend: > 0 } packed
            || slowValue is not HomogeneousFloatingAggregateArgument aggregate
            || !TryDecodePackedHfaConstant(appContext, packed, aggregate, out var decoded))
            return false;

        var unresolvedAbiComponents = aggregate.Components.Select((component, index) => (component, index)).All(entry =>
            entry.component is LocalVariable local
            && local.Register.Name == $"V{entry.index}"
            && graph.Instructions.All(instruction => !ReferenceEquals(instruction.Destination, local)));
        if (!unresolvedAbiComponents && !HaveSameFloatingBits(aggregate.Components, decoded.Components))
            return false;

        publicValue = decoded;
        return true;
    }

    /// <summary>
    /// 识别AddWithResize对原生调用者保存寄存器造成的字段载体重载。
    /// 两次读取之间只允许出现这一处集合扩容调用，避免吞掉真正的字段更新。
    /// </summary>
    private static bool IsRedundantCallClobberRefresh(
        ISILControlFlowGraph graph,
        Instruction refresh)
    {
        if (refresh is not
            {
                Index: >= 0,
                OpCode: OpCode.Move,
                Operands: [LocalVariable destination, FieldReference field],
            })
            return false;

        foreach (var candidate in graph.Instructions)
        {
            if (ReferenceEquals(candidate, refresh)
                || candidate.Index < 0
                || candidate.Index >= refresh.Index
                || candidate is not
                {
                    OpCode: OpCode.Move,
                    Operands: [var priorDestination, FieldReference priorField],
                }
                || !ReferenceEquals(priorDestination, destination)
                || !AreSameFieldRead(priorField, field))
                continue;

            var interveningCalls = graph.Instructions.Where(instruction =>
                instruction.Index > candidate.Index
                && instruction.Index < refresh.Index
                && instruction.IsCall).ToList();
            if (interveningCalls is
                [
                    {
                        OpCode: OpCode.CallVoid,
                        Operands: [MethodAnalysisContext { Name: "AddWithResize" }, ..],
                    },
                ])
                return true;
        }

        return false;
    }

    private static bool AreSameFieldRead(FieldReference left, FieldReference right)
        => ReferenceEquals(left.Field, right.Field)
           && ReferenceEquals(left.Local, right.Local)
           && left.Offset == right.Offset;

    private static bool TryDecodePackedHfaConstant(
        ApplicationAnalysisContext appContext,
        MemoryOperand packed,
        HomogeneousFloatingAggregateArgument aggregate,
        out HomogeneousFloatingAggregateArgument decoded)
    {
        decoded = null!;
        if (!Arm64CallingConventionResolver.TryGetHomogeneousFloatingAggregateFields(
                aggregate.AggregateType,
                out var fields)
            || fields.Count != aggregate.Components.Count
            || fields.Count is < 1 or > 4)
            return false;

        var isSingle = fields.All(field => field.FieldType.FullName == "System.Single");
        var isDouble = fields.All(field => field.FieldType.FullName == "System.Double");
        if (!isSingle && !isDouble)
            return false;

        var elementSize = isSingle ? sizeof(float) : sizeof(double);
        var byteCount = checked(fields.Count * elementSize);
        try
        {
            if (!appContext.Binary.TryMapVirtualAddressToRaw(unchecked((ulong)packed.Addend), out var rawAddress))
                return false;
            var bytes = appContext.Binary.Reader.ReadByteArrayAtRawAddress(rawAddress, byteCount);
            var components = new List<IOperand>(fields.Count);
            for (var index = 0; index < fields.Count; index++)
            {
                var offset = index * elementSize;
                components.Add(isSingle
                    ? new FloatLiteral(BitConverter.ToSingle(bytes, offset))
                    : new DoubleLiteral(BitConverter.ToDouble(bytes, offset)));
            }

            decoded = new HomogeneousFloatingAggregateArgument(aggregate.AggregateType, components);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool HaveSameFloatingBits(
        IReadOnlyList<IOperand> left,
        IReadOnlyList<IOperand> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var index = 0; index < left.Count; index++)
        {
            var same = left[index] switch
            {
                FloatLiteral leftFloat when right[index] is FloatLiteral rightFloat =>
                    BitConverter.SingleToInt32Bits(leftFloat.Value) == BitConverter.SingleToInt32Bits(rightFloat.Value),
                DoubleLiteral leftDouble when right[index] is DoubleLiteral rightDouble =>
                    BitConverter.DoubleToInt64Bits(leftDouble.Value) == BitConverter.DoubleToInt64Bits(rightDouble.Value),
                _ => false,
            };
            if (!same)
                return false;
        }

        return true;
    }

    private static bool CollectUInt32IndexNormalization(
        IReadOnlyList<Instruction> instructions,
        Instruction scale,
        LocalVariable receiver,
        IOperand sizeState,
        IOperand scaleSource,
        ICollection<Instruction> allowed)
    {
        var scaleIndex = IndexOf(instructions, scale);
        if (scaleIndex < 3
            || instructions[scaleIndex - 3] is not
            {
                OpCode: OpCode.And,
                Operands: [LocalVariable masked, var maskSource, Immediate { Value: 0xFFFFFFFFL }]
            } and
            || instructions[scaleIndex - 2] is not
            {
                OpCode: OpCode.Xor,
                Operands: [LocalVariable biased, var xorSource, Immediate { Value: 0x80000000L }]
            } xor
            || instructions[scaleIndex - 1] is not
            {
                OpCode: OpCode.Subtract,
                Operands: [LocalVariable normalized, var subtractSource, Immediate { Value: 0x80000000L }]
            } subtract
            || !IsSameStateOperand(maskSource, sizeState, receiver, "_size")
            || !ReferenceEquals(xorSource, masked)
            || !ReferenceEquals(subtractSource, biased)
            || !ReferenceEquals(scaleSource, normalized))
            return false;

        allowed.Add(and);
        allowed.Add(xor);
        allowed.Add(subtract);
        return true;
    }

    private static int IndexOf(IReadOnlyList<Instruction> instructions, Instruction target)
    {
        for (var index = 0; index < instructions.Count; index++)
        {
            if (ReferenceEquals(instructions[index], target))
                return index;
        }

        return -1;
    }

    private static bool IsBefore(
        IReadOnlyList<Instruction> instructions,
        Instruction candidate,
        Instruction use)
    {
        var candidateIndex = IndexOf(instructions, candidate);
        var useIndex = IndexOf(instructions, use);
        return candidateIndex >= 0 && candidateIndex < (useIndex >= 0 ? useIndex : instructions.Count);
    }

    private static bool IsStateRefresh(
        Instruction instruction,
        LocalVariable receiver,
        string fieldName,
        LocalVariable destination)
        => instruction is { OpCode: OpCode.Move, Operands: [var target, FieldReference field] }
           && ReferenceEquals(target, destination)
           && IsField(field, receiver, fieldName);

    private static bool IsSameStateOperand(
        IOperand left,
        IOperand right,
        LocalVariable receiver,
        string fieldName)
        => ReferenceEquals(left, right)
           || IsDirectStateOperand(left, receiver, fieldName)
           && IsDirectStateOperand(right, receiver, fieldName);

    /// <summary>
    /// 判断操作数是否直接读取同一集合状态；<see cref="ListCount"/> 只等价于具体
    /// <c>List&lt;T&gt;</c> 接收者的 <c>_size</c> 读取，不参与版本或数组字段匹配。
    /// </summary>
    private static bool IsDirectStateOperand(
        IOperand operand,
        LocalVariable receiver,
        string fieldName)
        => operand is FieldReference field && IsField(field, receiver, fieldName)
           || IsPublicListSize(operand, receiver, fieldName);

    private static bool IsPublicListSize(
        IOperand operand,
        LocalVariable receiver,
        string fieldName)
        => fieldName == "_size"
           && operand is ListCount count
           && ReferenceEquals(count.Value, receiver)
           && string.Equals(count.ListType.FullName, receiver.Type?.FullName, StringComparison.Ordinal);

    private static bool TryCreatePublicAddTarget(
        ConcreteGenericMethodAnalysisContext addWithResize,
        out ConcreteGenericMethodAnalysisContext addTarget)
    {
        addTarget = null!;
        var listDefinition = addWithResize.BaseMethodContext.DeclaringType;
        var add = listDefinition?.Methods.SingleOrDefault(method =>
            method.Name == "Add"
            && !method.IsStatic
            && method.Parameters.Count == 1);
        if (add == null)
            return false;

        addTarget = new ConcreteGenericMethodAnalysisContext(add, addWithResize.TypeGenericParameters, []);
        return true;
    }

    private static void Rewrite(
        ISILControlFlowGraph graph,
        Block rewriteHead,
        List<Block> headPath,
        Block fastBlock,
        Block slowBlock,
        Block merge,
        Instruction rewriteStart,
        Instruction slowCall,
        IReadOnlyList<Instruction> slowTail,
        IReadOnlyList<Instruction> preservedHeadBusiness,
        ConcreteGenericMethodAnalysisContext addTarget,
        LocalVariable receiver,
        IOperand value)
    {
        var firstRemovedIndex = rewriteHead.Instructions.IndexOf(rewriteStart);
        rewriteHead.Instructions.RemoveRange(firstRemovedIndex, rewriteHead.Instructions.Count - firstRemovedIndex);
        rewriteHead.Instructions.AddRange(preservedHeadBusiness);
        rewriteHead.Instructions.Add(new Instruction(slowCall.Index, OpCode.CallVoid, addTarget, receiver, value));
        rewriteHead.Instructions.AddRange(slowTail);

        foreach (var pathBlock in headPath.Skip(1).ToList())
            Detach(graph, pathBlock);
        Detach(graph, fastBlock);
        Detach(graph, slowBlock);

        rewriteHead.Successors.Clear();
        rewriteHead.Successors.Add(merge);
        if (!merge.Predecessors.Contains(rewriteHead))
            merge.Predecessors.Add(rewriteHead);
        rewriteHead.CalculateBlockType();
    }

    private static void Detach(ISILControlFlowGraph graph, Block block)
    {
        foreach (var predecessor in block.Predecessors.ToList())
            predecessor.Successors.Remove(block);
        foreach (var successor in block.Successors.ToList())
            successor.Predecessors.Remove(block);
        block.Predecessors.Clear();
        block.Successors.Clear();
        graph.Blocks.Remove(block);
    }

    private static List<Instruction> SemanticInstructions(Block block)
        => block.Instructions.Where(instruction => instruction.OpCode != OpCode.Nop).ToList();

    private static List<Instruction> PatternInstructions(Block block)
        => SemanticInstructions(block)
            .Where(instruction => !IsIgnorableRuntimeMetadataMove(instruction))
            .ToList();

    private static bool IsField(FieldReference field, LocalVariable receiver, string name)
        => ReferenceEquals(field.Local, receiver) && field.Field.Name == name;

    private static bool IsItemsAddressBase(IOperand operand, LocalVariable receiver, LocalVariable loadedItems)
        => ReferenceEquals(operand, loadedItems)
           || operand is FieldReference field && IsField(field, receiver, "_items");

    // 泛型List内联会夹带methodof、Il2CppClass和rgctx句柄装载；调用目标已经解析后，
    // 这些值只服务原生隐藏参数，不属于公开托管Add的可观察语义。
    private static bool IsIgnorableRuntimeMetadataMove(Instruction instruction)
        => instruction is { OpCode: OpCode.Move, Operands: [LocalVariable destination, var source] }
           && destination.Type is RuntimeClassTypeAnalysisContext
               or RgctxTableTypeAnalysisContext
               or RuntimeMethodInfoAnalysisContext
           && source is MemoryOperand
               or RuntimeMethodInfoAnalysisContext
               or RuntimeClassTypeAnalysisContext
               or RgctxTableTypeAnalysisContext
               or Immediate;

    private static bool HaveIdenticalCarrierMoves(
        IReadOnlyList<Instruction> fastTail,
        IReadOnlyList<Instruction> slowTail)
    {
        if (fastTail.Count != slowTail.Count)
            return false;

        for (var index = 0; index < fastTail.Count; index++)
        {
            if (fastTail[index] is not { OpCode: OpCode.Move, Operands: [var fastDestination, var fastSource] }
                || slowTail[index] is not { OpCode: OpCode.Move, Operands: [var slowDestination, var slowSource] }
                || !ReferenceEquals(fastDestination, slowDestination)
                || !ReferenceEquals(fastSource, slowSource))
                return false;
        }

        return true;
    }

}
