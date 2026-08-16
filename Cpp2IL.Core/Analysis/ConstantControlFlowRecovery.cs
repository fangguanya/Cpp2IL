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
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());
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
        IReadOnlyDictionary<LocalVariable, Instruction> definitions,
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
            || !definitions.TryGetValue(local, out var definition))
        {
            value = false;
            return false;
        }

        try
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
                && TryInteger(definition.Operands[1], out var left)
                && TryInteger(definition.Operands[2], out var right))
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
        finally
        {
            visiting.Remove(local);
        }
    }

    private static bool TryInteger(IOperand operand, out long value)
    {
        if (operand is Immediate immediate)
        {
            value = immediate.Value;
            return true;
        }

        value = 0;
        return false;
    }
}
