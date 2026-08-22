using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 清除已绑定托管调用之前仅为 IL2CPP 原生 ABI 准备的 MethodInfo 虚表半槽装载。
/// 托管调用操作数已经由 <see cref="CallArgumentTrimmer"/> 收束为接收者和公开参数，
/// 因而该装载既不是托管实参，也不应在 CIL 中表现为非托管内存读取。
/// </summary>
public static class ResolvedManagedCallScaffoldingRecovery
{
    public static int Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph == null)
            return 0;

        var removed = 0;
        foreach (var block in method.ControlFlowGraph.Blocks)
        {
            for (var callIndex = 0; callIndex < block.Instructions.Count; callIndex++)
            {
                var call = block.Instructions[callIndex];
                if (call.OpCode is not (OpCode.Call or OpCode.CallVoid)
                    || call.Operands.Count == 0
                    || call.Operands[0] is not MethodAnalysisContext)
                    continue;

                var loadIndex = FindPreviousMeaningfulInstruction(block.Instructions, callIndex);
                if (loadIndex < 0
                    || block.Instructions[loadIndex] is not
                    {
                        OpCode: OpCode.Move,
                        Operands: [LocalVariable destination,
                            MemoryOperand { Base: LocalVariable klass, Index: null, Scale: 0 } source]
                    }
                    || klass.Type is not RuntimeClassTypeAnalysisContext
                    || !Il2CppClassUsefulOffsets.IsVirtualInvokeMethodInfoOffset(
                        source.Addend,
                        method.AppContext.Binary.is32Bit)
                    || DeadCodeEliminator.EnumerateUsedLocals(call)
                        .Any(used => ReferenceEquals(used, destination)))
                    continue;

                // 中文注释：这里只删除本条已经完成托管绑定的 MethodInfo 隐参装载；
                // 上游类指针若也失去用途，由紧随其后的退 SSA 活跃性分析统一级联删除。
                var load = block.Instructions[loadIndex];
                load.OpCode = OpCode.Nop;
                load.SetOperands();
                removed++;
            }
        }

        return removed;
    }

    /// <summary>
    /// 在同一基本块内反向寻找调用之前的真实指令；Nop 不携带 ABI 语义。
    /// </summary>
    private static int FindPreviousMeaningfulInstruction(IReadOnlyList<Instruction> instructions, int startIndex)
    {
        for (var index = startIndex - 1; index >= 0; index--)
            if (instructions[index].OpCode != OpCode.Nop)
                return index;

        return -1;
    }
}
