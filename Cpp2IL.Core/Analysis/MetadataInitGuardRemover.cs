using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Logging;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Removes the IL2CPP runtime-metadata initialization guards the compiler emits near the top of
/// (almost) every method, plus il2cpp_runtime_class_init blocks.
/// </summary>
public static class MetadataInitGuardRemover
{
    private const string InitializeRuntimeMetadata = "il2cpp_codegen_initialize_runtime_metadata";
    private const string InitializeMethod = "il2cpp_codegen_initialize_method";
    private const string ClassInitExport = "il2cpp_runtime_class_init_export";
    private const string ClassInitActual = "il2cpp_runtime_class_init_actual";
    private const string ClassInitCodegen = "il2cpp_codegen_runtime_class_init";

    // Byte holding Il2CppClass's bitfield, of which bit 0 is initialized_and_no_error.
    // TODO this is almost certainly not correct on every version... but which?
    private const long InitialisedFlagOffset64 = 0x135;
    private const long InitialisedFlagOffset32 = 0xBD;

    public static void Run(MethodAnalysisContext method)
        => Run(method.ControlFlowGraph!, method.AppContext.Binary.is32Bit ? InitialisedFlagOffset32 : InitialisedFlagOffset64);

    public static void Run(ISILControlFlowGraph cfg, long initialisedFlagOffset)
    {
        foreach (var guard in cfg.Blocks.ToList())
            TryRemoveGuard(cfg, guard, initialisedFlagOffset);
    }

    private static bool TryRemoveGuard(ISILControlFlowGraph cfg, Block guard, long initialisedFlagOffset)
    {
        if (guard.BlockType != BlockType.TwoWay || guard.Successors.Count != 2
            || guard.Instructions.Count == 0 || guard.Instructions[^1].OpCode != OpCode.ConditionalJump)
            return false;

        // see if we're checking Il2CppClass::initialized_and_no_error
        // that means this is runtime_init boilerplate and we can drop the block
        var initialisedFlagTest = guard.Instructions.Any(i => i.OpCode == OpCode.And
            && i.Operands is [_, MemoryOperand { Index: null, Scale: 0, Base: LocalVariable } flag, { } mask]
            && flag.Addend == initialisedFlagOffset && IsOne(mask));
        var constantMetadataFlagTest = GuardPrefixBlocks(guard)
            .Any(HasConstantMetadataFlagTest);

        // Either successor could be the init entry; the other is then the merge.
        var first = guard.Successors[0];
        var second = guard.Successors[1];

        return TryExcise(cfg, guard, first, second, initialisedFlagTest, constantMetadataFlagTest)
            || TryExcise(cfg, guard, second, first, initialisedFlagTest, constantMetadataFlagTest);
    }

    private static bool IsOne(IOperand operand) => operand is Immediate { Value: 1 };

    internal static bool IsConstantMetadataFlagTest(Instruction instruction) =>
        instruction.OpCode == OpCode.And
        && instruction.Operands is [LocalVariable, MemoryOperand { IsConstant: true }, { } mask]
        && IsOne(mask);

    private static bool HasConstantMetadataFlagTest(Block block) =>
        block.Instructions.Any(IsConstantMetadataFlagTest)
        || TryFindConstantMetadataFlagTestChain(block, out _, out _, out _);

    internal static bool TryFindConstantMetadataFlagTestChain(
        Block block,
        out Instruction flagLoad,
        out Instruction bitTest,
        out Instruction conditionTest)
    {
        foreach (var load in block.Instructions)
        {
            if (load.OpCode != OpCode.Move
                || load.Operands is not [LocalVariable loadedFlag, MemoryOperand { IsConstant: true }])
                continue;

            var and = block.Instructions.FirstOrDefault(instruction =>
                instruction.OpCode == OpCode.And
                && instruction.Operands is [LocalVariable, LocalVariable source, { } mask]
                && ReferenceEquals(source, loadedFlag)
                && IsOne(mask));
            if (and?.Destination is not LocalVariable testedBit)
                continue;

            var comparison = block.Instructions.FirstOrDefault(instruction =>
                instruction.OpCode == OpCode.CheckNotEqual
                && instruction.Operands is [LocalVariable, LocalVariable source, Immediate { Value: 0 }]
                && ReferenceEquals(source, testedBit));
            if (comparison?.Destination is not LocalVariable condition
                || block.Instructions[^1].OpCode != OpCode.ConditionalJump
                || block.Instructions[^1].Operands.Count < 2
                || !ReferenceEquals(block.Instructions[^1].Operands[1], condition))
                continue;

            flagLoad = load;
            bitTest = and;
            conditionTest = comparison;
            return true;
        }

        flagLoad = null!;
        bitTest = null!;
        conditionTest = null!;
        return false;
    }

    internal static int RemoveConstantMetadataFlagTests(Block guard)
    {
        var removed = 0;
        if (TryFindConstantMetadataFlagTestChain(guard, out var flagLoad, out var bitTest, out var conditionTest))
        {
            foreach (var instruction in new[] { flagLoad, bitTest, conditionTest })
            {
                instruction.OpCode = OpCode.Nop;
                instruction.SetOperands();
                removed++;
            }
        }

        foreach (var instruction in guard.Instructions.Where(IsConstantMetadataFlagTest))
        {
            instruction.OpCode = OpCode.Nop;
            instruction.SetOperands();
            removed++;
        }

        return removed;
    }

    internal static int RemoveConstantMetadataFlagTestsFromGuardPrefix(Block guard)
    {
        foreach (var block in GuardPrefixBlocks(guard))
        {
            var removed = RemoveConstantMetadataFlagTests(block);
            if (removed > 0)
                return removed;
        }

        return 0;
    }

    private static IEnumerable<Block> GuardPrefixBlocks(Block guard)
    {
        var current = guard;
        var visited = new HashSet<Block>();
        while (visited.Add(current))
        {
            yield return current;
            if (current.Predecessors is not [{ } predecessor]
                || predecessor.Successors.Count != 1
                || predecessor.BlockType is BlockType.Entry or BlockType.Exit)
                yield break;

            current = predecessor;
        }
    }

    private static bool TryExcise(
        ISILControlFlowGraph cfg,
        Block guard,
        Block initEntry,
        Block merge,
        bool initialisedFlagTest,
        bool constantMetadataFlagTest)
    {
        if (merge == cfg.EntryBlock || merge == cfg.ExitBlock)
            return false;

        if (!TryCollectRegion(cfg, guard, initEntry, merge, initialisedFlagTest, out var region))
            return false;

        Excise(cfg, guard, initEntry, merge, region, constantMetadataFlagTest);
        return true;
    }

    private static bool TryCollectRegion(ISILControlFlowGraph cfg, Block guard, Block initEntry, Block merge,
        bool initialisedFlagTest, out HashSet<Block> region)
    {
        region = [];

        if (initEntry == merge || initEntry == guard)
            return false;

        var sawMetadataInit = false;
        var sawClassInit = false;
        var sawFlagStore = false;
        var reconverges = false;

        var queue = new Queue<Block>();
        queue.Enqueue(initEntry);

        while (queue.Count > 0)
        {
            var block = queue.Dequeue();

            if (block == merge)
            {
                reconverges = true;
                continue;
            }

            // The region must not run into the method boundary or loop back through the guard.
            if (block == cfg.EntryBlock || block == cfg.ExitBlock || block == guard)
                return false;

            if (!region.Add(block))
                continue;

            if (!ClassifyBlock(block, initialisedFlagTest, ref sawMetadataInit, ref sawClassInit, ref sawFlagStore))
                return false;

            foreach (var successor in block.Successors)
                queue.Enqueue(successor);
        }

        if (!reconverges || !(sawClassInit || (sawMetadataInit && sawFlagStore)))
            return false;

        var collected = region;
        foreach (var block in collected)
        {
            if (block.Predecessors.Any(predecessor => predecessor != guard && !collected.Contains(predecessor)))
                return false;
            if (block.Successors.Any(successor => successor != merge && !collected.Contains(successor)))
                return false;
        }

        return true;
    }

    // A region block is acceptable only if every instruction is intra-region control flow, an init
    // call, the flag store, or otherwise side-effect-free (writes a local, not memory). A managed call
    // or any other store would have an effect we cannot silently drop, so it disqualifies the region.
    private static bool ClassifyBlock(Block block, bool initialisedFlagTest, ref bool sawMetadataInit, ref bool sawClassInit, ref bool sawFlagStore)
    {
        foreach (var instruction in block.Instructions)
        {
            switch (instruction.OpCode)
            {
                case OpCode.Jump:
                    break;

                // Behind an initialized_and_no_error test the callee is the class initializer, even if we didn't resolve it.
                // If we didn't, that's fine, just skip.
                case OpCode.Call or OpCode.CallVoid when initialisedFlagTest:
                    sawClassInit = true;
                    break;

                case OpCode.Call or OpCode.CallVoid:
                    if (instruction.Operands is not [StringLiteral { Value: var name }, ..])
                        return false;

                    if (name is InitializeRuntimeMetadata or InitializeMethod)
                        sawMetadataInit = true;
                    else if (name is ClassInitExport or ClassInitActual or ClassInitCodegen)
                        sawClassInit = true;
                    else
                        return false;

                    break;

                case OpCode.Move when instruction.Operands is [MemoryOperand { IsConstant: true }, _]:
                    sawFlagStore = true;
                    break;

                default:
                    if (!IsSideEffectFree(instruction))
                        return false;
                    break;
            }
        }

        return true;
    }

    // True for instructions that only compute a value into a local (or do nothing). A store - any
    // instruction whose destination operand is a memory or field reference rather than a local - is
    // excluded, as is anything that transfers control or merges values (phi/return/indirect).
    private static bool IsSideEffectFree(Instruction instruction) =>
        instruction.OpCode switch
        {
            OpCode.Nop => true,
            OpCode.Move or OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide
                or OpCode.ShiftLeft or OpCode.ShiftRight or OpCode.And or OpCode.Or or OpCode.Xor
                or OpCode.Not or OpCode.Negate
                or OpCode.ConvertFloatingPointPrecision or OpCode.ConvertFloatToSignedInteger
                or OpCode.ConvertSignedIntegerToFloat
                or OpCode.ReinterpretIntegerBitsAsFloat or OpCode.ReinterpretFloatBitsAsInteger
                or OpCode.RoundFloatTowardPositiveInfinity or OpCode.RoundFloatTowardNegativeInfinity
                or (>= OpCode.CheckEqual and <= OpCode.CheckLessOrEqualUnsigned)
                => instruction.Operands is [LocalVariable, ..],
            _ => false,
        };

    private static void Excise(
        ISILControlFlowGraph cfg,
        Block guard,
        Block initEntry,
        Block merge,
        HashSet<Block> region,
        bool constantMetadataFlagTest)
    {
        // 1. Repair the merge's phis: drop the inputs from the region's back-edges.
        for (var i = merge.Predecessors.Count - 1; i >= 0; i--)
        {
            if (!region.Contains(merge.Predecessors[i]))
                continue;

            foreach (var phi in merge.Instructions)
                if (phi.OpCode == OpCode.Phi && 1 + i < phi.Operands.Count)
                    phi.RemoveOperandAt(1 + i);

            merge.Predecessors.RemoveAt(i);
        }

        // 2. Fold the guard so it goes straight to the merge.
        guard.Successors.Remove(initEntry);
        initEntry.Predecessors.Remove(guard);

        var terminator = guard.Instructions[^1];
        // 仅在初始化区域已被完整证明后删除同一保护块的绝对地址位测试；普通业务条件不匹配此形态。
        var removedFlagTests = 0;
        if (constantMetadataFlagTest)
            removedFlagTests = RemoveConstantMetadataFlagTestsFromGuardPrefix(guard);
        Logger.VerboseNewline(
            $"元数据初始化保护段删除：guard=b{guard.ID}，region={region.Count}，" +
            $"prefix={string.Join(",", GuardPrefixBlocks(guard).Select(block => $"b{block.ID}"))}，" +
            $"constantFlag={constantMetadataFlagTest}，removedFlagTests={removedFlagTests}");
        terminator.OpCode = OpCode.Jump;
        terminator.SetOperands(merge);
        guard.CalculateBlockType();

        // 3. Delete the region. 
        foreach (var block in region)
        {
            foreach (var successor in block.Successors)
                successor.Predecessors.Remove(block);
            foreach (var predecessor in block.Predecessors)
                predecessor.Successors.Remove(block);

            block.Successors.Clear();
            block.Predecessors.Clear();
            cfg.Blocks.Remove(block);
        }
    }
}
