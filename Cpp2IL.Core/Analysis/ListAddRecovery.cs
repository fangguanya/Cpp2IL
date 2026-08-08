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
                    out var itemsLoad,
                    out var items))
                continue;
            if (!TryGetFastBlock(head, slowBlock, merge, out var fastBlock))
                continue;
            if (!TryMatchFastPath(fastBlock, merge, receiver, items, value, slowTail))
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
                itemsLoad,
                slowCall,
                slowTail,
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
        out Instruction itemsLoad,
        out LocalVariable items)
    {
        rewriteHead = null!;
        headPath = [];
        itemsLoad = null!;
        items = null!;

        if (!TryCollectHeadSuffix(head, 5, out var suffix, out headPath))
            return false;

        if (suffix is not
            [
                (_, { OpCode: OpCode.Move, Operands: [LocalVariable loadedItems, FieldReference itemsField] } load),
                (_, { OpCode: OpCode.Add, Operands: [LocalVariable version, var versionSource, Immediate { Value: 1 }] }),
                (_, { OpCode: OpCode.Move, Operands: [FieldReference versionDestination, var versionValue] }),
                (_, { OpCode: OpCode.CheckGreaterOrEqualUnsigned, Operands: [LocalVariable condition, FieldReference sizeField, ArrayLength length] }),
                (_, { OpCode: OpCode.ConditionalJump, Operands: [Block target, var branchCondition] })
            ]
            || !ReferenceEquals(target, slowBlock)
            || !ReferenceEquals(version, versionValue)
            || !ReferenceEquals(condition, branchCondition)
            || !ReferenceEquals(loadedItems, length.Array)
            || !IsField(itemsField, receiver, "_items")
            || versionSource is not FieldReference versionField
            || !IsField(versionField, receiver, "_version")
            || !IsField(versionDestination, receiver, "_version")
            || !IsField(sizeField, receiver, "_size"))
            return false;

        rewriteHead = suffix[0].Block;
        var rewriteStart = headPath.IndexOf(rewriteHead);
        headPath = headPath.GetRange(rewriteStart, headPath.Count - rewriteStart);
        itemsLoad = load;
        items = loadedItems;
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
        IOperand value,
        IReadOnlyList<Instruction> slowTail)
    {
        var instructions = SemanticInstructions(block);
        if (instructions.Count < 6
            || instructions[0] is not { Operands: [LocalVariable elementOffset, var sizeForOffset, Immediate] } scale
            || instructions[1] is not { OpCode: OpCode.Add, Operands: [LocalVariable elementAddress, var addressLeft, var addressRight] }
            || instructions[2] is not { OpCode: OpCode.Add, Operands: [LocalVariable newSize, var sizeSource, Immediate { Value: 1 }] }
            || instructions[3] is not { OpCode: OpCode.Move, Operands: [FieldReference sizeDestination, var sizeValue] }
            || instructions[4] is not { OpCode: OpCode.Move, Operands: [MemoryOperand memory, var storedValue] }
            || instructions[^1] is not { OpCode: OpCode.Jump, Operands: [Block target] }
            || scale.OpCode is not (OpCode.ShiftLeft or OpCode.Multiply)
            || sizeForOffset is not FieldReference offsetSize
            || sizeSource is not FieldReference incrementSize
            || !IsField(offsetSize, receiver, "_size")
            || !IsField(incrementSize, receiver, "_size")
            || !IsField(sizeDestination, receiver, "_size")
            || !ReferenceEquals(newSize, sizeValue)
            || !ReferenceEquals(elementAddress, memory.Base)
            || !ReferenceEquals(storedValue, value)
            || !ReferenceEquals(target, merge)
            || !HaveIdenticalCarrierMoves(
                instructions
                    .GetRange(5, instructions.Count - 6)
                    .Where(instruction => !IsIgnorableRuntimeMetadataMove(instruction))
                    .ToList(),
                slowTail))
            return false;

        return (IsItemsAddressBase(addressLeft, receiver, items) && ReferenceEquals(addressRight, elementOffset))
               || (IsItemsAddressBase(addressRight, receiver, items) && ReferenceEquals(addressLeft, elementOffset));
    }

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
        Instruction itemsLoad,
        Instruction slowCall,
        IReadOnlyList<Instruction> slowTail,
        ConcreteGenericMethodAnalysisContext addTarget,
        LocalVariable receiver,
        IOperand value)
    {
        var firstRemovedIndex = rewriteHead.Instructions.IndexOf(itemsLoad);
        rewriteHead.Instructions.RemoveRange(firstRemovedIndex, rewriteHead.Instructions.Count - firstRemovedIndex);
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
