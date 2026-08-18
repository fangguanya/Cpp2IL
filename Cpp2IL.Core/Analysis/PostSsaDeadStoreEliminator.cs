using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 在退 SSA 后按控制流活跃性删除纯局部死写。此时一个物理局部允许有多个定义，不能再用
/// SSA 全局定义标记；必须从每个块的出口活跃集向前驱传播，并在块内按逆序裁决具体定义。
/// </summary>
public static class PostSsaDeadStoreEliminator
{
    public static int Run(ISILControlFlowGraph graph)
    {
        var liveIn = graph.Blocks.ToDictionary(block => block, _ => new HashSet<LocalVariable>());
        var liveOut = graph.Blocks.ToDictionary(block => block, _ => new HashSet<LocalVariable>());
        var pending = new Queue<Block>(graph.Blocks);
        var queued = new HashSet<Block>(graph.Blocks);

        // 中文注释：使用前驱工作队列求解单调活跃性方程；每个变化只通知真实前驱，
        // 不对整张方法图执行固定次数的重复扫描。
        while (pending.Count > 0)
        {
            var block = pending.Dequeue();
            queued.Remove(block);
            var nextOut = new HashSet<LocalVariable>();
            foreach (var successor in block.Successors)
                nextOut.UnionWith(liveIn[successor]);

            var nextIn = Transfer(block, nextOut);
            var outChanged = !liveOut[block].SetEquals(nextOut);
            var inChanged = !liveIn[block].SetEquals(nextIn);
            if (outChanged)
                liveOut[block] = nextOut;
            if (!inChanged)
                continue;

            liveIn[block] = nextIn;
            foreach (var predecessor in block.Predecessors)
                if (queued.Add(predecessor))
                    pending.Enqueue(predecessor);
        }

        var removed = 0;
        foreach (var block in graph.Blocks)
        {
            var live = new HashSet<LocalVariable>(liveOut[block]);
            for (var index = block.Instructions.Count - 1; index >= 0; index--)
            {
                var instruction = block.Instructions[index];
                if (instruction.Destination is LocalVariable destination
                    && DeadCodeEliminator.IsRemovable(instruction.OpCode)
                    && !live.Contains(destination))
                {
                    instruction.OpCode = OpCode.Nop;
                    instruction.SetOperands();
                    removed++;
                    continue;
                }

                if (instruction.Destination is LocalVariable defined)
                    live.Remove(defined);
                live.UnionWith(DeadCodeEliminator.EnumerateUsedLocals(instruction));
            }
        }

        return removed;
    }

    /// <summary>
    /// 给定块出口活跃集计算入口活跃集。未被后续读取的纯局部定义不读取其源操作数，
    /// 因而同一逆向传递同时完成死写级联，不需要额外的删除后重算。
    /// </summary>
    private static HashSet<LocalVariable> Transfer(Block block, IReadOnlyCollection<LocalVariable> liveOut)
    {
        var live = new HashSet<LocalVariable>(liveOut);
        for (var index = block.Instructions.Count - 1; index >= 0; index--)
        {
            var instruction = block.Instructions[index];
            if (instruction.Destination is LocalVariable destination
                && DeadCodeEliminator.IsRemovable(instruction.OpCode)
                && !live.Contains(destination))
                continue;

            if (instruction.Destination is LocalVariable defined)
                live.Remove(defined);
            live.UnionWith(DeadCodeEliminator.EnumerateUsedLocals(instruction));
        }

        return live;
    }
}
