using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 将 IL2CPP 展开的 <c>List&lt;T&gt;.Clear</c> 实现恢复为公开托管调用。
/// </summary>
/// <remarks>
/// 原生实现会同时读取旧 <c>_size</c>、递增 <c>_version</c>、把 <c>_size</c> 清零，
/// 并仅在旧大小大于零时调用 <c>Array.Clear</c>。字段不是 SSA 值，直接输出会把清零后的
/// <c>_size</c> 错当成旧大小。本恢复器要求版本递增、大小清零、条件分支和数组清理完整闭合，
/// 任一字段、接收者、常量或控制流不一致时都保留原图。
/// </remarks>
public static class ListClearRecovery
{
    public static int Run(MethodAnalysisContext method)
    {
        var graph = method.ControlFlowGraph!;
        var recovered = 0;

        foreach (var clearBlock in graph.Blocks.ToList())
        {
            if (!TryMatchClearBlock(
                    clearBlock,
                    out var clearCall,
                    out var receiver,
                    out var clearPath,
                    out var merge))
                continue;
            if (clearBlock.Predecessors is not [var head])
                continue;
            if (!TryMatchHead(
                    head,
                    clearBlock,
                    merge,
                    receiver,
                    out var rewriteHead,
                    out var headPath,
                    out var firstRemoved))
                continue;
            if (!TryCreatePublicClearTarget(receiver, out var clearTarget))
                continue;

            Rewrite(
                graph,
                rewriteHead,
                headPath,
                clearPath,
                merge,
                firstRemoved,
                clearCall,
                clearTarget,
                receiver);
            recovered++;
        }

        return recovered;
    }

    private static bool TryMatchClearBlock(
        Block block,
        out Instruction call,
        out LocalVariable receiver,
        out List<Block> clearPath,
        out Block merge)
    {
        call = null!;
        receiver = null!;
        clearPath = [];
        merge = null!;

        if (block.Successors is not [var successor])
            return false;

        var instructions = SemanticInstructions(block);
        if (instructions is not [{ OpCode: OpCode.CallVoid, Operands.Count: 4 } candidate]
            || candidate.Operands[0] is not MethodAnalysisContext
            {
                Name: "Clear",
                DeclaringType.FullName: "System.Array"
            }
            || candidate.Operands[1] is not FieldReference { Field.Name: "_items" } items
            || candidate.Operands[2] is not Immediate { Value: 0 }
            || candidate.Operands[3] is not FieldReference { Field.Name: "_size" } size
            || !ReferenceEquals(items.Local, size.Local))
            return false;

        clearPath.Add(block);
        if (successor.Predecessors is [var predecessor]
            && ReferenceEquals(predecessor, block)
            && successor.Successors is [var forwarded]
            && SemanticInstructions(successor) is
                [{ OpCode: OpCode.Jump, Operands: [Block target] }]
            && ReferenceEquals(target, forwarded))
        {
            clearPath.Add(successor);
            successor = forwarded;
        }

        call = candidate;
        receiver = items.Local;
        merge = successor;
        return true;
    }

    private static bool TryMatchHead(
        Block head,
        Block clearBlock,
        Block merge,
        LocalVariable receiver,
        out Block rewriteHead,
        out List<Block> headPath,
        out Instruction firstRemoved)
    {
        rewriteHead = null!;
        headPath = [];
        firstRemoved = null!;

        if (head.Successors.Count != 2
            || !head.Successors.Contains(clearBlock)
            || !head.Successors.Contains(merge)
            || !TryCollectHeadSuffix(head, 5, out var suffix, out headPath))
            return false;

        if (suffix is not
            [
                (_, { OpCode: OpCode.Add, Operands: [LocalVariable version, FieldReference versionSource, Immediate { Value: 1 }] } add),
                (_, { OpCode: OpCode.Move, Operands: [FieldReference sizeDestination, var zero] }),
                (_, { OpCode: OpCode.Move, Operands: [FieldReference versionDestination, var versionValue] }),
                (_, { OpCode: OpCode.CheckLess, Operands: [LocalVariable condition, FieldReference sizeForCondition, Immediate { Value: 1 }] }),
                (_, { OpCode: OpCode.ConditionalJump, Operands: [Block target, var branchCondition] })
            ]
            || !ReferenceEquals(target, merge)
            || !ReferenceEquals(condition, branchCondition)
            || !ReferenceEquals(version, versionValue)
            || !IsZeroValue(zero)
            || !IsField(versionSource, receiver, "_version")
            || !IsField(versionDestination, receiver, "_version")
            || !IsField(sizeDestination, receiver, "_size")
            || !IsField(sizeForCondition, receiver, "_size"))
            return false;

        rewriteHead = suffix[0].Block;
        var rewriteStart = headPath.IndexOf(rewriteHead);
        headPath = headPath.GetRange(rewriteStart, headPath.Count - rewriteStart);
        firstRemoved = add;
        return true;
    }

    private static bool TryCreatePublicClearTarget(
        LocalVariable receiver,
        out ConcreteGenericMethodAnalysisContext clearTarget)
    {
        clearTarget = null!;
        if (receiver.Type is not GenericInstanceTypeAnalysisContext
            {
                GenericType.FullName: "System.Collections.Generic.List`1"
            } listType)
            return false;

        var clear = listType.GenericType.Methods.SingleOrDefault(method =>
            method.Name == "Clear"
            && !method.IsStatic
            && method.Parameters.Count == 0);
        if (clear == null)
            return false;

        clearTarget = new ConcreteGenericMethodAnalysisContext(clear, listType.GenericArguments, []);
        return true;
    }

    private static void Rewrite(
        ISILControlFlowGraph graph,
        Block rewriteHead,
        IReadOnlyList<Block> headPath,
        IReadOnlyList<Block> clearPath,
        Block merge,
        Instruction firstRemoved,
        Instruction clearCall,
        ConcreteGenericMethodAnalysisContext clearTarget,
        LocalVariable receiver)
    {
        var firstRemovedIndex = rewriteHead.Instructions.IndexOf(firstRemoved);
        rewriteHead.Instructions.RemoveRange(firstRemovedIndex, rewriteHead.Instructions.Count - firstRemovedIndex);
        rewriteHead.Instructions.Add(new Instruction(clearCall.Index, OpCode.CallVoid, clearTarget, receiver));

        foreach (var pathBlock in headPath.Skip(1).ToList())
            Detach(graph, pathBlock);
        foreach (var clearPathBlock in clearPath)
            Detach(graph, clearPathBlock);

        rewriteHead.Successors.Clear();
        rewriteHead.Successors.Add(merge);
        if (!merge.Predecessors.Contains(rewriteHead))
            merge.Predecessors.Add(rewriteHead);
        rewriteHead.CalculateBlockType();
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
            semanticCount += SemanticInstructions(current).Count;
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
            .SelectMany(block => SemanticInstructions(block).Select(instruction => (block, instruction)))
            .ToList();
        suffix = entries.GetRange(entries.Count - instructionCount, instructionCount);
        return true;
    }

    private static bool IsZeroValue(IOperand operand)
        => operand is Immediate { Value: 0 }
           || operand is LocalVariable
           {
               Register.Name: "W31",
               Type.FullName: "System.Int32"
           };

    private static bool IsField(FieldReference field, LocalVariable receiver, string name)
        => ReferenceEquals(field.Local, receiver) && field.Field.Name == name;

    private static List<Instruction> SemanticInstructions(Block block)
        => block.Instructions.Where(instruction => instruction.OpCode != OpCode.Nop).ToList();

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
}
