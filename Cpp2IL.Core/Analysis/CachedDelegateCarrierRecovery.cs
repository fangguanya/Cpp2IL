using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 恢复静态委托缓存菱形中被退 SSA 丢失的调用实参载体。
/// </summary>
/// <remarks>
/// IL2CPP 会先读取编译器生成的静态委托缓存；缓存为空时构造委托并写回，两个路径最终以
/// 同一物理寄存器调用 LINQ 等托管方法。若寄存器合流复制被早期简化删除，调用实参会成为
/// 无定义局部。此恢复器只在空值门、唯一构造、唯一静态写回和双路径汇合同时成立时，
/// 将该无定义实参恢复为同一静态缓存字段读取；任一证据不唯一时保持红门。
/// </remarks>
public static class CachedDelegateCarrierRecovery
{
    public static int Run(MethodAnalysisContext method)
    {
        if (method == null)
            throw new ArgumentNullException(nameof(method));

        var graph = method.ControlFlowGraph;
        if (graph == null)
            return 0;

        var homeBlocks = graph.Blocks
            .SelectMany(block => block.Instructions.Select(instruction => (instruction, block)))
            .ToDictionary(pair => pair.instruction, pair => pair.block);
        var definitions = graph.Instructions
            .Where(instruction => instruction.Destination is LocalVariable)
            .GroupBy(instruction => (LocalVariable)instruction.Destination!)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var parameters = new HashSet<LocalVariable>(method.ParameterLocals ?? []);
        var recovered = 0;

        foreach (var call in graph.Instructions.Where(instruction => instruction.IsCall).ToArray())
        {
            if (!homeBlocks.TryGetValue(call, out var joinBlock)
                || joinBlock.Predecessors.Count != 2)
                continue;

            foreach (var operandIndex in CallArgumentIndexes(call))
            {
                if (call.Operands[operandIndex] is not LocalVariable carrier
                    || parameters.Contains(carrier)
                    || definitions.ContainsKey(carrier)
                    || !IsDelegateType(carrier.Type)
                    || CountDirectUses(graph, carrier) != 1)
                    continue;

                var candidates = FindCacheCandidates(
                    graph,
                    homeBlocks,
                    joinBlock,
                    carrier.Type!);
                if (candidates.Count != 1)
                    continue;

                // 中文注释：静态缓存是两个原生路径的共同权威值。缓存缺失路径在进入汇合前
                // 已完成唯一写回，命中路径已由非空门证明可读，因此直接重绑调用实参不会
                // 引入默认局部，也不会重复执行委托构造。
                call.SetOperand(operandIndex, candidates[0].CacheField);
                recovered++;
            }
        }

        return recovered;
    }

    private static List<CacheCandidate> FindCacheCandidates(
        ISILControlFlowGraph graph,
        IReadOnlyDictionary<Instruction, Block> homeBlocks,
        Block joinBlock,
        TypeAnalysisContext delegateType)
    {
        var result = new List<CacheCandidate>();
        var stores = graph.Instructions
            .Where(instruction => instruction is
            {
                OpCode: OpCode.Move,
                Operands: [FieldReference { Field.IsStatic: true } field, LocalVariable source]
            }
            && IsSameType(field.Field.FieldType, delegateType)
            && IsSameType(source.Type, delegateType))
            .ToArray();

        foreach (var store in stores)
        {
            var storeField = (FieldReference)store.Operands[0];
            var created = (LocalVariable)store.Operands[1];
            var sameFieldStores = graph.Instructions.Count(instruction => instruction is
            {
                OpCode: OpCode.Move,
                Operands: [FieldReference field, _]
            } && ReferenceEquals(field.Field, storeField.Field));
            if (sameFieldStores != 1
                || !homeBlocks.TryGetValue(store, out var creatorBlock)
                || !HasUniqueDelegateAllocation(graph, homeBlocks, creatorBlock, created, delegateType))
                continue;

            var gates = graph.Blocks
                .Select(block => TryMatchCacheGate(
                    block,
                    storeField.Field,
                    creatorBlock,
                    joinBlock,
                    out var cacheField)
                    ? new CacheCandidate(cacheField!)
                    : null)
                .Where(candidate => candidate != null)
                .Cast<CacheCandidate>()
                .ToList();
            if (gates.Count == 1)
                result.Add(gates[0]);
        }

        return result;
    }

    private static bool HasUniqueDelegateAllocation(
        ISILControlFlowGraph graph,
        IReadOnlyDictionary<Instruction, Block> homeBlocks,
        Block creatorBlock,
        LocalVariable created,
        TypeAnalysisContext delegateType)
    {
        var definitions = graph.Instructions
            .Where(instruction => ReferenceEquals(instruction.Destination, created))
            .ToArray();
        return definitions is
        [
            {
                OpCode: OpCode.Newobj,
                Operands: [LocalVariable, TypeAnalysisContext allocationType]
            } allocation
        ]
        && homeBlocks.TryGetValue(allocation, out var allocationBlock)
        && ReferenceEquals(allocationBlock, creatorBlock)
        && IsSameType(allocationType, delegateType);
    }

    private static bool TryMatchCacheGate(
        Block gate,
        FieldAnalysisContext expectedField,
        Block creatorBlock,
        Block joinBlock,
        out FieldReference? cacheField)
    {
        cacheField = null;
        if (gate.Successors.Count != 2
            || gate.Instructions.LastOrDefault() is not
            { OpCode: OpCode.ConditionalJump } jump
            || jump.Operands.OfType<Block>().Distinct().ToArray() is not [var jumpTarget]
            || !gate.Successors.Contains(jumpTarget))
            return false;

        var condition = jump.Operands.OfType<LocalVariable>().SingleOrDefault();
        if (condition == null)
            return false;

        var comparisons = gate.Instructions
            .Where(instruction => ReferenceEquals(instruction.Destination, condition)
                                  && instruction.OpCode is OpCode.CheckEqual or OpCode.CheckNotEqual)
            .ToArray();
        if (comparisons is not [var comparison]
            || !TryGetFieldZeroComparison(comparison, expectedField, out cacheField))
            return false;

        var fallthrough = gate.Successors.Single(successor => !ReferenceEquals(successor, jumpTarget));
        var missStart = comparison.OpCode == OpCode.CheckEqual ? jumpTarget : fallthrough;
        var hitStart = comparison.OpCode == OpCode.CheckEqual ? fallthrough : jumpTarget;

        return CanReachBefore(missStart, creatorBlock, joinBlock)
               && CanReachBefore(creatorBlock, joinBlock, gate)
               && CanReachBefore(hitStart, joinBlock, creatorBlock);
    }

    private static bool TryGetFieldZeroComparison(
        Instruction comparison,
        FieldAnalysisContext expectedField,
        out FieldReference? cacheField)
    {
        cacheField = comparison.Operands
            .Skip(1)
            .OfType<FieldReference>()
            .SingleOrDefault(field => ReferenceEquals(field.Field, expectedField));
        if (cacheField == null)
            return false;

        return comparison.Operands
            .Skip(1)
            .Any(operand => operand is Immediate { Value: 0 });
    }

    private static bool CanReachBefore(Block start, Block target, Block stop)
    {
        var visited = new HashSet<Block>();
        var queue = new Queue<Block>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (ReferenceEquals(current, target))
                return true;
            if (ReferenceEquals(current, stop) || !visited.Add(current))
                continue;
            foreach (var successor in current.Successors)
                queue.Enqueue(successor);
        }

        return false;
    }

    private static IEnumerable<int> CallArgumentIndexes(Instruction call)
    {
        var first = call.OpCode == OpCode.Call ? 2 : 1;
        for (var index = first; index < call.Operands.Count; index++)
            yield return index;
    }

    private static int CountDirectUses(ISILControlFlowGraph graph, LocalVariable local)
        => graph.Instructions.Count(instruction => instruction.Operands.Any(operand =>
            ReferenceEquals(operand, local)
            && !ReferenceEquals(instruction.Destination, local)));

    private static bool IsDelegateType(TypeAnalysisContext? type)
        => type switch
        {
            GenericInstanceTypeAnalysisContext generic => generic.GenericType.IsDelegate,
            { } concrete => concrete.IsDelegate,
            _ => false
        };

    private static bool IsSameType(TypeAnalysisContext? left, TypeAnalysisContext? right)
        => left != null
           && right != null
           && GenericCallRebinder.TypesEquivalent(left, right);

    private sealed record CacheCandidate(FieldReference CacheField);
}
