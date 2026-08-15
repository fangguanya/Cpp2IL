using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Extensions;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.Il2CppApiFunctions;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Logging;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using LibCpp2IL;

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

    private static readonly HashSet<string> ObjectUnboxFunctions =
    [
        "il2cpp_object_unbox",
        "il2cpp_vm_object_unbox",
        "il2cpp_codegen_object_unbox",
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
                if (instruction.Operands is not [StringLiteral { Value: var keyFunction }, ..])
                    continue;

                if (!ObjectBoxFunctions.Contains(keyFunction))
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
    /// 接口与普通调用的真实签名均已绑定后，使用托管消费者的值类型形参恢复 Object::Unbox。
    /// 该阶段与早期装箱恢复分离，避免在间接调用仍未定型时重复扫描或猜测值类型。
    /// </summary>
    public static void RewriteUnboxing(MethodAnalysisContext method)
    {
        var instructions = method.ControlFlowGraph!.Blocks
            .SelectMany(block => block.Instructions)
            .ToList();
        foreach (var instruction in instructions)
        {
            if (instruction.Operands is [StringLiteral { Value: var keyFunction }, ..]
                && ObjectUnboxFunctions.Contains(keyFunction))
                RewriteObjectUnbox(method, instruction, instructions);
        }
    }

    /// <summary>
    /// 把 Object::Unbox 返回的原生值地址与其零偏移读取合并为托管 unbox.any。
    /// 值类型必须由该读取进入的真实托管形参唯一证明；全部消费者都必须是同一地址的零偏移读取。
    /// </summary>
    private static void RewriteObjectUnbox(
        MethodAnalysisContext method,
        Instruction instruction,
        IReadOnlyList<Instruction> instructions)
    {
        if (instruction is not
            {
                OpCode: OpCode.Call,
                Operands: [StringLiteral, LocalVariable result, IOperand source, ..],
            })
            return;

        // ARM64 常先把返回地址从 X0 搬入保存寄存器，再于后续块解引用。先闭合只含
        // LocalVariable->LocalVariable 的 SSA Move 载体；其他算术、Phi 或带偏移地址仍拒绝。
        var carriers = new List<LocalVariable> { result };
        var carrierMoves = new HashSet<Instruction>();
        var carrierAdded = true;
        while (carrierAdded)
        {
            carrierAdded = false;
            foreach (var candidate in instructions)
            {
                if (candidate.Index <= instruction.Index
                    || candidate is not
                    {
                        OpCode: OpCode.Move,
                        Operands: [LocalVariable destination, LocalVariable sourceCarrier],
                    }
                    || !carriers.Any(carrier => SameSsaLocal(carrier, sourceCarrier)))
                    continue;

                carrierMoves.Add(candidate);
                if (carriers.Any(carrier => SameSsaLocal(carrier, destination)))
                    continue;

                carriers.Add(destination);
                carrierAdded = true;
            }
        }

        var consumers = new List<(Instruction Instruction, int OperandIndex, LocalVariable Value)>();
        TypeAnalysisContext? unboxedType = null;
        foreach (var candidate in instructions)
        {
            if (ReferenceEquals(candidate, instruction) || carrierMoves.Contains(candidate))
                continue;

            for (var operandIndex = 0; operandIndex < candidate.Operands.Count; operandIndex++)
            {
                var operand = candidate.Operands[operandIndex];
                if (operand is MemoryOperand
                    {
                        Base: LocalVariable memoryBase,
                        Index: null,
                        Scale: 0,
                        Addend: 0,
                    }
                    && carriers.FirstOrDefault(carrier => SameSsaLocal(carrier, memoryBase)) is { } valueCarrier)
                {
                    consumers.Add((candidate, operandIndex, valueCarrier));
                    var candidateType = ResolveManagedConsumerType(candidate, operandIndex);
                    Logger.VerboseNewline(
                        $"Object::Unbox消费者：method={method.Definition?.Name}，call={instruction.Index}，consumer={candidate.Index}，operand={operandIndex}，type={candidateType?.FullName ?? "<未解析>"}，valueType={candidateType?.IsValueType.ToString() ?? "<未解析>"}",
                        nameof(KeyFunctionRecovery));
                    if (candidateType == null)
                        continue;
                    if (!candidateType.IsValueType
                        || unboxedType != null
                        && !GenericCallRebinder.TypesEquivalent(unboxedType, candidateType))
                        return;
                    unboxedType ??= candidateType;
                    continue;
                }

                // 指针结果一旦被直接使用、带偏移读取或嵌入其他地址表达式，便不再满足 unbox.any 的值语义。
                if (carriers.Any(carrier => ReferencesLocal(operand, carrier)))
                {
                    Logger.VerboseNewline(
                        $"Object::Unbox拒绝：method={method.Definition?.Name}，call={instruction.Index}，consumer={candidate.Index}，operand={operandIndex}，instruction={candidate}，reason=直接或非零偏移地址使用",
                        nameof(KeyFunctionRecovery));
                    return;
                }
            }
        }

        if (consumers.Count == 0 || unboxedType is not { IsValueType: true })
        {
            Logger.VerboseNewline(
                $"Object::Unbox拒绝：method={method.Definition?.Name}，call={instruction.Index}，consumers={consumers.Count}，type={unboxedType?.FullName ?? "<未解析>"}，reason=缺少唯一值类型证据",
                nameof(KeyFunctionRecovery));
            return;
        }

        result.Type = unboxedType;
        foreach (var carrier in carriers)
            carrier.Type = unboxedType;
        instruction.OpCode = OpCode.Unbox;
        instruction.SetOperands(result, source, unboxedType);
        foreach (var (consumer, operandIndex, value) in consumers)
            consumer.SetOperand(operandIndex, value);
    }

    /// <summary>
    /// 从绑定后的托管调用参数或强类型 Move 目标读取零偏移值的期望类型。
    /// </summary>
    private static TypeAnalysisContext? ResolveManagedConsumerType(Instruction consumer, int operandIndex)
    {
        if (consumer.IsCall && consumer.Operands[0] is MethodAnalysisContext called)
        {
            var firstParameter = 1
                                 + (consumer.OpCode == OpCode.Call ? 1 : 0)
                                 + (called.IsStatic ? 0 : 1);
            var parameterIndex = operandIndex - firstParameter;
            if (parameterIndex >= 0 && parameterIndex < called.Parameters.Count)
                return called.Parameters[parameterIndex].ParameterType;
        }

        if (consumer is { OpCode: OpCode.Move, Operands: [LocalVariable { Type: { } destinationType }, _] }
            && operandIndex == 1)
            return destinationType;
        if (consumer is { OpCode: OpCode.Move, Operands: [FieldReference field, _] }
            && operandIndex == 1)
            return field.Field.FieldType;

        return null;
    }

    private static bool ReferencesLocal(IOperand operand, LocalVariable local)
        => operand is LocalVariable candidate && SameSsaLocal(candidate, local)
           || operand is MemoryOperand memory
           && (memory.Base != null && ReferencesLocal(memory.Base, local)
               || memory.Index != null && ReferencesLocal(memory.Index, local))
           || operand is AddressOf address && ReferencesLocal(address.Target, local)
           || operand is FieldReference field && SameSsaLocal(field.Local, local)
           || operand is ArrayAccess array
           && (SameSsaLocal(array.Array, local) || ReferencesLocal(array.Index, local));

    private static bool SameSsaLocal(LocalVariable left, LocalVariable right)
        => ReferenceEquals(left, right) || left.Register == right.Register;

    /// <summary>
    /// 把运行时 Object::IsInst 调用恢复为托管 isinst。原生帮助器返回原对象或 null，
    /// 与强制转换的异常语义不同，因此必须保留独立操作码。
    /// </summary>
    public static void RewriteTypeTests(
        MethodAnalysisContext method,
        IReadOnlyCollection<ulong>? initializedRuntimeMetadataSlots = null)
    {
        Func<ulong, TypeAnalysisContext?>? metadataSlotTypeResolver = null;
        if (initializedRuntimeMetadataSlots is { Count: > 0 })
        {
            var appContext = method.AppContext;
            var libContext = appContext.LibCpp2IlContext;
            TypeAnalysisContext? ResolveTypeUsage(MetadataUsage? usage)
            {
                return usage?.Type is MetadataUsageType.Type or MetadataUsageType.TypeInfo
                    ? appContext.ResolveIl2CppType(usage.AsType())
                    : null;
            }

            metadataSlotTypeResolver = address =>
            {
                var directDescription = "<unread>";
                var tableDescription = "<skipped>";
                var resolvedType = ResolveMetadataTypeSlot(
                    address,
                    candidate =>
                    {
                        var usage = libContext.GetAnyGlobalByAddress(candidate);
                        directDescription = DescribeMetadataUsage(usage);
                        return ResolveTypeUsage(usage);
                    },
                    (candidate, offset) =>
                    {
                        var usage = libContext.CheckForPost27GlobalTableEntryAt(candidate, offset);
                        tableDescription = DescribeMetadataUsage(usage);
                        return ResolveTypeUsage(usage);
                    });
                Logger.VerboseNewline(
                    $"类型测试元数据槽：method={method.Name}，slot=0x{address:X}，" +
                    $"direct={directDescription}，table={tableDescription}",
                    "KeyFunctionRecovery");
                return resolvedType;
            };
        }

        RewriteTypeTests(method, initializedRuntimeMetadataSlots, metadataSlotTypeResolver);
    }

    private static string DescribeMetadataUsage(MetadataUsage? usage)
        => usage == null ? "<null>" : $"{usage.Type}/0x{usage.RawValue:X}";

    /// <summary>
    /// 解析只接受类型结果的绝对元数据槽。直接地址若可解码成其他usage，不得遮蔽
    /// post-27二级表中的Type/TypeInfo项。
    /// </summary>
    internal static TypeAnalysisContext? ResolveMetadataTypeSlot(
        ulong address,
        Func<ulong, TypeAnalysisContext?> directTypeResolver,
        Func<ulong, long, TypeAnalysisContext?> tableTypeResolver)
        => MetadataResolver.ResolveAbsoluteSlotUsage(
            address,
            directTypeResolver,
            tableTypeResolver);

    /// <summary>
    /// 执行可注入元数据槽解析器的类型测试恢复。生产路径传入真实二进制解析器，测试路径
    /// 传入确定性解析函数；两条路径共享同一份指令筛选和改写逻辑，避免重复实现。
    /// </summary>
    internal static void RewriteTypeTests(
        MethodAnalysisContext method,
        IReadOnlyCollection<ulong>? initializedRuntimeMetadataSlots,
        Func<ulong, TypeAnalysisContext?>? metadataSlotTypeResolver)
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

            var testedType = ResolveRuntimeClassType(
                typeHandle,
                definitions,
                out var terminalTypeHandle);
            testedType ??= ResolveInitializedMetadataType(
                terminalTypeHandle,
                definitions,
                initializedRuntimeMetadataSlots,
                metadataSlotTypeResolver);
            testedType ??= ResolveTypeTestResultStorageType(
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
    /// 恢复post-27二层TypeInfo槽：类型测试实参必须是对唯一地址载体的零偏移解引用，
    /// 该载体必须经唯一Move/Phi链收敛到已初始化的绝对元数据槽。任一证据缺失时保持原生调用。
    /// </summary>
    private static TypeAnalysisContext? ResolveInitializedMetadataType(
        IOperand operand,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions,
        IReadOnlyCollection<ulong>? initializedRuntimeMetadataSlots,
        Func<ulong, TypeAnalysisContext?>? metadataSlotTypeResolver)
    {
        if (initializedRuntimeMetadataSlots is not { Count: > 0 }
            || metadataSlotTypeResolver == null)
            return null;

        if (operand is not MemoryOperand
            {
                Base: { } tableBase,
                Index: null,
                Scale: 0,
                Addend: 0,
            })
        {
            Logger.VerboseNewline(
                $"类型测试元数据槽形态未匹配：operand={operand}",
                "KeyFunctionRecovery");
            return null;
        }

        if (!TryResolveMetadataTableBaseAddress(
                tableBase,
                definitions,
                new HashSet<LocalVariable>(),
                out var address))
        {
            Logger.VerboseNewline(
                $"类型测试元数据槽基址未收敛到唯一绝对槽：base={tableBase}",
                "KeyFunctionRecovery");
            return null;
        }

        if (!initializedRuntimeMetadataSlots.Contains(address))
        {
            Logger.VerboseNewline(
                $"类型测试元数据槽未在初始化目录：slot=0x{address:X}，" +
                $"initialized={string.Join(',', initializedRuntimeMetadataSlots.Select(slot => $"0x{slot:X}"))}",
                "KeyFunctionRecovery");
            return null;
        }

        return metadataSlotTypeResolver(address);
    }

    /// <summary>
    /// 把类型表基址沿唯一Move/Phi定义链收敛到绝对槽。Phi的全部输入必须可解析且地址
    /// 完全相同；循环、缺失定义、非绝对内存或冲突地址均不构成元数据证据。
    /// </summary>
    private static bool TryResolveMetadataTableBaseAddress(
        IOperand operand,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions,
        ISet<LocalVariable> visited,
        out ulong address)
    {
        if (operand is MemoryOperand
            {
                Base: null,
                Index: null,
                Scale: 0,
                Addend: >= 0,
            } absolute)
        {
            address = (ulong)absolute.Addend;
            return true;
        }

        if (operand is not LocalVariable local
            || !visited.Add(local)
            || !definitions.TryGetValue(local, out var definition))
        {
            address = 0;
            return false;
        }

        if (definition is { OpCode: OpCode.Move, Operands.Count: 2 })
            return TryResolveMetadataTableBaseAddress(
                definition.Operands[1],
                definitions,
                visited,
                out address);

        if (definition.OpCode != OpCode.Phi || definition.Operands.Count < 2)
        {
            address = 0;
            return false;
        }

        ulong? commonAddress = null;
        for (var index = 1; index < definition.Operands.Count; index++)
        {
            if (!TryResolveMetadataTableBaseAddress(
                    definition.Operands[index],
                    definitions,
                    new HashSet<LocalVariable>(visited),
                    out var sourceAddress)
                || commonAddress.HasValue && commonAddress.Value != sourceAddress)
            {
                address = 0;
                return false;
            }

            commonAddress ??= sourceAddress;
        }

        address = commonAddress.GetValueOrDefault();
        return commonAddress.HasValue;
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
        IReadOnlyDictionary<LocalVariable, Instruction> definitions,
        out IOperand terminalOperand)
    {
        terminalOperand = operand;
        var visited = new HashSet<LocalVariable>();
        while (operand is LocalVariable local && visited.Add(local))
        {
            if (local.Type is RuntimeClassTypeAnalysisContext runtimeClass)
            {
                terminalOperand = local;
                return runtimeClass.RepresentedType;
            }
            if (!definitions.TryGetValue(local, out var definition)
                || definition is not { OpCode: OpCode.Move, Operands.Count: 2 })
            {
                terminalOperand = local;
                return null;
            }

            operand = definition.Operands[1];
            terminalOperand = operand;
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
