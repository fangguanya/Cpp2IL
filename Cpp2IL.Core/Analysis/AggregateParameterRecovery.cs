using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 将AAPCS64以连续X寄存器传入的小型值类型参数恢复为托管参数的具体字段读取。
/// </summary>
public static class AggregateParameterRecovery
{
    private const int RegisterSlotSize = 8;

    private readonly record struct Projection(
        LocalVariable PhysicalLocal,
        FieldReference Field);

    public static int Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph == null || method.Parameters.Count == 0)
            return 0;

        var projections = CollectProjections(method);
        if (projections.Count == 0)
            return 0;

        var rewritten = 0;
        foreach (var instruction in method.ControlFlowGraph.Instructions)
        {
            for (var operandIndex = 0; operandIndex < instruction.Operands.Count; operandIndex++)
            {
                if (instruction.Operands[operandIndex] is not LocalVariable local
                    || ReferenceEquals(instruction.Destination, local))
                    continue;

                var matches = projections
                    .Where(projection => ReferenceEquals(projection.PhysicalLocal, local))
                    .ToArray();
                if (matches.Length != 1)
                    continue;

                instruction.SetOperand(operandIndex, matches[0].Field);
                rewritten++;
            }
        }

        return rewritten;
    }

    private static List<Projection> CollectProjections(MethodAnalysisContext method)
    {
        var result = new List<Projection>();
        var integerRegisterIndex = method.IsStatic ? 0 : 1;

        foreach (var parameter in method.Parameters)
        {
            var parameterType = parameter.ParameterType;
            if (X64CallingConventionResolver.IsFloatingPoint(parameterType))
                continue;

            if (Arm64CallingConventionResolver.TryGetHomogeneousFloatingAggregateFields(
                    parameterType,
                    out _))
                continue;

            if (parameterType is not GenericInstanceTypeAnalysisContext
                {
                    IsValueType: true
                } aggregate
                || !TryGetRegisterFieldLayout(aggregate, out var fields))
            {
                integerRegisterIndex++;
                continue;
            }

            var parameterLocal = method.ParameterLocals.FirstOrDefault(local =>
                !local.IsThis
                && !local.IsMethodInfo
                && local.Name == parameter.ParameterName);
            if (parameterLocal == null)
            {
                integerRegisterIndex += fields.Count;
                continue;
            }

            for (var componentIndex = 0; componentIndex < fields.Count; componentIndex++)
            {
                var registerName = $"X{integerRegisterIndex + componentIndex}";
                var physicalLocal = method.Locals.FirstOrDefault(local =>
                    local.Register.Version == -1
                    && local.Register.Name == registerName);
                if (physicalLocal == null)
                    continue;

                result.Add(new(
                    physicalLocal,
                    new FieldReference(
                        fields[componentIndex].Field,
                        parameterLocal,
                        checked((int)fields[componentIndex].Offset))));
            }

            integerRegisterIndex += fields.Count;
        }

        return result;
    }

    /// <summary>
    /// 只接受每个八字节寄存器槽恰好对应一个字段的聚合体。
    /// 多字段共享同一槽或字段跨槽时保留原始证据，避免把位段组合猜成错误字段。
    /// </summary>
    private static bool TryGetRegisterFieldLayout(
        GenericInstanceTypeAnalysisContext aggregate,
        out IReadOnlyList<GenericInstanceFieldLayout.ConcreteFieldLayout> fields)
    {
        fields = [];
        var layout = GenericInstanceFieldLayout.GetConcreteFieldLayout(aggregate);
        if (layout is not { Count: > 0 }
            || layout.Count > 2)
            return false;

        for (var index = 0; index < layout.Count; index++)
        {
            var field = layout[index];
            if (field.Offset != index * RegisterSlotSize
                || field.Size != RegisterSlotSize)
                return false;
        }

        fields = layout;
        return true;
    }
}
