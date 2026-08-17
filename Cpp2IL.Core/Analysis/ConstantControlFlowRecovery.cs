using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 折叠退 SSA 后已经完全常量化的条件边，并同步修正控制流图。
/// </summary>
/// <remarks>
/// ARM64 异常清理状态机常把固定状态码保留成多层比较；常量传播只替换操作数而不裁边，
/// 会让本来不可达的异常返回继续进入 CIL。该恢复器只计算纯整数/布尔表达式，未知值保持原图。
/// </remarks>
public static class ConstantControlFlowRecovery
{
    public static int Run(ISILControlFlowGraph graph)
    {
        var definitions = graph.Instructions
            .Where(instruction => instruction.Destination is LocalVariable)
            .GroupBy(instruction => (LocalVariable)instruction.Destination!)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<Instruction>)group.ToArray());
        var rewritten = 0;

        foreach (var block in graph.Blocks.ToArray())
        {
            if (block.Instructions.LastOrDefault() is not
                { OpCode: OpCode.ConditionalJump, Operands: [Block target, var condition] } branch
                || !TryEvaluate(condition, definitions, [], out var taken))
                continue;

            var fallthroughs = block.Successors
                .Where(successor => !ReferenceEquals(successor, target))
                .Distinct()
                .ToArray();
            if (fallthroughs.Length != 1)
                continue;

            if (taken)
            {
                branch.OpCode = OpCode.Jump;
                branch.SetOperands(target);
                Detach(block, fallthroughs[0]);
            }
            else
            {
                branch.OpCode = OpCode.Nop;
                branch.SetOperands();
                Detach(block, target);
            }

            block.CalculateBlockType();
            rewritten++;
        }

        if (rewritten == 0)
            return 0;

        graph.RemoveUnreachableBlocks();
        graph.RemoveNops();
        graph.RemoveEmptyBlocks();
        foreach (var block in graph.Blocks)
            block.CalculateBlockType();
        return rewritten;
    }

    private static void Detach(Block source, Block target)
    {
        source.Successors.RemoveAll(successor => ReferenceEquals(successor, target));
        target.Predecessors.RemoveAll(predecessor => ReferenceEquals(predecessor, source));
    }

    private static bool TryEvaluate(
        IOperand operand,
        IReadOnlyDictionary<LocalVariable, IReadOnlyList<Instruction>> definitions,
        HashSet<LocalVariable> visiting,
        out bool value)
    {
        if (operand is Immediate immediate)
        {
            value = immediate.Value != 0;
            return true;
        }

        if (operand is not LocalVariable local
            || !visiting.Add(local)
            || !definitions.TryGetValue(local, out var localDefinitions)
            || localDefinitions.Count == 0)
        {
            value = false;
            return false;
        }

        try
        {
            bool? consensus = null;
            foreach (var definition in localDefinitions)
            {
                if (!TryEvaluateDefinition(definition, definitions, visiting, out var candidate)
                    || consensus.HasValue && consensus.Value != candidate)
                {
                    value = false;
                    return false;
                }

                consensus = candidate;
            }

            value = consensus.GetValueOrDefault();
            return consensus.HasValue;
        }
        finally
        {
            visiting.Remove(local);
        }
    }

    /// <summary>
    /// 计算单条定义的布尔结果。多定义局部由调用方执行一致性裁决，避免把不同 Phi 入边
    /// 当作同一个常量；整数比较会继续沿只读复制链读取各入边的精确常量。
    /// </summary>
    private static bool TryEvaluateDefinition(
        Instruction definition,
        IReadOnlyDictionary<LocalVariable, IReadOnlyList<Instruction>> definitions,
        HashSet<LocalVariable> visiting,
        out bool value)
    {
        if (definition is { OpCode: OpCode.Move, Operands: [_, var movedSource] })
            return TryEvaluate(movedSource, definitions, visiting, out value);

        if (definition is { OpCode: OpCode.Not, Operands: [_, var negatedSource] }
            && TryEvaluate(negatedSource, definitions, visiting, out var sourceValue))
        {
            value = !sourceValue;
            return true;
        }

        if (definition.Operands.Count >= 3
            && TryInteger(definition.Operands[1], definitions, visiting, out var left)
            && TryInteger(definition.Operands[2], definitions, visiting, out var right))
        {
            value = definition.OpCode switch
            {
                OpCode.CheckEqual => left == right,
                OpCode.CheckNotEqual => left != right,
                OpCode.CheckGreater => left > right,
                OpCode.CheckGreaterOrEqual => left >= right,
                OpCode.CheckLess => left < right,
                OpCode.CheckLessOrEqual => left <= right,
                OpCode.CheckGreaterUnsigned => (ulong)left > (ulong)right,
                OpCode.CheckGreaterOrEqualUnsigned => (ulong)left >= (ulong)right,
                OpCode.CheckLessUnsigned => (ulong)left < (ulong)right,
                OpCode.CheckLessOrEqualUnsigned => (ulong)left <= (ulong)right,
                _ => false,
            };
            return definition.OpCode is >= OpCode.CheckEqual and <= OpCode.CheckLessOrEqualUnsigned;
        }

        value = false;
        return false;
    }

    /// <summary>
    /// 沿 Move 定义求整数常量；多定义局部只有全部入边成功且值完全一致时才是常量。
    /// 该规则覆盖退 SSA 后的零值 Phi，同时保留冲突入边和循环定义的原控制流。
    /// </summary>
    private static bool TryInteger(
        IOperand operand,
        IReadOnlyDictionary<LocalVariable, IReadOnlyList<Instruction>> definitions,
        HashSet<LocalVariable> visiting,
        out long value)
    {
        if (operand is Immediate immediate)
        {
            value = immediate.Value;
            return true;
        }

        if (operand is not LocalVariable local
            || !visiting.Add(local)
            || !definitions.TryGetValue(local, out var localDefinitions)
            || localDefinitions.Count == 0)
        {
            value = 0;
            return false;
        }

        try
        {
            long? consensus = null;
            foreach (var definition in localDefinitions)
            {
                if (definition is not { OpCode: OpCode.Move, Operands: [_, var movedSource] }
                    || !TryInteger(movedSource, definitions, visiting, out var candidate)
                    || consensus.HasValue && consensus.Value != candidate)
                {
                    value = 0;
                    return false;
                }

                consensus = candidate;
            }

            value = consensus.GetValueOrDefault();
            return consensus.HasValue;
        }
        finally
        {
            visiting.Remove(local);
        }
    }
}
