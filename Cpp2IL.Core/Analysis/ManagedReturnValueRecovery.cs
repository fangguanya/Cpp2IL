using System.Collections.Generic;
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
        var definitions = new HashSet<LocalVariable>(graph.Instructions
            .Where(instruction => instruction.Destination is LocalVariable)
            .Select(instruction => (LocalVariable)instruction.Destination!));
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
                        && IsReturnCarrierCompatible(method, returned, candidate, definitions)
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

    private static bool IsReturnCarrierCompatible(
        MethodAnalysisContext method,
        LocalVariable returned,
        LocalVariable candidate,
        HashSet<LocalVariable> definitions)
    {
        if (candidate.Register.Number == returned.Register.Number)
            return true;

        // 中文注释：ARM64 托管引用先由调用返回到 X0，再保存到 X19-X28 跨清理调用存活。
        // 只有返回局部无定义、不是参数且最近生产值仍在 X0 时，才恢复被异常 Phi 删除的保存复制。
        return !definitions.Contains(returned)
               && !method.ParameterLocals.Contains(returned)
               && candidate.Register.Name == "X0"
               && CalleeSavedManagedReceiverRecovery.IsCalleeSavedArm64Register(returned.Register.Name);
    }
}
