using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 恢复ARM64以16字节向量块加标量尾块复制的大值类型栈局部，并把尾块绑定到具体字段类型。
/// </summary>
public static class AggregateStackCopyRecovery
{
    private readonly record struct StackTransfer(
        LocalVariable SourceStack,
        LocalVariable TransferLocal,
        LocalVariable DestinationStack,
        int LoadIndex,
        int StoreIndex);

    public static bool Run(MethodAnalysisContext method)
    {
        var changed = false;
        foreach (var block in method.ControlFlowGraph!.Blocks)
            changed |= RecoverBlock(block.Instructions);

        return changed;
    }

    internal static bool RecoverBlock(IReadOnlyList<Instruction> instructions)
    {
        var vectorTransfers = CollectTransfers(instructions, IsVectorRegister);
        var scalarTransfers = CollectTransfers(instructions, IsScalarRegister);
        var changed = false;

        foreach (var vector in vectorTransfers)
        {
            if (!TryGetStackOffset(vector.SourceStack, out var sourceBaseOffset)
                || !TryGetStackOffset(vector.DestinationStack, out var destinationBaseOffset)
                || vector.SourceStack.Type is not GenericInstanceTypeAnalysisContext
                {
                    IsValueType: true
                } aggregateType)
                continue;

            changed |= SetExactType(vector.TransferLocal, aggregateType);
            changed |= SetExactType(vector.DestinationStack, aggregateType);

            foreach (var scalar in scalarTransfers)
            {
                if (scalar.LoadIndex < vector.LoadIndex
                    || scalar.StoreIndex < vector.StoreIndex
                    || !TryGetStackOffset(scalar.SourceStack, out var sourceFieldOffset)
                    || !TryGetStackOffset(scalar.DestinationStack, out var destinationFieldOffset))
                    continue;

                var sourceRelativeOffset = sourceFieldOffset - sourceBaseOffset;
                var destinationRelativeOffset = destinationFieldOffset - destinationBaseOffset;
                if (sourceRelativeOffset <= 0 || sourceRelativeOffset != destinationRelativeOffset)
                    continue;

                var matchingVectors = vectorTransfers.Count(candidate =>
                    candidate.LoadIndex <= scalar.LoadIndex
                    && candidate.StoreIndex <= scalar.StoreIndex
                    && TryGetStackOffset(candidate.SourceStack, out var candidateSourceBase)
                    && TryGetStackOffset(candidate.DestinationStack, out var candidateDestinationBase)
                    && sourceFieldOffset - candidateSourceBase == destinationFieldOffset - candidateDestinationBase);
                if (matchingVectors != 1)
                    continue;

                if (GenericInstanceFieldLayout.FindConcreteFieldAtOffset(aggregateType, sourceRelativeOffset) is not { } field)
                    continue;

                changed |= SetExactType(scalar.SourceStack, field.FieldType);
                changed |= SetExactType(scalar.TransferLocal, field.FieldType);
                changed |= SetExactType(scalar.DestinationStack, field.FieldType);
            }
        }

        return changed;
    }

    private static List<StackTransfer> CollectTransfers(
        IReadOnlyList<Instruction> instructions,
        Func<LocalVariable, bool> transferPredicate)
    {
        var loads = new Dictionary<LocalVariable, (LocalVariable Stack, int Index)>();
        var transfers = new List<StackTransfer>();

        for (var index = 0; index < instructions.Count; index++)
        {
            var instruction = instructions[index];
            if (instruction.OpCode != OpCode.Move
                || instruction.Operands.Count < 2
                || instruction.Operands[0] is not LocalVariable destination
                || instruction.Operands[1] is not LocalVariable source)
                continue;

            if (transferPredicate(destination) && IsStackLocal(source))
            {
                loads[destination] = (source, index);
                continue;
            }

            if (IsStackLocal(destination)
                && transferPredicate(source)
                && loads.TryGetValue(source, out var load))
                transfers.Add(new(load.Stack, source, destination, load.Index, index));
        }

        return transfers;
    }

    private static bool SetExactType(LocalVariable local, TypeAnalysisContext type)
    {
        if (GenericCallRebinder.TypesEquivalent(local.Type, type))
            return false;

        // 分析不动点只能从未知或共享object占位单向晋级到具体类型。
        // 两个不同具体聚合若碰巧复用同一物理栈槽，保持既有结论并交给上层消歧，
        // 绝不在两者之间来回覆盖。
        if (!CanPromoteSharedPlaceholder(local.Type, type))
            return false;

        local.Type = type;
        return true;
    }

    private static bool CanPromoteSharedPlaceholder(
        TypeAnalysisContext? current,
        TypeAnalysisContext exact)
    {
        if (current == null || current.FullName == "System.Object")
            return true;

        if (current is not GenericInstanceTypeAnalysisContext currentGeneric
            || exact is not GenericInstanceTypeAnalysisContext exactGeneric
            || currentGeneric.GenericType.FullName != exactGeneric.GenericType.FullName
            || currentGeneric.GenericArguments.Count != exactGeneric.GenericArguments.Count)
            return false;

        return currentGeneric.GenericArguments.All(argument => argument.FullName == "System.Object")
               && exactGeneric.GenericArguments.Any(argument => argument.FullName != "System.Object");
    }

    private static bool IsStackLocal(LocalVariable local)
        => local.Register.Name.StartsWith("stack_", StringComparison.Ordinal);

    private static bool IsVectorRegister(LocalVariable local)
        => local.Register.Name.Length > 1
           && local.Register.Name[0] == 'V'
           && int.TryParse(local.Register.Name.Substring(1), out _);

    private static bool IsScalarRegister(LocalVariable local)
        => local.Register.Name.Length > 1
           && local.Register.Name[0] == 'X'
           && int.TryParse(local.Register.Name.Substring(1), out _);

    internal static bool TryGetStackOffset(LocalVariable local, out int offset)
    {
        const string prefix = "stack_";
        var name = local.Register.Name;
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
        {
            offset = 0;
            return false;
        }

        var value = name.Substring(prefix.Length);
        var negative = value.StartsWith("-", StringComparison.Ordinal);
        if (negative)
            value = value.Substring(1);

        if (!int.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var magnitude))
        {
            offset = 0;
            return false;
        }

        offset = negative ? -magnitude : magnitude;
        return true;
    }
}
