using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Disarm;
using Disarm.InternalDisassembly;
using LibCpp2IL;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 恢复 ARM64 清理区前被退 SSA 合并丢失的“命中对象/默认对象”引用返回 Phi。
/// </summary>
/// <remarks>
/// 原生代码会让被调用方保存寄存器在命中边保留当前对象，在枚举耗尽边改写为默认对象，
/// 清理 IDisposable 后再复制到 X0 返回。本恢复器同时要求原生寄存器证据与托管 CFG 的
/// 搜索循环证据，任一证据不唯一都保持现有红门。
/// </remarks>
public static class ManagedReturnPhiRecovery
{
    internal readonly record struct NativeReturnPhiEvidence(
        string ReturnCarrier,
        string DefaultCarrier);

    public static int Run(MethodAnalysisContext method)
    {
        if (method == null)
            throw new ArgumentNullException(nameof(method));
        if (method.AppContext.Binary.InstructionSetId != DefaultInstructionSets.ARM_V8
            || method.ReturnType.IsValueType
            || method.RawBytes.Length == 0
            || !TryResolveNativeEvidence(method, out var evidence))
            return 0;

        return Run(method, evidence);
    }

    /// <summary>
    /// 测试入口显式注入已经由 ARM64 指令证明的寄存器事实，只验证托管 CFG 选择语义。
    /// </summary>
    internal static int Run(MethodAnalysisContext method, NativeReturnPhiEvidence evidence)
    {
        if (method == null)
            throw new ArgumentNullException(nameof(method));
        if (!IsCalleeSavedRegister(evidence.ReturnCarrier)
            || !IsCalleeSavedRegister(evidence.DefaultCarrier)
            || string.Equals(evidence.ReturnCarrier, evidence.DefaultCarrier, StringComparison.Ordinal))
            return 0;

        var graph = method.ControlFlowGraph!;
        var homeBlocks = graph.Blocks
            .SelectMany(block => block.Instructions.Select(instruction => (instruction, block)))
            .ToDictionary(pair => pair.instruction, pair => pair.block);
        var definitions = graph.Instructions
            .Where(instruction => instruction.Destination is LocalVariable)
            .GroupBy(instruction => (LocalVariable)instruction.Destination!)
            .ToDictionary(group => group.Key, group => group.ToList());
        var recovered = 0;

        foreach (var returnBlock in graph.Blocks.ToArray())
        {
            if (returnBlock.Instructions.LastOrDefault() is not
                { OpCode: OpCode.Return, Operands: [LocalVariable defaultResult] }
                || !GenericCallRebinder.TypesEquivalent(defaultResult.Type, method.ReturnType)
                || !definitions.TryGetValue(defaultResult, out var defaultDefinitions)
                || defaultDefinitions is not [{ OpCode: OpCode.Newobj } defaultDefinition])
                continue;

            var candidates = graph.Instructions
                .Where(instruction => instruction is
                {
                    OpCode: OpCode.CastClass,
                    Destination: LocalVariable
                })
                .Select(instruction => (instruction, candidate: (LocalVariable)instruction.Destination!))
                .Where(pair => GenericCallRebinder.TypesEquivalent(pair.candidate.Type, method.ReturnType)
                               && IsCastCarrier(pair.candidate.Register.Name, evidence.ReturnCarrier))
                .ToList();
            if (candidates.Count != 1)
                continue;

            var (candidateDefinition, candidate) = candidates[0];
            var candidateBlock = homeBlocks[candidateDefinition];
            var defaultBlock = homeBlocks[defaultDefinition];
            var predicateMatches = graph.Blocks
                .Where(block => block.Successors.Count == 2
                                && block.Instructions.LastOrDefault()?.OpCode == OpCode.ConditionalJump
                                && block.Instructions.Any(instruction => Uses(instruction, candidate)))
                .Select(block => TryResolveMatchExit(block, candidateBlock, returnBlock, out var exit)
                    ? (Matched: true, Block: block, Exit: exit)
                    : (Matched: false, Block: block, Exit: (Block?)null))
                .Where(match => match.Matched)
                .ToList();
            if (predicateMatches.Count != 1
                || predicateMatches[0].Exit is not { Predecessors.Count: 1 } matchExit
                || !ReferenceEquals(matchExit.Predecessors[0], predicateMatches[0].Block)
                || !HasIndependentDefaultExit(defaultBlock, candidateBlock, returnBlock))
                continue;

            // 中文注释：匹配边拥有唯一前驱，可在清理区入口写回返回局部；枚举耗尽边不经过
            // 此赋值，继续保留 Newobj 产生的默认对象，精确对应原生 X21 的两条定义。
            matchExit.Instructions.Insert(0, new Instruction(-1, OpCode.Move, defaultResult, candidate));
            recovered++;
        }

        return recovered;
    }

    private static bool TryResolveNativeEvidence(
        MethodAnalysisContext method,
        out NativeReturnPhiEvidence evidence)
    {
        var instructions = Disassembler.Disassemble(
                method.RawBytes.AsSpan(),
                method.UnderlyingPointer,
                new Disassembler.Options(true, true, false))
            .ToList();
        var candidates = new HashSet<NativeReturnPhiEvidence>();

        for (var returnIndex = 0; returnIndex < instructions.Count; returnIndex++)
        {
            if (instructions[returnIndex].Mnemonic != Arm64Mnemonic.RET)
                continue;

            var returnMoveIndex = FindReturnCarrierMove(instructions, returnIndex, out var returnCarrier);
            if (returnMoveIndex < 0)
                continue;

            for (var resetIndex = returnMoveIndex - 1; resetIndex >= 0; resetIndex--)
            {
                var reset = instructions[resetIndex];
                if (!TryGetMoveRegisters(reset, out var resetDestination, out var defaultCarrier)
                    || !string.Equals(resetDestination, returnCarrier, StringComparison.Ordinal)
                    || !IsCalleeSavedRegister(defaultCarrier)
                    || string.Equals(defaultCarrier, returnCarrier, StringComparison.Ordinal))
                    continue;

                var defaultWasSavedFromX0 = instructions
                    .Take(resetIndex)
                    .Any(instruction => TryGetMoveRegisters(instruction, out var destination, out var source)
                                        && string.Equals(destination, defaultCarrier, StringComparison.Ordinal)
                                        && source == "X0");
                if (defaultWasSavedFromX0)
                    candidates.Add(new NativeReturnPhiEvidence(returnCarrier, defaultCarrier));
                break;
            }
        }

        if (candidates.Count == 1)
        {
            evidence = candidates.Single();
            return true;
        }

        evidence = default;
        return false;
    }

    private static int FindReturnCarrierMove(
        IReadOnlyList<Arm64Instruction> instructions,
        int returnIndex,
        out string returnCarrier)
    {
        returnCarrier = string.Empty;
        for (var index = returnIndex - 1; index >= Math.Max(0, returnIndex - 12); index--)
        {
            var instruction = instructions[index];
            if (TryGetMoveRegisters(instruction, out var destination, out var source)
                && destination == "X0"
                && IsCalleeSavedRegister(source))
            {
                returnCarrier = source;
                return index;
            }

            // 中文注释：普通寄存器恢复和栈平衡可位于返回复制之后；新的调用或分支意味着
            // 已跨出当前尾声，继续向前猜测会把其他路径的 X0 赋值误作返回证据。
            if (instruction.Mnemonic is Arm64Mnemonic.BL or Arm64Mnemonic.BLR or Arm64Mnemonic.BR
                or Arm64Mnemonic.B)
                break;
        }

        return -1;
    }

    private static bool TryGetMoveRegisters(
        Arm64Instruction instruction,
        out string destination,
        out string source)
    {
        destination = string.Empty;
        source = string.Empty;
        if (instruction.Mnemonic != Arm64Mnemonic.MOV
            || instruction.Op0Kind != Arm64OperandKind.Register
            || instruction.Op1Kind != Arm64OperandKind.Register)
            return false;

        destination = instruction.Op0Reg.ToString().ToUpperInvariant();
        source = instruction.Op1Reg.ToString().ToUpperInvariant();
        return destination.StartsWith("X", StringComparison.Ordinal)
               && source.StartsWith("X", StringComparison.Ordinal);
    }

    private static bool TryResolveMatchExit(
        Block predicate,
        Block candidate,
        Block returnBlock,
        out Block? matchExit)
    {
        var matches = predicate.Successors
            .Where(successor => CanReach(successor, returnBlock, candidate)
                                && !CanReach(successor, candidate))
            .ToList();
        var loops = predicate.Successors
            .Where(successor => CanReach(successor, candidate))
            .ToList();
        matchExit = matches.Count == 1 && loops.Count == 1 ? matches[0] : null;
        return matchExit != null;
    }

    private static bool HasIndependentDefaultExit(
        Block defaultBlock,
        Block candidate,
        Block returnBlock)
    {
        foreach (var gate in ReachableBlocks(defaultBlock, candidate))
        {
            if (gate.Successors.Count != 2)
                continue;
            var candidateEdges = gate.Successors.Count(successor => CanReach(successor, candidate));
            var defaultEdges = gate.Successors.Count(successor => CanReach(successor, returnBlock, candidate));
            if (candidateEdges == 1 && defaultEdges == 1)
                return true;
        }

        return false;
    }

    private static IEnumerable<Block> ReachableBlocks(Block start, Block? forbidden = null)
    {
        var visited = new HashSet<Block>();
        var queue = new Queue<Block>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (ReferenceEquals(current, forbidden) || !visited.Add(current))
                continue;
            yield return current;
            foreach (var successor in current.Successors)
                queue.Enqueue(successor);
        }
    }

    private static bool CanReach(Block start, Block target, Block? forbidden = null)
        => ReachableBlocks(start, forbidden).Any(block => ReferenceEquals(block, target));

    private static bool Uses(Instruction instruction, LocalVariable candidate)
    {
        foreach (var operand in instruction.Operands)
        {
            if (ReferenceEquals(operand, candidate)
                && !ReferenceEquals(instruction.Destination, operand))
                return true;
            if (operand is MemoryOperand { Base: LocalVariable memoryBase }
                && ReferenceEquals(memoryBase, candidate))
                return true;
            if (operand is AddressOf { Target: LocalVariable addressed }
                && ReferenceEquals(addressed, candidate))
                return true;
        }

        return false;
    }

    private static bool IsCastCarrier(string? registerName, string returnCarrier)
        => registerName != null
           && registerName.StartsWith($"CAST_{returnCarrier}_", StringComparison.Ordinal);

    private static bool IsCalleeSavedRegister(string? registerName)
        => registerName != null
           && CalleeSavedManagedReceiverRecovery.IsCalleeSavedArm64Register(registerName);
}
