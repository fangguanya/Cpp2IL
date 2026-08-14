using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 将跳往纯复制尾返回适配器的无条件边恢复为直接返回。
/// IL2CPP常让数百个命中分支先写共享局部，再跳到Move/Return尾链；保留该形态会让
/// C#反编译器生成超深else树。这里只折叠无副作用且返回值来源唯一的透明尾链。
/// </summary>
public static class TailReturnRecovery
{
    public static int Run(ISILControlFlowGraph graph)
    {
        var contracts = BuildReturnContracts(graph.Blocks);
        var changed = 0;

        foreach (var block in graph.Blocks.ToList())
        {
            var jump = block.Instructions.LastOrDefault(instruction => instruction.OpCode != OpCode.Nop);
            if (jump is not { OpCode: OpCode.Jump, Operands.Count: 1 }
                || jump.Operands[0] is not Block target
                || block.Successors.Count != 1
                || !ReferenceEquals(block.Successors[0], target)
                || !contracts.TryGetValue(target, out var returnedOperand)
                || returnedOperand == null)
                continue;

            jump.OpCode = OpCode.Return;
            jump.SetOperands(returnedOperand);

            ISILControlFlowGraph.RemovePredecessorAndPhiInputs(target, block);
            block.Successors.Clear();
            if (!block.Successors.Contains(graph.ExitBlock))
                block.Successors.Add(graph.ExitBlock);
            if (!graph.ExitBlock.Predecessors.Contains(block))
                graph.ExitBlock.Predecessors.Add(block);
            block.CalculateBlockType();
            changed++;
        }

        if (changed > 0)
            graph.RemoveUnreachableBlocks();

        return changed;
    }

    /// <summary>
    /// 为每个透明尾块计算“从该块进入最终会返回哪个当前可用操作数”。
    /// 记忆化状态保证每块只分析一次；访问中的块再次出现即为循环，不建立契约。
    /// </summary>
    private static Dictionary<Block, IOperand?> BuildReturnContracts(IReadOnlyList<Block> blocks)
    {
        var contracts = new Dictionary<Block, IOperand?>();
        var states = new Dictionary<Block, VisitState>();

        foreach (var block in blocks)
            ResolveContract(block, contracts, states);

        return contracts;
    }

    private static IOperand? ResolveContract(
        Block block,
        Dictionary<Block, IOperand?> contracts,
        Dictionary<Block, VisitState> states)
    {
        if (states.TryGetValue(block, out var state))
            return state == VisitState.Completed ? contracts[block] : null;

        states[block] = VisitState.Visiting;
        var effective = block.Instructions.Where(instruction => instruction.OpCode != OpCode.Nop).ToList();
        IOperand? contract = null;

        if (effective is [{ OpCode: OpCode.Return, Operands.Count: 1 } terminalReturn])
        {
            contract = terminalReturn.Operands[0];
        }
        else if (TryGetOnlySuccessor(block, effective, out var body, out var successor)
                 && ResolveContract(successor, contracts, states) is { } successorContract)
        {
            if (body.Count == 0)
            {
                contract = successorContract;
            }
            else if (body is
                     [
                         {
                             OpCode: OpCode.Move,
                             Operands: [LocalVariable destination, IOperand source]
                         }
                     ]
                     && ReferenceEquals(destination, successorContract))
            {
                contract = source;
            }
        }

        states[block] = VisitState.Completed;
        contracts[block] = contract;
        return contract;
    }

    /// <summary>
    /// 提取透明块的唯一后继和去掉无条件跳转后的有效主体；条件边及多后继块均不参与。
    /// </summary>
    private static bool TryGetOnlySuccessor(
        Block block,
        IReadOnlyList<Instruction> effective,
        out IReadOnlyList<Instruction> body,
        out Block successor)
    {
        body = [];
        successor = null!;
        if (block.Successors.Count != 1)
            return false;

        successor = block.Successors[0];
        if (effective.Count > 0 && effective[^1].OpCode == OpCode.Jump)
        {
            if (effective[^1].Operands.Count != 1
                || effective[^1].Operands[0] is not Block jumpTarget
                || !ReferenceEquals(jumpTarget, successor))
                return false;

            body = effective.Take(effective.Count - 1).ToList();
            return true;
        }

        body = effective;
        return true;
    }

    private enum VisitState
    {
        Visiting,
        Completed,
    }
}
