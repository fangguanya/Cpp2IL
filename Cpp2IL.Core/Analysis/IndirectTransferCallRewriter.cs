using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 将已精确解析目标的间接调用或尾调用统一改写为托管调用，集中维护返回槽、原始参数和尾返回语义。
/// </summary>
public static class IndirectTransferCallRewriter
{
    public static void Rewrite(
        MethodAnalysisContext owner,
        Instruction transfer,
        Block block,
        MethodAnalysisContext resolved,
        LocalVariable? receiverOverride = null)
    {
        var isTailCall = transfer.OpCode == OpCode.IndirectJump;

        if (isTailCall)
        {
            var operands = new List<IOperand> { resolved };

            if (!resolved.IsVoid)
                operands.Add(new LocalVariable("indirectTailCallResult", CallingConventionResolver.ReturnRegister(resolved)));

            // 间接转移的前两个操作数分别是目标和原始返回槽，之后才是完整调用寄存器快照。
            operands.AddRange(transfer.Operands.Skip(2));
            transfer.SetOperands(operands);
        }
        else
        {
            if (resolved.IsVoid)
                transfer.RemoveOperandAt(1);

            transfer.SetOperand(0, resolved);
        }

        transfer.OpCode = resolved.IsVoid ? OpCode.CallVoid : OpCode.Call;

        if (receiverOverride != null)
            transfer.SetOperand(resolved.IsVoid ? 1 : 2, receiverOverride);

        CallingConventionResolver.RemapRawArguments(transfer, resolved);

        if (!resolved.IsVoid && transfer.Operands.Count > 1 && transfer.Operands[1] is LocalVariable result)
        {
            // 中文注释：ARM64 的 X0 同时是首个实参寄存器与返回寄存器。退 SSA 的复制合并
            // 可能让调用前接收者和调用后结果引用同一逻辑局部，也可能让返回槽仍携带上一段
            // ArrayList 等具体生命期类型；直接覆盖都会把整段旧生命期污染为 Int32。只有未知、
            // Object 或原生指针 ABI 占位可以原地精确定型，其余冲突一律分裂托管返回槽。
            if (RequiresDistinctResultSlot(transfer, result, resolved.ReturnType))
            {
                result = new LocalVariable(
                    $"{result.Name}_managedCallResult_{transfer.Index}",
                    result.Register,
                    resolved.ReturnType)
                {
                    IsReturn = result.IsReturn,
                };
                transfer.SetOperand(1, result);
            }
            else
            {
                result.Type = resolved.ReturnType;
            }
        }

        if (!isTailCall)
            return;

        var returnOperands = !owner.IsVoid && !resolved.IsVoid
            ? new List<IOperand> { transfer.Operands[1] }
            : [];

        block.AddInstruction(new Instruction(-1, OpCode.Return, returnOperands));
        block.CalculateBlockType();
    }

    /// <summary>
    /// 判断两个操作数是否表示复制合并后的同一逻辑局部。
    /// </summary>
    private static bool SameLogicalLocal(IOperand operand, LocalVariable expected)
        => operand is LocalVariable local
           && (ReferenceEquals(local, expected)
               || local.Name == expected.Name && local.Register == expected.Register);

    /// <summary>
    /// 判断原始返回槽是否属于调用前仍有效的另一段类型生命期。
    /// </summary>
    private static bool RequiresDistinctResultSlot(
        Instruction transfer,
        LocalVariable result,
        TypeAnalysisContext returnType)
    {
        if (transfer.Operands.Skip(2).Any(operand => SameLogicalLocal(operand, result)))
            return true;

        if (result.Type == null || GenericCallRebinder.TypesEquivalent(result.Type, returnType))
            return false;

        return result.Type.FullName is not (
            "System.Object" or "System.IntPtr" or "System.UIntPtr");
    }
}
