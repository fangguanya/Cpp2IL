using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 恢复异常清理裁边后同一 ABI 返回寄存器上的直接托管返回值。
/// </summary>
/// <remarks>
/// 仅沿唯一前驱、唯一后继链回溯，且要求最近的 Call/Newobj 结果与方法返回类型、物理寄存器
/// 同时一致；分支汇合、寄存器不同或类型不同均保持原返回红门。
/// </remarks>
public static class ManagedReturnValueRecovery
{
    public static int Run(MethodAnalysisContext method)
    {
        var graph = method.ControlFlowGraph!;
        var recovered = 0;

        foreach (var returnBlock in graph.Blocks.ToArray())
        {
            if (returnBlock.Instructions.LastOrDefault() is not
                { OpCode: OpCode.Return, Operands: [LocalVariable returned] } returnInstruction)
                continue;

            var current = returnBlock;
            var beforeIndex = current.Instructions.Count - 2;
            while (true)
            {
                for (var index = beforeIndex; index >= 0; index--)
                {
                    var candidateInstruction = current.Instructions[index];
                    if (candidateInstruction.OpCode is OpCode.Nop or OpCode.Jump)
                        continue;

                    if (candidateInstruction.OpCode is OpCode.Call or OpCode.Newobj
                        && candidateInstruction.Destination is LocalVariable candidate
                        && candidate.Register.Number == returned.Register.Number
                        && GenericCallRebinder.TypesEquivalent(candidate.Type, method.ReturnType))
                    {
                        returnInstruction.SetOperand(0, candidate);
                        recovered++;
                    }

                    goto NextReturn;
                }

                if (current.Predecessors.Count != 1)
                    break;
                var predecessor = current.Predecessors[0];
                if (predecessor.Successors.Count != 1)
                    break;

                current = predecessor;
                beforeIndex = current.Instructions.Count - 1;
            }

        NextReturn:
            ;
        }

        return recovered;
    }
}
