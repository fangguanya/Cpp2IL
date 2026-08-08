using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 删除没有任何可观察读取的取址载体。
///
/// 原生异常清理路径常生成“取局部地址→写入临时寄存器”的孤立链；若该链不再传给
/// 调用、内存读写或返回，保留它只会让ILSpy发射object*并产生不可编译的伪转换。
/// 只有唯一的Move(AddressOf)定义且全图没有读取时才删除，任何真实ref/out语义保持不动。
/// </summary>
public static class DeadAddressCarrierRecovery
{
    public static int Run(MethodAnalysisContext method)
    {
        var cfg = method.ControlFlowGraph!;
        var uses = CollectUses(cfg.Instructions);
        var removed = 0;

        foreach (var instruction in cfg.Instructions)
        {
            if (instruction is not
                {
                    OpCode: OpCode.Move,
                    Operands: [LocalVariable destination, AddressOf]
                })
                continue;

            if (uses.Contains(destination))
                continue;

            instruction.OpCode = OpCode.Nop;
            instruction.SetOperands();
            removed++;
        }

        return removed;
    }

    private static HashSet<LocalVariable> CollectUses(IReadOnlyList<Instruction> instructions)
    {
        var uses = new HashSet<LocalVariable>();
        foreach (var instruction in instructions)
        {
            var destination = instruction.Destination;
            foreach (var operand in instruction.Operands)
                CollectOperand(operand, destination, uses);
        }

        return uses;
    }

    private static void CollectOperand(IOperand operand, IOperand? destination, HashSet<LocalVariable> uses)
    {
        switch (operand)
        {
            case LocalVariable local when !ReferenceEquals(local, destination):
                uses.Add(local);
                break;
            case MemoryOperand memory:
                if (memory.Base is LocalVariable baseLocal)
                    uses.Add(baseLocal);
                if (memory.Index is LocalVariable indexLocal)
                    uses.Add(indexLocal);
                break;
            case FieldReference { Local: LocalVariable fieldLocal }:
                uses.Add(fieldLocal);
                break;
            case AddressOf { Target: LocalVariable addressed }:
                uses.Add(addressed);
                break;
            case ArrayAccess { Array: LocalVariable array }:
                uses.Add(array);
                break;
        }
    }
}
