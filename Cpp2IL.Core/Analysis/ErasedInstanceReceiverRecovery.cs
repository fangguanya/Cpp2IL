using System.Linq;
using Cpp2IL.Core.Extensions;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 恢复原生优化已擦除、但托管实例调用仍要求存在的接收者。
/// </summary>
public static class ErasedInstanceReceiverRecovery
{
    public static int Run(MethodAnalysisContext method)
    {
        if (method.IsStatic || method.DeclaringType == null || method.ControlFlowGraph == null)
            return 0;

        LocalVariable? thisLocal = null;
        var rewrittenCount = 0;
        foreach (var instruction in method.ControlFlowGraph.Instructions)
        {
            if (!instruction.IsCall
                || instruction.Operands.Count == 0
                || instruction.Operands[0] is not MethodAnalysisContext
                {
                    IsStatic: false,
                    DeclaringType: { } targetType,
                } targetMethod
                || targetMethod.Name is ".ctor" or ".cctor")
                continue;

            var receiverIndex = instruction.OpCode == OpCode.Call ? 2 : 1;
            if (receiverIndex >= instruction.Operands.Count
                || instruction.Operands[receiverIndex] is not LocalVariable { Type: { } receiverType })
                continue;

            // 已经可赋值的接收者保留原身份；只有原生寄存器值与托管签名矛盾时才恢复入口this。
            if (receiverType.IsAssignableTo(targetType)
                || !method.DeclaringType.IsAssignableTo(targetType))
                continue;

            thisLocal ??= GetOrCreateThisLocal(method);
            if (thisLocal == null)
                continue;

            instruction.SetOperand(receiverIndex, thisLocal);
            rewrittenCount++;
        }

        return rewrittenCount;
    }

    private static LocalVariable? GetOrCreateThisLocal(MethodAnalysisContext method)
    {
        var existingParameter = method.ParameterLocals.FirstOrDefault(local => local.IsThis);
        if (existingParameter != null)
            return existingParameter;

        if (method.ParameterOperands.Count == 0
            || method.ParameterOperands[0] is not Register entryRegister
            || method.DeclaringType == null)
            return null;

        var entryLocal = method.Locals.FirstOrDefault(local =>
            local.Register.Number == entryRegister.Number
            && local.Register.Version == entryRegister.Version);
        if (entryLocal == null)
        {
            entryLocal = new LocalVariable("this", entryRegister, method.DeclaringType);
            method.Locals.Add(entryLocal);
        }

        entryLocal.Name = "this";
        entryLocal.Type = method.DeclaringType;
        entryLocal.IsThis = true;
        method.ParameterLocals.Add(entryLocal);
        return entryLocal;
    }
}
