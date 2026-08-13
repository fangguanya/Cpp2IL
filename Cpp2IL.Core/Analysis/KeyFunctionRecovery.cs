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

    private const string ObjectIsInstFunction = nameof(BaseKeyFunctionAddresses.il2cpp_vm_object_is_inst);

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

        var definitions = BuildUniqueDefinitions(instructions);
        var boxTypesByHandle = BuildProvenBoxTypesByHandle(
            method.ControlFlowGraph.Blocks,
            definitions,
            method.DominatorInfo);

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
                    method.DominatorInfo,
                    boxTypesByHandle);
            }
        }
    }

    /// <summary>
    /// 把运行时 Object::IsInst 调用恢复为托管 isinst。原生帮助器返回原对象或 null，
    /// 与强制转换的异常语义不同，因此必须保留独立操作码。
    /// </summary>
    public static void RewriteTypeTests(MethodAnalysisContext method)
    {
        var instructions = method.ControlFlowGraph!.Blocks.SelectMany(block => block.Instructions).ToList();
        var definitions = BuildUniqueDefinitions(instructions);
        foreach (var instruction in instructions)
        {
            if (instruction is not
                {
                    OpCode: OpCode.Call,
                    Operands: [StringLiteral { Value: ObjectIsInstFunction }, LocalVariable result, { } source,
                        { } typeHandle, ..],
                })
                continue;

            var testedType = ResolveRuntimeClassType(typeHandle, definitions)
                             ?? ResolveTypeTestResultStorageType(
                                 instruction,
                                 result,
                                 instructions,
                                 definitions);
            if (testedType is not { IsValueType: false })
                continue;

            result.Type = testedType;
            instruction.OpCode = OpCode.IsInst;
            instruction.SetOperands(result, source, testedType);
        }
    }

    /// <summary>
    /// 当ARM64共享代码只把类型句柄保留为泛型寄存器时，使用 isinst 结果随后写入的
    /// 唯一强类型栈槽恢复目标类型。该证据来自同一个SSA结果的真实数据流，且多个
    /// 消费者必须指向等价引用类型；类型冲突或值类型一律保持原始调用。
    /// </summary>
    private static TypeAnalysisContext? ResolveTypeTestResultStorageType(
        Instruction typeTest,
        LocalVariable result,
        IReadOnlyList<Instruction> instructions,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions)
    {
        TypeAnalysisContext? provenType = null;
        foreach (var instruction in instructions)
        {
            if (instruction.Index <= typeTest.Index
                || instruction is not
                {
                    OpCode: OpCode.Move,
                    Operands: [MemoryOperand memory, LocalVariable source],
                }
                || !ReferenceEquals(source, result)
                || ResolveExactAddressedSlotType(memory, definitions) is not { IsValueType: false } candidate)
                continue;

            if (provenType != null && !GenericCallRebinder.TypesEquivalent(provenType, candidate))
                return null;

            provenType ??= candidate;
        }

        return provenType;
    }

    /// <summary>
    /// 只接受零偏移、无索引的精确栈槽地址，避免把对象字段或数组元素类型误当成
    /// isinst 的目标类型。
    /// </summary>
    private static TypeAnalysisContext? ResolveExactAddressedSlotType(
        MemoryOperand memory,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions)
    {
        if (memory is not { Base: LocalVariable carrier, Index: null, Scale: 0, Addend: 0 })
            return null;

        // SSA重命名后的地址寄存器会直接携带 T&；这比同物理寄存器跨块出现的多个
        // AddressOf定义更精确，因此先读取其元素类型，并继续由调用者限制为引用类型。
        if (carrier.Type is ByRefTypeAnalysisContext { ElementType: { } elementType })
            return elementType;

        if (!definitions.TryGetValue(carrier, out var definition)
            || definition is not
            {
                OpCode: OpCode.Move,
                Operands: [LocalVariable destination, AddressOf { Target: LocalVariable slot }],
            }
            || !ReferenceEquals(destination, carrier))
            return null;

        return slot.Type;
    }

    private static TypeAnalysisContext? ResolveRuntimeClassType(
        IOperand operand,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions)
    {
        var visited = new HashSet<LocalVariable>();
        while (operand is LocalVariable local && visited.Add(local))
        {
            if (local.Type is RuntimeClassTypeAnalysisContext runtimeClass)
                return runtimeClass.RepresentedType;
            if (!definitions.TryGetValue(local, out var definition)
                || definition is not { OpCode: OpCode.Move, Operands.Count: 2 })
                return null;

            operand = definition.Operands[1];
        }

        return operand switch
        {
            RuntimeClassTypeAnalysisContext runtimeClass => runtimeClass.RepresentedType,
            // 元数据解析器会把直接 TypeInfo 常量表示为被描述的托管类型；普通局部的
            // Type 只描述局部自身，已在上方排除，二者不可混用。
            TypeAnalysisContext type when type is not RuntimeClassTypeAnalysisContext => type,
            _ => null,
        };
    }

    private static IReadOnlyDictionary<string, TypeAnalysisContext> BuildProvenBoxTypesByHandle(
        IReadOnlyList<Block> blocks,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions,
        DominatorInfo? dominance)
    {
        var proven = new Dictionary<string, TypeAnalysisContext>();
        var ambiguous = new HashSet<string>();

        foreach (var block in blocks)
        {
            for (var instructionIndex = 0; instructionIndex < block.Instructions.Count; instructionIndex++)
            {
                var instruction = block.Instructions[instructionIndex];
                if (instruction.OpCode != OpCode.Call
                    || instruction.Operands is not [StringLiteral { Value: var keyFunction }, _, { } typeHandle, { } dataAddress, ..]
                    || !ObjectBoxFunctions.Contains(keyFunction)
                    || ResolveAddressedValue(dataAddress, definitions) is not { } addressed
                    || ResolveLatestStackValue(
                        addressed,
                        blocks,
                        block,
                        instructionIndex,
                        instruction.Index,
                        dominance) is not { Type: { IsValueType: true } boxedType }
                    || !TryCanonicalizeBoxTypeHandle(typeHandle, definitions, out var handleKey)
                    || ambiguous.Contains(handleKey))
                    continue;

                if (proven.TryGetValue(handleKey, out var existing)
                    && !GenericCallRebinder.TypesEquivalent(existing, boxedType))
                {
                    // 同一句柄出现互斥值类型时证据不再唯一，禁止向未知栈槽传播任一猜测。
                    proven.Remove(handleKey);
                    ambiguous.Add(handleKey);
                    continue;
                }

                proven[handleKey] = boxedType;
            }
        }

        return proven;
    }

    internal static IReadOnlyCollection<Instruction> FindBoxDataWriteRoots(MethodAnalysisContext method)
    {
        var blocks = method.ControlFlowGraph?.Blocks;
        if (blocks == null)
            return [];

        var instructions = blocks.SelectMany(block => block.Instructions).ToList();
        var hasImmediateTarget = instructions.Any(instruction =>
            instruction.OpCode == OpCode.Call
            && instruction.Operands.FirstOrDefault() is Immediate);
        HashSet<ulong> boxAddresses = [];
        if (hasImmediateTarget)
        {
            var addresses = method.AppContext.GetOrCreateKeyFunctionAddresses();
            boxAddresses =
            [
                addresses.il2cpp_value_box,
                addresses.il2cpp_vm_object_box,
                addresses.il2cpp_codegen_object_box,
            ];
            boxAddresses.Remove(0);
        }

        var definitions = BuildUniqueDefinitions(instructions);
        var roots = new HashSet<Instruction>();
        foreach (var block in blocks)
        {
            for (var instructionIndex = 0; instructionIndex < block.Instructions.Count; instructionIndex++)
            {
                var instruction = block.Instructions[instructionIndex];
                if (!IsObjectBoxCall(instruction, boxAddresses)
                    || instruction.Operands.Count < 4
                    || ResolveAddressedValue(instruction.Operands[3], definitions) is not { } addressed)
                    continue;

                var value = ResolveLatestStackValue(
                    addressed,
                    blocks,
                    block,
                    instructionIndex,
                    instruction.Index,
                    method.DominatorInfo);
                if (definitions.TryGetValue(value, out var definition))
                    roots.Add(definition);
            }
        }

        return roots;
    }

    private static IReadOnlyDictionary<LocalVariable, Instruction> BuildUniqueDefinitions(
        IEnumerable<Instruction> instructions)
        => instructions
            .Where(instruction => instruction.Destination is LocalVariable)
            .GroupBy(instruction => (LocalVariable)instruction.Destination!)
            // 多定义局部不满足SSA唯一生产者证明；保留原始调用，禁止任选一个版本。
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());

    private static bool IsObjectBoxCall(
        Instruction instruction,
        ISet<ulong> boxAddresses)
        => instruction.OpCode == OpCode.Call
            && instruction.Operands.FirstOrDefault() switch
            {
                StringLiteral { Value: var name } => ObjectBoxFunctions.Contains(name),
                Immediate address => boxAddresses.Contains(address.UnsignedValue),
                _ => false,
            };

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
        DominatorInfo? dominance,
        IReadOnlyDictionary<string, TypeAnalysisContext> boxTypesByHandle)
    {
        // 原生Object::Box接收类型句柄和数据地址；地址可能先经寄存器局部传递。
        if (instruction.OpCode != OpCode.Call
            || instruction.Operands is not [_, { } result, _, { } dataAddress, ..])
            return;

        var addressed = ResolveAddressedValue(dataAddress, definitions);
        var resolution = addressed != null
            ? BoxValueResolution.Direct(ResolveLatestStackValue(
                addressed,
                allBlocks,
                callBlock,
                instructionIndex,
                instruction.Index,
                dominance))
            : ResolveAddressPhiBoxValue(dataAddress, definitions, callBlock)
                ?? BoxValueResolution.Direct(
                    ResolveAdjacentArm64BoxStackWrite(dataAddress, callBlock, instructionIndex));
        var value = resolution?.Value;
        var directlyProvenTypes = resolution?.Sources
            .Select(source => source.Type)
            .Where(type => type is { IsValueType: true })
            .Cast<TypeAnalysisContext>()
            .ToList() ?? [];
        var boxedType = directlyProvenTypes.Count > 0
            && directlyProvenTypes.All(type => GenericCallRebinder.TypesEquivalent(type, directlyProvenTypes[0]))
                ? directlyProvenTypes[0]
                : TryCanonicalizeBoxTypeHandle(instruction.Operands[2], definitions, out var handleKey)
                    && boxTypesByHandle.TryGetValue(handleKey, out var provenType)
                        ? provenType
                        : null;
        if (value == null || boxedType == null || resolution == null)
            return;

        // 类型句柄只在同方法内、同一规范链且类型唯一时补全未知栈槽，随后由Box参与主类型不动点。
        foreach (var source in resolution.Sources)
            source.Type ??= boxedType;
        value.Type ??= boxedType;
        if (resolution.PendingPhi != null)
            callBlock.Instructions.Insert(0, resolution.PendingPhi);

        instruction.OpCode = OpCode.Box;
        instruction.SetOperands(result, value, boxedType);
    }

    private static BoxValueResolution? ResolveAddressPhiBoxValue(
        IOperand dataAddress,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions,
        Block callBlock)
    {
        if (dataAddress is not LocalVariable carrier
            || !definitions.TryGetValue(carrier, out var definition)
            || definition.OpCode != OpCode.Phi
            || definition.Operands.Count != callBlock.Predecessors.Count + 1)
            return null;

        var sources = new List<LocalVariable>(callBlock.Predecessors.Count);
        for (var predecessorIndex = 0; predecessorIndex < callBlock.Predecessors.Count; predecessorIndex++)
        {
            if (ResolveAddressedValue(definition.Operands[predecessorIndex + 1], definitions) is not { } addressed
                || ResolveIncomingStackValue(addressed, callBlock.Predecessors[predecessorIndex]) is not { } source)
                return null;

            sources.Add(source);
        }

        var merged = new LocalVariable(
            $"boxPhi_{definition.Index}",
            new Register(null, $"BOX_PHI_{definition.Index}"));
        var phiOperands = new List<IOperand>(sources.Count + 1) { merged };
        phiOperands.AddRange(sources);
        var phi = new Instruction(-1, OpCode.Phi, phiOperands);
        return new BoxValueResolution(merged, sources, phi);
    }

    private static LocalVariable ResolveIncomingStackValue(AddressedValue addressed, Block predecessor)
    {
        for (var index = predecessor.Instructions.Count - 1; index >= 0; index--)
        {
            if (predecessor.Instructions[index].Destination is LocalVariable candidate
                && candidate.Register.Number == addressed.Slot.Register.Number)
                return candidate;
        }

        return addressed.Slot;
    }

    private static LocalVariable? ResolveAdjacentArm64BoxStackWrite(
        IOperand dataAddress,
        Block callBlock,
        int instructionIndex)
    {
        // AArch64 Object::Box的第二个原生实参固定走X1。若取址Move已被早期清理，
        // 只接受同基本块内紧邻（中间仅Nop）的栈槽写入，绝不跨调用或控制转移猜测。
        if (dataAddress is not LocalVariable { Register.Name: "X1" })
            return null;

        for (var index = instructionIndex - 1; index >= 0; index--)
        {
            var candidate = callBlock.Instructions[index];
            if (candidate.OpCode == OpCode.Nop)
                continue;

            return candidate is
            {
                OpCode: OpCode.Move,
                Destination: LocalVariable { Register.Name: var registerName } destination,
            } && registerName.StartsWith("stack_", System.StringComparison.Ordinal)
                ? destination
                : null;
        }

        return null;
    }

    private static bool TryCanonicalizeBoxTypeHandle(
        IOperand operand,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions,
        out string key)
    {
        var visited = new HashSet<LocalVariable>();
        return TryCanonicalizeBoxTypeHandle(operand, definitions, visited, out key);
    }

    private static bool TryCanonicalizeBoxTypeHandle(
        IOperand operand,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions,
        ISet<LocalVariable> visited,
        out string key)
    {
        if (operand is LocalVariable local)
        {
            if (!visited.Add(local)
                || !definitions.TryGetValue(local, out var definition))
            {
                key = string.Empty;
                return false;
            }

            if (definition.OpCode == OpCode.Move && definition.Operands.Count >= 2)
                return TryCanonicalizeBoxTypeHandle(definition.Operands[1], definitions, visited, out key);

            if (definition.OpCode == OpCode.Phi && definition.Operands.Count >= 2)
            {
                string? commonKey = null;
                for (var index = 1; index < definition.Operands.Count; index++)
                {
                    if (!TryCanonicalizeBoxTypeHandle(
                            definition.Operands[index],
                            definitions,
                            new HashSet<LocalVariable>(visited),
                            out var sourceKey)
                        || commonKey != null && commonKey != sourceKey)
                    {
                        key = string.Empty;
                        return false;
                    }

                    commonKey ??= sourceKey;
                }

                key = commonKey ?? string.Empty;
                return commonKey != null;
            }

            key = string.Empty;
            return false;
        }

        if (operand is MemoryOperand memory)
        {
            var baseKey = "null";
            if (memory.Base != null
                && !TryCanonicalizeBoxTypeHandle(memory.Base, definitions, visited, out baseKey))
            {
                key = string.Empty;
                return false;
            }

            var indexKey = "null";
            if (memory.Index != null
                && !TryCanonicalizeBoxTypeHandle(memory.Index, definitions, visited, out indexKey))
            {
                key = string.Empty;
                return false;
            }

            key = $"memory({baseKey},{memory.Addend},{indexKey},{memory.Scale})";
            return true;
        }

        if (operand is Immediate immediate)
        {
            key = $"immediate({immediate.Value})";
            return true;
        }

        if (operand is TypeAnalysisContext type)
        {
            key = $"type({type.FullName})";
            return true;
        }

        key = string.Empty;
        return false;
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

    private sealed record BoxValueResolution(
        LocalVariable Value,
        IReadOnlyList<LocalVariable> Sources,
        Instruction? PendingPhi)
    {
        public static BoxValueResolution? Direct(LocalVariable? value)
            => value == null ? null : new BoxValueResolution(value, [value], null);
    }
}
