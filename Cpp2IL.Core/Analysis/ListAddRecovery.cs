using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

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

        foreach (var slowBlock in graph.Blocks.ToList())
        {
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
            if (slowBlock.Successors is not [var merge])
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
                    fastBlock,
                    merge,
                    receiver,
                    items,
                    sizeState,
                    versionResult,
                    value,
                    slowTail))
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
                slowTail,
                preservedHeadBusiness,
                addTarget,
                receiver,
                value);
            recovered++;
        }

        return recovered;
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
            || instructions.Skip(callIndex + 1).Any(instruction => instruction.OpCode != OpCode.Move))
            return false;

        call = candidate;
        target = method;
        receiver = list;
        value = candidate.Operands[2];
        tail = instructions
            .Skip(callIndex + 1)
            .Where(instruction => !IsIgnorableRuntimeMetadataMove(instruction))
            .ToList();
        return true;
    }

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
        Block block,
        Block merge,
        LocalVariable receiver,
        LocalVariable items,
        IOperand sizeState,
        LocalVariable versionResult,
        IOperand value,
        IReadOnlyList<Instruction> slowTail)
    {
        var instructions = PatternInstructions(block);
        if (instructions.Count < 6
            || instructions[^1] is not { OpCode: OpCode.Jump, Operands: [Block target] }
            || !ReferenceEquals(target, merge))
            return false;

        var stores = instructions.Where(instruction =>
            instruction is { OpCode: OpCode.Move, Operands: [MemoryOperand, var storedValue] }
            && ReferenceEquals(storedValue, value)).ToList();
        var sizeAdds = instructions.Where(instruction =>
            instruction is { OpCode: OpCode.Add, Operands: [LocalVariable, var source, Immediate { Value: 1 }] }
            && IsSameStateOperand(source, sizeState, receiver, "_size")).ToList();
        if (stores.Count != 1
            || sizeAdds.Count != 1
            || stores[0].Operands[0] is not MemoryOperand memory
            || sizeAdds[0].Operands[0] is not LocalVariable newSize)
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
        var slowBusinessTail = slowTail.Where(instruction => !slowStateRefreshes.Contains(instruction)).ToList();
        return HaveIdenticalCarrierMoves(fastTail, slowBusinessTail);
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
           || left is FieldReference leftField
           && right is FieldReference rightField
           && IsField(leftField, receiver, fieldName)
           && IsField(rightField, receiver, fieldName);

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
