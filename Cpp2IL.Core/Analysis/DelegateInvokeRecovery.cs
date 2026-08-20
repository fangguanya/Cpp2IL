using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Logging;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

// 将委托 invoke_impl 间接转移恢复为对应委托类型的 Invoke 调用。
public static class DelegateInvokeRecovery
{
    public static void Run(MethodAnalysisContext method)
    {
        var cfg = method.ControlFlowGraph!;
        var instructions = cfg.Blocks.SelectMany(block => block.Instructions).ToList();

        // Il2CppObject 先包含 klass 与 monitor，随后依次是 method_ptr 和 invoke_impl。
        var invokeImplOffset = (method.AppContext.Binary.is32Bit ? 4 : 8) * 3;

        foreach (var block in cfg.Blocks.ToList())
        {
            foreach (var instruction in block.Instructions.ToList())
            {
                if (instruction.OpCode is not (OpCode.IndirectCall or OpCode.IndirectJump))
                    continue;

                if (GetDelegateLocal(instruction, instructions, invokeImplOffset) is not { } delegateLocal)
                    continue;

                if (ResolveInvoke(delegateLocal.Type) is not { } invoke)
                {
                    Logger.VerboseNewline(
                        $"{method.DeclaringType?.FullName}.{method.Name} 的委托间接转移 {instruction.Index} 缺少精确 Invoke 类型",
                        nameof(DelegateInvokeRecovery));
                    continue;
                }

                IndirectTransferCallRewriter.Rewrite(method, instruction, block, invoke, delegateLocal);
            }
        }
    }

    // 调用目标既可能仍是内存读取，也可能已被字段解析器转换为 MulticastDelegate.invoke_impl 字段。
    private static LocalVariable? GetDelegateLocal(Instruction call, List<Instruction> instructions, int invokeImplOffset)
    {
        if (call.Operands.Count == 0)
            return null;

        if (GetDelegateLocal(call.Operands[0], invokeImplOffset) is { } folded)
            return folded;

        if (call.Operands[0] is not LocalVariable target)
            return null;

        var definition = instructions.FirstOrDefault(i => ReferenceEquals(i.Destination, target));
        return definition is { OpCode: OpCode.Move, Operands.Count: > 1 }
            ? GetDelegateLocal(definition.Operands[1], invokeImplOffset)
            : null;
    }

    private static LocalVariable? GetDelegateLocal(IOperand target, int invokeImplOffset)
    {
        return target switch
        {
            FieldReference { Field.Name: "invoke_impl", Local: var delegateLocal } => delegateLocal,
            MemoryOperand
            {
                Base: LocalVariable delegateLocal,
                Index: null,
                Scale: 0,
                Addend: var addend,
            } when addend == invokeImplOffset => delegateLocal,
            _ => null,
        };
    }

    private static MethodAnalysisContext? ResolveInvoke(TypeAnalysisContext? delegateType)
    {
        if (delegateType is GenericInstanceTypeAnalysisContext genericInstance)
        {
            if (!genericInstance.GenericType.IsDelegate
                || genericInstance.GenericType.Methods.FirstOrDefault(candidate => candidate.Name == "Invoke") is not { } genericInvoke)
                return null;

            return new ConcreteGenericMethodAnalysisContext(genericInvoke, genericInstance.GenericArguments, []);
        }

        return delegateType is { IsDelegate: true }
            ? delegateType.Methods.FirstOrDefault(candidate => candidate.Name == "Invoke")
            : null;
    }
}
