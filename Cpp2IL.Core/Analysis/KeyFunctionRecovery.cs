using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.Il2CppApiFunctions;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Maps calls to KeyFunctionAddresses to their underlying IL opcodes. E.g. il2cpp_codegen_object_new => newobj.
/// Eventually will include box/unbox/throw/etc
/// </summary>
public static class KeyFunctionRecovery
{
    //All of these have the same params in the same order so we treat them as equal.
    private static readonly HashSet<string> ObjectNewFunctions =
    [
        "il2cpp_object_new",
        "il2cpp_vm_object_new",
        "il2cpp_codegen_object_new",
    ];

    private static readonly HashSet<string> ObjectBoxFunctions =
    [
        "il2cpp_value_box",
        "il2cpp_vm_object_box",
        "il2cpp_codegen_object_box",
    ];

    public static void RewriteAllocationsAndBarriers(MethodAnalysisContext method)
    {
        foreach (var instruction in method.ControlFlowGraph!.Blocks.SelectMany(block => block.Instructions))
        {
            if (instruction.Operands is not [StringLiteral { Value: var keyFunction }, ..])
                continue;

            if (ObjectNewFunctions.Contains(keyFunction))
                RewriteObjectNew(instruction);
            else if (keyFunction == nameof(BaseKeyFunctionAddresses.il2cpp_codegen_write_barrier))
                RemoveWriteBarrier(instruction);
        }
    }

    public static void RewriteBoxing(MethodAnalysisContext method)
    {
        var instructions = method.ControlFlowGraph!.Blocks
            .SelectMany(block => block.Instructions)
            .ToList();
        if (!instructions.Any(instruction =>
                instruction.Operands is [StringLiteral { Value: var keyFunction }, ..]
                && ObjectBoxFunctions.Contains(keyFunction)))
            return;

        var definitions = instructions
            .Where(instruction => instruction.Destination is LocalVariable)
            .GroupBy(instruction => (LocalVariable)instruction.Destination!)
            // 多定义局部不满足SSA唯一生产者证明；保留原始调用，禁止任选一个版本。
            .Where(group => group.Count() == 1)
            .ToDictionary(
                group => group.Key,
                group => group.Single());

        foreach (var block in method.ControlFlowGraph.Blocks)
        {
            for (var instructionIndex = 0; instructionIndex < block.Instructions.Count; instructionIndex++)
            {
                var instruction = block.Instructions[instructionIndex];
                if (instruction.Operands is not [StringLiteral { Value: var keyFunction }, ..]
                    || !ObjectBoxFunctions.Contains(keyFunction))
                    continue;

                RewriteObjectBox(
                    instruction,
                    definitions,
                    method.ControlFlowGraph.Blocks,
                    block,
                    instructionIndex,
                    method.DominatorInfo);
            }
        }
    }

    private static void RemoveWriteBarrier(Instruction instruction)
    {
        instruction.OpCode = OpCode.Nop;
        instruction.SetOperands();
    }
    
    private static void RewriteObjectNew(Instruction instruction)
    {
        // Needs the function name, the result, and the class argument.
        if (instruction.OpCode != OpCode.Call || instruction.Operands.Count < 3)
            return;

        var result = instruction.Operands[1];
        var klass = instruction.Operands[2];

        instruction.OpCode = OpCode.Newobj;
        instruction.SetOperands(result, klass);
    }

    private static void RewriteObjectBox(
        Instruction instruction,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions,
        IReadOnlyList<Block> allBlocks,
        Block callBlock,
        int instructionIndex,
        DominatorInfo? dominance)
    {
        // 原生Object::Box接收类型句柄和数据地址；地址可能先经寄存器局部传递。
        if (instruction.OpCode != OpCode.Call
            || instruction.Operands is not [_, { } result, _, { } dataAddress, ..])
            return;

        var addressed = ResolveAddressedValue(dataAddress, definitions);
        var value = addressed == null
            ? null
            : ResolveLatestStackValue(
                addressed,
                allBlocks,
                callBlock,
                instructionIndex,
                instruction.Index,
                dominance);
        if (value is not { Type: { IsValueType: true } boxedType })
            return;

        instruction.OpCode = OpCode.Box;
        instruction.SetOperands(result, value, boxedType);
    }

    private static LocalVariable ResolveLatestStackValue(
        AddressedValue addressed,
        IReadOnlyList<Block> allBlocks,
        Block callBlock,
        int beforeIndex,
        int callInstructionIndex,
        DominatorInfo? dominance)
    {
        var linearInstructions = allBlocks.SelectMany(block => block.Instructions).ToList();
        var hasControlTransfer = linearInstructions.Any(candidate =>
            candidate.Index > addressed.AddressTakenIndex
            && candidate.Index < callInstructionIndex
            && candidate.OpCode is OpCode.Jump or OpCode.ConditionalJump or OpCode.IndirectJump or OpCode.Return or OpCode.Throw);
        if (addressed.AddressTakenIndex != int.MinValue && !hasControlTransfer)
        {
            var sequentialDefinition = linearInstructions
                .Where(candidate => candidate.Index > addressed.AddressTakenIndex
                    && candidate.Index < callInstructionIndex
                    && candidate.Destination is LocalVariable destination
                    && destination.Register.Number == addressed.Slot.Register.Number)
                .OrderByDescending(candidate => candidate.Index)
                .Select(candidate => candidate.Destination as LocalVariable)
                .FirstOrDefault();
            if (sequentialDefinition != null)
                return sequentialDefinition;
        }

        if (dominance != null)
        {
            var dominatingDefinition = allBlocks
                .SelectMany(block => block.Instructions.Select((instruction, index) => (block, instruction, index)))
                .Where(candidate => candidate.instruction.Destination is LocalVariable destination
                    && destination.Register.Number == addressed.Slot.Register.Number
                    && (ReferenceEquals(candidate.block, callBlock)
                        ? candidate.index < beforeIndex
                        : dominance.Dominates(candidate.block, callBlock)))
                .OrderByDescending(candidate => candidate.instruction.Index)
                .Select(candidate => candidate.instruction.Destination as LocalVariable)
                .FirstOrDefault();

            if (dominatingDefinition != null)
                return dominatingDefinition;
        }

        // ARM64常先计算SP+offset，随后才把值写入该槽；沿唯一前驱链查找可证明支配Box的最近写入。
        var visited = new HashSet<Block>();
        var block = callBlock;
        var endExclusive = beforeIndex;
        while (visited.Add(block))
        {
            for (var index = endExclusive - 1; index >= 0; index--)
            {
                if (block.Instructions[index].Destination is LocalVariable candidate
                    && candidate.Register.Number == addressed.Slot.Register.Number)
                    return candidate;
            }

            Block? predecessor = null;
            if (dominance?.ImmediateDominators.TryGetValue(block, out var immediateDominator) == true)
                predecessor = immediateDominator;
            else if (block.Predecessors is [{ } solePredecessor])
                predecessor = solePredecessor;

            if (predecessor == null || ReferenceEquals(predecessor, block))
                break;

            block = predecessor;
            endExclusive = block.Instructions.Count;
        }

        return addressed.Slot;
    }

    private static AddressedValue? ResolveAddressedValue(
        IOperand operand,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions)
    {
        var visited = new HashSet<LocalVariable>();
        var addressTakenIndex = int.MinValue;
        while (true)
        {
            if (operand is AddressOf { Target: LocalVariable addressed })
                return new AddressedValue(addressed, addressTakenIndex);

            if (operand is not LocalVariable carrier
                || !visited.Add(carrier)
                || !definitions.TryGetValue(carrier, out var definition)
                || definition.OpCode != OpCode.Move
                || definition.Operands.Count < 2)
                return null;

            addressTakenIndex = definition.Index;
            operand = definition.Operands[1];
        }
    }

    private sealed record AddressedValue(LocalVariable Slot, int AddressTakenIndex);
}
