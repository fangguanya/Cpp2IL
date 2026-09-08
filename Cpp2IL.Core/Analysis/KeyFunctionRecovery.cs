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
                    method,
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
        var valueFlow = UnboxValueFlowIndex.Create(instructions);
        var definitions = BuildUniqueDefinitions(instructions);
        foreach (var instruction in instructions)
        {
            if (instruction.Operands is [StringLiteral { Value: var keyFunction }, ..]
                && ObjectUnboxFunctions.Contains(keyFunction))
                RewriteObjectUnbox(method, instruction, instructions, valueFlow, definitions);
        }
    }

    /// <summary>
    /// 把 Object::Unbox 返回的原生值地址与其零偏移读取合并为托管 unbox.any。
    /// 值类型必须由该读取进入的真实托管形参唯一证明；全部消费者都必须是同一地址的零偏移读取。
    /// </summary>
    private static void RewriteObjectUnbox(
        MethodAnalysisContext method,
        Instruction instruction,
        IReadOnlyList<Instruction> instructions,
        UnboxValueFlowIndex valueFlow,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions)
    {
        if (instruction is not
            {
                OpCode: OpCode.Call,
                Operands: [StringLiteral, LocalVariable result, ..],
            })
            return;

        // ARM64 调用分析会保留调用点仍活跃的物理寄存器，Object::Unbox 的真实对象
        // 因而不保证紧邻返回局部。只接受唯一的 System.Object 值或其 ref/out 栈槽地址。
        var source = ResolveUnboxSource(instruction.Operands.Skip(2));
        if (source == null)
        {
            Logger.VerboseNewline(
                $"Object::Unbox拒绝：method={method.Definition?.Name}，call={instruction.Index}，reason=对象源不唯一",
                nameof(KeyFunctionRecovery));
            return;
        }

        // 共享泛型方法的 T 在静态元数据中不带值类型约束，但原生 Object::Unbox 调用、
        // Il2CppClass<T> 与托管消费者 T 三者一致时，CIL 的 unbox.any !!T 对值/引用实例均精确。
        var runtimeTypes = new List<TypeAnalysisContext>();
        foreach (var operand in instruction.Operands.Skip(2))
        {
            var runtimeType = ResolveUnboxRuntimeClassType(operand, definitions);
            if (runtimeType != null
                && runtimeTypes.All(existing => !GenericCallRebinder.TypesEquivalent(existing, runtimeType)))
                runtimeTypes.Add(runtimeType);
        }
        if (runtimeTypes.Count > 1)
        {
            Logger.VerboseNewline(
                $"Object::Unbox拒绝：method={method.Definition?.Name}，call={instruction.Index}，" +
                $"runtimeTypes={string.Join(",", runtimeTypes.Select(type => type.FullName))}，reason=运行时类冲突",
                nameof(KeyFunctionRecovery));
            return;
        }
        var runtimeUnboxedType = runtimeTypes.SingleOrDefault();

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
                    var candidateType = ResolveManagedConsumerType(
                        candidate,
                        operandIndex,
                        valueFlow,
                        runtimeUnboxedType);
                    Logger.VerboseNewline(
                        $"Object::Unbox消费者：method={method.Definition?.Name}，call={instruction.Index}，consumer={candidate.Index}，operand={operandIndex}，type={candidateType?.FullName ?? "<未解析>"}，valueType={candidateType?.IsValueType.ToString() ?? "<未解析>"}",
                        nameof(KeyFunctionRecovery));
                    if (candidateType == null)
                        continue;
                    if (!IsRecoverableUnboxType(candidateType, runtimeUnboxedType)
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

        if (consumers.Count == 0
            || unboxedType == null
            || !IsRecoverableUnboxType(unboxedType, runtimeUnboxedType))
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
        {
            consumer.SetOperand(operandIndex, value);
            if (consumer is { OpCode: OpCode.Move, Operands: [LocalVariable destination, _] }
                && destination.Type == null)
            {
                destination.Type = unboxedType;
                PropagateRecoveredUnboxType(destination, unboxedType, valueFlow, []);
            }
        }

        if (unboxedType is GenericParameterTypeAnalysisContext)
            RemoveRedundantGenericUnboxGuard(method, instruction, unboxedType, definitions);
    }

    /// <summary>
    /// IL2CPP 在共享泛型拆箱前内联比较 object 类与 Il2CppClass&lt;T&gt;，失败时抛出
    /// InvalidCastException。恢复成 unbox.any !!T 后该 CIL 已完整承载同一检查与异常语义，
    /// 因此仅在“类身份比较 + 单一异常闭包 + 成功块以该 Unbox 开始”同时成立时折叠原生守卫。
    /// </summary>
    private static void RemoveRedundantGenericUnboxGuard(
        MethodAnalysisContext method,
        Instruction unbox,
        TypeAnalysisContext unboxedType,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions)
    {
        var cfg = method.ControlFlowGraph!;
        var methodLogName = method.Definition?.Name ?? "<注入方法>";
        var success = cfg.Blocks.SingleOrDefault(block => block.Instructions.Contains(unbox));
        if (success?.Predecessors is not [{ } guard])
        {
            Logger.VerboseNewline(
                $"泛型拆箱类型守卫跳过：method={methodLogName}，reason=成功块前驱不唯一",
                nameof(KeyFunctionRecovery));
            return;
        }
        if (guard.Successors.Count != 2 || !guard.Successors.Contains(success))
        {
            Logger.VerboseNewline(
                $"泛型拆箱类型守卫跳过：method={methodLogName}，guard=b{guard.ID}，reason=守卫后继不匹配",
                nameof(KeyFunctionRecovery));
            return;
        }
        if (guard.Instructions.LastOrDefault() is not
            {
                OpCode: OpCode.ConditionalJump,
                Operands: [Block _, LocalVariable condition]
            })
        {
            Logger.VerboseNewline(
                $"泛型拆箱类型守卫跳过：method={methodLogName}，guard=b{guard.ID}，reason=条件跳转不匹配",
                nameof(KeyFunctionRecovery));
            return;
        }
        if (!definitions.TryGetValue(condition, out var conditionDefinition))
        {
            Logger.VerboseNewline(
                $"泛型拆箱类型守卫跳过：method={methodLogName}，guard=b{guard.ID}，reason=条件定义不唯一",
                nameof(KeyFunctionRecovery));
            return;
        }

        var equality = conditionDefinition;
        if (conditionDefinition is { OpCode: OpCode.Not, Operands: [_, LocalVariable inner] }
            && definitions.TryGetValue(inner, out var innerDefinition))
            equality = innerDefinition;
        if (equality is not { OpCode: OpCode.CheckEqual, Operands.Count: 3 })
        {
            Logger.VerboseNewline(
                $"泛型拆箱类型守卫跳过：method={methodLogName}，guard=b{guard.ID}，reason=相等比较不匹配",
                nameof(KeyFunctionRecovery));
            return;
        }
        var resolvedLeft = ResolveUniqueMoveSource(equality.Operands[1], definitions);
        var resolvedRight = ResolveUniqueMoveSource(equality.Operands[2], definitions);
        if (resolvedLeft is not MemoryOperand left
            || resolvedRight is not MemoryOperand right
            || !TryGetComparedRuntimeClass(left, out var leftType)
            || !TryGetComparedRuntimeClass(right, out var rightType)
            || !IsObjectAndGenericPair(leftType, rightType, unboxedType))
        {
            Logger.VerboseNewline(
                $"泛型拆箱类型守卫跳过：method={methodLogName}，guard=b{guard.ID}，reason=类身份比较不匹配",
                nameof(KeyFunctionRecovery));
            return;
        }

        var failure = guard.Successors.Single(block => !ReferenceEquals(block, success));
        if (!IsInvalidCastFailureRegion(failure))
        {
            Logger.VerboseNewline(
                $"泛型拆箱类型守卫跳过：method={methodLogName}，guard=b{guard.ID}，reason=异常闭包不匹配",
                nameof(KeyFunctionRecovery));
            return;
        }

        guard.Successors.Remove(failure);
        failure.Predecessors.Remove(guard);
        var terminator = guard.Instructions[^1];
        terminator.OpCode = OpCode.Jump;
        terminator.SetOperands(success);
        guard.CalculateBlockType();
        cfg.RemoveUnreachableBlocks();
        DeadCodeEliminator.Run(method);
        Logger.VerboseNewline(
            $"泛型拆箱类型守卫删除：method={methodLogName}，guard=b{guard.ID}，type={unboxedType.FullName}",
            nameof(KeyFunctionRecovery));
    }

    private static IOperand ResolveUniqueMoveSource(
        IOperand operand,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions)
    {
        var visited = new HashSet<LocalVariable>();
        while (operand is LocalVariable local
               && visited.Add(local)
               && definitions.TryGetValue(local, out var definition)
               && definition is { OpCode: OpCode.Move, Operands: [_, { } source] })
            operand = source;
        return operand;
    }

    private static bool TryGetComparedRuntimeClass(
        MemoryOperand operand,
        out TypeAnalysisContext representedType)
    {
        representedType = null!;
        if (operand is not
            {
                Base: LocalVariable
                {
                    Type: RuntimeClassTypeAnalysisContext { RepresentedType: var represented }
                },
                Index: null,
                Scale: 0,
                Addend: 0x40
            })
            return false;
        representedType = represented;
        return true;
    }

    private static bool IsObjectAndGenericPair(
        TypeAnalysisContext left,
        TypeAnalysisContext right,
        TypeAnalysisContext genericType)
        => left.FullName == "System.Object"
           && GenericCallRebinder.TypesEquivalent(right, genericType)
           || right.FullName == "System.Object"
           && GenericCallRebinder.TypesEquivalent(left, genericType);

    private static bool IsInvalidCastFailureRegion(Block entry)
    {
        var visited = new HashSet<Block>();
        var current = entry;
        while (visited.Add(current))
        {
            var throws = current.Instructions.Where(instruction => instruction.OpCode == OpCode.Throw).ToList();
            if (throws.Count == 1
                && throws[0].Operands is [TypeAnalysisContext { FullName: "System.InvalidCastException" }]
                && current.Instructions.All(instruction =>
                    instruction.OpCode is OpCode.Nop or OpCode.Phi or OpCode.Throw or OpCode.Return))
                return true;
            if (current.Successors.Count != 1
                || current.Instructions.Any(instruction =>
                    instruction.OpCode is not (OpCode.Nop or OpCode.Phi or OpCode.Jump)))
                return false;
            current = current.Successors.Single();
        }

        return false;
    }

    private static IOperand? ResolveUnboxSource(IEnumerable<IOperand> operands)
    {
        var candidates = new List<IOperand>();
        foreach (var operand in operands)
        {
            var candidate = operand switch
            {
                LocalVariable { Type: { } type } local
                    when type.FullName == "System.Object" => local,
                AddressOf { Target: LocalVariable { Type: { } type } slot }
                    when type.FullName == "System.Object" => slot,
                _ => null,
            };
            if (candidate != null && !candidates.Contains(candidate))
                candidates.Add(candidate);
        }

        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static TypeAnalysisContext? ResolveUnboxRuntimeClassType(
        IOperand operand,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions)
    {
        var visited = new HashSet<LocalVariable>();
        while (operand is LocalVariable local && visited.Add(local))
        {
            if (local.Type is RuntimeClassTypeAnalysisContext runtimeClass)
                return runtimeClass.RepresentedType;
            if (!definitions.TryGetValue(local, out var definition)
                || definition is not { OpCode: OpCode.Move, Operands: [_, { } source] })
                return null;
            operand = source;
        }

        return operand is RuntimeClassTypeAnalysisContext direct ? direct.RepresentedType : null;
    }

    private static bool IsRecoverableUnboxType(
        TypeAnalysisContext candidate,
        TypeAnalysisContext? runtimeType)
    {
        if (runtimeType != null && !GenericCallRebinder.TypesEquivalent(candidate, runtimeType))
            return false;
        return candidate.IsValueType
               || candidate is GenericParameterTypeAnalysisContext && runtimeType != null;
    }

    /// <summary>
    /// 从绑定后的托管调用参数或强类型 Move 目标读取零偏移值的期望类型。
    /// </summary>
    private static TypeAnalysisContext? ResolveManagedConsumerType(
        Instruction consumer,
        int operandIndex,
        UnboxValueFlowIndex valueFlow,
        TypeAnalysisContext? runtimeUnboxedType)
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

        if (consumer is { OpCode: OpCode.Move, Operands: [LocalVariable destination, _] }
            && operandIndex == 1)
            return destination.Type
                   ?? ResolveUniqueValueConsumerType(destination, valueFlow, [], runtimeUnboxedType);
        if (consumer is { OpCode: OpCode.Move, Operands: [FieldReference field, _] }
            && operandIndex == 1)
            return field.Field.FieldType;

        return null;
    }

    /// <summary>
    /// ARM64 会先把拆箱地址读取到未定型局部变量，再进行整数运算。仅当该 SSA 值的全部
    /// 已定型算术消费者给出同一个值类型时，才把该类型作为拆箱证据；冲突或引用类型保持未知。
    /// </summary>
    private static TypeAnalysisContext? ResolveUniqueValueConsumerType(
        LocalVariable value,
        UnboxValueFlowIndex valueFlow,
        List<LocalVariable> path,
        TypeAnalysisContext? runtimeUnboxedType)
    {
        if (path.Any(visited => SameSsaLocal(visited, value)))
            return null;
        path.Add(value);

        TypeAnalysisContext? resolved = null;
        foreach (var candidate in valueFlow.GetConsumers(value))
        {
            TypeAnalysisContext? candidateType = null;
            if (candidate is { OpCode: OpCode.Move, Operands: [LocalVariable moveDestination, LocalVariable moveSource] }
                && SameSsaLocal(moveSource, value))
                candidateType = moveDestination.Type
                                ?? ResolveUniqueValueConsumerType(
                                    moveDestination,
                                    valueFlow,
                                    path,
                                    runtimeUnboxedType);
            else if (candidate is { OpCode: OpCode.Move, Operands: [FieldReference field, LocalVariable fieldSource] }
                     && SameSsaLocal(fieldSource, value))
                candidateType = field.Field.FieldType;
            else if (candidate.OpCode is
                         OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide
                         or OpCode.And or OpCode.Or or OpCode.Xor
                     && candidate.Operands is [LocalVariable arithmeticDestination, { } leftOperand, { } rightOperand]
                     && (ReferencesLocal(leftOperand, value) || ReferencesLocal(rightOperand, value)))
                candidateType = arithmeticDestination.Type
                                ?? ResolveUniqueValueConsumerType(
                                    arithmeticDestination,
                                    valueFlow,
                                    path,
                                    runtimeUnboxedType);
            else if (candidate is { OpCode: OpCode.Phi, Operands: [LocalVariable phiDestination, ..] }
                     && candidate.Operands.Skip(1).Any(input => ReferencesLocal(input, value)))
            {
                candidateType = phiDestination.Type
                                ?? ResolveUniqueValueConsumerType(
                                    phiDestination,
                                    valueFlow,
                                    path,
                                    runtimeUnboxedType);
                if (candidateType != null
                    && candidate.Operands.Skip(1).Any(input =>
                        !ReferencesLocal(input, value)
                        && !IsCompatiblePhiInput(input, candidateType, valueFlow, [])))
                    candidateType = null;
            }
            else if (candidate.IsCall && candidate.Operands[0] is MethodAnalysisContext called)
            {
                var firstParameter = 1
                                     + (candidate.OpCode == OpCode.Call ? 1 : 0)
                                     + (called.IsStatic ? 0 : 1);
                for (var operandIndex = firstParameter; operandIndex < candidate.Operands.Count; operandIndex++)
                {
                    if (!ReferencesLocal(candidate.Operands[operandIndex], value))
                        continue;

                    var parameterIndex = operandIndex - firstParameter;
                    if (parameterIndex >= 0 && parameterIndex < called.Parameters.Count)
                        candidateType = called.Parameters[parameterIndex].ParameterType;
                    break;
                }
            }

            if (candidateType == null)
                continue;

            if (!IsRecoverableUnboxType(candidateType, runtimeUnboxedType))
            {
                path.RemoveAt(path.Count - 1);
                return null;
            }
            if (resolved != null && !GenericCallRebinder.TypesEquivalent(resolved, candidateType))
            {
                path.RemoveAt(path.Count - 1);
                return null;
            }

            resolved = candidateType;
        }

        path.RemoveAt(path.Count - 1);
        return resolved;
    }

    /// <summary>
    /// 已证明拆箱类型后，只沿直接 Move 与“同型值或零默认值”Phi 写回类型。
    /// 算术结果仍由其自身消费者定型，避免把操作数类型盲目扩散到结果。
    /// </summary>
    private static void PropagateRecoveredUnboxType(
        LocalVariable value,
        TypeAnalysisContext recoveredType,
        UnboxValueFlowIndex valueFlow,
        List<LocalVariable> visited)
    {
        if (visited.Any(existing => SameSsaLocal(existing, value)))
            return;
        visited.Add(value);

        foreach (var candidate in valueFlow.GetConsumers(value))
        {
            LocalVariable? destination = null;
            if (candidate is { OpCode: OpCode.Move, Operands: [LocalVariable moveDestination, LocalVariable source] }
                && SameSsaLocal(source, value))
                destination = moveDestination;
            else if (candidate is { OpCode: OpCode.Phi, Operands: [LocalVariable phiDestination, ..] }
                     && candidate.Operands.Skip(1).Any(input => ReferencesLocal(input, value))
                     && candidate.Operands.Skip(1).All(input =>
                         ReferencesLocal(input, value)
                         || IsCompatiblePhiInput(input, recoveredType, valueFlow, [])))
                destination = phiDestination;

            if (destination == null
                || destination.Type != null
                && !GenericCallRebinder.TypesEquivalent(destination.Type, recoveredType))
                continue;

            destination.Type ??= recoveredType;
            PropagateRecoveredUnboxType(destination, recoveredType, valueFlow, visited);
        }

        visited.RemoveAt(visited.Count - 1);
    }

    /// <summary>
    /// Phi 的其他输入必须是同一值类型，或由唯一 Move 链证明的零默认值。
    /// </summary>
    private static bool IsCompatiblePhiInput(
        IOperand input,
        TypeAnalysisContext expectedType,
        UnboxValueFlowIndex valueFlow,
        List<LocalVariable> visited)
    {
        if (input is Immediate { Value: 0 })
            return true;
        if (input is not LocalVariable local)
            return false;
        if (local.Type != null)
            return GenericCallRebinder.TypesEquivalent(local.Type, expectedType);
        if (visited.Any(existing => SameSsaLocal(existing, local)))
            return false;
        visited.Add(local);

        var compatible = valueFlow.GetUniqueDefinition(local) is
                             { OpCode: OpCode.Move, Operands: [_, var source] }
                         && IsCompatiblePhiInput(source, expectedType, valueFlow, visited);
        visited.RemoveAt(visited.Count - 1);
        return compatible;
    }

    /// <summary>
    /// Object::Unbox 的 SSA 推导只建立一次定义和消费者索引，全部递归查询复用该不可变目录。
    /// </summary>
    private sealed class UnboxValueFlowIndex
    {
        private readonly IReadOnlyDictionary<Register, IReadOnlyList<Instruction>> _consumers;
        private readonly IReadOnlyDictionary<Register, Instruction?> _definitions;

        private UnboxValueFlowIndex(
            IReadOnlyDictionary<Register, IReadOnlyList<Instruction>> consumers,
            IReadOnlyDictionary<Register, Instruction?> definitions)
        {
            _consumers = consumers;
            _definitions = definitions;
        }

        public IReadOnlyList<Instruction> GetConsumers(LocalVariable value)
            => _consumers.TryGetValue(value.Register, out var consumers) ? consumers : [];

        public Instruction? GetUniqueDefinition(LocalVariable value)
            => _definitions.TryGetValue(value.Register, out var definition) ? definition : null;

        public static UnboxValueFlowIndex Create(IReadOnlyList<Instruction> instructions)
        {
            var consumers = new Dictionary<Register, List<Instruction>>();
            var definitions = new Dictionary<Register, Instruction?>();
            foreach (var instruction in instructions)
            {
                var seenRegisters = new HashSet<Register>();
                foreach (var local in instruction.Operands.OfType<LocalVariable>())
                {
                    if (!seenRegisters.Add(local.Register))
                        continue;
                    if (!consumers.TryGetValue(local.Register, out var localConsumers))
                    {
                        localConsumers = [];
                        consumers.Add(local.Register, localConsumers);
                    }
                    localConsumers.Add(instruction);
                }

                if (instruction.Operands.FirstOrDefault() is not LocalVariable destination)
                    continue;
                if (!definitions.TryAdd(destination.Register, instruction))
                    definitions[destination.Register] = null;
            }

            return new UnboxValueFlowIndex(
                consumers.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<Instruction>)pair.Value),
                definitions);
        }
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
                $"initialized={string.Join(",", initializedRuntimeMetadataSlots.Select(slot => $"0x{slot:X}"))}",
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
        MethodAnalysisContext method,
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
                        : ResolveMetadataBoxTypeHandle(method, instruction.Operands[2], definitions);
        if (value == null || boxedType == null || resolution == null)
        {
            var addressedText = addressed?.Slot.ToString() ?? "<未解析>";
            var valueText = value?.ToString() ?? "<未解析>";
            var valueTypeText = value?.Type?.FullName ?? "<未解析>";
            var sourceTypesText = string.Join(",", directlyProvenTypes.Select(type => type.FullName));
            Instruction? valueDefinition = null;
            var definitionText = value != null && definitions.TryGetValue(value, out valueDefinition)
                ? valueDefinition.ToString()
                : "<未解析>";
            var sourceDefinitionText = valueDefinition?.Operands.ElementAtOrDefault(1) is LocalVariable valueSource
                                       && definitions.TryGetValue(valueSource, out var sourceDefinition)
                ? sourceDefinition.ToString()
                : "<未解析>";
            Logger.VerboseNewline(
                $"Object::Box拒绝：call={instruction.Index}，addressed={addressedText}，" +
                $"value={valueText}，valueType={valueTypeText}，" +
                $"definition={definitionText}，sourceDefinition={sourceDefinitionText}，" +
                $"sourceTypes={sourceTypesText}，reason=缺少唯一装箱值或值类型",
                nameof(KeyFunctionRecovery));
            return;
        }

        // 类型句柄只在同方法内、同一规范链且类型唯一时补全未知栈槽，随后由Box参与主类型不动点。
        foreach (var source in resolution.Sources)
            source.Type ??= boxedType;
        value.Type ??= boxedType;
        if (resolution.PendingPhi != null)
            callBlock.Instructions.Insert(0, resolution.PendingPhi);

        instruction.OpCode = OpCode.Box;
        instruction.SetOperands(result, value, boxedType);
    }

    /// <summary>
    /// 从 post-27 元数据表的绝对槽与条目偏移恢复 Object::Box 类型句柄。该查询只接受
    /// 元数据登记为 Type/TypeInfo 的项；普通内存、字符串、方法句柄和未登记槽均保持未知。
    /// </summary>
    private static TypeAnalysisContext? ResolveMetadataBoxTypeHandle(
        MethodAnalysisContext method,
        IOperand typeHandle,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions)
    {
        typeHandle = ResolveUniqueMoveSource(typeHandle, definitions);
        if (typeHandle is not MemoryOperand
            {
                Base: LocalVariable tableBase,
                Index: null,
                Addend: >= 0,
            } entry)
        {
            Logger.VerboseNewline(
                $"Object::Box类型句柄形态未匹配：kind={typeHandle.GetType().FullName}，value={typeHandle}",
                nameof(KeyFunctionRecovery));
            return null;
        }

        var tableAddress = MetadataResolver.ResolveAbsoluteSlotAddress(tableBase, definitions, []);
        if (tableAddress == null)
        {
            var definitionText = definitions.TryGetValue(tableBase, out var tableDefinition)
                ? tableDefinition.ToString()
                : "<未解析>";
            Logger.VerboseNewline(
                $"Object::Box类型表根未解析：base={tableBase}，definition={definitionText}，offset=0x{entry.Addend:X}",
                nameof(KeyFunctionRecovery));
            return null;
        }

        var appContext = method.AppContext;
        if (appContext == null)
        {
            Logger.VerboseNewline("Object::Box类型句柄缺少应用上下文", nameof(KeyFunctionRecovery));
            return null;
        }

        var libContext = appContext.LibCpp2IlContext;
        if (libContext == null)
        {
            Logger.VerboseNewline("Object::Box类型句柄缺少LibCpp2IL上下文", nameof(KeyFunctionRecovery));
            return null;
        }

        var usage = libContext.CheckForPost27GlobalTableEntryAt(
            tableAddress.Value,
            entry.Addend);
        if (usage?.Type is MetadataUsageType.Type or MetadataUsageType.TypeInfo)
        {
            var resolvedType = appContext.ResolveIl2CppType(usage.AsType());
            return resolvedType.IsValueType ? resolvedType : null;
        }

        var defaultsTypeName = Il2CppDefaultsUsefulOffsets.GetBoxedSystemTypeName(
            entry.Addend,
            appContext.Binary.PointerSizeBytes);
        if (defaultsTypeName == "System.Int32")
            return appContext.SystemTypes.SystemInt32Type;

        Logger.VerboseNewline(
            $"Object::Box类型句柄未命中元数据或默认类型表：callTable=0x{tableAddress.Value:X}，offset=0x{entry.Addend:X}",
            nameof(KeyFunctionRecovery));
        return null;
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

    /// <summary>
    /// 依据 IL2CPP 内部调用名称解析唯一的托管方法；重载只在参数数量唯一时接受。
    /// </summary>
    internal static MethodAnalysisContext? ResolveInternalCallName(
        ApplicationAnalysisContext appContext,
        string name)
    {
        var separator = name.IndexOf("::", StringComparison.Ordinal);
        if (separator < 0)
            return null;

        var typeName = name[..separator];
        var signature = name[(separator + 2)..];
        var parenthesis = signature.IndexOf('(');
        var methodName = parenthesis < 0 ? signature : signature[..parenthesis];

        if (appContext.LibCpp2IlContext.ReflectionCache.GetTypeByFullName(typeName) is not { } typeDefinition
            || appContext.ResolveContextForType(typeDefinition) is not { } type)
            return null;

        var candidates = type.Methods.Where(method => method.Name == methodName).ToList();
        if (candidates.Count <= 1)
            return candidates.FirstOrDefault();

        var parameters = parenthesis < 0 ? string.Empty : signature[(parenthesis + 1)..].TrimEnd(')');
        var parameterCount = parameters.Length == 0 ? 0 : parameters.Split(',').Length;
        var matchingCandidates = candidates
            .Where(method => method.Parameters.Count == parameterCount)
            .ToList();

        return matchingCandidates.Count == 1 ? matchingCandidates[0] : null;
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
