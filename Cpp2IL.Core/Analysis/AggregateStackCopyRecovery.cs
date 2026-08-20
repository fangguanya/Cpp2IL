using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Logging;
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

    private readonly record struct AggregateCopy(
        StackTransfer Vector,
        StackTransfer Scalar,
        GenericInstanceTypeAnalysisContext AggregateType,
        FieldAnalysisContext TailField,
        int TailOffset);

    private readonly record struct FieldAlias(
        LocalVariable StackField,
        LocalVariable Aggregate,
        FieldAnalysisContext Field,
        int Offset);

    public static bool Run(MethodAnalysisContext method)
    {
        var changed = false;
        foreach (var block in method.ControlFlowGraph!.Blocks)
            changed |= RecoverBlock(block.Instructions);

        return changed;
    }

    internal static bool RecoverBlock(IReadOnlyList<Instruction> instructions)
    {
        var changed = false;
        foreach (var copy in CollectAggregateCopies(instructions))
        {
            changed |= SetExactType(copy.Vector.TransferLocal, copy.AggregateType);
            changed |= SetExactType(copy.Vector.DestinationStack, copy.AggregateType);
            changed |= SetExactType(copy.Scalar.SourceStack, copy.TailField.FieldType);
            changed |= SetExactType(copy.Scalar.TransferLocal, copy.TailField.FieldType);
            changed |= SetExactType(copy.Scalar.DestinationStack, copy.TailField.FieldType);

            Logger.VerboseNewline(
                $"ARM64聚合复制证据：source={copy.Vector.SourceStack}，destination={copy.Vector.DestinationStack}，" +
                $"tailSource={copy.Scalar.SourceStack}，tailDestination={copy.Scalar.DestinationStack}，" +
                $"field={copy.TailField.Name}，fieldOffset=0x{copy.TailOffset:X}，" +
                $"vector={copy.Vector.LoadIndex}->{copy.Vector.StoreIndex}，" +
                $"scalar={copy.Scalar.LoadIndex}->{copy.Scalar.StoreIndex}");
        }

        return changed;
    }

    public static bool RewriteResolvedCopies(MethodAnalysisContext method)
    {
        var aliases = new List<FieldAlias>();
        var changed = false;
        foreach (var block in method.ControlFlowGraph!.Blocks)
            changed |= RewriteResolvedBlockCore(block.Instructions, aliases);

        foreach (var block in method.ControlFlowGraph.Blocks)
            changed |= RewriteFieldAliases(block.Instructions, aliases);

        return changed;
    }

    internal static bool RewriteResolvedBlock(IReadOnlyList<Instruction> instructions)
    {
        var aliases = new List<FieldAlias>();
        var changed = RewriteResolvedBlockCore(instructions, aliases);
        return RewriteFieldAliases(instructions, aliases) || changed;
    }

    private static bool RewriteResolvedBlockCore(
        IReadOnlyList<Instruction> instructions,
        ICollection<FieldAlias> aliases)
    {
        var changed = false;
        foreach (var copy in CollectAggregateCopies(instructions))
        {
            if (!GenericCallRebinder.TypesEquivalent(
                    copy.Vector.DestinationStack.Type,
                    copy.AggregateType))
                continue;

            var vectorStore = instructions[copy.Vector.StoreIndex];
            if (vectorStore.OpCode != OpCode.Move
                || vectorStore.Operands.Count < 2
                || !ReferenceEquals(vectorStore.Operands[0], copy.Vector.DestinationStack)
                || !ReferenceEquals(vectorStore.Operands[1], copy.Vector.TransferLocal))
                continue;

            if (TryRedirectAggregateReturnDestination(instructions, copy))
            {
                aliases.Add(new(
                    copy.Scalar.DestinationStack,
                    copy.Vector.DestinationStack,
                    copy.TailField,
                    copy.TailOffset));
                changed = true;
                continue;
            }

            // 类型已经收敛且标量尾块证明复制覆盖完整结构，此时才把 ABI 分块搬运
            // 还原为一次托管值赋值；随后对字段槽的访问统一指向目标聚合字段。
            vectorStore.SetOperand(1, copy.Vector.SourceStack);
            foreach (var scalarIndex in new[] { copy.Scalar.LoadIndex, copy.Scalar.StoreIndex })
            {
                var scalarInstruction = instructions[scalarIndex];
                scalarInstruction.OpCode = OpCode.Nop;
                scalarInstruction.SetOperands();
            }

            aliases.Add(new(
                copy.Scalar.DestinationStack,
                copy.Vector.DestinationStack,
                copy.TailField,
                copy.TailOffset));
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// 隐藏返回缓冲区紧接着被完整复制到最终栈槽时，把托管调用结果直接绑定到最终槽。
    /// 原生临时缓冲区随后常被异常清理参数覆盖；若只保留 source 到 destination 的普通
    /// Move，SSA 简化会把覆盖值错误传播为 default(Enumerator)。只有源聚合槽恰好由一个
    /// 同类型调用定义，且除本次向量加载外没有其它读取时才重定向。
    /// </summary>
    private static bool TryRedirectAggregateReturnDestination(
        IReadOnlyList<Instruction> instructions,
        AggregateCopy copy)
    {
        var definingCalls = instructions
            .Select((instruction, index) => (instruction, index))
            .Where(item => item.index < copy.Vector.LoadIndex
                && item.instruction.IsCall
                && ReferenceEquals(item.instruction.Destination, copy.Vector.SourceStack)
                && item.instruction.Operands[0] is MethodAnalysisContext target
                && GenericCallRebinder.TypesEquivalent(target.ReturnType, copy.AggregateType))
            .ToArray();
        if (definingCalls.Length != 1)
            return false;

        var definingCall = definingCalls[0].instruction;
        for (var instructionIndex = 0; instructionIndex < instructions.Count; instructionIndex++)
        {
            var instruction = instructions[instructionIndex];
            for (var operandIndex = 0; operandIndex < instruction.Operands.Count; operandIndex++)
            {
                if (!ReferenceEquals(instruction.Operands[operandIndex], copy.Vector.SourceStack))
                    continue;

                var isDefinition = ReferenceEquals(instruction, definingCall)
                    && operandIndex == 1;
                var isAggregateLoad = instructionIndex == copy.Vector.LoadIndex
                    && operandIndex == 1;
                if (!isDefinition && !isAggregateLoad)
                    return false;
            }
        }

        definingCall.SetOperand(1, copy.Vector.DestinationStack);
        foreach (var index in new[]
                 {
                     copy.Vector.LoadIndex,
                     copy.Scalar.LoadIndex,
                     copy.Vector.StoreIndex,
                     copy.Scalar.StoreIndex,
                 })
        {
            var instruction = instructions[index];
            instruction.OpCode = OpCode.Nop;
            instruction.SetOperands();
        }

        return true;
    }

    private static bool RewriteFieldAliases(
        IReadOnlyList<Instruction> instructions,
        IReadOnlyCollection<FieldAlias> aliases)
    {
        var changed = false;
        foreach (var instruction in instructions)
        {
            for (var index = 0; index < instruction.Operands.Count; index++)
            {
                if (instruction.Operands[index] is not LocalVariable local)
                    continue;

                var matches = aliases.Where(alias => ReferenceEquals(alias.StackField, local)).ToArray();
                if (matches.Length != 1)
                    continue;

                var alias = matches[0];
                instruction.SetOperand(index, new FieldReference(
                    alias.Field,
                    alias.Aggregate,
                    alias.Offset));
                if (instruction is
                    {
                        OpCode: OpCode.Move,
                        Operands: [LocalVariable destination, _]
                    }
                    && index == 1)
                {
                    // 中文注释：聚合字段身份来自同一次完整结构复制，强于物理寄存器复用
                    // 遗留的 IDisposable& 等类型；此时仍在 SSA 中，可精确覆盖该唯一结果。
                    destination.Type = alias.Field.FieldType;
                }
                changed = true;
            }
        }

        return changed;
    }

    private static List<AggregateCopy> CollectAggregateCopies(IReadOnlyList<Instruction> instructions)
    {
        var vectorTransfers = CollectTransfers(instructions, IsVectorRegister);
        var scalarTransfers = CollectTransfers(instructions, IsScalarRegister);
        var copies = new List<AggregateCopy>();

        foreach (var vector in vectorTransfers)
        {
            if (!TryGetStackOffset(vector.SourceStack, out var sourceBaseOffset)
                || !TryGetStackOffset(vector.DestinationStack, out var destinationBaseOffset)
                || vector.SourceStack.Type is not GenericInstanceTypeAnalysisContext
                {
                    IsValueType: true
                } aggregateType)
                continue;

            foreach (var scalar in scalarTransfers)
            {
                // ARM64可以先读取标量尾字段，再读取16字节向量主体；两次读取只需都发生在
                // 两次写入之前，加载顺序和写入顺序本身不影响两个互不重叠栈区间的复制语义。
                if (!FormsCompleteCopyWindow(vector, scalar)
                    || !TryGetStackOffset(scalar.SourceStack, out var sourceFieldOffset)
                    || !TryGetStackOffset(scalar.DestinationStack, out var destinationFieldOffset))
                    continue;

                var sourceRelativeOffset = sourceFieldOffset - sourceBaseOffset;
                var destinationRelativeOffset = destinationFieldOffset - destinationBaseOffset;
                if (sourceRelativeOffset <= 0 || sourceRelativeOffset != destinationRelativeOffset)
                    continue;

                var matchingVectors = vectorTransfers.Count(candidate =>
                    FormsCompleteCopyWindow(candidate, scalar)
                    && TryGetStackOffset(candidate.SourceStack, out var candidateSourceBase)
                    && TryGetStackOffset(candidate.DestinationStack, out var candidateDestinationBase)
                    && sourceFieldOffset - candidateSourceBase == destinationFieldOffset - candidateDestinationBase);
                if (matchingVectors != 1
                    || GenericInstanceFieldLayout.FindConcreteFieldAtOffset(
                        aggregateType,
                        sourceRelativeOffset) is not { } field)
                    continue;

                copies.Add(new(vector, scalar, aggregateType, field, sourceRelativeOffset));
            }
        }

        return copies;
    }

    /// <summary>
    /// 两个源块必须在任一目标块写入前全部读取，防止把交叠搬运或寄存器被覆盖的序列误判为聚合复制。
    /// </summary>
    private static bool FormsCompleteCopyWindow(StackTransfer vector, StackTransfer scalar)
        => Math.Max(vector.LoadIndex, scalar.LoadIndex) < Math.Min(vector.StoreIndex, scalar.StoreIndex);

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
