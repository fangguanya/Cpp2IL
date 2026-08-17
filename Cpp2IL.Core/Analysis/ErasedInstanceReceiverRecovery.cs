using System.Collections.Generic;
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

        // 中文注释：退SSA后同一合流局部会有多条边Move；一次构建完整定义目录，既供标量
        // 值图审计复用，也派生唯一目录保护运行时元数据复制闭包。
        var allDefinitions = method.ControlFlowGraph.Instructions
            .Where(instruction => instruction.Destination is LocalVariable)
            .GroupBy(instruction => (LocalVariable)instruction.Destination!)
            .ToDictionary(group => group.Key, group => group.ToList());
        var uniqueDefinitions = allDefinitions
            .Where(pair => pair.Value.Count == 1)
            .ToDictionary(pair => pair.Key, pair => pair.Value[0]);
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
                || instruction.Operands[receiverIndex] is not LocalVariable { Type: { } receiverType } receiver)
                continue;

            // 泛型值T既可能是值类型，也可能是引用类型；它对Object实例方法的调用必须由
            // constrained.callvirt保留真实接收者。IsAssignableTo在开放泛型上没有足够信息，
            // 因此绝不能把T误判成原生残留寄存器并改写成当前方法的this。
            if (receiverType is GenericParameterTypeAnalysisContext)
                continue;

            // 泛型方法的隐藏 MethodInfo 会被保存后搬入 X0 调用 rgctx 初始化入口；该原生
            // 地址可能暂时绑定为当前托管实例方法。运行时元数据闭包必须保留给后续保护段
            // 删除器，禁止恢复器把它改写成入口 this 并抹掉原始载体证据。
            if (LocalVariables.ContainsRuntimeMetadataCarrier(receiver, uniqueDefinitions))
                continue;

            // 类型传播会依据被调签名把合流结果强制标成目标类型，但它的真实原生入边仍可能只是
            // 调用前遗留的整数参数。除显式类型冲突外，仅接受“全部Move/Phi闭包全为标量常量，
            // 且至少含一个非零值”的擦除证据；单独的零值仍可能是源码显式空接收者，保持原样。
            var hasErasedScalarValueGraph = HasErasedScalarValueGraph(receiver, allDefinitions);
            if ((receiverType.IsAssignableTo(targetType) && !hasErasedScalarValueGraph)
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

    private static bool HasErasedScalarValueGraph(
        LocalVariable receiver,
        IReadOnlyDictionary<LocalVariable, List<Instruction>> allDefinitions)
    {
        var visiting = new HashSet<LocalVariable>();
        var sawNonZero = false;
        return ContainsOnlyScalarConstants(receiver, allDefinitions, visiting, ref sawNonZero)
               && sawNonZero;
    }

    private static bool ContainsOnlyScalarConstants(
        IOperand value,
        IReadOnlyDictionary<LocalVariable, List<Instruction>> allDefinitions,
        ISet<LocalVariable> visiting,
        ref bool sawNonZero)
    {
        if (value is Immediate immediate)
        {
            sawNonZero |= immediate.Value != 0;
            return true;
        }

        if (value is not LocalVariable local
            || !visiting.Add(local)
            || !allDefinitions.TryGetValue(local, out var definitions)
            || definitions.Count == 0)
            return false;

        foreach (var definition in definitions)
        {
            var sources = definition.OpCode switch
            {
                OpCode.Move when definition.Operands.Count >= 2 => definition.Operands.Skip(1),
                OpCode.Phi when definition.Operands.Count >= 2 => definition.Operands.Skip(1),
                _ => [],
            };
            var hasSource = false;
            foreach (var source in sources)
            {
                hasSource = true;
                if (!ContainsOnlyScalarConstants(source, allDefinitions, visiting, ref sawNonZero))
                {
                    visiting.Remove(local);
                    return false;
                }
            }

            if (!hasSource)
            {
                visiting.Remove(local);
                return false;
            }
        }

        visiting.Remove(local);
        return true;
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
