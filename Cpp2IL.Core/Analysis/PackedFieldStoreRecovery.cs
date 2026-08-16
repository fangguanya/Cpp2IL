using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 把 ARM64 对同一托管对象的成组零写恢复为逐字段默认值写入。
///
/// 优化器常用重叠的 STR/STUR 一次清零多个相邻字段，写入起点甚至位于某个整数内部。
/// 只有同一基本块、同一接收者、精确写宽、常量零、字段布局全覆盖且无中间观察同时成立时，
/// 才把整个连通写区间拆成字段写入；部分字段、未知结构体、歧义布局和非零载荷保持原图。
/// </summary>
public static class PackedFieldStoreRecovery
{
    private readonly record struct ByteRange(long Start, long End)
    {
        public bool Overlaps(ByteRange other) => Start < other.End && other.Start < End;
    }

    private sealed record StoreCandidate(
        Block Block,
        Instruction Instruction,
        LocalVariable Receiver,
        ByteRange Range);

    private sealed record FieldLayout(
        FieldAnalysisContext Field,
        ByteRange Range);

    public static int Run(MethodAnalysisContext method)
    {
        var cfg = method.ControlFlowGraph!;
        var definitions = MetadataResolver.BuildDefinitionIndex(cfg.Instructions);
        var replacedFields = 0;

        foreach (var block in cfg.Blocks)
        {
            var candidates = CollectCandidates(block, definitions);
            foreach (var receiverGroup in candidates.GroupBy(candidate => candidate.Receiver))
            {
                foreach (var component in ConnectedComponents(receiverGroup))
                    replacedFields += TryRewriteComponent(method, block, component);
            }
        }

        return replacedFields;
    }

    private static List<StoreCandidate> CollectCandidates(
        Block block,
        IReadOnlyDictionary<LocalVariable, Instruction[]> definitions)
    {
        var result = new List<StoreCandidate>();
        foreach (var instruction in block.Instructions)
        {
            if (instruction is not
                {
                    OpCode: OpCode.Move,
                    MemoryAccessWidthBits: > 0,
                    Operands: [MemoryOperand { Index: null, Scale: 0 } memory, Immediate { Value: 0 }]
                }
                || instruction.MemoryAccessWidthBits % 8 != 0
                || !MetadataResolver.TryResolveFieldBase(
                    memory,
                    definitions,
                    out var receiver,
                    out var start)
                || receiver.Type == null
                || receiver.Type is GenericInstanceTypeAnalysisContext
                || receiver.Type.GenericParameters.Count > 0)
                continue;

            var width = instruction.MemoryAccessWidthBits / 8;
            long end;
            try
            {
                end = checked(start + width);
            }
            catch (OverflowException)
            {
                continue;
            }

            if (start < 0 || end <= start)
                continue;

            result.Add(new(block, instruction, receiver, new(start, end)));
        }

        return result;
    }

    private static IEnumerable<List<StoreCandidate>> ConnectedComponents(
        IEnumerable<StoreCandidate> candidates)
    {
        var ordered = candidates
            .OrderBy(candidate => candidate.Range.Start)
            .ThenBy(candidate => candidate.Range.End)
            .ToArray();
        if (ordered.Length == 0)
            yield break;

        var current = new List<StoreCandidate> { ordered[0] };
        var currentEnd = ordered[0].Range.End;
        for (var i = 1; i < ordered.Length; i++)
        {
            var candidate = ordered[i];
            if (candidate.Range.Start <= currentEnd)
            {
                current.Add(candidate);
                currentEnd = Math.Max(currentEnd, candidate.Range.End);
                continue;
            }

            yield return current;
            current = [candidate];
            currentEnd = candidate.Range.End;
        }

        yield return current;
    }

    private static int TryRewriteComponent(
        MethodAnalysisContext method,
        Block block,
        IReadOnlyList<StoreCandidate> candidates)
    {
        var receiver = candidates[0].Receiver;
        if (receiver.Type == null)
            return 0;

        var ranges = MergeRanges(candidates.Select(candidate => candidate.Range));
        var layouts = GetFieldLayouts(receiver.Type, method.AppContext.Binary.PointerSizeBytes);
        if (layouts == null)
            return 0;

        var affected = layouts
            .Where(layout => ranges.Any(range => range.Overlaps(layout.Range)))
            .OrderBy(layout => layout.Range.Start)
            .ToArray();
        if (affected.Length < 2
            || affected.Any(layout => !IsCovered(layout.Range, ranges))
            || HasOverlappingFields(affected)
            || HasIntermediateObservation(block, candidates, affected, receiver))
            return 0;

        foreach (var candidate in candidates)
        {
            candidate.Instruction.OpCode = OpCode.Nop;
            candidate.Instruction.SetOperands();
        }

        var anchor = candidates
            .Select(candidate => block.Instructions.IndexOf(candidate.Instruction))
            .Where(index => index >= 0)
            .Min();
        var replacements = affected
            .Select(layout => new Instruction(
                candidates[0].Instruction.Index,
                OpCode.Move,
                new FieldReference(
                    layout.Field,
                    receiver,
                    checked((int)layout.Range.Start)),
                new Immediate(0)))
            .ToArray();
        block.Instructions.InsertRange(anchor, replacements);
        return replacements.Length;
    }

    private static IReadOnlyList<FieldLayout>? GetFieldLayouts(
        TypeAnalysisContext owner,
        int pointerSize)
    {
        var fields = new List<FieldAnalysisContext>();
        for (var current = owner; current != null; current = current.BaseType)
        {
            fields.AddRange(current.Fields.Where(field =>
                !field.IsStatic
                && field.Offset >= 0
                && (field.Attributes & FieldAttributes.Literal) == 0));
        }

        var ordered = fields.OrderBy(field => field.Offset).ToArray();
        var result = new List<FieldLayout>(ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var field = ordered[index];
            if (GenericInstanceFieldLayout.GetSizeAndAlignment(field.FieldType, pointerSize)
                is not var (size, _))
                return null;

            long end;
            try
            {
                end = checked(field.Offset + size);
            }
            catch (OverflowException)
            {
                return null;
            }

            result.Add(new(field, new(field.Offset, end)));
        }

        return result;
    }

    private static List<ByteRange> MergeRanges(IEnumerable<ByteRange> ranges)
    {
        var ordered = ranges.OrderBy(range => range.Start).ThenBy(range => range.End).ToArray();
        var result = new List<ByteRange>();
        foreach (var range in ordered)
        {
            if (result.Count == 0 || range.Start > result[^1].End)
            {
                result.Add(range);
                continue;
            }

            var previous = result[^1];
            result[^1] = new(previous.Start, Math.Max(previous.End, range.End));
        }

        return result;
    }

    private static bool IsCovered(ByteRange target, IReadOnlyList<ByteRange> coverage)
    {
        var cursor = target.Start;
        foreach (var range in coverage)
        {
            if (range.End <= cursor)
                continue;
            if (range.Start > cursor)
                return false;
            cursor = Math.Max(cursor, range.End);
            if (cursor >= target.End)
                return true;
        }

        return false;
    }

    private static bool HasOverlappingFields(IReadOnlyList<FieldLayout> fields)
    {
        for (var index = 1; index < fields.Count; index++)
            if (fields[index - 1].Range.End > fields[index].Range.Start)
                return true;

        return false;
    }

    private static bool HasIntermediateObservation(
        Block block,
        IReadOnlyList<StoreCandidate> candidates,
        IReadOnlyCollection<FieldLayout> fields,
        LocalVariable receiver)
    {
        var candidateInstructions = candidates
            .Select(candidate => candidate.Instruction)
            .ToHashSet();
        var first = candidates.Min(candidate => block.Instructions.IndexOf(candidate.Instruction));
        var last = candidates.Max(candidate => block.Instructions.IndexOf(candidate.Instruction));
        var affectedFields = fields.Select(field => field.Field).ToHashSet();

        for (var index = first; index <= last; index++)
        {
            var instruction = block.Instructions[index];
            if (candidateInstructions.Contains(instruction))
                continue;
            if (instruction.IsCall
                || instruction.OpCode is OpCode.Return
                    or OpCode.Throw
                    or OpCode.Jump
                    or OpCode.ConditionalJump
                    or OpCode.IndirectJump)
                return true;

            foreach (var operand in instruction.Operands)
            {
                if (operand is FieldReference field
                    && ReferenceEquals(field.Local, receiver)
                    && affectedFields.Contains(field.Field))
                    return true;
            }
        }

        return false;
    }
}
