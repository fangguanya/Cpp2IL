using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 把 ARM64 的“无符号范围检查 + RELA 指针表索引 + 默认字符串槽”恢复为托管字符串选择。
/// </summary>
public static class RuntimeMetadataStringTableRecovery
{
    private const int MaximumEntryCount = 256;

    /// <summary>
    /// 恢复当前方法中全部证据闭合的字符串表读取。
    /// </summary>
    public static int Run(MethodAnalysisContext method)
    {
        var libContext = method.AppContext.LibCpp2IlContext;
        return Rewrite(
            method.ControlFlowGraph!,
            method.AppContext.SystemTypes.SystemStringType,
            method.AppContext.SystemTypes.SystemInt32Type,
            method.AppContext.Binary.PointerSizeBytes,
            (address, offset) =>
            {
                var usage = libContext.CheckForPost27GlobalPointerTableEntryAt(address, offset);
                return usage?.Type == MetadataUsageType.StringLiteral
                    ? new StringLiteral(usage.AsLiteral())
                    : null;
            },
            address =>
            {
                var usage = MetadataResolver.ResolveAbsoluteSlotUsage(
                    address,
                    libContext.GetAnyGlobalByAddress,
                    libContext.CheckForPost27GlobalTableEntryAt,
                    candidate => candidate.Type == MetadataUsageType.StringLiteral);
                return usage?.Type == MetadataUsageType.StringLiteral
                    ? new StringLiteral(usage.AsLiteral())
                    : null;
            });
    }

    /// <summary>
    /// 使用可注入解析器执行确定性改写，供基本、边界和异常输入测试复用同一生产逻辑。
    /// </summary>
    internal static int Rewrite(
        ISILControlFlowGraph graph,
        TypeAnalysisContext stringType,
        TypeAnalysisContext int32Type,
        int pointerSize,
        Func<ulong, long, StringLiteral?> pointerTableEntryResolver,
        Func<ulong, StringLiteral?> absoluteSlotResolver)
    {
        if (pointerSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(pointerSize));

        var instructions = graph.Instructions;
        var definitions = BuildUniqueDefinitions(instructions);
        var recovered = 0;

        foreach (var tableBlock in graph.Blocks.ToList())
        {
            foreach (var tableLoad in tableBlock.Instructions.ToList())
            {
                if (tableLoad is not
                    {
                        OpCode: OpCode.Move,
                        Operands:
                        [
                            LocalVariable destination,
                            MemoryOperand
                            {
                                Base: LocalVariable tableBase,
                                Index: LocalVariable tableIndex,
                                Addend: 0
                            } tableMemory
                        ]
                    }
                    || tableMemory.Scale != pointerSize
                    || tableMemory.IndexExtension is not (
                        MemoryIndexExtension.None
                        or MemoryIndexExtension.ZeroExtend32
                        or MemoryIndexExtension.ZeroExtend64)
                    || ResolveAbsoluteTableAddress(tableBase, definitions, []) is not { } tableAddress
                    || !TryMatchUnsignedUpperBound(
                        tableBlock,
                        tableIndex,
                        definitions,
                        out var defaultBlock,
                        out var maximumIndex)
                    || maximumIndex < 0
                    || maximumIndex >= MaximumEntryCount
                    || !TryFindDefaultLoad(
                        defaultBlock,
                        destination,
                        absoluteSlotResolver,
                        out var defaultLoad,
                        out var defaultLiteral)
                    || !HasExactlyTwoDefinitions(instructions, destination, tableLoad, defaultLoad)
                    || !TryResolveEntries(
                        tableAddress,
                        checked((int)maximumIndex + 1),
                        pointerSize,
                        pointerTableEntryResolver,
                        out var values))
                    continue;

                var lookup = new MetadataStringTableLookup(tableIndex, values, defaultLiteral);
                tableLoad.SetOperand(1, lookup);
                defaultLoad.SetOperand(1, defaultLiteral);
                destination.Type = stringType;
                tableIndex.Type ??= int32Type;
                RewriteTerminalDereferences(graph, destination);
                recovered++;
            }
        }

        return recovered;
    }

    /// <summary>
    /// 表项必须全部解析为字符串后一次性提交，任何缺项都禁止部分改写。
    /// </summary>
    private static bool TryResolveEntries(
        ulong tableAddress,
        int entryCount,
        int pointerSize,
        Func<ulong, long, StringLiteral?> resolver,
        out IReadOnlyList<StringLiteral> values)
    {
        var resolved = new List<StringLiteral>(entryCount);
        for (var index = 0; index < entryCount; index++)
        {
            var offset = checked((long)index * pointerSize);
            if (resolver(tableAddress, offset) is not { } literal)
            {
                values = [];
                return false;
            }

            resolved.Add(literal);
        }

        values = resolved;
        return true;
    }

    /// <summary>
    /// 只接受“index &gt; max 时跳往默认块”的原生无符号上界保护。
    /// </summary>
    private static bool TryMatchUnsignedUpperBound(
        Block tableBlock,
        LocalVariable tableIndex,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions,
        out Block defaultBlock,
        out long maximumIndex)
    {
        defaultBlock = null!;
        maximumIndex = -1;

        foreach (var guardBlock in tableBlock.Predecessors)
        {
            var conditionalJump = guardBlock.Instructions.LastOrDefault();
            if (conditionalJump is not
                {
                    OpCode: OpCode.ConditionalJump,
                    Operands: [Block jumpTarget, LocalVariable condition]
                }
                || ReferenceEquals(jumpTarget, tableBlock)
                || !guardBlock.Successors.Contains(tableBlock)
                || !guardBlock.Successors.Contains(jumpTarget)
                || !definitions.TryGetValue(condition, out var comparison)
                || comparison is not
                {
                    OpCode: OpCode.CheckGreaterUnsigned,
                    Operands: [LocalVariable, var comparedIndex, Immediate upperBound]
                }
                || !OperandsRepresentSameValue(tableIndex, comparedIndex, definitions))
                continue;

            defaultBlock = jumpTarget;
            maximumIndex = upperBound.Value;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 默认块必须给同一结果局部写入一个可由元数据槽精确解析的字符串。
    /// </summary>
    private static bool TryFindDefaultLoad(
        Block defaultBlock,
        LocalVariable destination,
        Func<ulong, StringLiteral?> resolver,
        out Instruction defaultLoad,
        out StringLiteral literal)
    {
        defaultLoad = null!;
        literal = default;
        var candidates = defaultBlock.Instructions
            .Where(instruction => instruction is
            {
                OpCode: OpCode.Move,
                Operands:
                [
                    LocalVariable candidate,
                    MemoryOperand
                    {
                        Base: null,
                        Index: null,
                        Scale: 0,
                        Addend: >= 0
                    }
                ]
            } && ReferenceEquals(candidate, destination))
            .ToArray();
        if (candidates.Length != 1
            || candidates[0].Operands[1] is not MemoryOperand memory
            || resolver((ulong)memory.Addend) is not { } resolved)
            return false;

        defaultLoad = candidates[0];
        literal = resolved;
        return true;
    }

    /// <summary>
    /// 同一退 SSA 结果只能由表分支和默认分支定义，额外来源会使字符串身份不唯一。
    /// </summary>
    private static bool HasExactlyTwoDefinitions(
        IReadOnlyList<Instruction> instructions,
        LocalVariable destination,
        Instruction tableLoad,
        Instruction defaultLoad)
    {
        var definitions = instructions
            .Where(instruction => ReferenceEquals(instruction.Destination, destination))
            .ToArray();
        return definitions.Length == 2
            && definitions.Contains(tableLoad)
            && definitions.Contains(defaultLoad);
    }

    /// <summary>
    /// 表分支与默认分支都已成为托管字符串后，删除返回点多余的原生解引用。
    /// </summary>
    private static void RewriteTerminalDereferences(
        ISILControlFlowGraph graph,
        LocalVariable destination)
    {
        foreach (var instruction in graph.Instructions)
        {
            for (var operandIndex = 0; operandIndex < instruction.Operands.Count; operandIndex++)
            {
                if (instruction.Operands[operandIndex] is MemoryOperand
                    {
                        Base: LocalVariable baseLocal,
                        Index: null,
                        Scale: 0,
                        Addend: 0
                    }
                    && ReferenceEquals(baseLocal, destination)
                    && (instruction.OpCode == OpCode.Return
                        || instruction is { OpCode: OpCode.Move, Operands: [LocalVariable, _] }
                        && operandIndex == 1))
                    instruction.SetOperand(operandIndex, destination);
            }
        }
    }

    /// <summary>
    /// 比较操作数沿唯一 Move 复制链收敛到同一值时才视为同一个索引。
    /// </summary>
    private static bool OperandsRepresentSameValue(
        IOperand left,
        IOperand right,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions)
        => ReferenceEquals(ResolveCopiedValue(left, definitions, []),
            ResolveCopiedValue(right, definitions, []));

    private static IOperand ResolveCopiedValue(
        IOperand operand,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions,
        HashSet<LocalVariable> visited)
    {
        if (operand is not LocalVariable local
            || !visited.Add(local)
            || !definitions.TryGetValue(local, out var definition)
            || definition is not { OpCode: OpCode.Move, Operands: [LocalVariable, var source] })
            return operand;

        return ResolveCopiedValue(source, definitions, visited);
    }

    /// <summary>
    /// 解析 ADRP/ADD 已投影成的常量地址链；只接受非负常量、加法和同址 Phi。
    /// </summary>
    internal static ulong? ResolveAbsoluteTableAddress(
        IOperand operand,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions,
        HashSet<LocalVariable> visited)
    {
        if (operand is Immediate { Value: >= 0 } immediate)
            return (ulong)immediate.Value;
        if (operand is not LocalVariable local
            || !visited.Add(local)
            || !definitions.TryGetValue(local, out var definition))
            return null;

        ulong? result = definition switch
        {
            { OpCode: OpCode.Move, Operands: [LocalVariable, var source] }
                => ResolveAbsoluteTableAddress(source, definitions, visited),
            { OpCode: OpCode.Add, Operands: [LocalVariable, var left, var right] }
                => CheckedAdd(
                    ResolveAbsoluteTableAddress(left, definitions, visited),
                    ResolveAbsoluteTableAddress(right, definitions, visited)),
            _ => null,
        };
        visited.Remove(local);
        return result;
    }

    private static ulong? CheckedAdd(ulong? left, ulong? right)
    {
        if (left == null || right == null || left.Value > ulong.MaxValue - right.Value)
            return null;
        return left.Value + right.Value;
    }

    private static Dictionary<LocalVariable, Instruction> BuildUniqueDefinitions(
        IReadOnlyList<Instruction> instructions)
        => instructions
            .Where(instruction => instruction.Destination is LocalVariable)
            .GroupBy(instruction => (LocalVariable)instruction.Destination!)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());
}
