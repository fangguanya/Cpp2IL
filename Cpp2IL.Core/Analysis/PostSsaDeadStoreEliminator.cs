using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

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
                live.UnionWith(EnumerateEmittedUses(instruction));
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
            live.UnionWith(EnumerateEmittedUses(instruction));
        }

        return live;
    }

    /// <summary>
    /// 按最终 CIL 的真实发射语义枚举读取。未绑定到托管方法的直接调用只会由
    /// <see cref="IlGenerator"/> 生成诊断文本，不会装载原生调用约定携带的寄存器和栈参数；
    /// 因而这些伪参数不得让零初始化、地址载体等死写保持存活。已经解析为托管方法的调用
    /// 仍完整读取接收者与实参，保证 ref/out、实例调用和普通参数的数据流不变。
    /// </summary>
    private static IEnumerable<LocalVariable> EnumerateEmittedUses(Instruction instruction)
    {
        if (instruction.OpCode is OpCode.Call or OpCode.CallVoid
            && instruction.Operands.Count > 0
            && instruction.Operands[0] is not MethodAnalysisContext)
            yield break;

        foreach (var local in DeadCodeEliminator.EnumerateUsedLocals(instruction))
            yield return local;
    }
}
