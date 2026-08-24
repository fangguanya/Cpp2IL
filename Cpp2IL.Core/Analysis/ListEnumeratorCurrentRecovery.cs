using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Logging;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 恢复 <c>List&lt;T&gt;.Enumerator</c> 聚合返回槽与其 <c>current</c> 子槽之间的栈别名。
/// </summary>
/// <remarks>
/// ARM64 会把 Enumerator 直接写入调用方栈槽，随后以“基址 + current 偏移”读取当前元素。
/// SSA 把两个物理重叠的栈名字视为独立局部时，子槽会错误继承函数序言写入的零值。本恢复器
/// 只在 GetEnumerator、MoveNext 循环回边、支配关系和具体字段布局四项证据唯一时建立字段引用。
/// </remarks>
public static class ListEnumeratorCurrentRecovery
{
    private readonly record struct Candidate(
        Block LoopHeader,
        Block LoopBodyEntry,
        LocalVariable Enumerator,
        FieldAnalysisContext CurrentField,
        int CurrentStackOffset,
        int FieldOffset);

    private readonly record struct RewriteSite(
        Instruction Instruction,
        int OperandIndex,
        Candidate Candidate);

    /// <summary>
    /// 在复制传播前恢复当前元素字段，返回实际改写的读取数。
    /// </summary>
    public static int Run(MethodAnalysisContext method) =>
        method.ControlFlowGraph is null ? 0 : Rewrite(method.ControlFlowGraph);

    /// <summary>
    /// 对已经建立 SSA 的控制流图执行精确改写；该入口用于三类回归测试复用同一生产逻辑。
    /// </summary>
    internal static int Rewrite(ISILControlFlowGraph graph)
    {
        var dominance = new DominatorInfo(graph);
        var reachability = new Reachability();
        var definitionCount = graph.Blocks.SelectMany(block => block.Instructions)
            .Count(instruction => TryGetEnumeratorDefinition(instruction, out _, out _));
        var candidates = CollectCandidates(graph, dominance, reachability);
        if (definitionCount != 0)
            Logger.VerboseNewline(
                $"List Enumerator当前元素候选：definitions={definitionCount}，candidates={candidates.Count}");
        if (candidates.Count == 0)
            return 0;

        var sites = new List<RewriteSite>();
        var ambiguousSites = new HashSet<(Instruction Instruction, int OperandIndex)>();
        foreach (var block in graph.Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                for (var operandIndex = 0; operandIndex < instruction.Operands.Count; operandIndex++)
                {
                    if (instruction.Operands[operandIndex] is not LocalVariable source
                        || ReferenceEquals(instruction.Destination, source)
                        || !AggregateStackCopyRecovery.TryGetStackOffset(source, out var sourceOffset))
                        continue;

                    var matches = candidates.Where(candidate =>
                        sourceOffset == candidate.CurrentStackOffset
                        && dominance.Dominates(candidate.LoopBodyEntry, block)
                        && reachability.CanReach(block, candidate.LoopHeader))
                        .ToArray();
                    if (matches.Length == 1)
                        sites.Add(new(instruction, operandIndex, matches[0]));
                    else if (matches.Length > 1)
                        ambiguousSites.Add((instruction, operandIndex));
                }
            }
        }

        // 中文注释：同一读取若同时属于两个重叠 Enumerator 生命周期，现有证据不足以选定
        // 所有者；整组保持原样，禁止依赖候选枚举顺序产生不稳定改写。
        var rewritten = 0;
        foreach (var site in sites)
        {
            if (ambiguousSites.Contains((site.Instruction, site.OperandIndex)))
                continue;

            site.Instruction.SetOperand(site.OperandIndex, new FieldReference(
                site.Candidate.CurrentField,
                site.Candidate.Enumerator,
                site.Candidate.FieldOffset));
            if (site.Instruction is { OpCode: OpCode.Move, Operands: [LocalVariable destination, _] }
                && site.OperandIndex == 1)
                destination.Type = site.Candidate.CurrentField.FieldType;

            Logger.VerboseNewline(
                $"List Enumerator当前元素栈别名：enumerator={site.Candidate.Enumerator}，" +
                $"stackOffset={FormatStackOffset(site.Candidate.CurrentStackOffset)}，" +
                $"field={site.Candidate.CurrentField.Name}，instruction={site.Instruction.Index}");
            rewritten++;
        }

        Logger.VerboseNewline(
            $"List Enumerator当前元素改写：sites={sites.Count}，ambiguous={ambiguousSites.Count}，rewritten={rewritten}");

        return rewritten;
    }

    private static List<Candidate> CollectCandidates(
        ISILControlFlowGraph graph,
        DominatorInfo dominance,
        Reachability reachability)
    {
        var candidates = new List<Candidate>();
        foreach (var definitionBlock in graph.Blocks)
        {
            foreach (var call in definitionBlock.Instructions)
            {
                if (!TryGetEnumeratorDefinition(call, out var enumerator, out var enumeratorType))
                    continue;

                if (!AggregateStackCopyRecovery.TryGetStackOffset(enumerator, out var baseOffset))
                {
                    Logger.VerboseNewline($"List Enumerator当前元素候选拒绝：reason=返回目标不是栈槽，target={enumerator}");
                    continue;
                }

                if (!TryGetCurrentField(enumeratorType, out var currentField, out var fieldOffset))
                {
                    var layout = GenericInstanceFieldLayout.GetConcreteFieldLayout(enumeratorType);
                    Logger.VerboseNewline(
                        $"List Enumerator当前元素候选拒绝：reason=current字段布局不唯一，" +
                        $"fields={string.Join(",", layout?.Select(entry => $"{entry.Field.Name}@{entry.Offset:X}") ?? [])}");
                    continue;
                }

                var matchingHeaders = new List<(Block Header, Block BodyEntry)>();
                foreach (var block in graph.Blocks)
                {
                    if (dominance.Dominates(definitionBlock, block)
                        && ContainsMoveNext(block, enumeratorType)
                        && TryGetUniqueLoopBodyEntry(block, reachability, out var loopBodyEntry))
                        matchingHeaders.Add((block, loopBodyEntry));
                }

                if (matchingHeaders.Count != 1)
                {
                    var moveNextBlocks = graph.Blocks.Where(block =>
                        ContainsMoveNext(block, enumeratorType)).ToArray();
                    var moveNextEvidence = graph.Blocks.SelectMany(block => block.Instructions)
                        .Where(instruction => instruction.IsCall
                            && instruction.Operands.Count != 0
                            && instruction.Operands[0] is MethodAnalysisContext { Name: "MoveNext" })
                        .Select(instruction =>
                        {
                            var target = (MethodAnalysisContext)instruction.Operands[0];
                            var receivers = instruction.Operands.Skip(2)
                                .Select(ReceiverLocal)
                                .Where(local => local is not null)
                                .Select(local => $"{local!.Register}/{local.Type?.FullName}")
                                .ToArray();
                            return $"{TypeDefinitionName(target.DeclaringType)}[{string.Join("|", receivers)}]";
                        });
                    Logger.VerboseNewline(
                        $"List Enumerator当前元素候选拒绝：reason=循环头不唯一，" +
                        $"moveNextBlocks={string.Join(",", moveNextBlocks.Select(block => $"b{block.ID}:{block.Successors.Count}"))}，" +
                        $"matchingHeaders={matchingHeaders.Count}，" +
                        $"enumerator={enumerator.Register}/{enumeratorType.FullName}，" +
                        $"moveNextEvidence={string.Join(",", moveNextEvidence)}");
                    continue;
                }

                var (loopHeader, bodyEntry) = matchingHeaders[0];
                if (!dominance.Dominates(definitionBlock, bodyEntry))
                {
                    Logger.VerboseNewline("List Enumerator当前元素候选拒绝：reason=循环体入口支配关系不成立");
                    continue;
                }

                var currentStackOffset = checked(baseOffset + fieldOffset);
                var bodyBlocks = graph.Blocks.Where(block =>
                    dominance.Dominates(bodyEntry, block)
                    && reachability.CanReach(block, loopHeader))
                    .ToArray();

                // 中文注释：迭代体若主动写入重叠子槽，该槽具有独立生命周期，不能解释为
                // Enumerator.current。GetEnumerator 对聚合基槽的定义不属于该子槽写入。
                if (bodyBlocks.SelectMany(block => block.Instructions)
                    .Any(instruction => instruction.Destination is LocalVariable destination
                        && AggregateStackCopyRecovery.TryGetStackOffset(destination, out var destinationOffset)
                        && destinationOffset == currentStackOffset))
                {
                    Logger.VerboseNewline(
                        $"List Enumerator当前元素候选拒绝：reason=循环体写入current子槽，offset=0x{currentStackOffset:X}");
                    continue;
                }

                candidates.Add(new(
                    loopHeader,
                    bodyEntry,
                    enumerator,
                    currentField,
                    currentStackOffset,
                    fieldOffset));
            }
        }

        return candidates;
    }

    private static bool TryGetEnumeratorDefinition(
        Instruction instruction,
        out LocalVariable enumerator,
        out GenericInstanceTypeAnalysisContext enumeratorType)
    {
        enumerator = null!;
        enumeratorType = null!;
        if (instruction is not
            {
                OpCode: OpCode.Call,
                Operands: [MethodAnalysisContext { Name: "GetEnumerator" } target,
                    LocalVariable destination, ..]
            }
            || TypeDefinitionName(target.DeclaringType) != "System.Collections.Generic.List`1"
            || target.ReturnType is not GenericInstanceTypeAnalysisContext
            {
                IsValueType: true
            } concreteEnumerator
            || TypeDefinitionName(concreteEnumerator) !=
                "System.Collections.Generic.List`1+Enumerator")
            return false;

        enumerator = destination;
        enumeratorType = concreteEnumerator;
        return true;
    }

    private static bool TryGetCurrentField(
        GenericInstanceTypeAnalysisContext enumeratorType,
        out FieldAnalysisContext field,
        out int offset)
    {
        field = null!;
        offset = 0;
        var matches = GenericInstanceFieldLayout.GetConcreteFieldLayout(enumeratorType)?
            .Where(entry => entry.Field.Name.TrimStart('_') == "current")
            .ToArray() ?? [];
        if (matches.Length != 1 || matches[0].Offset is < int.MinValue or > int.MaxValue)
            return false;

        field = matches[0].Field;
        offset = (int)matches[0].Offset;
        return true;
    }

    private static bool ContainsMoveNext(
        Block block,
        GenericInstanceTypeAnalysisContext enumeratorType) =>
        block.Instructions.Any(instruction =>
            instruction.IsCall
            && instruction.Operands.Count >= 2
            && instruction.Operands[0] is MethodAnalysisContext { Name: "MoveNext" } target
            && TypeDefinitionName(target.DeclaringType) ==
                "System.Collections.Generic.List`1+Enumerator"
            && instruction.Operands.Skip(2).Any(operand =>
                ReceiverLocal(operand) is { } receiver
                && GenericCallRebinder.TypesEquivalent(receiver.Type, enumeratorType)));

    /// <summary>
    /// 晚期 ref/out 收敛前接收者仍是值类型栈局部，收敛后则包装为取址；两者指向同一存储身份。
    /// </summary>
    private static LocalVariable? ReceiverLocal(IOperand operand) => operand switch
    {
        LocalVariable local => local,
        AddressOf { Target: LocalVariable local } => local,
        _ => null,
    };

    private static bool TryGetUniqueLoopBodyEntry(
        Block loopHeader,
        Reachability reachability,
        out Block bodyEntry)
    {
        bodyEntry = null!;
        var loopSuccessors = loopHeader.Successors
            .Where(successor => reachability.CanReach(successor, loopHeader))
            .Distinct()
            .ToArray();
        if (loopSuccessors.Length != 1 || loopHeader.Successors.Distinct().Count() != 2)
            return false;

        bodyEntry = loopSuccessors[0];
        return true;
    }

    private sealed class Reachability
    {
        private readonly Dictionary<(Block Start, Block Target), bool> _cache = [];

        public bool CanReach(Block start, Block target)
        {
            if (_cache.TryGetValue((start, target), out var cached))
                return cached;

            var visited = new HashSet<Block>();
            var pending = new Queue<Block>();
            pending.Enqueue(start);
            while (pending.Count != 0)
            {
                var current = pending.Dequeue();
                if (!visited.Add(current))
                    continue;
                if (ReferenceEquals(current, target))
                {
                    _cache[(start, target)] = true;
                    return true;
                }

                foreach (var successor in current.Successors)
                    pending.Enqueue(successor);
            }

            _cache[(start, target)] = false;
            return false;
        }
    }

    private static string? TypeDefinitionName(TypeAnalysisContext? type) =>
        type is GenericInstanceTypeAnalysisContext generic ? generic.GenericType.FullName : type?.FullName;

    private static string FormatStackOffset(int offset) =>
        offset < 0 ? $"-0x{-(long)offset:X}" : $"0x{offset:X}";

}
