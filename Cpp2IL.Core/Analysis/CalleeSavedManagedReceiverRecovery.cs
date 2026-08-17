using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Extensions;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 恢复 ARM64 被调用方保存寄存器中跨调用存活的托管实例接收者。
/// </summary>
/// <remarks>
/// 异常清理边会把 X19-X28 的入口旧值并入 Phi；若旧值没有托管定义，退 SSA 会丢弃真实
/// 赋值边，留下一个未定义接收者。本恢复器仅在接收者无定义、位于被调用方保存寄存器、
/// 此前恰有一个相容的托管调用/分配结果时重绑，并同步具体化共享泛型调用目标。
/// </remarks>
public static class CalleeSavedManagedReceiverRecovery
{
    public static int Run(MethodAnalysisContext method)
    {
        var instructions = method.ControlFlowGraph!.Instructions;
        var definitions = instructions
            .Where(instruction => instruction.Destination is LocalVariable)
            .GroupBy(instruction => (LocalVariable)instruction.Destination!)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var producers = instructions
            .Where(instruction => instruction.OpCode is OpCode.Call or OpCode.Newobj
                                  && instruction.Destination is LocalVariable { Type: { IsValueType: false } })
            .ToArray();
        var recovered = 0;

        foreach (var call in instructions.Where(instruction => instruction.IsCall).ToArray())
        {
            if (call.Operands[0] is not MethodAnalysisContext { IsStatic: false } target)
                continue;

            var receiverIndex = call.OpCode == OpCode.CallVoid ? 1 : 2;
            if (receiverIndex >= call.Operands.Count
                || call.Operands[receiverIndex] is not LocalVariable receiver
                || definitions.ContainsKey(receiver)
                || method.ParameterLocals.Contains(receiver)
                || !IsCalleeSavedArm64Register(receiver.Register.Name))
                continue;

            var candidates = producers
                .Where(producer => producer.Index >= 0
                                   && call.Index >= 0
                                   && producer.Index < call.Index
                                   && producer.Destination is LocalVariable candidate
                                   && IsCompatible(candidate.Type, target.DeclaringType))
                .Select(producer => (LocalVariable)producer.Destination!)
                .Distinct()
                .ToArray();
            if (candidates.Length != 1)
                continue;

            call.SetOperand(receiverIndex, candidates[0]);
            GenericCallRebinder.TryRebind(call);
            recovered++;
        }

        return recovered;
    }

    private static bool IsCompatible(TypeAnalysisContext? candidate, TypeAnalysisContext? declaringType)
    {
        if (candidate == null || declaringType == null || candidate.IsValueType)
            return false;

        return GenericCallRebinder.TypesEquivalent(candidate, declaringType)
               || candidate.IsAssignableTo(declaringType)
               || IsGenericEnumeratorAssignableToNonGeneric(candidate, declaringType)
               || GenericCallRebinder.IsSharedObjectPlaceholder(declaringType, candidate);
    }

    private static bool IsGenericEnumeratorAssignableToNonGeneric(
        TypeAnalysisContext candidate,
        TypeAnalysisContext declaringType)
        => declaringType.FullName == "System.Collections.IEnumerator"
           && candidate is GenericInstanceTypeAnalysisContext genericCandidate
           && genericCandidate.GenericType.FullName == "System.Collections.Generic.IEnumerator`1";

    private static bool IsCalleeSavedArm64Register(string? name)
    {
        if (name is not { Length: >= 3 } || name[0] != 'X')
            return false;

        return int.TryParse(name.Substring(1), out var number) && number is >= 19 and <= 28;
    }
}
