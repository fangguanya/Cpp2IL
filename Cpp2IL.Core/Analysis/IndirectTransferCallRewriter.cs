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
            result.Type = resolved.ReturnType;

        if (!isTailCall)
            return;

        var returnOperands = !owner.IsVoid && !resolved.IsVoid
            ? new List<IOperand> { transfer.Operands[1] }
            : [];

        block.AddInstruction(new Instruction(-1, OpCode.Return, returnOperands));
        block.CalculateBlockType();
    }
}
