using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;

namespace Cpp2IL.Core;

/// <summary>
/// 对单个已生成方法体执行一次确定性的标签与求值栈验证。
/// </summary>
internal static class CilStackValidator
{
    internal readonly record struct ValidationResult(int InstructionCount, int MaxStack);

    internal static ValidationResult Validate(CilMethodBody body, string methodFullName)
    {
        try
        {
            // 标签验证同时计算指令偏移；栈计算复用这些偏移，禁止重复遍历计算偏移。
            body.VerifyLabels();
            var maxStack = body.ComputeMaxStack(calculateOffsets: false);
            body.MaxStack = maxStack;
            body.ComputeMaxStackOnBuild = false;
            body.VerifyLabelsOnBuild = false;
            return new ValidationResult(body.Instructions.Count, maxStack);
        }
        catch (StackImbalanceException exception)
        {
            // 所有深度诊断共享一次数据流结果，禁止对大型失败方法重复计算可达栈状态。
            var reachableDepths = ComputeReachableStackDepths(exception.Body);
            throw new DecompilerException(
                $"CIL 栈验证失败：method={methodFullName}，offset=IL_{exception.Offset:X4}，" +
                $"detail={exception.Message}，window={FormatInstructionWindow(exception.Body, exception.Offset)}，" +
                $"incoming={FormatIncomingEdges(exception.Body, exception.Offset)}，" +
                $"sequenceEntry={FormatSequenceEntry(exception.Body, exception.Offset)}，" +
                $"entryDepths={FormatSequenceEntryDepths(exception.Body, exception.Offset, reachableDepths)}，" +
                $"entryEdgeDepths={FormatSequenceEntryEdgeDepths(exception.Body, exception.Offset, reachableDepths)}，" +
                $"depthConflicts={FormatDepthConflicts(exception.Body, reachableDepths)}",
                exception);
        }
        catch (InvalidCilInstructionException exception)
        {
            throw new DecompilerException(
                $"CIL 标签验证失败：method={methodFullName}，detail={exception.Message}",
                exception);
        }
        catch (AggregateException exception)
        {
            throw new DecompilerException(
                $"CIL 标签验证失败：method={methodFullName}，errors={exception.InnerExceptions.Count}",
                exception);
        }
    }

    /// <summary>
    /// 生成失败指令前后各三条的紧凑窗口，使全量账本可以直接追到具体发射器。
    /// </summary>
    private static string FormatInstructionWindow(CilMethodBody body, int failureOffset)
    {
        var instructions = body.Instructions;
        if (instructions.Count == 0)
            return "EMPTY";

        var failureIndex = -1;
        for (var index = 0; index < instructions.Count; index++)
        {
            if (instructions[index].Offset != failureOffset)
                continue;

            failureIndex = index;
            break;
        }

        // 异常偏移不落在指令首地址时，选择其后的第一条；末尾异常选择最后一条。
        if (failureIndex < 0)
        {
            failureIndex = instructions.Count - 1;
            for (var index = 0; index < instructions.Count; index++)
            {
                if (instructions[index].Offset < failureOffset)
                    continue;

                failureIndex = index;
                break;
            }
        }

        // 短方法完整输出，避免分支目标位于三条窗口之外；大型方法仍保持固定邻接窗口。
        var first = instructions.Count <= 128 ? 0 : Math.Max(0, failureIndex - 3);
        var last = instructions.Count <= 128 ? instructions.Count - 1 : Math.Min(instructions.Count - 1, failureIndex + 3);
        var window = new List<string>(last - first + 1);
        for (var index = first; index <= last; index++)
        {
            var instruction = instructions[index];
            var marker = index == failureIndex ? ">" : string.Empty;
            var operand = instruction.Operand?.ToString()?.Replace('\r', ' ').Replace('\n', ' ') ?? string.Empty;
            window.Add($"{marker}IL_{instruction.Offset:X4}:{instruction.OpCode.Code}:{operand}");
        }

        return string.Join("|", window);
    }

    /// <summary>
    /// 列出失败位置的所有显式控制流入边，避免仅凭邻接窗口误判大型方法中的远距离来源。
    /// </summary>
    private static string FormatIncomingEdges(CilMethodBody body, int failureOffset)
    {
        var incomingEdges = new List<string>();
        foreach (var instruction in body.Instructions)
        {
            switch (instruction.Operand)
            {
                case ICilLabel label when label.Offset == failureOffset:
                    incomingEdges.Add(FormatEdge(instruction));
                    break;
                case IEnumerable<ICilLabel> labels:
                    foreach (var target in labels)
                    {
                        if (target.Offset == failureOffset)
                            incomingEdges.Add(FormatEdge(instruction));
                    }

                    break;
            }
        }

        return incomingEdges.Count == 0 ? "NONE" : string.Join("|", incomingEdges);
    }

    private static string FormatEdge(CilInstruction instruction)
    {
        var operand = instruction.Operand?.ToString()?.Replace('\r', ' ').Replace('\n', ' ') ?? string.Empty;
        return $"IL_{instruction.Offset:X4}:{instruction.OpCode.Code}:{operand}";
    }

    /// <summary>
    /// 回溯失败指令所在的顺序执行片段，并报告片段入口的显式入边。
    /// </summary>
    private static string FormatSequenceEntry(CilMethodBody body, int failureOffset)
    {
        var instructions = body.Instructions;
        var failureIndex = -1;
        for (var index = 0; index < instructions.Count; index++)
        {
            if (instructions[index].Offset == failureOffset)
            {
                failureIndex = index;
                break;
            }
        }

        if (failureIndex < 0)
            return "UNKNOWN";

        var entryIndex = failureIndex;
        while (entryIndex > 0 && CanFallThrough(instructions[entryIndex - 1]))
            entryIndex--;

        var entry = instructions[entryIndex];
        return $"IL_{entry.Offset:X4}:{entry.OpCode.Code};edges={FormatIncomingEdges(body, entry.Offset)}";
    }

    private static bool CanFallThrough(CilInstruction instruction)
    {
        return instruction.OpCode.FlowControl is not (CilFlowControl.Branch or CilFlowControl.Return or CilFlowControl.Throw);
    }

    /// <summary>
    /// 计算失败片段入口可达的求值栈深度集合，直接区分发射器多压值与分支结构错误。
    /// </summary>
    private static string FormatSequenceEntryDepths(
        CilMethodBody body,
        int failureOffset,
        IReadOnlyDictionary<CilInstruction, SortedSet<int>> depths)
    {
        var instructions = body.Instructions;
        var failureIndex = FindInstructionIndex(instructions, failureOffset);
        if (failureIndex < 0)
            return "UNKNOWN";

        var entryIndex = failureIndex;
        while (entryIndex > 0 && CanFallThrough(instructions[entryIndex - 1]))
            entryIndex--;

        return depths.TryGetValue(instructions[entryIndex], out var entryDepths)
            ? string.Join(",", entryDepths)
            : "UNREACHABLE";
    }

    private static string FormatSequenceEntryEdgeDepths(
        CilMethodBody body,
        int failureOffset,
        IReadOnlyDictionary<CilInstruction, SortedSet<int>> depths)
    {
        var instructions = body.Instructions;
        var failureIndex = FindInstructionIndex(instructions, failureOffset);
        if (failureIndex < 0)
            return "UNKNOWN";

        var entryIndex = failureIndex;
        while (entryIndex > 0 && CanFallThrough(instructions[entryIndex - 1]))
            entryIndex--;

        var entryOffset = instructions[entryIndex].Offset;
        var edges = new List<string>();
        foreach (var instruction in instructions)
        {
            if (!TargetsOffset(instruction, entryOffset) || !depths.TryGetValue(instruction, out var sourceDepths))
                continue;

            var resultingDepths = new SortedSet<int>();
            foreach (var sourceDepth in sourceDepths)
            {
                var result = sourceDepth - instruction.GetStackPopCount(body) + instruction.GetStackPushCount();
                if (result >= 0)
                    resultingDepths.Add(result);
            }

            var sourceEntry = FindSequenceEntryInstruction(body, instruction.Offset);
            var sourceEntryDepths = sourceEntry != null && depths.TryGetValue(sourceEntry, out var values)
                ? string.Join(",", values)
                : "UNREACHABLE";
            var sourceEntryOffset = sourceEntry?.Offset ?? -1;
            edges.Add(
                $"IL_{instruction.Offset:X4}:{string.Join(",", resultingDepths)};" +
                $"sourceEntry=IL_{sourceEntryOffset:X4};sourceEntryDepths={sourceEntryDepths};" +
                $"sourceEntryEdges={FormatIncomingEdgesWithDepths(body, sourceEntryOffset, depths)};" +
                $"sourceWindow={FormatInstructionWindow(body, instruction.Offset)}");
        }

        return edges.Count == 0 ? "NONE" : string.Join("|", edges);
    }

    private static bool TargetsOffset(CilInstruction instruction, int targetOffset)
    {
        if (instruction.Operand is ICilLabel label)
            return label.Offset == targetOffset;

        if (instruction.Operand is not IEnumerable<ICilLabel> labels)
            return false;

        foreach (var target in labels)
        {
            if (target.Offset == targetOffset)
                return true;
        }

        return false;
    }

    private static CilInstruction? FindSequenceEntryInstruction(CilMethodBody body, int offset)
    {
        var instructions = body.Instructions;
        var index = FindInstructionIndex(instructions, offset);
        if (index < 0)
            return null;

        var visitedOffsets = new HashSet<int>();
        while (visitedOffsets.Add(instructions[index].Offset))
        {
            while (index > 0 && CanFallThrough(instructions[index - 1]))
                index--;

            // CIL 生成器会用无条件桥接分支连接块；单一桥接入边属于同一顺序片段，继续向前追溯。
            CilInstruction? soleBridge = null;
            foreach (var candidate in instructions)
            {
                if (!TargetsOffset(candidate, instructions[index].Offset) || candidate.OpCode.FlowControl != CilFlowControl.Branch)
                    continue;

                if (soleBridge != null)
                    return instructions[index];

                soleBridge = candidate;
            }

            if (soleBridge == null)
                break;

            index = FindInstructionIndex(instructions, soleBridge.Offset);
        }

        return instructions[index];
    }

    private static string FormatIncomingEdgesWithDepths(
        CilMethodBody body,
        int targetOffset,
        IReadOnlyDictionary<CilInstruction, SortedSet<int>> depths)
    {
        var edges = new List<string>();
        foreach (var instruction in body.Instructions)
        {
            if (!TargetsOffset(instruction, targetOffset) || !depths.TryGetValue(instruction, out var sourceDepths))
                continue;

            var resultingDepths = new SortedSet<int>();
            foreach (var sourceDepth in sourceDepths)
            {
                var result = sourceDepth - instruction.GetStackPopCount(body) + instruction.GetStackPushCount();
                if (result >= 0)
                    resultingDepths.Add(result);
            }

            edges.Add(
                $"IL_{instruction.Offset:X4}:{string.Join(",", resultingDepths)};" +
                $"window={FormatInstructionWindow(body, instruction.Offset)}");
        }

        return edges.Count == 0 ? "NONE" : string.Join("|", edges);
    }

    private static string FormatDepthConflicts(
        CilMethodBody body,
        IReadOnlyDictionary<CilInstruction, SortedSet<int>> depths)
    {
        var conflicts = new List<string>();
        foreach (var pair in depths.OrderBy(candidate => candidate.Key.Offset))
        {
            var instruction = pair.Key;
            var instructionDepths = pair.Value;
            if (instructionDepths.Count < 2)
                continue;

            conflicts.Add(
                $"IL_{instruction.Offset:X4}:{instruction.OpCode.Code}:{string.Join(",", instructionDepths)};" +
                $"entry={FormatSequenceEntry(body, instruction.Offset)};" +
                $"edges={FormatSequenceEntryEdgeDepths(body, instruction.Offset, depths)}");
            if (conflicts.Count == 12)
                break;
        }

        return conflicts.Count == 0 ? "NONE" : string.Join("|", conflicts);
    }

    private static int FindInstructionIndex(IList<CilInstruction> instructions, int offset)
    {
        for (var index = 0; index < instructions.Count; index++)
        {
            if (instructions[index].Offset == offset)
                return index;
        }

        return -1;
    }

    private static Dictionary<CilInstruction, SortedSet<int>> ComputeReachableStackDepths(CilMethodBody body)
    {
        var instructions = body.Instructions;
        var result = new Dictionary<CilInstruction, SortedSet<int>>();
        if (instructions.Count == 0)
            return result;

        var indexes = new Dictionary<CilInstruction, int>(instructions.Count);
        for (var index = 0; index < instructions.Count; index++)
            indexes[instructions[index]] = index;

        var pending = new Queue<(CilInstruction Instruction, int Depth)>();
        pending.Enqueue((instructions[0], 0));
        while (pending.Count > 0)
        {
            var (instruction, depth) = pending.Dequeue();
            if (!result.TryGetValue(instruction, out var knownDepths))
            {
                knownDepths = new SortedSet<int>();
                result[instruction] = knownDepths;
            }

            if (!knownDepths.Add(depth))
                continue;

            var nextDepth = depth - instruction.GetStackPopCount(body) + instruction.GetStackPushCount();
            // 诊断遍历只保留 CLR 可表示的非负深度，并设置固定上界，防止损坏循环无限增栈。
            if (nextDepth is < 0 or > 1024)
                continue;

            foreach (var successor in GetSuccessors(instruction, instructions, indexes))
                pending.Enqueue((successor, nextDepth));
        }

        return result;
    }

    private static IEnumerable<CilInstruction> GetSuccessors(
        CilInstruction instruction,
        IList<CilInstruction> instructions,
        IReadOnlyDictionary<CilInstruction, int> indexes)
    {
        if (instruction.Operand is ICilLabel label && label.Offset >= 0)
        {
            var targetIndex = FindInstructionIndex(instructions, label.Offset);
            if (targetIndex >= 0)
                yield return instructions[targetIndex];
        }
        else if (instruction.Operand is IEnumerable<ICilLabel> labels)
        {
            foreach (var target in labels)
            {
                var targetIndex = FindInstructionIndex(instructions, target.Offset);
                if (targetIndex >= 0)
                    yield return instructions[targetIndex];
            }
        }

        if (instruction.OpCode.FlowControl is CilFlowControl.Branch or CilFlowControl.Return or CilFlowControl.Throw)
            yield break;

        var fallThroughIndex = indexes[instruction] + 1;
        if (fallThroughIndex < instructions.Count)
            yield return instructions[fallThroughIndex];
    }
}
