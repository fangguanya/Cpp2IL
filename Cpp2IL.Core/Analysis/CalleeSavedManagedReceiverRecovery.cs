using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Extensions;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Logging;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 恢复 ARM64 被调用方保存寄存器中跨调用存活的托管实例接收者。
/// </summary>
/// <remarks>
/// 异常清理边会把 X19-X29 的入口旧值并入 Phi；若旧值没有托管定义，退 SSA 会丢弃真实
/// 赋值边，留下一个未定义接收者。本恢复器仅在接收者无定义、位于被调用方保存寄存器、
/// 此前恰有一个相容的托管调用/分配结果时重绑，并同步具体化共享泛型调用目标。
/// </remarks>
public static class CalleeSavedManagedReceiverRecovery
{
    /// <summary>
    /// 退 SSA 后恢复被调用方保存寄存器和引用 Phi 中的托管复制载体类型。
    /// </summary>
    /// <remarks>
    /// 只有该局部的全部定义均为同一托管引用类型的直接复制、字段读取或空值时才提交；
    /// 非保存寄存器还必须具有至少两个定义和一条退 SSA Phi 复制。真实指针算术、异型引用和
    /// 运行时元数据载体均保持原类型。该规则用于数组循环以及业务数据项的合流字段读取。
    /// </remarks>
    public static int ResolveManagedCopyCarrierTypes(MethodAnalysisContext method)
        => ResolveManagedCopyCarrierTypes(
            method.ControlFlowGraph!.Instructions,
            method.ParameterLocals);

    /// <summary>
    /// 以单调收敛循环共同恢复托管复制载体与字段偏移。
    /// </summary>
    /// <remarks>
    /// 字段读取会为 Phi 来源提供具体类型，Phi 定型后又会使以其为基址的后续字段可解析；
    /// 两个变换都只会将未解析状态推进为已解析状态，因此在无新变化时精确结束。
    /// </remarks>
    public static int ResolveManagedCopyCarrierTypesAndFields(MethodAnalysisContext method)
    {
        var recovered = 0;
        while (true)
        {
            var recoveredThisRound = ResolveManagedCopyCarrierTypes(method);
            recovered += recoveredThisRound;
            var resolvedField = MetadataResolver.ResolveFieldOffsets(method);
            if (recoveredThisRound == 0 && !resolvedField)
                return recovered;
        }
    }

    internal static int ResolveManagedCopyCarrierTypes(
        IReadOnlyList<Instruction> instructions,
        IReadOnlyCollection<LocalVariable>? parameterLocals = null)
    {
        var definitions = instructions
            .Where(instruction => instruction.Destination is LocalVariable)
            .GroupBy(instruction => (LocalVariable)instruction.Destination!)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var recovered = 0;

        foreach (var pair in definitions)
        {
            var destination = pair.Key;
            var isCalleeSavedCarrier = IsCalleeSavedArm64Register(destination.Register.Name);
            var isReferencePhiCarrier = pair.Value.Length >= 2
                                        && pair.Value.Any(definition => definition is
                                        {
                                            Index: < 0,
                                            OpCode: OpCode.Move,
                                        });
            if (isCalleeSavedCarrier || isReferencePhiCarrier)
            {
                Logger.VerboseNewline(
                    $"托管复制载体定型候选：destination={destination.Register}，" +
                    $"type={destination.Type?.FullName ?? "<未定型>"}，calleeSaved={isCalleeSavedCarrier}，" +
                    $"referencePhi={isReferencePhiCarrier}，definitions=" +
                    string.Join(" | ", pair.Value.Select(DescribeManagedCopyDefinition)),
                    nameof(CalleeSavedManagedReceiverRecovery));
            }

            if ((!isCalleeSavedCarrier && !isReferencePhiCarrier)
                || parameterLocals?.Contains(destination) == true
                || !IsReplaceableManagedCopyCarrier(destination.Type))
                continue;

            var sourceTypes = new List<TypeAnalysisContext>();
            var valid = true;
            foreach (var definition in pair.Value)
            {
                if (definition is
                    {
                        OpCode: OpCode.Move,
                        Operands: [LocalVariable, Immediate { Value: 0 }],
                    })
                    continue;

                if (definition is not
                    {
                        OpCode: OpCode.Move,
                        Operands: [LocalVariable, var source],
                    }
                    || !TryGetConcreteManagedReferenceSourceType(source, out var sourceType))
                {
                    valid = false;
                    break;
                }

                sourceTypes.Add(sourceType);
            }

            if (!valid || sourceTypes.Count == 0)
            {
                Logger.VerboseNewline(
                    $"托管复制载体定型拒绝：destination={destination.Register}，" +
                    $"valid={valid}，sourceTypes={string.Join(",", sourceTypes.Select(type => type.FullName))}",
                    nameof(CalleeSavedManagedReceiverRecovery));
                continue;
            }

            var consensusType = sourceTypes[0];
            if (sourceTypes.Skip(1).Any(sourceType =>
                    !GenericCallRebinder.TypesEquivalent(sourceType, consensusType)))
            {
                Logger.VerboseNewline(
                    $"托管复制载体定型拒绝：destination={destination.Register}，" +
                    $"异型来源={string.Join(",", sourceTypes.Select(type => type.FullName))}",
                    nameof(CalleeSavedManagedReceiverRecovery));
                continue;
            }

            destination.Type = consensusType;
            Logger.VerboseNewline(
                $"托管复制载体定型提交：destination={destination.Register}，type={consensusType.FullName}",
                nameof(CalleeSavedManagedReceiverRecovery));
            recovered++;
        }

        return recovered;
    }

    private static bool IsReplaceableManagedCopyCarrier(TypeAnalysisContext? type)
        => type == null || type.FullName is "System.Object" or "System.IntPtr" or "System.UIntPtr";

    private static bool IsConcreteManagedReference(TypeAnalysisContext type)
        => !type.IsValueType
           && type is not (RuntimeClassTypeAnalysisContext
               or StaticFieldStorageTypeAnalysisContext
               or RuntimeMethodInfoAnalysisContext)
           && type.FullName != "System.Object";

    /// <summary>
    /// 从直接局部复制或已解析字段读取中提取具体托管引用类型。
    /// </summary>
    private static bool TryGetConcreteManagedReferenceSourceType(
        IOperand source,
        out TypeAnalysisContext sourceType)
    {
        sourceType = source switch
        {
            LocalVariable { IsMethodInfo: false, Type: { } localType } => localType,
            FieldReference { Field.FieldType: { } fieldType } => fieldType,
            _ => null!,
        };
        return sourceType != null && IsConcreteManagedReference(sourceType);
    }

    private static string DescribeManagedCopyDefinition(Instruction definition)
    {
        var source = definition.Operands.Count > 1 ? definition.Operands[1] : null;
        var sourceType = source switch
        {
            LocalVariable local => local.Type?.FullName ?? "<未定型局部>",
            FieldReference field => field.Field.FieldType.FullName,
            Immediate { Value: 0 } => "null",
            null => "<无来源>",
            _ => source.GetType().Name,
        };
        return $"{definition.Index}:{definition.OpCode}:{sourceType}";
    }

    /// <summary>
    /// 在 SSA 局部刚建立时冻结 X19-X29 的直接局部复制身份。
    /// </summary>
    internal static void CaptureSsaCopyEvidence(MethodAnalysisContext method)
    {
        method.CalleeSavedSsaCopyEvidence.Clear();
        var phiInstructions = new List<Instruction>();
        var copyInstructions = new List<(LocalVariable Destination, LocalVariable Source, int Index)>();
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction is
                {
                    OpCode: OpCode.Phi,
                    Operands: [LocalVariable phiDestination, ..],
                }
                && IsCalleeSavedArm64Register(phiDestination.Register.Name))
            {
                phiInstructions.Add(instruction);
                Logger.VerboseNewline(
                    $"保存寄存器Phi冻结候选：method={method.FullName}，index={instruction.Index}，" +
                    $"destination={phiDestination.Register}，sources=" +
                    string.Join(",", instruction.Operands.Skip(1).OfType<LocalVariable>().Select(source => source.Register)),
                    nameof(CalleeSavedManagedReceiverRecovery));
            }

            if (instruction is not
                {
                    OpCode: OpCode.Move,
                    Operands: [LocalVariable destination, LocalVariable source],
                }
                || (!IsCalleeSavedArm64Register(destination.Register.Name)
                    && !IsCalleeSavedArm64Register(source.Register.Name)))
                continue;

            // 中文注释：只冻结直接局部复制；内存、字段、常量和算术来源均缺少可证明的
            // 托管对象身份，留给既有字段与类型恢复链处理。
            copyInstructions.Add((destination, source, instruction.Index));
            method.CalleeSavedSsaCopyEvidence.Add((destination, source, instruction.Index));
            Logger.VerboseNewline(
                $"保存寄存器复制冻结：method={method.FullName}，index={instruction.Index}，" +
                $"destination={destination.Register}，source={source.Register}",
                nameof(CalleeSavedManagedReceiverRecovery));
        }

        // 中文注释：异常处理和循环会形成包含入口旧值的长 Phi 链。这里只沿同一物理
        // 保存寄存器传播已经冻结的直接复制来源；不会把 Phi 的未知输入伪造成定义。
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var copy in copyInstructions)
            {
                var inheritedEvidence = method.CalleeSavedSsaCopyEvidence
                    .Where(evidence => SameSsaLocal(evidence.Destination, copy.Source))
                    .ToArray();
                foreach (var evidence in inheritedEvidence)
                {
                    var propagatedIndex = Math.Max(copy.Index, evidence.Index);
                    if (method.CalleeSavedSsaCopyEvidence.Any(existing =>
                            SameSsaLocal(existing.Destination, copy.Destination)
                            && ReferenceEquals(existing.Source, evidence.Source)
                            && existing.Index == propagatedIndex))
                        continue;

                    method.CalleeSavedSsaCopyEvidence.Add((copy.Destination, evidence.Source, propagatedIndex));
                    changed = true;
                }
            }

            foreach (var phi in phiInstructions)
            {
                var destination = (LocalVariable)phi.Operands[0];
                foreach (var source in phi.Operands.Skip(1).OfType<LocalVariable>()
                             .Where(source => source.Register.Name == destination.Register.Name))
                {
                    var inheritedEvidence = method.CalleeSavedSsaCopyEvidence
                        .Where(evidence => SameSsaLocal(evidence.Destination, source))
                        .ToArray();
                    foreach (var evidence in inheritedEvidence)
                    {
                        if (method.CalleeSavedSsaCopyEvidence.Any(existing =>
                                SameSsaLocal(existing.Destination, destination)
                                && ReferenceEquals(existing.Source, evidence.Source)
                                && existing.Index == evidence.Index))
                            continue;

                        method.CalleeSavedSsaCopyEvidence.Add((destination, evidence.Source, evidence.Index));
                        changed = true;
                    }
                }
            }
        }
    }

    public static int Run(MethodAnalysisContext method)
    {
        var instructions = method.ControlFlowGraph!.Instructions;
        var definitions = instructions
            .Where(instruction => instruction.Destination is LocalVariable)
            .GroupBy(instruction => (LocalVariable)instruction.Destination!)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var producers = instructions
            .Where(instruction => instruction.OpCode is OpCode.Call or OpCode.Newobj
                                  && instruction.Destination is LocalVariable { Type: { IsValueType: false } })
            .ToArray();
        var recovered = 0;

        foreach (var call in instructions.Where(instruction => instruction.IsCall).ToArray())
        {
            if (call.Operands[0] is not MethodAnalysisContext { IsStatic: false } target)
                continue;

            var receiverIndex = call.OpCode == OpCode.CallVoid ? 1 : 2;
            if (receiverIndex >= call.Operands.Count
                || call.Operands[receiverIndex] is not LocalVariable receiver
                || (!IsCalleeSavedArm64Register(receiver.Register.Name)
                    && !HasFrozenEvidence(method, receiver)))
                continue;

            if (method.ParameterLocals.Contains(receiver))
            {
                Logger.VerboseNewline(
                    $"保存寄存器接收者拒绝：method={method.FullName}，call={call.Index}，" +
                    $"receiver={receiver.Register}，target={target.FullName}，" +
                    $"defined={definitions.ContainsKey(receiver)}，parameter={method.ParameterLocals.Contains(receiver)}",
                    nameof(CalleeSavedManagedReceiverRecovery));
                continue;
            }

            var candidates = GetCandidates(method, producers, receiver, target.DeclaringType, call.Index);
            Logger.VerboseNewline(
                $"保存寄存器接收者候选：method={method.FullName}，call={call.Index}，" +
                $"receiver={receiver.Register}，target={target.FullName}，candidates=" +
                string.Join(",", candidates.Select(candidate => candidate.Register + ":" + candidate.Type?.FullName)),
                nameof(CalleeSavedManagedReceiverRecovery));
            if (candidates.Length != 1
                || !HasOnlyProvenGeneratedDefinitions(method, producers, definitions, receiver, candidates[0]))
                continue;

            call.SetOperand(receiverIndex, candidates[0]);
            GenericCallRebinder.TryRebind(call);
            recovered++;
        }

        foreach (var instruction in instructions)
        {
            for (var operandIndex = 0; operandIndex < instruction.Operands.Count; operandIndex++)
            {
                if (instruction.Operands[operandIndex] is not LocalVariable value
                    || ReferenceEquals(instruction.Destination, value)
                    || method.ParameterLocals.Contains(value)
                    || !IsCalleeSavedArm64Register(value.Register.Name)
                    || value.Type is not { IsValueType: false })
                    continue;

                var candidates = GetCandidates(method, producers, value, value.Type, instruction.Index);
                if (candidates.Length != 1
                    || !HasOnlyProvenGeneratedDefinitions(method, producers, definitions, value, candidates[0]))
                    continue;

                // 中文注释：实例调用已在上方按声明类型恢复；这里处理同一未定义保存寄存器
                // 的普通读取，例如对象与 null 的比较。证据仍须来自同一 SSA Phi 闭包。
                instruction.SetOperand(operandIndex, candidates[0]);
                recovered++;
            }
        }

        foreach (var instruction in instructions)
        {
            for (var operandIndex = 0; operandIndex < instruction.Operands.Count; operandIndex++)
            {
                if (instruction.Operands[operandIndex] is not FieldReference field
                    || method.ParameterLocals.Contains(field.Local)
                    || !IsCalleeSavedArm64Register(field.Local.Register.Name))
                    continue;

                var candidates = GetCandidates(
                    method,
                    producers,
                    field.Local,
                    field.Local.Type ?? field.Field.DeclaringType,
                    instruction.Index);
                if (candidates.Length != 1)
                    continue;
                if (!HasOnlyProvenGeneratedDefinitions(method, producers, definitions, field.Local, candidates[0]))
                    continue;

                // 中文注释：异常 Phi 会丢掉 X19-X29 中保存的托管身份；字段读取尚未
                // 转成 Count 前，唯一相容的托管生产值就是该布局读取的实例接收者。
                field.Local = candidates[0];
                recovered++;
            }
        }

        return recovered;
    }

    /// <summary>
    /// 接收者没有定义时直接放行；有定义时只接受退 SSA 生成、且全部归一到同一来源的边复制。
    /// </summary>
    private static bool HasOnlyProvenGeneratedDefinitions(
        MethodAnalysisContext method,
        IReadOnlyList<Instruction> producers,
        IReadOnlyDictionary<LocalVariable, Instruction[]> definitions,
        LocalVariable receiver,
        LocalVariable candidate)
    {
        if (!definitions.TryGetValue(receiver, out var receiverDefinitions))
            return true;

        foreach (var definition in receiverDefinitions)
        {
            if (definition is not
                {
                    Index: -1,
                    OpCode: OpCode.Move,
                    Operands: [LocalVariable destination, LocalVariable source],
                }
                || !SameSsaLocal(destination, receiver))
                return false;

            if (SameSsaLocal(source, candidate))
                continue;

            var sourceCandidates = GetCandidates(method, producers, source, receiver.Type, int.MaxValue);
            if (sourceCandidates.Length != 1 || !SameSsaLocal(sourceCandidates[0], candidate))
                return false;
        }

        return true;
    }

    /// <summary>
    /// 优先消费同一 SSA 版本的直接复制证据；没有直接证据时才使用原有的全方法唯一候选规则。
    /// </summary>
    private static LocalVariable[] GetCandidates(
        MethodAnalysisContext method,
        IReadOnlyList<Instruction> producers,
        LocalVariable receiver,
        TypeAnalysisContext? declaringType,
        int consumerIndex)
    {
        var producerLocals = producers
            .Select(producer => (LocalVariable)producer.Destination!)
            .ToArray();
        var managedOrigins = producerLocals
            .Concat(method.ParameterLocals.Where(parameter => parameter.Type is { IsValueType: false }))
            .Distinct()
            .ToArray();
        var matchingEvidence = method.CalleeSavedSsaCopyEvidence
            .Where(evidence => SameSsaLocal(evidence.Destination, receiver)
                               && evidence.Index >= 0
                               && consumerIndex >= 0
                               && evidence.Index < consumerIndex)
            .ToArray();
        if (matchingEvidence.Length > 0)
            return matchingEvidence
                .SelectMany(evidence => managedOrigins.Where(origin =>
                    SameSsaLocal(origin, evidence.Source)
                    && IsCompatible(origin.Type, declaringType)))
                .Distinct()
                .ToArray();

        return producers
                .Where(producer => producer.Index >= 0
                                   && consumerIndex >= 0
                                   && producer.Index < consumerIndex
                                   && producer.Destination is LocalVariable candidate
                                   && IsCompatible(candidate.Type, declaringType))
                .Select(producer => (LocalVariable)producer.Destination!)
                .Distinct()
                .ToArray();
    }

    private static bool SameSsaLocal(LocalVariable left, LocalVariable right)
        => ReferenceEquals(left, right) || left.Register.Equals(right.Register);

    private static bool HasFrozenEvidence(MethodAnalysisContext method, LocalVariable local)
        => method.CalleeSavedSsaCopyEvidence.Any(evidence => SameSsaLocal(evidence.Destination, local));

    private static bool IsCompatible(TypeAnalysisContext? candidate, TypeAnalysisContext? declaringType)
    {
        if (candidate == null || declaringType == null || candidate.IsValueType)
            return false;

        return GenericCallRebinder.TypesEquivalent(candidate, declaringType)
               || candidate.IsAssignableTo(declaringType)
               || IsGenericEnumeratorAssignableToNonGeneric(candidate, declaringType)
               || GenericCallRebinder.IsSharedObjectPlaceholder(declaringType, candidate);
    }

    private static bool IsGenericEnumeratorAssignableToNonGeneric(
        TypeAnalysisContext candidate,
        TypeAnalysisContext declaringType)
        => declaringType.FullName == "System.Collections.IEnumerator"
           && candidate is GenericInstanceTypeAnalysisContext genericCandidate
           && genericCandidate.GenericType.FullName == "System.Collections.Generic.IEnumerator`1";

    internal static bool IsCalleeSavedArm64Register(string? name)
    {
        if (name is not { Length: >= 3 } || name[0] != 'X')
            return false;

        // 中文注释：X29 通常是帧指针，但 IL2CPP 在保存旧 X29 后会把它复用为长期 this；
        // 后续恢复仍要求同一 SSA 复制证据和托管来源，因此普通栈基址不会进入候选集。
        return int.TryParse(name.Substring(1), out var number) && number is >= 19 and <= 29;
    }
}
