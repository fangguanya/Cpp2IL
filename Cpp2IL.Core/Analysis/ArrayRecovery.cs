using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Analysis;

// Turns the raw Il2CppArray layout (header, then length, then inline elements) back into array ops
public static class ArrayRecovery
{
    private static readonly HashSet<string> ArrayNewFunctions =
    [
        "SzArrayNew",
        "il2cpp_vm_array_new_specific",
        "il2cpp_array_new_specific",
    ];

    // Il2CppArray is {Il2CppObject obj; void* bounds; il2cpp_array_size_t max_length;} then the elements, on all versions(?)
    private static long LengthOffset(int pointerSize) => 3L * pointerSize;
    private static long ElementsOffset(int pointerSize) => 4L * pointerSize;

    public static void Run(MethodAnalysisContext method)
    {
        RecoverPointerDerivedAccesses(
            method.ControlFlowGraph!,
            method.AppContext.Binary.PointerSizeBytes,
            method.AppContext.SystemTypes);
        RecoverAccesses(method);
        RecoverStructElementAddresses(method);
        GroupInitialisers(method.ControlFlowGraph!);
    }

    /// <summary>
    /// ARM64 对引用数组的访问通常先计算 <c>array + ElementsOffset</c>，再用
    /// <c>[data + index * pointerSize]</c> 取元素。若只看最终内存操作，基址已经是
    /// IntPtr，普通数组恢复就会遗漏，后续会把元素退化成 object 与原生指针算术。
    /// 这里沿单一定义回溯地址仿射式，只有根节点、元素偏移和步长全部精确匹配时才
    /// 改写为 ArrayAccess；多定义、非数组根或不完整步长保持原始操作。
    /// </summary>
    internal static void RecoverPointerDerivedAccesses(ISILControlFlowGraph cfg, int pointerSize)
        => RecoverPointerDerivedAccesses(cfg, pointerSize, null);

    internal static void RecoverPointerDerivedAccesses(
        ISILControlFlowGraph cfg,
        int pointerSize,
        SystemTypesContext? systemTypes)
    {
        var definitions = SingleDefinitions(cfg);
        var loopCursors = DiscoverLoopArrayCursors(cfg, pointerSize, systemTypes);
        var temporaryIndex = 0;

        foreach (var block in cfg.Blocks)
        {
            for (var instructionIndex = 0; instructionIndex < block.Instructions.Count; instructionIndex++)
            {
                var instruction = block.Instructions[instructionIndex];
                for (var operandIndex = 0; operandIndex < instruction.Operands.Count; operandIndex++)
                {
                    if (instruction.Operands[operandIndex] is not MemoryOperand memory)
                        continue;

                    if (LoopReferenceArrayIndex(memory, block, pointerSize, loopCursors) is { } loopAccess)
                    {
                        var index = MaterializeArrayIndex(
                            block,
                            instruction,
                            ref instructionIndex,
                            loopAccess.Index,
                            loopAccess.IndexDelta,
                            32,
                            ref temporaryIndex);

                        instruction.SetOperand(operandIndex, new ArrayAccess(loopAccess.Array, index));
                        continue;
                    }

                    if (ReferenceArrayIndex(memory, pointerSize, definitions) is { } access)
                    {
                        BindArrayIndexType(access.Index, memory.IndexExtension, systemTypes);
                        var index = MaterializeArrayIndex(
                            block,
                            instruction,
                            ref instructionIndex,
                            access.Index,
                            access.IndexDelta,
                            ArrayIndexWidthBits(memory.IndexExtension, pointerSize),
                            ref temporaryIndex);
                        instruction.SetOperand(operandIndex, new ArrayAccess(access.Array, index));
                    }
                }
            }
        }
    }

    /// <summary>
    /// 识别退 SSA 后成对推进的数组指针游标与托管列索引。只有初始化块、循环更新块、
    /// 元素步长、初始偏移和回边全部闭合，并且候选索引唯一时才建立映射。
    /// </summary>
    private static Dictionary<LocalVariable, LoopArrayCursor> DiscoverLoopArrayCursors(
        ISILControlFlowGraph cfg,
        int pointerSize,
        SystemTypesContext? systemTypes)
    {
        var definitions = DefinitionSites(cfg);
        var dominators = new DominatorInfo(cfg);
        var result = new Dictionary<LocalVariable, LoopArrayCursor>();
        var inductions = definitions
            .Select(pair => TrySplitInductionDefinitions(
                pair.Key,
                pair.Value,
                out var initialisation,
                out var update,
                out var step)
                ? new Induction(pair.Key, initialisation, update, step)
                : null)
            .OfType<Induction>()
            .ToArray();
        var inductionsByLoopAndStep = inductions.ToLookup(induction =>
            (induction.Initialisation.Block, induction.Update.Block, induction.Step));

        foreach (var cursorInduction in inductions)
        {
            var cursor = cursorInduction.Local;
            if (cursorInduction.Step <= 0
                || !TryArrayCursorInitialisation(
                    cursorInduction.Initialisation.Instruction,
                    pointerSize,
                    out var array,
                    out var elementSize,
                    out var cursorInitialIndex)
                || cursorInduction.Step % elementSize != 0
                || ReferenceEquals(cursorInduction.Initialisation.Block, cursorInduction.Update.Block)
                || !dominators.Dominates(
                    cursorInduction.Initialisation.Block,
                    cursorInduction.Update.Block)
                || !IsCyclicBlock(cursorInduction.Update.Block))
                continue;

            var indexStep = cursorInduction.Step / elementSize;
            var candidates = new List<LoopArrayCursor>();
            var pairedInductions = inductionsByLoopAndStep[
                (cursorInduction.Initialisation.Block, cursorInduction.Update.Block, indexStep)];
            foreach (var indexInduction in pairedInductions)
            {
                var index = indexInduction.Local;
                if (ReferenceEquals(index, cursor)
                    || indexInduction.Initialisation.Instruction is not
                    {
                        OpCode: OpCode.Move,
                        Operands: [_, Immediate { Value: var initialIndex }],
                    }
                    || initialIndex < 0
                    || !SupportedArrayIndexType(index.Type))
                    continue;

                var bias = cursorInitialIndex - initialIndex;
                if (bias < 0 || bias > int.MaxValue)
                    continue;

                candidates.Add(new LoopArrayCursor(array, index, initialIndex, bias, []));
            }

            // 中文注释：相同步长的多个标量计数器无法证明哪一个对应数组列号；保持原始内存红门。
            if (candidates.Count != 1)
                continue;

            var loopBlocks = StronglyConnectedLoopBlocks(cfg, cursorInduction.Update.Block);
            if (loopBlocks.Count == 0)
                continue;

            if (candidates[0].Index.Type == null && systemTypes != null)
                candidates[0].Index.Type = systemTypes.SystemInt32Type;
            result[cursor] = candidates[0] with { LoopBlocks = loopBlocks };
        }

        return result;
    }

    /// <summary>
    /// 把循环游标的内存偏移换算为“列索引 + 常量增量”；负索引、非整元素偏移和循环外消费均拒绝。
    /// </summary>
    private static LoopArrayAccess? LoopReferenceArrayIndex(
        MemoryOperand memory,
        Block block,
        int pointerSize,
        Dictionary<LocalVariable, LoopArrayCursor> cursors)
    {
        if (memory is not
            {
                Base: LocalVariable cursor,
                Index: null,
                Scale: 0,
            }
            || !cursors.TryGetValue(cursor, out var recovered)
            || !recovered.LoopBlocks.Contains(block))
            return null;

        var arrayType = (SzArrayTypeAnalysisContext)recovered.Array.Type!;
        var elementSize = ElementSize(arrayType.ElementType, pointerSize);
        if (elementSize <= 0 || memory.Addend % elementSize != 0)
            return null;

        var indexDelta = recovered.Bias + memory.Addend / elementSize;
        if (indexDelta < 0
            || indexDelta > int.MaxValue
            || recovered.InitialIndex > int.MaxValue - indexDelta)
            return null;

        return new LoopArrayAccess(recovered.Array, recovered.Index, indexDelta);
    }

    private static bool TryArrayCursorInitialisation(
        Instruction instruction,
        int pointerSize,
        out LocalVariable array,
        out long elementSize,
        out long initialIndex)
    {
        array = null!;
        elementSize = 0;
        initialIndex = 0;
        if (instruction is not { OpCode: OpCode.Add, Operands: [_, var left, var right] })
            return false;

        if (!TryArrayAndImmediate(left, right, out array, out var byteOffset)
            && !TryArrayAndImmediate(right, left, out array, out byteOffset))
            return false;

        var arrayType = (SzArrayTypeAnalysisContext)array.Type!;
        elementSize = ElementSize(arrayType.ElementType, pointerSize);
        var elementOffset = byteOffset - ElementsOffset(pointerSize);
        if (elementSize <= 0 || elementOffset < 0 || elementOffset % elementSize != 0)
            return false;

        initialIndex = elementOffset / elementSize;
        return true;
    }

    private static bool TryArrayAndImmediate(
        IOperand arrayOperand,
        IOperand immediateOperand,
        out LocalVariable array,
        out long byteOffset)
    {
        if (arrayOperand is LocalVariable { Type: SzArrayTypeAnalysisContext } candidate
            && immediateOperand is Immediate { Value: var value })
        {
            array = candidate;
            byteOffset = value;
            return true;
        }

        array = null!;
        byteOffset = 0;
        return false;
    }

    private static bool TrySplitInductionDefinitions(
        LocalVariable local,
        List<DefinitionSite> definitions,
        out DefinitionSite initialisation,
        out DefinitionSite update,
        out long step)
    {
        initialisation = null!;
        update = null!;
        step = 0;
        if (definitions.Count != 2)
            return false;

        var updates = definitions
            .Select(site => (Site: site, Step: SelfIncrement(site.Instruction, local)))
            .Where(candidate => candidate.Step.HasValue)
            .ToArray();
        if (updates.Length != 1)
            return false;

        update = updates[0].Site;
        step = updates[0].Step!.Value;
        initialisation = ReferenceEquals(definitions[0], update) ? definitions[1] : definitions[0];
        return true;
    }

    private static long? SelfIncrement(Instruction instruction, LocalVariable local)
    {
        if (instruction is not { OpCode: OpCode.Add, Operands: [var destination, var left, var right] }
            || !ReferenceEquals(destination, local))
            return null;

        if (ReferenceEquals(left, local) && right is Immediate { Value: var rightStep })
            return rightStep;
        if (ReferenceEquals(right, local) && left is Immediate { Value: var leftStep })
            return leftStep;
        return null;
    }

    private static Dictionary<LocalVariable, List<DefinitionSite>> DefinitionSites(ISILControlFlowGraph cfg)
    {
        var definitions = new Dictionary<LocalVariable, List<DefinitionSite>>();
        foreach (var block in cfg.Blocks)
        foreach (var instruction in block.Instructions)
        {
            if (instruction.Destination is not LocalVariable destination)
                continue;
            if (!definitions.TryGetValue(destination, out var sites))
                definitions[destination] = sites = [];
            sites.Add(new DefinitionSite(instruction, block));
        }

        return definitions;
    }

    private static bool SupportedArrayIndexType(TypeAnalysisContext? type) =>
        type == null
        || type.FullName is "System.Int32" or "System.UInt32" or "System.IntPtr" or "System.UIntPtr";

    private static bool IsCyclicBlock(Block block)
    {
        var visited = new HashSet<Block>();
        var pending = new Stack<Block>(block.Successors);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (ReferenceEquals(current, block))
                return true;
            if (!visited.Add(current))
                continue;
            foreach (var successor in current.Successors)
                pending.Push(successor);
        }

        return false;
    }

    private static HashSet<Block> StronglyConnectedLoopBlocks(ISILControlFlowGraph cfg, Block updateBlock)
    {
        var forward = ReachableFrom(updateBlock, block => block.Successors);
        var backward = ReachableFrom(updateBlock, block => block.Predecessors);
        forward.IntersectWith(backward);
        forward.IntersectWith(cfg.Blocks);
        return forward;
    }

    private static HashSet<Block> ReachableFrom(Block start, Func<Block, IEnumerable<Block>> edges)
    {
        var visited = new HashSet<Block>();
        var pending = new Stack<Block>();
        pending.Push(start);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current))
                continue;
            foreach (var next in edges(current))
                pending.Push(next);
        }

        return visited;
    }

    private sealed record DefinitionSite(Instruction Instruction, Block Block);

    private sealed record Induction(
        LocalVariable Local,
        DefinitionSite Initialisation,
        DefinitionSite Update,
        long Step);

    private sealed record LoopArrayCursor(
        LocalVariable Array,
        LocalVariable Index,
        long InitialIndex,
        long Bias,
        HashSet<Block> LoopBlocks);

    private sealed record LoopArrayAccess(LocalVariable Array, LocalVariable Index, long IndexDelta);

    private sealed record ReferenceArrayAccess(LocalVariable Array, IOperand Index, long IndexDelta);

    /// <summary>
    /// 把数组基址携带的正向元素偏移统一物化为索引加法。循环游标和直接动态寻址共用
    /// 同一条生成路径，避免两套索引增量计算发生语义漂移。
    /// </summary>
    private static IOperand MaterializeArrayIndex(
        Block block,
        Instruction consumer,
        ref int consumerInstructionIndex,
        IOperand index,
        long indexDelta,
        int integerWidthBits,
        ref int temporaryIndex)
    {
        if (indexDelta == 0)
            return index;

        var computedIndex = new LocalVariable(
            $"arrayIndex{temporaryIndex}",
            new Register(null, $"ARRAY_INDEX_{temporaryIndex}"),
            index is LocalVariable localIndex ? localIndex.Type : null);
        temporaryIndex++;
        block.Instructions.Insert(
            consumerInstructionIndex,
            new Instruction(
                consumer.Index,
                OpCode.Add,
                computedIndex,
                index,
                new Immediate(indexDelta))
            {
                IntegerWidthBits = integerWidthBits,
            });
        consumerInstructionIndex++;
        return computedIndex;
    }

    /// <summary>
    /// ARM64 寻址扩展决定索引加法的整数宽度；无扩展时寄存器使用原生宽度。
    /// </summary>
    private static int ArrayIndexWidthBits(MemoryIndexExtension extension, int pointerSize) => extension switch
    {
        MemoryIndexExtension.ZeroExtend32 or MemoryIndexExtension.SignExtend32 => 32,
        MemoryIndexExtension.None => pointerSize * 8,
        MemoryIndexExtension.ZeroExtend64 or MemoryIndexExtension.SignExtend64 => 64,
        _ => throw new ArgumentOutOfRangeException(nameof(extension), extension, null),
    };

    /// <summary>
    /// 数组索引的 ARM64 扩展方式是整数宽度的直接证据；只为尚未定型的局部量绑定类型。
    /// </summary>
    internal static void BindArrayIndexType(
        IOperand index,
        MemoryIndexExtension extension,
        SystemTypesContext? systemTypes)
    {
        if (index is not LocalVariable { Type: null } local || systemTypes == null)
            return;

        local.Type = extension switch
        {
            MemoryIndexExtension.ZeroExtend32 => systemTypes.SystemUInt32Type,
            MemoryIndexExtension.SignExtend32 => systemTypes.SystemInt32Type,
            MemoryIndexExtension.ZeroExtend64 => systemTypes.SystemUIntPtrType,
            MemoryIndexExtension.None or MemoryIndexExtension.SignExtend64 => systemTypes.SystemIntPtrType,
            _ => throw new ArgumentOutOfRangeException(nameof(extension), extension, null)
        };
    }

    private static ReferenceArrayAccess? ReferenceArrayIndex(
        MemoryOperand memory,
        int pointerSize,
        Dictionary<LocalVariable, Instruction?> definitions)
    {
        if (memory.Base is not LocalVariable baseLocal)
            return null;

        if (FoldedReferenceArrayIndex(memory, baseLocal, pointerSize, definitions) is { } foldedAccess)
            return new ReferenceArrayAccess(foldedAccess.Array, foldedAccess.Index, 0);

        var evaluated = Evaluate(baseLocal, definitions, 0);
        if (evaluated is not
            {
                Root: LocalVariable { Type: SzArrayTypeAnalysisContext arrayType } array,
                Multiplier: 1,
                Offset: var baseOffset
            })
            return null;

        var elementSize = ElementSize(arrayType.ElementType, pointerSize);
        if (elementSize == 0)
            return null;

        var offset = baseOffset;
        try
        {
            checked
            {
                offset += memory.Addend - ElementsOffset(pointerSize);
            }
        }
        catch (OverflowException)
        {
            return null;
        }

        if (offset < 0 || offset % elementSize != 0)
            return null;

        if (memory.Index == null)
            return memory.Scale == 0
                ? new ReferenceArrayAccess(array, new Immediate(offset / elementSize), 0)
                : null;

        return memory.Scale == elementSize
            ? new ReferenceArrayAccess(array, memory.Index, offset / elementSize)
            : null;
    }

    /// <summary>
    /// ARM64 也会先把 <c>array + index * stride</c> 折进同一个地址局部，最后以内存
    /// addend 携带数组头大小。该形态包含数组根与索引两个独立变量，普通单根仿射式无法表达；
    /// 这里只接受精确的 Add、元素头偏移以及与元素大小一致的 ShiftLeft/Multiply。
    /// </summary>
    private static ArrayAccess? FoldedReferenceArrayIndex(
        MemoryOperand memory,
        LocalVariable baseLocal,
        int pointerSize,
        Dictionary<LocalVariable, Instruction?> definitions)
    {
        if (memory.Index != null
            || memory.Scale != 0
            || memory.Addend != ElementsOffset(pointerSize)
            || !definitions.TryGetValue(baseLocal, out var addressDefinition)
            || addressDefinition is not { OpCode: OpCode.Add, Operands: [_, var left, var right] })
            return null;

        return TryFoldedOperands(left, right, pointerSize, definitions)
               ?? TryFoldedOperands(right, left, pointerSize, definitions);
    }

    private static ArrayAccess? TryFoldedOperands(
        IOperand arrayOperand,
        IOperand scaledIndexOperand,
        int pointerSize,
        Dictionary<LocalVariable, Instruction?> definitions)
    {
        if (Evaluate(arrayOperand, definitions, 0) is not
            {
                Root: LocalVariable { Type: SzArrayTypeAnalysisContext arrayType } array,
                Multiplier: 1,
                Offset: 0,
            })
            return null;

        var elementSize = ElementSize(arrayType.ElementType, pointerSize);
        if (elementSize == 0
            || scaledIndexOperand is not LocalVariable scaledIndex
            || !definitions.TryGetValue(scaledIndex, out var scaleDefinition)
            || scaleDefinition == null
            || TryScaledIndex(scaleDefinition, elementSize) is not { } index)
            return null;

        return new ArrayAccess(array, index);
    }

    private static IOperand? TryScaledIndex(Instruction definition, long elementSize)
    {
        if (definition is
            {
                OpCode: OpCode.ShiftLeft,
                Operands: [_, var shifted, Immediate { Value: >= 0 and < 31 } shift],
            }
            && 1L << (int)shift.Value == elementSize)
            return shifted;

        if (definition is
            {
                OpCode: OpCode.Multiply,
                Operands: [_, var multiplied, Immediate { Value: var factor }],
            }
            && factor == elementSize)
            return multiplied;

        return null;
    }

    private static void RecoverAccesses(MethodAnalysisContext method)
    {
        var pointerSize = method.AppContext.Binary.PointerSizeBytes;

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            RecoverAllocation(instruction);

            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                if (instruction.Operands[i] is not MemoryOperand memory
                    || memory.Base is not LocalVariable { Type: SzArrayTypeAnalysisContext arrayType } array)
                    continue;

                if (memory.Index == null && memory.Scale == 0 && memory.Addend == LengthOffset(pointerSize))
                {
                    instruction.SetOperand(i, new ArrayLength(array));
                    continue;
                }

                if (ElementIndex(memory, arrayType, pointerSize) is { } index)
                {
                    BindArrayIndexType(index, memory.IndexExtension, method.AppContext.SystemTypes);
                    instruction.SetOperand(i, new ArrayAccess(array, index));
                }
            }
        }
    }

    // Group initializers after an array allocation together so ILSpy decompiles them better
    private static void GroupInitialisers(ISILControlFlowGraph cfg)
    {
        var movedAny = false;

        foreach (var block in cfg.Blocks.ToList())
        {
            foreach (var allocation in block.Instructions.ToList())
            {
                if (allocation.OpCode != OpCode.NewArr || allocation.Operands[0] is not LocalVariable array)
                    continue;

                var stores = new List<(Block Block, Instruction Instruction)>();
                var current = block;
                var index = current.Instructions.IndexOf(allocation) + 1;

                while (true)
                {
                    if (index >= current.Instructions.Count)
                    {
                        // only a straight-line run can be regrouped without changing what runs when
                        if (current.Successors.Count != 1 || current.Successors[0].Predecessors.Count != 1)
                            break;

                        current = current.Successors[0];
                        index = 0;
                        continue;
                    }

                    var instruction = current.Instructions[index];

                    if (IsElementStore(instruction, array))
                    {
                        stores.Add((current, instruction));
                        index++;
                        continue;
                    }

                    if (!ReadsArray(instruction, array))
                    {
                        index++;
                        continue;
                    }

                    // Found the first read. Move the allocation and its stores immediately in front, so the whole array is built in one chain with the elements already computed.
                    if (stores.Count > 1)
                    {
                        foreach (var (storeBlock, store) in stores)
                            storeBlock.Instructions.Remove(store);

                        block.Instructions.Remove(allocation);

                        var moved = new List<Instruction> { allocation };
                        moved.AddRange(stores.Select(s => s.Instruction));

                        current.Instructions.InsertRange(current.Instructions.IndexOf(instruction), moved);
                        movedAny = true;
                    }

                    break;
                }
            }
        }

        // Emptying a block out entirely leaves branches pointing at nothing to jump to
        if (movedAny)
            cfg.RemoveEmptyBlocks();
    }

    private static bool IsElementStore(Instruction instruction, LocalVariable array) =>
        instruction.OpCode == OpCode.Move && instruction.Operands[0] is ArrayAccess { Index: Immediate } stored
                                          && ReferenceEquals(stored.Array, array)
                                          && !ReadsArray(instruction, array);

    private static bool ReadsArray(Instruction instruction, LocalVariable array)
    {
        for (var i = 0; i < instruction.Operands.Count; i++)
        {
            if (i == 0 && instruction.OpCode == OpCode.Move)
                continue;

            var reads = instruction.Operands[i] switch
            {
                LocalVariable local => ReferenceEquals(local, array),
                ArrayAccess access => ReferenceEquals(access.Array, array),
                ArrayLength length => ReferenceEquals(length.Array, array),
                MemoryOperand memory => ReferenceEquals(memory.Base, array) || ReferenceEquals(memory.Index, array),
                AddressOf { Target: LocalVariable addressed } => ReferenceEquals(addressed, array),
                _ => false
            };

            if (reads)
                return true;
        }

        return false;
    }

    private static void RecoverAllocation(Instruction instruction)
    {
        // Call "SzArrayNew", result, typeof(T[]), length, ...
        if (!instruction.IsCall || instruction.Operands is not [StringLiteral { Value: var name }, LocalVariable result, TypeAnalysisContext type, { } length, ..]
            || !ArrayNewFunctions.Contains(name))
            return;

        instruction.OpCode = OpCode.NewArr;
        instruction.SetOperands(result, type, length);

        if (result.Type is not SzArrayTypeAnalysisContext)
            result.Type = type;
    }

    private static IOperand? ElementIndex(MemoryOperand memory, SzArrayTypeAnalysisContext arrayType, int pointerSize)
    {
        var elementSize = ElementSize(arrayType.ElementType, pointerSize);
        var offset = memory.Addend - ElementsOffset(pointerSize);

        if (offset < 0 || elementSize == 0 || offset % elementSize != 0)
            return null;

        if (memory.Index == null)
            return memory.Scale == 0 ? new Immediate(offset / elementSize) : null;

        return memory.Scale == elementSize && offset == 0 ? memory.Index : null;
    }

    private static long ElementSize(TypeAnalysisContext elementType, int pointerSize)
    {
        if (!elementType.IsValueType)
            return pointerSize;

        return elementType.FullName switch
        {
            "System.Boolean" or "System.Byte" or "System.SByte" => 1,
            "System.Int16" or "System.UInt16" or "System.Char" => 2,
            "System.Int32" or "System.UInt32" or "System.Single" => 4,
            "System.Int64" or "System.UInt64" or "System.Double" => 8,
            "System.IntPtr" or "System.UIntPtr" => pointerSize,
            _ => 0 // struct arrays are handled by the element-address path, which sizes them from metadata
        };
    }

    // Recovers &array[i] over struct arrays. Struct elements are never loaded outright, the compiler
    // computes their address via lea chains and calls through it. We solve the index as a linear function
    // of a local and demand an exact hit on the metadata stride, so a real load can't match by accident.
    private static void RecoverStructElementAddresses(MethodAnalysisContext method)
    {
        var pointerSize = method.AppContext.Binary.PointerSizeBytes;
        var cfg = method.ControlFlowGraph!;

        var definitions = SingleDefinitions(cfg);
        var uses = CollectUses(cfg);

        foreach (var instruction in cfg.Instructions)
        {
            if (instruction.IsCall)
            {
                for (var i = 1; i < instruction.Operands.Count; i++)
                {
                    if (MatchElementAddress(instruction.Operands[i], pointerSize, definitions) is { } inlined)
                        instruction.SetOperand(i, inlined);
                }

                continue;
            }

            // a Move from memory could be a load or a lifted lea, and only the uses tell them apart
            if (instruction is not { OpCode: OpCode.Move, Operands: [LocalVariable destination, MemoryOperand] })
                continue;

            if (definitions.TryGetValue(destination, out var single) && single == null)
                continue;

            if (!uses.TryGetValue(destination, out var destinationUses) || destinationUses.Count == 0
                || !destinationUses.All(u => u.Instruction.IsCall || IsMemoryBase(u.Instruction.Operands[u.OperandIndex], destination)))
                continue;

            if (MatchElementAddress(instruction.Operands[1], pointerSize, definitions) is { } address)
                instruction.SetOperand(1, address);
        }
    }

    private static AddressOf? MatchElementAddress(IOperand operand, int pointerSize, Dictionary<LocalVariable, Instruction?> definitions)
    {
        if (operand is not MemoryOperand memory
            || memory.Base is not LocalVariable { Type: SzArrayTypeAnalysisContext arrayType } array)
            return null;

        var elementType = arrayType.ElementType;
        if (!elementType.IsValueType || ElementSize(elementType, pointerSize) != 0)
            return null;

        var elementSize = MetadataElementSize(elementType, pointerSize);
        if (elementSize <= 0)
            return null;

        return StructElementIndex(memory, array, elementSize, pointerSize, definitions) is { } index
            ? new AddressOf(new ArrayAccess(array, index))
            : null;
    }

    private static bool IsMemoryBase(IOperand operand, LocalVariable local)
        => operand is MemoryOperand { Base: LocalVariable baseLocal } && ReferenceEquals(baseLocal, local);

    private static long MetadataElementSize(TypeAnalysisContext elementType, int pointerSize)
        => TypeSizes.UnboxedSize(elementType, pointerSize);

    private static IOperand? StructElementIndex(MemoryOperand memory, LocalVariable array, long elementSize, int pointerSize,
        Dictionary<LocalVariable, Instruction?> definitions)
    {
        var indexAffine = memory.Index is LocalVariable indexLocal
            ? ScaleBy(Evaluate(indexLocal, definitions, 0), Math.Max(memory.Scale, 1))
            : new Affine(null, 0, 0);

        if (Sum(indexAffine, new Affine(null, 0, memory.Addend)) is not { } address)
            return null;

        if (ReferenceEquals(address.Root, array))
            return null;

        var offset = address.Offset - ElementsOffset(pointerSize);

        if (address.Root != null)
            return address.Multiplier == elementSize && offset == 0 ? address.Root : null;

        return offset >= 0 && offset % elementSize == 0 ? new Immediate(offset / elementSize) : null;
    }

    // value = Multiplier * Root + Offset (a null Root means it's just a constant)
    private readonly record struct Affine(LocalVariable? Root, long Multiplier, long Offset);

    private static Affine? Evaluate(IOperand operand, Dictionary<LocalVariable, Instruction?> definitions, int depth)
    {
        if (depth > 8)
            return null;

        switch (operand)
        {
            case Immediate { Value: var value }:
                return new Affine(null, 0, value);

            case LocalVariable local:
            {
                if (!definitions.TryGetValue(local, out var definition) || definition == null)
                    return new Affine(local, 1, 0);

                return definition switch
                {
                    { OpCode: OpCode.Move, Operands: [_, MemoryOperand lea] } => EvaluateLea(lea, definitions, depth + 1),
                    // 中文注释：字段读取等非仿射来源已经给局部写入精确数组类型；来源本身不可求值时，
                    // 保留当前局部为数组根，而不是让后续元素地址恢复整体失效。
                    { OpCode: OpCode.Move, Operands: [_, var source] } =>
                        Evaluate(source, definitions, depth + 1) ?? new Affine(local, 1, 0),
                    { OpCode: OpCode.Add, Operands: [_, var left, var right] } => Sum(Evaluate(left, definitions, depth + 1), Evaluate(right, definitions, depth + 1)),
                    { OpCode: OpCode.ShiftLeft, Operands: [_, var left, Immediate { Value: >= 0 and < 32 } shift] } => ScaleBy(Evaluate(left, definitions, depth + 1), 1L << (int)shift.Value),
                    { OpCode: OpCode.Multiply, Operands: [_, var left, Immediate factor] } => ScaleBy(Evaluate(left, definitions, depth + 1), factor.Value),
                    _ => new Affine(local, 1, 0)
                };
            }

            default:
                return null;
        }
    }

    private static Affine? EvaluateLea(MemoryOperand lea, Dictionary<LocalVariable, Instruction?> definitions, int depth)
    {
        var result = (Affine?)new Affine(null, 0, lea.Addend);

        if (lea.Base != null)
            result = Sum(result, Evaluate(lea.Base, definitions, depth));

        if (lea.Index != null)
            result = Sum(result, ScaleBy(Evaluate(lea.Index, definitions, depth), Math.Max(lea.Scale, 1)));

        return result;
    }

    private static Affine? Sum(Affine? left, Affine? right)
    {
        if (left is not { } l || right is not { } r)
            return null;

        if (l.Root != null && r.Root != null && !ReferenceEquals(l.Root, r.Root))
            return null;

        return new Affine(l.Root ?? r.Root, l.Multiplier + r.Multiplier, l.Offset + r.Offset);
    }

    private static Affine? ScaleBy(Affine? value, long factor)
        => value is { } affine ? new Affine(affine.Root, affine.Multiplier * factor, affine.Offset * factor) : null;

    // null means the local has more than one definition
    private static Dictionary<LocalVariable, Instruction?> SingleDefinitions(ISILControlFlowGraph cfg)
    {
        var definitions = new Dictionary<LocalVariable, Instruction?>();

        foreach (var instruction in cfg.Instructions)
            if (instruction.Destination is LocalVariable destination)
            {
                if (!definitions.TryGetValue(destination, out var existing))
                {
                    definitions[destination] = instruction;
                    continue;
                }

                // 中文注释：退 SSA 会把同一个循环不变量地址在入口和回边各复制一次；只在两次
                // 定义都是从同一个局部量进行完全相同的 Move 时合并，其他多定义继续保持未知。
                if (existing == null || !AreEquivalentRepeatedCopies(existing, instruction, destination))
                    definitions[destination] = null;
            }

        // 中文注释：ref/out 调用会在原生层间接写回目标局部；即使 CFG 中只有一次显式 Move，
        // 也不得沿该旧值继续折叠地址。将取址实参标成未知定义，后续仿射求值会保留数组根身份。
        foreach (var instruction in cfg.Instructions.Where(instruction => instruction.IsCall))
            foreach (var addressed in instruction.Operands.OfType<AddressOf>())
                if (addressed.Target is LocalVariable local)
                    definitions[local] = null;

        return definitions;
    }

    private static bool AreEquivalentRepeatedCopies(
        Instruction first,
        Instruction second,
        LocalVariable destination)
        => first is { OpCode: OpCode.Move, Operands: [LocalVariable firstDestination, LocalVariable firstSource] }
           && second is { OpCode: OpCode.Move, Operands: [LocalVariable secondDestination, LocalVariable secondSource] }
           && ReferenceEquals(firstDestination, destination)
           && ReferenceEquals(secondDestination, destination)
           && ReferenceEquals(firstSource, secondSource);

    private static Dictionary<LocalVariable, List<(Instruction Instruction, int OperandIndex)>> CollectUses(ISILControlFlowGraph cfg)
    {
        var uses = new Dictionary<LocalVariable, List<(Instruction, int)>>();

        foreach (var instruction in cfg.Instructions)
        {
            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                var operand = instruction.Operands[i];

                if (ReferenceEquals(operand, instruction.Destination))
                    continue;

                foreach (var local in OperandLocals(operand))
                {
                    if (!uses.TryGetValue(local, out var sites))
                        uses[local] = sites = [];
                    sites.Add((instruction, i));
                }
            }
        }

        return uses;
    }

    private static IEnumerable<LocalVariable> OperandLocals(IOperand operand)
    {
        switch (operand)
        {
            case LocalVariable direct:
                yield return direct;
                break;
            case MemoryOperand memory:
                if (memory.Base is LocalVariable baseLocal)
                    yield return baseLocal;
                if (memory.Index is LocalVariable indexLocal)
                    yield return indexLocal;
                break;
            case AddressOf { Target: LocalVariable addressed }:
                yield return addressed;
                break;
        }
    }
}
