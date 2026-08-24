using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Logging;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 以已经解析的托管调用签名收紧唯一结果局部的引用类型。
/// </summary>
/// <remarks>
/// ARM64 寄存器先被后续 LINQ/接口参数登记成上界时，直接调用的声明返回类型仍是权威下界。
/// 本规则只处理单一定义、引用类型和可验证赋值关系；寄存器复用、值类型及冲突类型保持红门。
/// </remarks>
public static class ManagedCallResultTypeRecovery
{
    public static int Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is null)
            return 0;

        var definitions = method.ControlFlowGraph.Instructions
            .Where(instruction => instruction.Destination is LocalVariable)
            .GroupBy(instruction => (LocalVariable)instruction.Destination!)
            .ToDictionary(group => group.Key, group => group.Count());
        var recovered = 0;

        foreach (var instruction in method.ControlFlowGraph.Instructions)
        {
            if (instruction is not
                {
                    OpCode: OpCode.Call,
                    Operands: [MethodAnalysisContext target, LocalVariable destination, ..],
                })
                continue;
            if (!definitions.TryGetValue(destination, out var definitionCount)
                || destination.Type is not { IsValueType: false } currentType
                || target.ReturnType is not { IsValueType: false } returnType
                || returnType.FullName == "System.Void")
                continue;

            var equivalent = GenericCallRebinder.TypesEquivalent(currentType, returnType);
            if (equivalent)
                continue;

            var assignable = returnType is not (PointerTypeAnalysisContext or ByRefTypeAnalysisContext)
                             && currentType is not (PointerTypeAnalysisContext or ByRefTypeAnalysisContext)
                             && GenericCallRebinder.IsAssignableToManagedProjection(returnType, currentType);
            Logger.VerboseNewline(
                $"托管调用结果候选：{target.FullName}，当前={currentType.FullName}，" +
                $"声明={returnType.FullName}，定义数={definitionCount}，可收紧={assignable}。",
                nameof(ManagedCallResultTypeRecovery));
            if (definitionCount != 1 || !assignable)
                continue;

            destination.Type = returnType;
            recovered++;
        }

        return recovered;
    }
}
