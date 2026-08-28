using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 在退SSA控制流中审计并拆分局部变量的线性生存区。
/// </summary>
internal static class LocalLiveRangeHelper
{
    public static bool HasUseBeforeRedefinition(
        Instruction definition,
        LocalVariable original,
        IReadOnlyDictionary<Instruction, Graphs.Block> homeBlocks)
        => EnumerateBeforeRedefinition(definition, original, homeBlocks)
            .Any(instruction => instruction.Sources.Any(source => ReferenceEquals(source, original)));

    /// <summary>
    /// 为一个已证明返回值建立独立托管局部，只替换定义之后、重定义之前且不跨汇合点的读取。
    /// </summary>
    public static LocalVariable SplitResultLiveRange(
        MethodAnalysisContext method,
        Instruction definition,
        LocalVariable original,
        TypeAnalysisContext type,
        IReadOnlyDictionary<Instruction, Graphs.Block> homeBlocks,
        string role)
    {
        var fresh = new LocalVariable(
            $"{role}_{definition.Index}",
            // 中文注释：保留原生寄存器名称用于同一ABI链识别；唯一版本号只负责把
            // 退SSA后复用的物理槽拆成独立托管局部。
            original.Register.Copy(definition.Index),
            type);
        method.Locals.Add(fresh);

        foreach (var current in EnumerateBeforeRedefinition(definition, original, homeBlocks))
        {
            var destinationIndex = current.OpCode is OpCode.Call or OpCode.IndirectCall
                ? 1
                : current.Destination == null ? -1 : 0;
            for (var operandIndex = 0; operandIndex < current.Operands.Count; operandIndex++)
            {
                if (operandIndex != destinationIndex
                    && ReferenceEquals(current.Operands[operandIndex], original))
                {
                    current.SetOperand(operandIndex, fresh);
                }
            }
        }

        return fresh;
    }

    private static IEnumerable<Instruction> EnumerateBeforeRedefinition(
        Instruction definition,
        LocalVariable original,
        IReadOnlyDictionary<Instruction, Graphs.Block> homeBlocks)
    {
        if (!homeBlocks.TryGetValue(definition, out var home))
            yield break;

        var startIndex = home.Instructions.IndexOf(definition) + 1;
        var visited = new HashSet<Graphs.Block>();
        var queue = new Queue<(Graphs.Block Block, int Index)>();
        queue.Enqueue((home, startIndex));

        while (queue.Count > 0)
        {
            var (block, index) = queue.Dequeue();
            if (!visited.Add(block))
                continue;

            var killed = false;
            for (var instructionIndex = index;
                 instructionIndex < block.Instructions.Count;
                 instructionIndex++)
            {
                var current = block.Instructions[instructionIndex];
                if (ReferenceEquals(current.Destination, original))
                {
                    killed = true;
                    break;
                }

                yield return current;
            }

            if (killed)
                continue;
            foreach (var successor in block.Successors)
                if (successor.Predecessors.Count == 1)
                    queue.Enqueue((successor, 0));
        }
    }
}
