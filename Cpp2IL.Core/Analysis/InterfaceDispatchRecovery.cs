using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Logging;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Analysis;

// Recovers interface calls from GetInterfaceInvokeData which is usually inlined. that method scans klass->interfaceOffsets
// for the declaring interface, indexes the vtable with (entryOffset + slot), or falls back to a slow path
// helper when the scan fails.
public static class InterfaceDispatchRecovery
{
    public static void Run(MethodAnalysisContext method)
    {
        // offsets below are the 64-bit Il2CppClass layout
        if (method.AppContext.Binary.PointerSizeBytes != 8)
            return;

        var cfg = method.ControlFlowGraph!;

        var definitions = new Dictionary<LocalVariable, Instruction>();
        var homeBlock = new Dictionary<Instruction, Block>();

        foreach (var block in cfg.Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                homeBlock[instruction] = block;
                if (instruction.Destination is LocalVariable destination)
                    definitions[destination] = instruction;
            }
        }

        var changed = false;

        foreach (var block in cfg.Blocks.ToList())
        {
            foreach (var instruction in block.Instructions.ToList())
            {
                if (instruction.OpCode is not (OpCode.IndirectCall or OpCode.IndirectJump))
                    continue;

                if (MatchDispatch(method, instruction, definitions, homeBlock, out var rejection) is not { } match)
                {
                    Logger.VerboseNewline(
                        $"{method.DeclaringType?.FullName}.{method.Name} 的间接转移 {instruction.Index} 未匹配接口分派：{rejection}",
                        nameof(InterfaceDispatchRecovery));
                    continue;
                }

                switch (match)
                {
                    case DirectMatch direct:
                        RewriteDispatch(method, instruction, block, direct.Resolved, direct.InvokeDataPhi, definitions);
                        break;
                    case ConditionalMatch conditional:
                        RewriteConditionalDispatch(method, instruction, block, conditional, definitions);
                        break;
                }

                // 已确认的慢路径调用只是 GetInterfaceInvokeDataFromVTableSlowPath 查表，不是此前误绑定的托管方法。
                // 直调已经携带完整接口语义，必须删除该查表调用，避免输出伪 Dictionary 调用。
                match.SlowCall.OpCode = OpCode.Nop;
                match.SlowCall.SetOperands();

                if (match is DirectMatch)
                    TryExciseLookup(cfg, match, definitions, homeBlock);
                changed = true;
            }
        }

        if (changed)
            DeadCodeEliminator.Run(method);
    }

    private const long VTableOffset = 0x138;
    private const int InvokeDataShift = 4; // sizeof(VirtualInvokeData) == 16

    private abstract record Match(
        Instruction InvokeDataPhi,
        Block Merge,
        Instruction SlowCall,
        LocalVariable KlassLocal);

    private sealed record DirectMatch(
        MethodAnalysisContext Resolved,
        Instruction InvokeDataPhi,
        Block Merge,
        Instruction SlowCall,
        LocalVariable KlassLocal)
        : Match(InvokeDataPhi, Merge, SlowCall, KlassLocal);

    private sealed record ConditionalMatch(
        MethodAnalysisContext TrueTarget,
        MethodAnalysisContext FalseTarget,
        IOperand Condition,
        Instruction InvokeDataPhi,
        Block Merge,
        Instruction SlowCall,
        LocalVariable KlassLocal)
        : Match(InvokeDataPhi, Merge, SlowCall, KlassLocal);

    private readonly record struct VTableMatch(LocalVariable KlassLocal, HashSet<int> Slots);

    private readonly record struct SlotSelection(IOperand Condition, int TrueSlot, int FalseSlot);

    private static Match? MatchDispatch(
        MethodAnalysisContext method,
        Instruction dispatch,
        Dictionary<LocalVariable, Instruction> definitions,
        Dictionary<Instruction, Block> homeBlock,
        out string rejection)
    {
        rejection = "目标不是 VirtualInvokeData::methodPtr 加载";
        // the call target loads VirtualInvokeData::methodPtr, separately or folded in
        var targetLoad = dispatch.Operands[0] switch
        {
            MemoryOperand folded => folded,
            FieldReference { Local: var fieldBase, Offset: 0 } => new MemoryOperand(fieldBase),
            LocalVariable target when Definition(definitions, target) is { OpCode: OpCode.Move, Operands: [_, MemoryOperand loaded] } => loaded,
            LocalVariable target when Definition(definitions, target) is { OpCode: OpCode.Move, Operands: [_, FieldReference { Local: var fieldBase, Offset: 0 }] } => new MemoryOperand(fieldBase),
            _ => default(MemoryOperand?)
        };

        if (targetLoad is not { Index: null, Scale: 0, Addend: 0, Base: LocalVariable invokeData })
            return null;

        rejection = "VirtualInvokeData 指针没有双路 Phi";
        if (Definition(definitions, invokeData) is not { OpCode: OpCode.Phi, Operands: [_, LocalVariable first, LocalVariable second] } phi)
            return null;

        var firstDefinition = Definition(definitions, first);
        var secondDefinition = Definition(definitions, second);

        Instruction? slowCall;
        LocalVariable receiverArg;
        IOperand interfaceArg;
        IOperand slotArg;

        if (TryMatchSlowPathCall(definitions, firstDefinition, out receiverArg, out interfaceArg, out slotArg))
            slowCall = firstDefinition;
        else if (TryMatchSlowPathCall(definitions, secondDefinition, out receiverArg, out interfaceArg, out slotArg))
            slowCall = secondDefinition;
        else if (homeBlock.TryGetValue(phi, out var slowMerge)
                 && TryFindSlowPathCall(
                     definitions,
                     slowMerge,
                     out slowCall,
                     out receiverArg,
                     out interfaceArg,
                     out slotArg))
        {
        }
        else
        {
            receiverArg = null!;
            interfaceArg = null!;
            slotArg = null!;
            slowCall = null;
        }

        var vtableEntry = ReferenceEquals(slowCall, firstDefinition) ? secondDefinition : firstDefinition;

        rejection = "Phi 输入中没有慢路径调用";
        if (slowCall == null)
            return null;

        rejection = "快速路径 vtable 地址链不匹配";
        if (MatchVTableEntryChain(definitions, vtableEntry) is not { } vtableMatch)
        {
            rejection = $"快速路径 vtable 地址链不匹配：{vtableEntry?.ToString() ?? "<无定义>"}";
            return null;
        }

        rejection = "接口类型证据不足";
        if (ResolveDeclaringInterface(definitions, interfaceArg, receiverArg, vtableMatch.KlassLocal) is not { } declaringInterface)
            return null;

        rejection = "Phi 所属合并块缺失";
        if (!homeBlock.TryGetValue(phi, out var merge))
            return null;

        if (ResolveConstant(definitions, slotArg) is { } slotValue)
        {
            rejection = "慢路径槽位越界或与快速路径不一致";
            if (slotValue is < 0 or > ushort.MaxValue || !vtableMatch.Slots.SetEquals([(int)slotValue]))
                return null;

            rejection = "接口槽位没有方法定义";
            if (ResolveInterfaceSlot(declaringInterface, (int)slotValue) is not { } resolved)
                return null;

            rejection = string.Empty;
            return new DirectMatch(resolved, phi, merge, slowCall, vtableMatch.KlassLocal);
        }

        rejection = $"慢路径槽位既非常量也不是可证明的双路 Phi：{DescribeOperandDefinition(definitions, slotArg)}";
        if (!TryResolveConditionalSlots(method, definitions, homeBlock, slotArg, out var selection, out var slowSlots))
            return null;

        rejection = "慢路径与快速路径的条件槽位集合不一致";
        if (!vtableMatch.Slots.SetEquals(slowSlots))
            return null;

        rejection = "条件接口槽位没有方法定义";
        if (ResolveInterfaceSlot(declaringInterface, selection.TrueSlot) is not { } trueTarget
            || ResolveInterfaceSlot(declaringInterface, selection.FalseSlot) is not { } falseTarget)
            return null;

        rejection = "条件接口分派目前要求两个目标均返回 void";
        if (!trueTarget.IsVoid || !falseTarget.IsVoid)
            return null;

        Logger.VerboseNewline(
            $"条件接口分派：快速槽位=[{string.Join(",", vtableMatch.Slots.OrderBy(value => value))}]；" +
            $"慢速槽位=[{string.Join(",", slowSlots.OrderBy(value => value))}]；" +
            $"true={selection.TrueSlot}:{trueTarget.FullName}；false={selection.FalseSlot}:{falseTarget.FullName}",
            nameof(InterfaceDispatchRecovery));

        rejection = "条件接口分派块没有唯一后继";
        if (homeBlock.TryGetValue(dispatch, out var dispatchBlock) && dispatchBlock.Successors.Count != 1)
            return null;

        rejection = string.Empty;
        return new ConditionalMatch(
            trueTarget,
            falseTarget,
            selection.Condition,
            phi,
            merge,
            slowCall,
            vtableMatch.KlassLocal);
    }

    /// <summary>
    /// 匹配 GetInterfaceInvokeDataFromVTableSlowPath 的调用参数形状。
    /// 目标可能仍是数值地址，也可能因共享原生地址而被提前标成托管方法；接收者、接口 TypeInfo 与槽位证据保持不变。
    /// </summary>
    private static bool TryMatchSlowPathCall(
        Dictionary<LocalVariable, Instruction> definitions,
        Instruction? candidate,
        out LocalVariable receiver,
        out IOperand interfaceType,
        out IOperand slot)
    {
        if (candidate is not { OpCode: OpCode.Call or OpCode.CallVoid, Operands.Count: >= 4 }
            || candidate.Operands[1] is not LocalVariable receiverOperand)
        {
            receiver = null!;
            interfaceType = null!;
            slot = null!;
            return false;
        }

        var interfaceOperand = candidate.Operands[2];
        var slotOperand = candidate.Operands[3];
        if (!IsRuntimeClassOperand(definitions, interfaceOperand)
            || ResolveConstant(definitions, slotOperand) is not { } resolvedSlot
            || resolvedSlot is < 0 or > ushort.MaxValue)
        {
            receiver = null!;
            interfaceType = null!;
            slot = null!;
            return false;
        }

        receiver = receiverOperand;
        interfaceType = interfaceOperand;
        slot = slotOperand;
        return true;
    }

    /// <summary>
    /// Phi 边复制会把慢路径结果直接写入 Phi 输入局部，使该输入不再有 SSA 定义。
    /// 此时仅沿对应前驱块反查唯一调用，并要求调用参数同时满足接收者、接口 TypeInfo 与常量槽位证据。
    /// </summary>
    private static bool TryFindSlowPathCall(
        Dictionary<LocalVariable, Instruction> definitions,
        Block merge,
        out Instruction call,
        out LocalVariable receiver,
        out IOperand interfaceType,
        out IOperand slot)
    {
        var matches = merge.Predecessors
            .SelectMany(predecessor => predecessor.Instructions)
            .Where(instruction => TryMatchSlowPathCall(
                definitions,
                instruction,
                out _,
                out _,
                out _))
            .ToList();
        if (matches.Count == 1
            && TryMatchSlowPathCall(
                definitions,
                matches[0],
                out receiver,
                out interfaceType,
                out slot))
        {
            call = matches[0];
            return true;
        }

        call = null!;
        receiver = null!;
        interfaceType = null!;
        slot = null!;
        return false;
    }

    /// <summary>
    /// 判断操作数是否是接口 TypeInfo 指针。共享原生地址会把慢路径助手错标成泛型方法，
    /// 因此只能从实参定义的运行时类类型恢复其真实角色。
    /// </summary>
    internal static bool IsRuntimeClassOperand(
        Dictionary<LocalVariable, Instruction> definitions,
        IOperand operand)
    {
        if (operand is RuntimeClassTypeAnalysisContext)
            return true;
        if (operand is not LocalVariable local)
            return false;

        if (local.Type is RuntimeClassTypeAnalysisContext)
            return true;

        return ChaseCopies(definitions, local) is
        {
            OpCode: OpCode.Move,
            Operands: [_, RuntimeClassTypeAnalysisContext],
        };
    }

    internal static long? ResolveConstant(Dictionary<LocalVariable, Instruction> definitions, IOperand operand)
    {
        if (operand is Immediate immediate)
            return immediate.Value;

        return operand is LocalVariable local
               && ChaseCopies(definitions, local) is { OpCode: OpCode.Move, Operands: [_, Immediate copied] }
            ? copied.Value
            : null;
    }

    private static string DescribeOperandDefinition(Dictionary<LocalVariable, Instruction> definitions, IOperand operand)
        => operand is LocalVariable local && Definition(definitions, local) is { } definition
            ? definition.ToString()
            : operand.ToString() ?? "<null>";

    private static bool TryResolveConditionalSlots(
        MethodAnalysisContext method,
        Dictionary<LocalVariable, Instruction> definitions,
        Dictionary<Instruction, Block> homeBlock,
        IOperand slotOperand,
        out SlotSelection selection,
        out HashSet<int> slots)
    {
        selection = default;
        slots = [];

        if (slotOperand is not LocalVariable slotLocal
            || ChaseCopies(definitions, slotLocal) is not { OpCode: OpCode.Phi } slotPhi
            || !homeBlock.TryGetValue(slotPhi, out var slotMerge)
            || slotPhi.Operands.Count != slotMerge.Predecessors.Count + 1
            || slotMerge.Predecessors.Count != 2)
            return false;

        var slotByPredecessor = new Dictionary<Block, int>();
        for (var i = 0; i < slotMerge.Predecessors.Count; i++)
        {
            if (ResolveConstant(definitions, slotPhi.Operands[i + 1]) is not { } value
                || value is < 0 or > ushort.MaxValue)
                return false;

            slotByPredecessor[slotMerge.Predecessors[i]] = (int)value;
            slots.Add((int)value);
        }

        if (slots.Count != 2 || method.DominatorInfo is not { } dominance)
            return false;

        var predecessors = slotMerge.Predecessors;
        var commonDominators = dominance.Dominators[predecessors[0]]
            .Intersect(dominance.Dominators[predecessors[1]])
            .OrderByDescending(candidate => dominance.Dominators[candidate].Count);

        foreach (var candidate in commonDominators)
        {
            var conditional = candidate.Instructions.LastOrDefault(instruction => instruction.OpCode != OpCode.Nop);
            if (conditional is not { OpCode: OpCode.ConditionalJump, Operands: [Block trueSuccessor, IOperand condition] })
                continue;

            var falseSuccessors = candidate.Successors.Where(successor => !ReferenceEquals(successor, trueSuccessor)).ToList();
            if (falseSuccessors.Count != 1)
                continue;

            var trueSlot = FindExclusiveReachableSlot(trueSuccessor, slotMerge, slotByPredecessor);
            var falseSlot = FindExclusiveReachableSlot(falseSuccessors[0], slotMerge, slotByPredecessor);
            if (trueSlot is null || falseSlot is null || trueSlot == falseSlot)
                continue;

            selection = new SlotSelection(condition, trueSlot.Value, falseSlot.Value);
            return true;
        }

        return false;
    }

    private static int? FindExclusiveReachableSlot(
        Block start,
        Block stop,
        Dictionary<Block, int> slotByPredecessor)
    {
        int? result = null;
        foreach (var entry in slotByPredecessor)
        {
            if (!CanReachBeforeMerge(start, entry.Key, stop))
                continue;

            if (result is not null)
                return null;

            result = entry.Value;
        }

        return result;
    }

    private static bool CanReachBeforeMerge(Block start, Block target, Block stop)
    {
        var visited = new HashSet<Block>();
        var queue = new Queue<Block>();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current))
                continue;
            if (ReferenceEquals(current, target))
                return true;
            if (ReferenceEquals(current, stop))
                continue;

            foreach (var successor in current.Successors)
                queue.Enqueue(successor);
        }

        return false;
    }

    private static TypeAnalysisContext? ResolveDeclaringInterface(
        Dictionary<LocalVariable, Instruction> definitions,
        IOperand interfaceArg,
        LocalVariable receiverArg,
        LocalVariable klassLocal)
    {
        if (interfaceArg is LocalVariable interfaceLocal
            && ChaseCopies(definitions, interfaceLocal) is
            {
                OpCode: OpCode.Move,
                Operands: [_, TypeAnalysisContext metadataType],
            }
            && IsInterface(metadataType))
            return metadataType;

        if (interfaceArg is LocalVariable typedInterface
            && typedInterface.Type is RuntimeClassTypeAnalysisContext { RepresentedType: { } representedInterface }
            && IsInterface(representedInterface))
            return representedInterface;

        if (interfaceArg is RuntimeClassTypeAnalysisContext { RepresentedType: { } directInterface }
            && IsInterface(directInterface))
            return directInterface;

        if (receiverArg.Type is { } receiverType && IsInterface(receiverType))
            return receiverType;

        if (klassLocal.Type is RuntimeClassTypeAnalysisContext { RepresentedType: { } representedType }
            && IsInterface(representedType))
            return representedType;

        return null;
    }

    private static bool IsInterface(TypeAnalysisContext type)
        => type is not RuntimeMethodInfoAnalysisContext
           && (type is GenericInstanceTypeAnalysisContext { GenericType.IsInterface: true } || type.IsInterface);

    private static VTableMatch? MatchVTableEntryChain(Dictionary<LocalVariable, Instruction> definitions, Instruction? vtableEntry)
    {
        // 两个上层业务分支可以各自完成一次快速查表，再由 Phi 汇合为同一个 VirtualInvokeData。
        // 每个输入都必须独立通过完整地址链验证，最终只合并已经证明的槽位集合。
        if (vtableEntry is { OpCode: OpCode.Phi, Operands.Count: >= 3 } entryPhi)
        {
            LocalVariable? firstKlass = null;
            var combinedSlots = new HashSet<int>();

            Logger.VerboseNewline(
                $"接口 VirtualInvokeData Phi 输入数：{entryPhi.Operands.Count - 1}",
                nameof(InterfaceDispatchRecovery));

            foreach (var source in entryPhi.Operands.Skip(1))
            {
                var sourceDefinition = source is LocalVariable sourceLocal ? ChaseCopies(definitions, sourceLocal) : null;
                Logger.VerboseNewline(
                    $"接口 VirtualInvokeData Phi 输入：{source}；SSA 定义：{sourceDefinition?.ToString() ?? "<无>"}",
                    nameof(InterfaceDispatchRecovery));
                if (MatchVTableEntryChain(definitions, sourceDefinition) is not { } branchMatch)
                    return null;

                firstKlass ??= branchMatch.KlassLocal;
                combinedSlots.UnionWith(branchMatch.Slots);
            }

            return firstKlass == null || combinedSlots.Count == 0
                ? null
                : new VTableMatch(firstKlass, combinedSlots);
        }

        // ARM64 常先计算 klass + ((entryOffset + slot) << 4)，再单独加 Il2CppClass::vtable。
        var vtableOffsetIsOuter = false;
        if (vtableEntry is { OpCode: OpCode.Add, Operands: [_, LocalVariable beforeVTable, Immediate { Value: VTableOffset }] })
        {
            vtableEntry = ChaseCopies(definitions, beforeVTable);
            vtableOffsetIsOuter = true;
        }

        if (vtableEntry is not { OpCode: OpCode.Add, Operands: [_, LocalVariable addLeft, LocalVariable addRight] })
        {
            Logger.VerboseNewline(
                $"接口 vtable 基址加法不匹配：{vtableEntry?.ToString() ?? "<无>"}",
                nameof(InterfaceDispatchRecovery));
            return null;
        }

        var (klassCandidate, sum) = Definition(definitions, addRight) is { OpCode: OpCode.Move, Operands: [_, MemoryOperand { Index: null, Scale: 0, Addend: 0 }] }
            ? (addRight, addLeft)
            : (addLeft, addRight);

        if (!IsKlassLoad(definitions, klassCandidate, []))
        {
            Logger.VerboseNewline(
                $"接口 klass 加载不匹配：{DescribeOperandDefinition(definitions, klassCandidate)}；另一路={DescribeOperandDefinition(definitions, sum)}",
                nameof(InterfaceDispatchRecovery));
            return null;
        }

        LocalVariable shifted;
        if (vtableOffsetIsOuter)
            shifted = sum;
        else if (ChaseCopies(definitions, sum) is { OpCode: OpCode.Add, Operands: [_, LocalVariable nestedShifted, Immediate { Value: VTableOffset }] })
            shifted = nestedShifted;
        else
            return null;

        if (ChaseCopies(definitions, shifted) is not { OpCode: OpCode.ShiftLeft, Operands: [_, IOperand index, Immediate { Value: InvokeDataShift }] })
        {
            Logger.VerboseNewline(
                $"接口 VirtualInvokeData 位移不匹配：{DescribeOperandDefinition(definitions, shifted)}",
                nameof(InterfaceDispatchRecovery));
            return null;
        }

        var unwrappedIndex = Arm64AddOperandHelper.TryUnwrapSignedWordExtension(
            index,
            operand => operand is LocalVariable local ? Definition(definitions, local) : null);
        var normalizedIndex = unwrappedIndex ?? index;
        var slots = new HashSet<int>();
        var entryOffset = normalizedIndex is LocalVariable originalIndex ? ChaseCopies(definitions, originalIndex) : null;
        Logger.VerboseNewline(
            $"接口 vtable 索引：{normalizedIndex}；SSA 定义：{entryOffset?.ToString() ?? "<无>"}",
            nameof(InterfaceDispatchRecovery));
        if (entryOffset is { OpCode: OpCode.Phi, Operands.Count: >= 3 } indexPhi)
        {
            foreach (var source in indexPhi.Operands.Skip(1))
            {
                Logger.VerboseNewline(
                    $"接口 vtable 索引 Phi 输入：{source}；SSA 定义：{DescribeOperandDefinition(definitions, source)}",
                    nameof(InterfaceDispatchRecovery));
                if (!TryMatchEntryIndex(definitions, source, slots))
                    return null;
            }
        }
        else if (!TryMatchEntryIndex(definitions, normalizedIndex, slots))
            return null;

        return slots.Count == 0 ? null : new VTableMatch(klassCandidate, slots);
    }

    internal static bool IsKlassLoad(
        Dictionary<LocalVariable, Instruction> definitions,
        LocalVariable candidate,
        HashSet<LocalVariable> visited)
    {
        if (!visited.Add(candidate))
            return false;

        var definition = ChaseCopies(definitions, candidate);
        if (definition is { OpCode: OpCode.Move, Operands: [_, IOperand loadedSource] }
            && IsObjectHeaderClassLoad(loadedSource))
            return true;

        if (definition is not { OpCode: OpCode.Phi, Operands.Count: >= 3 } phi)
            return false;

        foreach (var source in phi.Operands.Skip(1))
            if (source is not LocalVariable sourceLocal || !IsKlassLoad(definitions, sourceLocal, visited))
                return false;

        return true;
    }

    /// <summary>
    /// 判断操作数是否为对象头首指针（Il2CppObject::klass）的精确零偏移读取。
    /// 当共享原生地址把接收者错误标成布尔等值类型时，字段解析会把同一条 <c>[receiver]</c>
    /// 写成零偏移 <see cref="FieldReference"/>；两种表示都必须归一为同一个对象头证据。
    /// </summary>
    internal static bool IsObjectHeaderClassLoad(IOperand source)
        => source is MemoryOperand { Index: null, Scale: 0, Addend: 0, Base: LocalVariable }
           or FieldReference { Offset: 0 };

    internal static bool TryMatchEntryIndex(
        Dictionary<LocalVariable, Instruction> definitions,
        IOperand index,
        HashSet<int> slots)
    {
        var definition = index is LocalVariable local ? ChaseCopies(definitions, local) : null;
        IOperand entryValue;
        long slot;

        if (definition is { OpCode: OpCode.Add, Operands: [_, IOperand beforeSlot, Immediate slotAddend] })
        {
            entryValue = beforeSlot;
            slot = slotAddend.Value;
        }
        else
        {
            entryValue = index;
            slot = 0;
        }

        if (slot is < 0 or > ushort.MaxValue)
            return false;

        var entryLoad = entryValue switch
        {
            MemoryOperand inlined => inlined,
            FieldReference { Local: var fieldBase, Offset: var fieldOffset } => new MemoryOperand(fieldBase, addend: fieldOffset),
            LocalVariable loadedLocal when ChaseCopies(definitions, loadedLocal) is
            {
                OpCode: OpCode.Move,
                Operands: [_, MemoryOperand loaded],
            } => loaded,
            LocalVariable fieldLocal when ChaseCopies(definitions, fieldLocal) is
            {
                OpCode: OpCode.Move,
                Operands: [_, FieldReference { Local: var fieldBase, Offset: var fieldOffset }],
            } => new MemoryOperand(fieldBase, addend: fieldOffset),
            _ => default(MemoryOperand?),
        };

        if (entryLoad is not { Base: not null })
        {
            Logger.VerboseNewline(
                $"接口 entryOffset 加载不匹配：值={entryValue}；SSA 定义={DescribeOperandDefinition(definitions, entryValue)}",
                nameof(InterfaceDispatchRecovery));
            return false;
        }

        slots.Add((int)slot);
        return true;
    }

    private static Instruction? Definition(Dictionary<LocalVariable, Instruction> definitions, LocalVariable local)
        => definitions.TryGetValue(local, out var definition) ? definition : null;

    private static Instruction? ChaseCopies(Dictionary<LocalVariable, Instruction> definitions, LocalVariable local)
    {
        var visited = new HashSet<LocalVariable>();

        while (visited.Add(local))
        {
            if (Definition(definitions, local) is not { } definition)
                return null;

            if (definition is { OpCode: OpCode.Move, Operands: [_, LocalVariable source] })
            {
                local = source;
                continue;
            }

            return definition;
        }

        return null;
    }

    private static MethodAnalysisContext? ResolveInterfaceSlot(TypeAnalysisContext declaringInterface, int slot)
    {
        Logger.VerboseNewline(
            $"接口槽位解析 {declaringInterface.FullName}[{slot}]：" +
            string.Join("；", declaringInterface.Methods.Select((method, index) =>
                $"序号{index}={method.Name}/元数据槽位{method.Definition?.slot.ToString() ?? "<无>"}")),
            nameof(InterfaceDispatchRecovery));

        if (declaringInterface is GenericInstanceTypeAnalysisContext genericInstance)
        {
            var baseMethod = genericInstance.GenericType.Methods.FirstOrDefault(m => m.Definition?.slot == slot);
            return baseMethod == null ? null : new ConcreteGenericMethodAnalysisContext(baseMethod, genericInstance.GenericArguments, []);
        }

        return declaringInterface.Methods.FirstOrDefault(m => m.Definition?.slot == slot);
    }

    private static void RewriteDispatch(
        MethodAnalysisContext method,
        Instruction dispatch,
        Block block,
        MethodAnalysisContext resolved,
        Instruction invokeDataPhi,
        Dictionary<LocalVariable, Instruction> definitions)
    {
        IndirectTransferCallRewriter.Rewrite(method, dispatch, block, resolved);

        // 将 [phi+8] 标记为隐藏 MethodInfo 参数。尾调用的目标寄存器也可能兼作参数槽，
        // 因而陈旧的 [phi] 加载也可能进入参数列表；用零占位后，VirtualInvokeData 指针即可被删除。
        var assembly = resolved.DeclaringType?.DeclaringAssembly ?? method.DeclaringType?.DeclaringAssembly;
        for (var i = 1; i < dispatch.Operands.Count; i++)
        {
            if (dispatch.Operands[i] is not LocalVariable argument
                || Definition(definitions, argument) is not { OpCode: OpCode.Move, Operands: [_, MemoryOperand { Index: null, Scale: 0, Base: LocalVariable loadBase } load] }
                || !ReferenceEquals(Definition(definitions, loadBase), invokeDataPhi))
                continue;

            if (load.Addend == 8 && assembly != null)
                dispatch.SetOperand(i, new RuntimeMethodInfoAnalysisContext(resolved, assembly));
            else if (load.Addend == 0)
                dispatch.SetOperand(i, new Immediate(0));
        }

    }

    private static void RewriteConditionalDispatch(
        MethodAnalysisContext method,
        Instruction dispatch,
        Block block,
        ConditionalMatch match,
        Dictionary<LocalVariable, Instruction> definitions)
    {
        var cfg = method.ControlFlowGraph!;
        var dispatchIndex = block.Instructions.IndexOf(dispatch);
        var suffix = block.Instructions.Skip(dispatchIndex + 1).ToList();
        var originalSuccessors = block.Successors.ToList();
        var nextBlockId = cfg.Blocks.Max(candidate => candidate.ID) + 1;

        var falseCallBlock = new Block { ID = nextBlockId++ };
        var trueCallBlock = new Block { ID = nextBlockId++ };
        var continuation = new Block { ID = nextBlockId };

        var falseCall = new Instruction(dispatch.Index, dispatch.OpCode, dispatch.Operands.ToList());
        var trueCall = new Instruction(dispatch.Index, dispatch.OpCode, dispatch.Operands.ToList());
        RewriteDispatch(method, falseCall, falseCallBlock, match.FalseTarget, match.InvokeDataPhi, definitions);
        RewriteDispatch(method, trueCall, trueCallBlock, match.TrueTarget, match.InvokeDataPhi, definitions);

        falseCallBlock.AddInstruction(falseCall);
        trueCallBlock.AddInstruction(trueCall);
        continuation.Instructions.AddRange(suffix);

        block.Instructions.RemoveRange(dispatchIndex, block.Instructions.Count - dispatchIndex);
        dispatch.OpCode = OpCode.ConditionalJump;
        dispatch.SetOperands(trueCallBlock, match.Condition);
        block.AddInstruction(dispatch);

        // ConvertedIsil 与 CFG 共享原始指令对象；原位改写旧间接调用，并只补入这次新建的两个直调。
        // 这样输出能反映新 CFG，同时保留其余方法的完整原生审计序列与指令计数。
        if (method.ConvertedIsil is { } linearIsil && linearIsil.IndexOf(dispatch) is var linearIndex && linearIndex >= 0)
        {
            linearIsil.Insert(linearIndex + 1, falseCall);
            linearIsil.Insert(linearIndex + 2, trueCall);
        }

        foreach (var successor in originalSuccessors)
        {
            for (var i = 0; i < successor.Predecessors.Count; i++)
                if (ReferenceEquals(successor.Predecessors[i], block))
                    successor.Predecessors[i] = continuation;
        }

        block.Successors.Clear();
        block.Successors.Add(falseCallBlock);
        block.Successors.Add(trueCallBlock);
        falseCallBlock.Predecessors.Add(block);
        trueCallBlock.Predecessors.Add(block);

        falseCallBlock.Successors.Add(continuation);
        trueCallBlock.Successors.Add(continuation);
        continuation.Predecessors.Add(falseCallBlock);
        continuation.Predecessors.Add(trueCallBlock);
        continuation.Successors.AddRange(originalSuccessors);

        cfg.Blocks.Add(falseCallBlock);
        cfg.Blocks.Add(trueCallBlock);
        cfg.Blocks.Add(continuation);

        block.CalculateBlockType();
        falseCallBlock.CalculateBlockType();
        trueCallBlock.CalculateBlockType();
        continuation.CalculateBlockType();
    }

    // Bailing here is fine, it just leaves the (already resolved) call with dead lookup around it
    private static void TryExciseLookup(ISILControlFlowGraph cfg, Match match, Dictionary<LocalVariable, Instruction> definitions, Dictionary<Instruction, Block> homeBlock)
    {
        var merge = match.Merge;

        if (!homeBlock.TryGetValue(match.SlowCall, out var slowBlock))
            return;

        if (Definition(definitions, match.KlassLocal) is not { } klassDefinition
            || !homeBlock.TryGetValue(klassDefinition, out var head) || head == merge)
            return;

        if (!TryCollectRegion(cfg, head, merge, out var region) || !region.Contains(slowBlock))
            return;

        if (!RegionIsSideEffectFree(region, match.SlowCall) || AnyValueEscapes(cfg, region, merge))
            return;

        if (!MergePhisAreDead(cfg, merge, out var removable))
            return;

        foreach (var instruction in removable)
        {
            instruction.OpCode = OpCode.Nop;
            instruction.SetOperands();
        }

        foreach (var successor in head.Successors)
            successor.Predecessors.Remove(head);
        head.Successors.Clear();
        head.Successors.Add(merge);

        var terminator = head.Instructions[^1];
        if (terminator.OpCode is OpCode.Jump or OpCode.ConditionalJump)
        {
            terminator.OpCode = OpCode.Jump;
            terminator.SetOperands(merge);
        }
        else
            head.AddInstruction(new Instruction(-1, OpCode.Jump, merge));

        head.CalculateBlockType();

        merge.Predecessors.RemoveAll(region.Contains);
        merge.Predecessors.Add(head);

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

    // The region has to be closed, so nothing else may enter or leave it
    private static bool TryCollectRegion(ISILControlFlowGraph cfg, Block head, Block merge, out HashSet<Block> region)
    {
        region = [];

        var queue = new Queue<Block>(merge.Predecessors);

        while (queue.Count > 0)
        {
            var block = queue.Dequeue();

            if (block == head)
                continue;

            if (block == merge || block == cfg.EntryBlock || block == cfg.ExitBlock || region.Count > 64)
                return false;

            if (!region.Add(block))
                continue;

            foreach (var predecessor in block.Predecessors)
                queue.Enqueue(predecessor);
        }

        if (region.Count == 0)
            return false;

        var collected = region;
        foreach (var block in collected)
        {
            if (block.Predecessors.Any(p => p != head && !collected.Contains(p)))
                return false;
            if (block.Successors.Any(s => s != merge && !collected.Contains(s)))
                return false;
        }

        // we rewrite the head's terminator, so it can't branch anywhere else
        return head.Successors.All(s => s == merge || collected.Contains(s));
    }

    private static bool RegionIsSideEffectFree(HashSet<Block> region, Instruction slowCall)
    {
        foreach (var block in region)
        {
            foreach (var instruction in block.Instructions)
            {
                if (ReferenceEquals(instruction, slowCall))
                    continue;

                var harmless = instruction.OpCode switch
                {
                    OpCode.Nop or OpCode.Jump or OpCode.ConditionalJump or OpCode.Phi => true,
                    OpCode.Move or OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide
                        or OpCode.ShiftLeft or OpCode.ShiftRight or OpCode.And or OpCode.Or or OpCode.Xor
                        or OpCode.Not or OpCode.Negate
                        or OpCode.ConvertFloatingPointPrecision or OpCode.ConvertFloatToSignedInteger
                        or OpCode.ConvertSignedIntegerToFloat
                        or OpCode.ReinterpretIntegerBitsAsFloat or OpCode.ReinterpretFloatBitsAsInteger
                        or OpCode.RoundFloatTowardPositiveInfinity or OpCode.RoundFloatTowardNegativeInfinity
                        or (>= OpCode.CheckEqual and <= OpCode.CheckLessOrEqualUnsigned)
                        => instruction.Destination is LocalVariable,
                    _ => false,
                };

                if (!harmless)
                    return false;
            }
        }

        return true;
    }

    // Merge phis are exempt, their deadness gets checked separately
    private static bool AnyValueEscapes(ISILControlFlowGraph cfg, HashSet<Block> region, Block merge)
    {
        var regionDefs = new HashSet<LocalVariable>();
        foreach (var block in region)
            foreach (var instruction in block.Instructions)
                if (instruction.Destination is LocalVariable destination)
                    regionDefs.Add(destination);

        foreach (var block in cfg.Blocks)
        {
            if (region.Contains(block))
                continue;

            foreach (var instruction in block.Instructions)
            {
                if (instruction.OpCode == OpCode.Phi && block == merge)
                    continue;

                if (Uses(instruction, regionDefs))
                    return true;
            }
        }

        return false;
    }

    // They may only feed loads off the VirtualInvokeData pointer, which must themselves be dead
    private static bool MergePhisAreDead(ISILControlFlowGraph cfg, Block merge, out List<Instruction> removable)
    {
        removable = [];

        var useSites = new Dictionary<LocalVariable, List<Instruction>>();
        foreach (var block in cfg.Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                foreach (var used in UsedLocals(instruction))
                {
                    if (!useSites.TryGetValue(used, out var sites))
                        useSites[used] = sites = [];
                    sites.Add(instruction);
                }
            }
        }

        foreach (var phi in merge.Instructions)
        {
            if (phi.OpCode != OpCode.Phi)
                continue;

            if (phi.Operands[0] is not LocalVariable phiDest)
                return false;

            foreach (var use in useSites.TryGetValue(phiDest, out var phiUses) ? phiUses : [])
            {
                if (use is not { OpCode: OpCode.Move, Operands: [LocalVariable loaded, MemoryOperand] }
                    || (useSites.TryGetValue(loaded, out var loadUses) && loadUses.Count > 0))
                    return false;

                removable.Add(use);
            }

            removable.Add(phi);
        }

        return true;
    }

    private static bool Uses(Instruction instruction, HashSet<LocalVariable> candidates)
        => UsedLocals(instruction).Any(candidates.Contains);

    private static IEnumerable<LocalVariable> UsedLocals(Instruction instruction)
    {
        for (var i = 0; i < instruction.Operands.Count; i++)
        {
            if (ReferenceEquals(instruction.Operands[i], instruction.Destination))
                continue;

            switch (instruction.Operands[i])
            {
                case LocalVariable local:
                    yield return local;
                    break;
                case AddressOf { Target: LocalVariable addressed }:
                    yield return addressed;
                    break;
                case MemoryOperand memory:
                    if (memory.Base is LocalVariable baseLocal)
                        yield return baseLocal;
                    if (memory.Index is LocalVariable indexLocal)
                        yield return indexLocal;
                    break;
            }
        }
    }
}
