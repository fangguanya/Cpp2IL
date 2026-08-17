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
    public static int RunIncompatibleTypeReceivers(MethodAnalysisContext method)
    {
        if (method.IsStatic || method.DeclaringType == null || method.ControlFlowGraph == null)
            return 0;

        // 中文注释：SSA阶段每个局部应有唯一定义；该目录只负责排除隐藏运行时元数据闭包。
        var uniqueDefinitions = method.ControlFlowGraph.Instructions
            .Where(instruction => instruction.Destination is LocalVariable)
            .GroupBy(instruction => (LocalVariable)instruction.Destination!)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());
        var rewrites = new List<(Instruction Instruction, int ReceiverIndex)>();
        foreach (var candidate in EnumerateCandidates(method))
        {
            // 泛型值T既可能是值类型，也可能是引用类型；它对Object实例方法的调用必须由
            // constrained.callvirt保留真实接收者。IsAssignableTo在开放泛型上没有足够信息，
            // 因此绝不能把T误判成原生残留寄存器并改写成当前方法的this。
            if (candidate.ReceiverType is GenericParameterTypeAnalysisContext)
                continue;

            // 泛型方法的隐藏 MethodInfo 会被保存后搬入 X0 调用 rgctx 初始化入口；该原生
            // 地址可能暂时绑定为当前托管实例方法。运行时元数据闭包必须保留给后续保护段
            // 删除器，禁止恢复器把它改写成入口 this 并抹掉原始载体证据。
            if (LocalVariables.ContainsRuntimeMetadataCarrier(candidate.Receiver, uniqueDefinitions))
                continue;

            if (candidate.ReceiverType.IsAssignableTo(candidate.TargetType)
                || !method.DeclaringType.IsAssignableTo(candidate.TargetType))
                continue;

            rewrites.Add((candidate.Instruction, candidate.ReceiverIndex));
        }

        return RewriteToThis(method, rewrites);
    }

    public static int RunScalarValueReceivers(MethodAnalysisContext method)
    {
        if (method.IsStatic || method.DeclaringType == null || method.ControlFlowGraph == null)
            return 0;

        // 中文注释：退SSA后同一合流局部会有多条边Move；完整目录只服务于终态标量值图审计。
        var allDefinitions = method.ControlFlowGraph.Instructions
            .Where(instruction => instruction.Destination is LocalVariable)
            .GroupBy(instruction => (LocalVariable)instruction.Destination!)
            .ToDictionary(group => group.Key, group => group.ToList());
        var rewrites = new List<(Instruction Instruction, int ReceiverIndex)>();
        foreach (var candidate in EnumerateCandidates(method))
        {
            // 该阶段与早期类型冲突阶段互斥；只处理已被调用签名定型为相容类型的接收者。
            if (candidate.ReceiverType is GenericParameterTypeAnalysisContext
                || !candidate.ReceiverType.IsAssignableTo(candidate.TargetType)
                || !method.DeclaringType.IsAssignableTo(candidate.TargetType)
                || !HasErasedScalarValueGraph(candidate.Receiver, allDefinitions))
                continue;

            rewrites.Add((candidate.Instruction, candidate.ReceiverIndex));
        }

        return RewriteToThis(method, rewrites);
    }

    private static IEnumerable<(
        Instruction Instruction,
        int ReceiverIndex,
        LocalVariable Receiver,
        TypeAnalysisContext ReceiverType,
        TypeAnalysisContext TargetType)> EnumerateCandidates(MethodAnalysisContext method)
    {
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
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

            yield return (instruction, receiverIndex, receiver, receiverType, targetType);
        }
    }

    private static int RewriteToThis(
        MethodAnalysisContext method,
        IReadOnlyList<(Instruction Instruction, int ReceiverIndex)> rewrites)
    {
        if (rewrites.Count == 0)
            return 0;

        var thisLocal = GetOrCreateThisLocal(method);
        if (thisLocal == null)
            return 0;

        foreach (var (instruction, receiverIndex) in rewrites)
        {
            instruction.SetOperand(receiverIndex, thisLocal);
        }

        return rewrites.Count;
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
