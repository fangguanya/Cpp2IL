using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Extensions;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Logging;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 在 SSA 复制前递之前恢复“栈临时逐槽初始化 + 取址按值传参”的大值类型实参。
/// AAPCS64 对超过十六字节的非 HFA 值类型按地址传参且只占一个通用寄存器槽：
/// 调用点先把每个字段写入栈临时区，再把临时区基址装入实参寄存器。
/// 若等到 SsaSimplifier，基槽写入会被折进 AddressOf，其余槽写入因没有读取而被死码删除，
/// 聚合结构随之整体丢失；因此本 pass 在 SSA 单一定义仍有效时对整个临时区做完整证明，
/// 证明闭合才把槽写入改写为精确字段写入（或把全零写归约为默认零初始化），
/// 并让调用按值直接使用该栈局部。任何一步证明失败都不改写图，方法保持原有未解决状态。
/// </summary>
public static class StackAggregateArgumentRecovery
{
    private readonly record struct LeafField(
        FieldAnalysisContext Field,
        long Offset,
        int Size,
        ManagedFieldSpanRecoveryHelper.ScalarKind Kind,
        FieldAnalysisContext Root,
        IReadOnlyList<FieldAnalysisContext>? ParentFields);

    private sealed record StackWrite(Instruction Instruction, LocalVariable Slot, int AbsoluteOffset, int WidthBytes);

    private sealed record ProvenStore(
        StackWrite Write,
        FieldAnalysisContext RootField,
        int RelativeOffset,
        IOperand TerminalSource,
        TypeAnalysisContext? WholeAggregateType);

    private sealed record MixedFieldCopy(
        FieldAnalysisContext TargetField,
        int TargetOffset,
        FieldAnalysisContext SourceField,
        int SourceOffset,
        int WidthBytes);

    private sealed record MixedFieldCopyPlan(
        StackWrite Write,
        Instruction LoadDefinition,
        LocalVariable SourceLocal,
        IReadOnlyList<MixedFieldCopy> Copies);

    // 超出该尺寸的栈临时说明初始化形态已超出本批合同，保守拒绝而不是分配巨型覆盖表。
    private const int MaxProvableInstanceSize = 1 << 20;

    // 仅诊断：设置 CPP2IL_STACK_AGGREGATE_TRIAGE=<路径> 后，逐候选记录合同门拒因。
    // 默认关闭；记录中的方法/类型名只作身份标识，绝不参与任何判定。
    private static readonly string? TriageLogPath = Environment.GetEnvironmentVariable("CPP2IL_STACK_AGGREGATE_TRIAGE");
    private static readonly object TriageLogLock = new();
    private static readonly List<string> TriagePending = [];

    private static void NoteTriage(MethodAnalysisContext method, Instruction call, int operandIndex,
        TypeAnalysisContext parameterType, string reason)
    {
        if (TriageLogPath == null)
            return;
        var target = call.Operands.Count > 0
            ? call.Operands[0] is MethodAnalysisContext callee ? callee.FullName : call.Operands[0].ToString()!
            : "?";
        // 受控诊断运行在高限制 JobObject 中：逐行打开文件的写放大实测达秒级，
        // 先在内存缓冲、按批落盘，进程退出时冲刷残余。
        lock (TriageLogLock)
        {
            TriagePending.Add(string.Join("\t",
                method.FullName, target, operandIndex.ToString(), parameterType.FullName, reason));
            if (TriagePending.Count < 64)
                return;
            File.AppendAllLines(TriageLogPath, TriagePending);
            TriagePending.Clear();
        }
    }

    static StackAggregateArgumentRecovery()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushTriage();
    }

    private static void FlushTriage()
    {
        lock (TriageLogLock)
        {
            if (TriagePending.Count == 0 || TriageLogPath == null)
                return;
            File.AppendAllLines(TriageLogPath, TriagePending);
            TriagePending.Clear();
        }
    }

    public static bool Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return false;
        var pointerSize = method.AppContext.Binary.PointerSizeBytes;
        if (pointerSize is not (4 or 8))
            return false;

        // 定义、读取与取址索引在 SSA 单一定义期只建一次，供全部候选实参复用。
        var positions = new Dictionary<Instruction, (Block Block, int Position)>();
        var definitions = new Dictionary<LocalVariable, List<Instruction>>();
        var uses = new Dictionary<LocalVariable, List<Instruction>>();
        var addressTaken = new HashSet<LocalVariable>();
        var stackWrites = new List<StackWrite>();
        foreach (var block in graph.Blocks)
        for (var position = 0; position < block.Instructions.Count; position++)
        {
            var instruction = block.Instructions[position];
            positions[instruction] = (block, position);
            if (instruction.Destination is LocalVariable destination)
            {
                if (!definitions.TryGetValue(destination, out var localDefinitions))
                    definitions.Add(destination, localDefinitions = []);
                localDefinitions.Add(instruction);
            }
            foreach (var used in DeadCodeEliminator.EnumerateUsedLocals(instruction))
            {
                if (!uses.TryGetValue(used, out var readers))
                    uses.Add(used, readers = []);
                readers.Add(instruction);
            }
            foreach (var operand in instruction.Operands)
                if (operand is AddressOf { Target: LocalVariable addressed })
                    addressTaken.Add(addressed);
            if (instruction.OpCode == OpCode.Move
                && instruction.MemoryAccessWidthBits > 0
                && instruction.MemoryAccessWidthBits % 8 == 0
                && instruction.Operands is [LocalVariable slot, _]
                && AggregateStackCopyRecovery.TryGetStackOffset(slot, out var stackOffset))
                stackWrites.Add(new(instruction, slot, stackOffset, instruction.MemoryAccessWidthBits / 8));
        }

        DominatorInfo? dominance = null;
        var changed = false;
        foreach (var block in graph.Blocks)
        for (var position = 0; position < block.Instructions.Count; position++)
        {
            var instruction = block.Instructions[position];
            if (!instruction.IsCall || instruction.Operands.Count == 0
                || instruction.Operands[0] is not MethodAnalysisContext target)
                continue;
            var firstParameter = CallArgumentTrimmer.FirstParameterOperandIndex(instruction, target);
            for (var index = firstParameter; index < instruction.Operands.Count; index++)
            {
                var parameterIndex = index - firstParameter;
                if (parameterIndex >= target.Parameters.Count)
                    break; // RGCTX 等隐参不属于托管形参。
                if (TryRecoverArgument(method, graph, instruction, index, target.Parameters[parameterIndex].ParameterType,
                        pointerSize, positions, definitions, uses, addressTaken, stackWrites,
                        block, position, ref dominance))
                    changed = true;
            }
        }
        return changed;
    }

    private static bool TryRecoverArgument(
        MethodAnalysisContext method,
        ISILControlFlowGraph graph,
        Instruction call,
        int operandIndex,
        TypeAnalysisContext parameterType,
        int pointerSize,
        Dictionary<Instruction, (Block Block, int Position)> positions,
        IReadOnlyDictionary<LocalVariable, List<Instruction>> definitions,
        IReadOnlyDictionary<LocalVariable, List<Instruction>> uses,
        HashSet<LocalVariable> addressTaken,
        List<StackWrite> stackWrites,
        Block callBlock,
        int callPosition,
        ref DominatorInfo? dominance)
    {
        // 取址探针与形参门都是纯查询，先后顺序不影响判定；诊断只记录真正走到
        // 取址候选的调用，避免无关调用的形参门噪音淹没拒因。
        if (!TryResolveAddressedStackLocal(call.Operands[operandIndex], definitions,
                out var stackLocal, out var baseOffset, out var addressDefinition))
            return false;

        bool Reject(string reason)
        {
            NoteTriage(method, call, operandIndex, parameterType, reason);
            return false;
        }

        // 形参门：按值的大聚合才受 AAPCS64 的间接传参规则约束；
        // ref/out 形参本身已经是地址，证明对象改用其封闭元素值类型，不能把地址类型当布局类型。
        // 开放泛型、浮点标量与 HFA 保持原有 ABI 分流，不由本 pass 接管。
        var isByRef = parameterType is ByRefTypeAnalysisContext;
        var recoveryType = parameterType is ByRefTypeAnalysisContext byRef
            ? byRef.ElementType
            : parameterType;
        if (recoveryType is GenericParameterTypeAnalysisContext)
            return Reject("parameter_byref_or_generic");
        if (!recoveryType.IsValueType)
            return Reject("parameter_not_value_type");
        if (!isByRef && X64CallingConventionResolver.IsFloatingPoint(recoveryType))
            return Reject("parameter_floating_scalar");
        if (!isByRef && Arm64CallingConventionResolver.TryGetHomogeneousFloatingAggregateFields(recoveryType, out _))
            return Reject("parameter_hfa");
        if (recoveryType.HasAnyGenericParameters() || recoveryType.GenericParameters.Count != 0)
            return Reject("parameter_open_generic");
        if (!TryGetInstanceSize(recoveryType, pointerSize, out var instanceSize))
            return Reject("parameter_size_unknown");
        if (!isByRef && instanceSize <= 16)
            return Reject("parameter_size_at_most_16");
        if (instanceSize > MaxProvableInstanceSize)
            return Reject("parameter_size_over_proof_limit");

        if (!TryGetLeafFields(recoveryType, pointerSize, out var leaves))
            return Reject("layout_unavailable");

        var regionEnd = checked(baseOffset + (int)instanceSize);
        // 与区域相交的栈写只有两类可接受：完整落入区域并支配调用的是候选槽写入；
        // 被调用支配的是调用后复用同一临时的未来覆盖，与本次实参值无关。
        // 其余相交形态都可能在本组写入与调用之间篡改临时区，整组放弃。
        var candidates = new List<StackWrite>();
        foreach (var write in stackWrites)
        {
            var writeEnd = checked(write.AbsoluteOffset + write.WidthBytes);
            if (writeEnd <= baseOffset || write.AbsoluteOffset >= regionEnd)
                continue;
            if (write.AbsoluteOffset < baseOffset || writeEnd > regionEnd)
                return Reject("write_partially_outside_region");
            var (writeBlock, writePosition) = positions[write.Instruction];
            var dominatesCall = writeBlock == callBlock
                ? writePosition < callPosition
                : (dominance ??= new DominatorInfo(graph)).Dominates(writeBlock, callBlock);
            if (dominatesCall)
            {
                candidates.Add(write);
                continue;
            }
            var afterCall = writeBlock == callBlock
                ? writePosition > callPosition
                // 不同块时上一行已惰性建立支配信息，此处必非空。
                : dominance!.Dominates(callBlock, writeBlock);
            if (!afterCall)
                return Reject("write_ordering_unproven");
        }

        // 布局探针局部只承载类型身份，供既有跨度描述合同按接收者类型重放布局；
        // 它是纯证明对象，绝不进入控制流图。
        var layoutProbe = new LocalVariable("layoutProbe", new Register(null, "layoutProbe"), recoveryType);

        // 字节级覆盖表：每个字段的每个字节必须被恰好一次写入覆盖；填充字节不受约束。
        var covered = new byte[(int)instanceSize];
        var provenStores = new List<ProvenStore>(candidates.Count);
        var mixedCopies = new List<MixedFieldCopyPlan>();
        var zeroRuns = new List<StackWrite>(candidates.Count);
        foreach (var write in candidates)
        {
            // 槽局部必须只有这一条定义；多余定义说明槽被复用，无法证明单一来源。
            // 已改写为字段写入的旧候选不再以栈槽为目标，同样不得混入本组证明。
            if (!definitions.TryGetValue(write.Slot, out var slotDefinitions)
                || slotDefinitions.Count != 1
                || !ReferenceEquals(slotDefinitions[0], write.Instruction)
                || write.Instruction.Operands is not [LocalVariable, _])
                return Reject("slot_multiple_definitions");
            // 基槽只允许被取址定义（或直接取址实参的调用）读取；其余槽写入后不得有任何读取。
            if (uses.TryGetValue(write.Slot, out var readers))
                foreach (var reader in readers)
                    if (!ReferenceEquals(write.Slot, stackLocal)
                        || !ReferenceEquals(reader, addressDefinition ?? call))
                        return Reject("slot_extra_reader");
            var relativeOffset = write.AbsoluteOffset - baseOffset;
            var end = relativeOffset + write.WidthBytes;
            for (var byteIndex = relativeOffset; byteIndex < end; byteIndex++)
            {
                if (covered[byteIndex] != 0)
                    return Reject("byte_covered_twice"); // 重叠写入：同一字节被覆盖两次，无法证明唯一值。
                covered[byteIndex] = 1;
            }

            if (!TryResolveSlotSource(write, definitions, addressTaken,
                    out var terminal, out var immediate, out var loadReference, out var loadDefinition,
                    out var sourceReason))
                return Reject(sourceReason);

            if (immediate.Value == 0 && loadReference == null)
            {
                // 全零写（STR XZR 块）：编译器按自身八字节块清零，允许跨越字段与填充边界；
                // 相交叶字段的零位模式必须等于托管默认值（标量/浮点为零、引用为 null）。
                // 证明后该写可由 CIL 局部的零初始化完整承担，整条指令归约为 Nop。
                foreach (var leaf in leaves)
                    if (leaf.Offset < end && leaf.Offset + leaf.Size > relativeOffset
                        && leaf.Kind is not (ManagedFieldSpanRecoveryHelper.ScalarKind.Integer
                            or ManagedFieldSpanRecoveryHelper.ScalarKind.NativeInteger
                            or ManagedFieldSpanRecoveryHelper.ScalarKind.Single
                            or ManagedFieldSpanRecoveryHelper.ScalarKind.Double
                            or ManagedFieldSpanRecoveryHelper.ScalarKind.ManagedReference))
                        return Reject("zero_run_over_nondefaultable_field");
                zeroRuns.Add(write);
                continue;
            }

            // 非零写入的覆盖证明完全复用既有跨度描述合同：以覆盖起点的根字段与写宽构造
            // 探针 FieldReference，由 ManagedFieldSpanRecoveryHelper 完成叶级平铺、间隙与
            // 部分覆盖校验——与生成期发射所用的描述逐字节一致，不手写第二份布局走查。
            var startLeaf = -1;
            for (var index = 0; index < leaves.Count; index++)
                if (leaves[index].Offset == relativeOffset)
                {
                    startLeaf = index;
                    break;
                }
            if (startLeaf < 0)
                return Reject("store_start_not_at_field_boundary");
            var probe = new FieldReference(leaves[startLeaf].Root, layoutProbe, relativeOffset);
            if (!ManagedFieldSpanRecoveryHelper.TryDescribe(probe, pointerSize, write.WidthBytes,
                    out var targetSpan, out var storeFailure, includeManagedReferences: true))
                return Reject("store_span_undescribed:" + storeFailure);

            if (loadReference != null)
            {
                // 字段读取来源：原生读取与栈写入必须同宽，且两侧跨度描述逐分量对齐。
                if (loadDefinition!.MemoryAccessWidthBits != write.Instruction.MemoryAccessWidthBits)
                    return Reject("load_width_mismatch");
                if (!ManagedFieldSpanRecoveryHelper.TryDescribe(loadReference, pointerSize, write.WidthBytes,
                        out var loadSpan, out var loadFailure, includeManagedReferences: true))
                    return Reject("load_span_undescribed:" + loadFailure);
                if (targetSpan.Segments.Any(segment =>
                        segment.Kind == ManagedFieldSpanRecoveryHelper.ScalarKind.Aggregate)
                    || loadSpan.Segments.Any(segment =>
                        segment.Kind == ManagedFieldSpanRecoveryHelper.ScalarKind.Aggregate))
                {
                    if (TryDescribeMixedFieldCopy(method, write, loadDefinition!, loadReference,
                            targetSpan, loadSpan, uses, out var mixedCopy))
                    {
                        mixedCopies.Add(mixedCopy);
                        continue;
                    }
                    // 只有两侧都能在不含托管引用的精确布局中展开为标量字段时，才把完整嵌套聚合
                    // 交给向量位通道；引用、填充、部分覆盖和未落定布局仍由既有红门拒绝。
                    if (!TryDescribeExpandedScalarSpan(probe, pointerSize, write.WidthBytes,
                            out var expandedTargetSpan)
                        || !TryDescribeExpandedScalarSpan(loadReference, pointerSize, write.WidthBytes,
                            out var expandedLoadSpan))
                        return Reject("aggregate_segment_without_whole_value_proof");
                    targetSpan = expandedTargetSpan;
                    loadSpan = expandedLoadSpan;
                }
                if (!SpansMatch(loadSpan.Segments, targetSpan.Segments))
                    return Reject("spans_mismatch");
                // 发射通道证明，与生成期各通道的接管条件一一对应：
                // · 单段整字段聚合：快照通道不接管整值对，普通字段存储按整个值发射；
                //   旧播种会把宽读目的地误标为首字段标量类型，提交段按两侧同一描述证明的
                //   聚合身份把末端源局部校正回精确聚合类型。
                // · 含托管引用：快照写回通道必然接管，按同一合同先证两侧访问边界。
                // · 纯标量多段/嵌套：只有向量位通道能发射，来源必须是 SIMD 载体局部。
                // · 单段根级标量但源为 SIMD 载体时，向量通道同样会接管并执行访问门。
                if (targetSpan.Segments[0].Kind == ManagedFieldSpanRecoveryHelper.ScalarKind.Aggregate)
                {
                    if (targetSpan.Segments.Count != 1 || terminal is not LocalVariable)
                        return Reject("aggregate_segment_without_whole_value_proof");
                    provenStores.Add(new(write, leaves[startLeaf].Root, relativeOffset, terminal!,
                        targetSpan.Segments[0].Field.FieldType));
                    continue;
                }
                // 混合跨度中的整聚合段没有对应发射通道，保持红门。
                if (targetSpan.Segments.Any(segment =>
                        segment.Kind == ManagedFieldSpanRecoveryHelper.ScalarKind.Aggregate))
                    return Reject("aggregate_segment_mixed");
                var needsAccessibilityProof =
                    targetSpan.Segments.Any(segment =>
                        segment.Kind == ManagedFieldSpanRecoveryHelper.ScalarKind.ManagedReference)
                    || targetSpan.Segments.Count > 1
                    || targetSpan.Segments[0].ParentFields is { Count: > 0 };
                if (!needsAccessibilityProof
                    && write.Instruction.MemoryAccessWidthBits is 64 or 128
                    && terminal is LocalVariable scalarTerminal && IsSimdRegister(scalarTerminal))
                    needsAccessibilityProof = true;
                if (needsAccessibilityProof)
                {
                    // 纯标量的多段或嵌套跨度只能由向量位通道发射；含引用跨度由快照通道发射，
                    // 不要求 SIMD 载体。
                    var needsVectorChannel =
                        !targetSpan.Segments.Any(segment =>
                            segment.Kind == ManagedFieldSpanRecoveryHelper.ScalarKind.ManagedReference)
                        && (targetSpan.Segments.Count > 1
                            || targetSpan.Segments[0].ParentFields is { Count: > 0 });
                    if (needsVectorChannel
                        && (terminal is not LocalVariable vectorTerminal || !IsSimdRegister(vectorTerminal)))
                        return Reject("channel_unavailable");
                    if (method.DeclaringType == null
                        || !SpanAccessible(method.DeclaringType, probe, targetSpan.Segments, write: true)
                        || !SpanAccessible(method.DeclaringType, loadReference, loadSpan.Segments, write: false))
                        return Reject("field_not_accessible");
                }
            }
            else
            {
                // 非零立即数只能经普通字段存储写入单个根级标量数值/布尔字段，且位模式可精确表示；
                // 嵌套叶与多段跨度没有普通通道，保持红门。
                if (targetSpan.Segments.Count != 1)
                    return Reject("immediate_nonzero_multi_field");
                if (targetSpan.Segments[0].ParentFields is { Count: > 0 })
                    return Reject("immediate_nested_leaf_no_channel");
                if (targetSpan.Segments[0].Kind is ManagedFieldSpanRecoveryHelper.ScalarKind.Aggregate)
                    return Reject("aggregate_segment_without_whole_value_proof");
                if (targetSpan.Segments[0].Kind is not (ManagedFieldSpanRecoveryHelper.ScalarKind.Integer
                        or ManagedFieldSpanRecoveryHelper.ScalarKind.NativeInteger))
                    return Reject("immediate_kind_unsupported");
                if (!ImmediateFitsField(immediate.Value, targetSpan.Segments[0].SizeBytes))
                    return Reject("immediate_value_unrepresentable");
            }
            provenStores.Add(new(write, leaves[startLeaf].Root, relativeOffset, terminal!, null));
        }
        // 完整覆盖合同：每个叶字段的每个字节恰好被写入一次；未写字节只能是无字段背书的填充。
        foreach (var leaf in leaves)
            for (var byteIndex = (int)leaf.Offset; byteIndex < leaf.Offset + leaf.Size; byteIndex++)
                if (covered[byteIndex] == 0)
                    return Reject("coverage_incomplete");

        // 证明闭合后才改写：基槽绑定精确元素值类型，非零槽写入改写为字段写入
        //（FieldReference 记录覆盖起点的根字段与写起点偏移，与探针同一形态），
        // 全零写归约为 Nop（既有 pass 模式与 AggregateStackCopyRecovery 一致）。
        // ref/out 的调用操作数仍必须是地址载体；只有按值的大聚合才替换成值局部。
        stackLocal.Type = recoveryType;
        foreach (var store in provenStores)
        {
            store.Write.Instruction.SetOperand(0,
                new FieldReference(store.RootField, stackLocal, store.RelativeOffset));
            store.Write.Instruction.SetOperand(1, store.TerminalSource);
            if (store.WholeAggregateType is { } wholeType
                && store.TerminalSource is LocalVariable wholeTerminal)
                // 旧播种把宽读目的地误标为首字段标量类型；两侧同一跨度描述证明的
                // 整字段聚合身份是权威证据，在此校正，普通整值存取通道才能按真实类型发射。
                wholeTerminal.Type = wholeType;
            stackWrites.Remove(store.Write);
        }
        foreach (var plan in mixedCopies)
        {
            // 宽向量只作为原生载体存在，且已证明只有当前栈写读取它；改写后以字段到字段的
            // 托管复制承接引用和完整嵌套聚合，避免把引用槽伪装成整数位模式。
            plan.LoadDefinition.OpCode = OpCode.Nop;
            plan.LoadDefinition.SetOperands();
            plan.LoadDefinition.MemoryAccessWidthBits = 0;
            var writeBlock = positions[plan.Write.Instruction].Block;
            var writePosition = writeBlock.Instructions.IndexOf(plan.Write.Instruction);
            for (var index = 0; index < plan.Copies.Count; index++)
            {
                var copy = plan.Copies[index];
                var replacement = index == 0
                    ? plan.Write.Instruction
                    : new Instruction(plan.Write.Instruction.Index + index, OpCode.Move);
                replacement.SetOperands(
                    new FieldReference(copy.TargetField, stackLocal, copy.TargetOffset),
                    new FieldReference(copy.SourceField, plan.SourceLocal, copy.SourceOffset));
                replacement.MemoryAccessWidthBits = checked(copy.WidthBytes * 8);
                if (index > 0)
                    writeBlock.Instructions.Insert(writePosition + index, replacement);
            }
            stackWrites.Remove(plan.Write);
            for (var index = 0; index < writeBlock.Instructions.Count; index++)
                positions[writeBlock.Instructions[index]] = (writeBlock, index);
        }
        foreach (var write in zeroRuns)
        {
            write.Instruction.OpCode = OpCode.Nop;
            write.Instruction.SetOperands();
            stackWrites.Remove(write);
        }
        if (isByRef)
        {
            if (call.Operands[operandIndex] is LocalVariable addressCarrier)
                addressCarrier.Type = parameterType;
        }
        else
            call.SetOperand(operandIndex, stackLocal);
        Logger.VerboseNewline(
            $"栈聚合实参恢复：call={call}，slot={stackLocal}，type={parameterType.FullName}，" +
            $"base=0x{baseOffset:X}，size=0x{instanceSize:X}，stores={provenStores.Count}，zeroRuns={zeroRuns.Count}");
        NoteTriage(method, call, operandIndex, parameterType, "recovered");
        return true;
    }

    /// <summary>
    /// 混合跨度只在宽字段读的唯一消费者正是当前栈槽、两侧段逐段等价且均可直接访问时，
    /// 退化为字段到字段的托管复制。该路径不经过向量位通道，也不接受嵌套访问器或部分字段。
    /// </summary>
    private static bool TryDescribeMixedFieldCopy(
        MethodAnalysisContext method,
        StackWrite write,
        Instruction loadDefinition,
        FieldReference loadReference,
        ManagedFieldSpanRecoveryHelper.Description targetSpan,
        ManagedFieldSpanRecoveryHelper.Description loadSpan,
        IReadOnlyDictionary<LocalVariable, List<Instruction>> uses,
        out MixedFieldCopyPlan plan)
    {
        plan = null!;
        if (!targetSpan.Segments.Any(segment =>
                segment.Kind == ManagedFieldSpanRecoveryHelper.ScalarKind.ManagedReference)
            || !targetSpan.Segments.Any(segment =>
                segment.Kind == ManagedFieldSpanRecoveryHelper.ScalarKind.Aggregate)
            || loadDefinition.Destination is not LocalVariable sourceValue
            || !uses.TryGetValue(sourceValue, out var readers)
            || readers.Count != 1
            || !ReferenceEquals(readers[0], write.Instruction)
            || targetSpan.Segments.Count != loadSpan.Segments.Count)
            return false;

        if (targetSpan.Segments.Any(segment => segment.ParentFields is { Count: > 0 })
            || loadSpan.Segments.Any(segment => segment.ParentFields is { Count: > 0 })
            || method.DeclaringType == null
            || !DirectFieldCopyAccess(method.DeclaringType, loadReference, loadSpan.Segments)
            || !DirectFieldCopyAccess(method.DeclaringType, targetSpan.Segments))
            return false;

        var copies = new List<MixedFieldCopy>(targetSpan.Segments.Count);
        for (var index = 0; index < targetSpan.Segments.Count; index++)
        {
            var target = targetSpan.Segments[index];
            var source = loadSpan.Segments[index];
            if (target.RelativeOffsetBytes != source.RelativeOffsetBytes
                || target.SizeBytes != source.SizeBytes
                || target.Kind != source.Kind
                || !GenericCallRebinder.TypesEquivalent(target.Field.FieldType, source.Field.FieldType,
                    requireDefinitionIdentity: true))
                return false;
            copies.Add(new(target.RootField, target.RelativeOffsetBytes,
                source.RootField, checked(loadReference.Offset + source.RelativeOffsetBytes), target.SizeBytes));
        }

        plan = new MixedFieldCopyPlan(write, loadDefinition, sourceValue, copies);
        return true;
    }

    private static bool DirectFieldCopyAccess(
        TypeAnalysisContext declaringType,
        FieldReference reference,
        IReadOnlyList<ManagedFieldSpanRecoveryHelper.Segment> segments)
    {
        if (reference.Field.IsStatic || segments.Any(segment =>
                segment.Field.IsStatic || segment.ParentFields is { Count: > 0 }))
            return false;
        return segments.All(segment =>
            PropertyBackingFieldRecovery.CanDirectlyAccessPrivateField(declaringType, segment.Field));
    }

    private static bool DirectFieldCopyAccess(
        TypeAnalysisContext declaringType,
        IReadOnlyList<ManagedFieldSpanRecoveryHelper.Segment> segments)
        => segments.All(segment =>
            !segment.Field.IsStatic
            && segment.ParentFields is not { Count: > 0 }
            && PropertyBackingFieldRecovery.CanDirectlyAccessPrivateField(declaringType, segment.Field));

    /// <summary>
    /// 仅展开精确跨度内的纯标量嵌套聚合；托管引用和聚合残留都不属于该合同。
    /// </summary>
    private static bool TryDescribeExpandedScalarSpan(
        FieldReference reference,
        int pointerSize,
        int widthBytes,
        out ManagedFieldSpanRecoveryHelper.Description description)
    {
        if (!ManagedFieldSpanRecoveryHelper.TryDescribe(reference, pointerSize, widthBytes,
                out description, out _, includeManagedReferences: false,
                allowOpaqueSmallAggregate: false))
            return false;

        return description.Segments.All(segment =>
            segment.Kind != ManagedFieldSpanRecoveryHelper.ScalarKind.Aggregate
            && segment.Kind != ManagedFieldSpanRecoveryHelper.ScalarKind.ManagedReference);
    }

    /// <summary>实参必须直接是栈槽取址，或其唯一定义链末端是一条栈槽取址 Move。</summary>
    private static bool TryResolveAddressedStackLocal(
        IOperand operand,
        IReadOnlyDictionary<LocalVariable, List<Instruction>> definitions,
        out LocalVariable stackLocal,
        out int baseOffset,
        out Instruction? addressDefinition)
    {
        stackLocal = null!;
        baseOffset = 0;
        addressDefinition = null;
        if (operand is AddressOf { Target: LocalVariable direct }
            && AggregateStackCopyRecovery.TryGetStackOffset(direct, out baseOffset))
        {
            stackLocal = direct;
            return true;
        }
        if (operand is not LocalVariable argument)
            return false;
        var current = argument;
        var visited = new HashSet<LocalVariable>();
        while (visited.Add(current))
        {
            if (!definitions.TryGetValue(current, out var chain) || chain.Count != 1
                || chain[0].OpCode != OpCode.Move || chain[0].Operands.Count != 2)
                return false;
            switch (chain[0].Operands[1])
            {
                case LocalVariable next:
                    current = next;
                    continue;
                case AddressOf { Target: LocalVariable pointee }
                    when AggregateStackCopyRecovery.TryGetStackOffset(pointee, out baseOffset):
                    stackLocal = pointee;
                    addressDefinition = chain[0];
                    return true;
                default:
                    return false;
            }
        }
        return false;
    }

    /// <summary>
    /// 沿 SSA 唯一定义的纯 Move 链（含等值单源 Phi）找到槽写入的末端来源：
    /// 字段读取局部（覆盖溢出/重载形态）或立即数。链上栈槽中间局部不得被取址；
    /// 其它形态一律拒绝。字段读取来源额外返回读取指令与引用，供宽度与跨度对齐证明。
    /// </summary>
    private static bool TryResolveSlotSource(
        StackWrite write,
        IReadOnlyDictionary<LocalVariable, List<Instruction>> definitions,
        HashSet<LocalVariable> addressTaken,
        out IOperand? terminal,
        out Immediate immediate,
        out FieldReference? loadReference,
        out Instruction? loadDefinition,
        out string reason)
    {
        terminal = null;
        immediate = default;
        loadReference = null;
        loadDefinition = null;
        reason = "source_chain_unproven";
        var source = write.Instruction.Operands[1];
        if (source is Immediate directImmediate)
        {
            terminal = directImmediate;
            immediate = directImmediate;
            return true;
        }
        if (source is not LocalVariable sourceLocal)
        {
            reason = "source_not_local_or_immediate";
            return false;
        }
        var current = sourceLocal;
        var visited = new HashSet<LocalVariable>();
        while (visited.Add(current))
        {
            if (!definitions.TryGetValue(current, out var chain) || chain.Count != 1)
            {
                reason = "source_chain_multi_definition";
                return false;
            }
            var definition = chain[0];
            // 单源 Phi 在剪枝 SSA 中等价于一次复制：所有入边携带同一局部。
            if (definition.OpCode == OpCode.Phi)
            {
                LocalVariable? phiSource = null;
                var transparent = definition.Operands.Count >= 2;
                for (var index = 1; index < definition.Operands.Count && transparent; index++)
                    if (definition.Operands[index] is LocalVariable candidate
                        && (phiSource == null || ReferenceEquals(phiSource, candidate)))
                        phiSource = candidate;
                    else
                        transparent = false;
                if (!transparent || phiSource == null)
                {
                    reason = "source_chain_opaque_phi";
                    return false;
                }
                current = phiSource;
                continue;
            }
            if (definition.OpCode != OpCode.Move || definition.Operands.Count != 2)
            {
                reason = "source_chain_non_move_definition";
                return false;
            }
            switch (definition.Operands[1])
            {
                case LocalVariable next:
                    if (AggregateStackCopyRecovery.TryGetStackOffset(next, out _) && addressTaken.Contains(next))
                    {
                        reason = "source_chain_stack_slot_address_taken";
                        return false;
                    }
                    current = next;
                    continue;
                case Immediate chainedImmediate:
                    terminal = current;
                    immediate = chainedImmediate;
                    return true;
                case FieldReference load:
                    terminal = current;
                    loadReference = load;
                    loadDefinition = definition;
                    return true;
                default:
                    reason = "source_chain_non_move_definition";
                    return false;
            }
        }
        return false;
    }

    private static bool ImmediateFitsField(long value, int sizeBytes)
    {
        if (sizeBytes >= 8)
            return true;
        var shift = 64 - sizeBytes * 8;
        // 零扩展或符号扩展往返后仍能精确还原，才认为该常量可由目标字段宽度表示。
        return unchecked((long)((ulong)value << shift >> shift)) == value
               || value << shift >> shift == value;
    }

    /// <summary>读取与写入两侧的跨度描述逐分量对齐：偏移、尺寸、种类一致，类型定义身份等价或沿用既有引用安全上转规则。</summary>
    private static bool SpansMatch(
        IReadOnlyList<ManagedFieldSpanRecoveryHelper.Segment> loadSegments,
        IReadOnlyList<ManagedFieldSpanRecoveryHelper.Segment> storeSegments)
    {
        if (loadSegments.Count != storeSegments.Count)
            return false;
        for (var index = 0; index < loadSegments.Count; index++)
        {
            var load = loadSegments[index];
            var store = storeSegments[index];
            if (load.RelativeOffsetBytes != store.RelativeOffsetBytes
                || load.SizeBytes != store.SizeBytes
                || load.Kind != store.Kind)
                return false;
            if (GenericCallRebinder.TypesEquivalent(load.Field.FieldType, store.Field.FieldType,
                    requireDefinitionIdentity: true))
                continue;
            // 只接受同一原始类型图证明的引用向上赋值，绝不以显示名称跨越字段类型。
            if (load.Kind != ManagedFieldSpanRecoveryHelper.ScalarKind.ManagedReference
                || !ReferenceEquals(load.Field.FieldType.AppContext, store.Field.FieldType.AppContext)
                || !load.Field.FieldType.IsAssignableTo(store.Field.FieldType))
                return false;
        }
        return true;
    }

    /// <summary>
    /// 与发射侧 LoadRecoveredFieldSpanReceiver/EmitRecoveredFieldOperation 同一访问合同：
    /// 嵌套父链必须逐级为可直接访问的值类型字段（写入侧不接受只读父链与只读叶），
    /// 嵌套叶可使用唯一公开访问器；根级字段同样允许唯一公开访问器。
    /// </summary>
    private static bool SpanAccessible(
        TypeAnalysisContext declaringType,
        FieldReference reference,
        IReadOnlyList<ManagedFieldSpanRecoveryHelper.Segment> segments,
        bool write)
    {
        foreach (var segment in segments)
        {
            if (segment.ParentFields is { Count: > 0 } parents)
            {
                foreach (var parent in parents)
                {
                    if (!parent.FieldType.IsValueType
                        || !PropertyBackingFieldRecovery.CanDirectlyAccessPrivateField(declaringType, parent))
                        return false;
                    if (write && (parent.Attributes & FieldAttributes.InitOnly) != 0)
                        return false;
                }
                if (!PropertyBackingFieldRecovery.CanDirectlyAccessPrivateField(declaringType, segment.Field))
                {
                    var leafReference = new FieldReference(segment.Field, reference.Local,
                        checked(reference.Offset + segment.RelativeOffsetBytes));
                    var receiverType = parents[^1].FieldType;
                    var hasAccessor = write
                        ? PropertyBackingFieldRecovery.TryResolveSetter(
                            declaringType, leafReference, receiverType, out _)
                        : PropertyBackingFieldRecovery.TryResolveGetter(
                            declaringType, leafReference, receiverType, out _);
                    if (!hasAccessor)
                        return false;
                }
                if (write && (segment.Field.Attributes & FieldAttributes.InitOnly) != 0)
                    return false;
                continue;
            }
            if (PropertyBackingFieldRecovery.CanDirectlyAccessPrivateField(declaringType, segment.Field))
                continue;
            var leaf = new FieldReference(segment.Field, reference.Local,
                checked(reference.Offset + segment.RelativeOffsetBytes));
            if (write
                ? !PropertyBackingFieldRecovery.TryResolveSetter(declaringType, leaf, out _)
                : !PropertyBackingFieldRecovery.TryResolveGetter(declaringType, leaf, out _))
                return false;
        }
        return true;
    }

    /// <summary>实例尺寸与既有调用合同同一来源：封闭泛型走具体字段布局，其余走原始未装箱尺寸。</summary>
    private static bool TryGetInstanceSize(TypeAnalysisContext type, int pointerSize, out long size)
    {
        size = type is GenericInstanceTypeAnalysisContext
            ? GenericInstanceFieldLayout.GetSizeAndAlignment(type, pointerSize)?.Size ?? 0
            : TypeSizes.UnboxedSize(type, pointerSize);
        return size > 0;
    }

    /// <summary>
    /// 递归展开嵌套值类型，枚举值类型的全部叶字段及其精确偏移、种类与最外层声明字段；
    /// 泛型实例只接受封闭布局证明，循环嵌套或布局未知一律失败。
    /// </summary>
    private static bool TryGetLeafFields(
        TypeAnalysisContext type,
        int pointerSize,
        out IReadOnlyList<LeafField> leaves)
    {
        leaves = [];
        var result = new List<LeafField>();
        var active = new HashSet<TypeAnalysisContext>();
        if (!Expand(type, 0, null, null, result))
            return false;
        if (result.Count == 0)
            return false;
        leaves = result.OrderBy(leaf => leaf.Offset).ToArray();
        return true;

        bool Expand(
            TypeAnalysisContext owner,
            long baseOffset,
            FieldAnalysisContext? root,
            IReadOnlyList<FieldAnalysisContext>? parents,
            List<LeafField> output)
        {
            if (parents is { Count: >= 32 } || !active.Add(owner))
                return false;
            try
            {
                foreach (var (field, offset) in GetDeclaredFields(owner))
                {
                    if (offset < 0 || offset > int.MaxValue
                        || !ManagedFieldSpanRecoveryHelper.TryGetScalarLayout(
                            field.FieldType, pointerSize, true, out var size, out var kind))
                        return false;
                    var absolute = checked(baseOffset + offset);
                    if (kind == ManagedFieldSpanRecoveryHelper.ScalarKind.Aggregate)
                    {
                        // 与既有跨度描述同一展开合同：嵌套聚合展开为原始未装箱叶字段。
                        var chain = (parents ?? (IReadOnlyList<FieldAnalysisContext>)[])
                            .Append(field).ToArray();
                        if (!Expand(field.FieldType, absolute, root ?? field, chain, output))
                            return false;
                        continue;
                    }
                    output.Add(new(field, absolute, size, kind, root ?? field, parents));
                }
                return true;
            }
            finally { active.Remove(owner); }
        }
    }

    /// <summary>枚举类型的根级声明实例字段：封闭泛型走具体布局，其余走继承链原始偏移。</summary>
    private static List<(FieldAnalysisContext Field, long Offset)> GetDeclaredFields(TypeAnalysisContext owner)
    {
        if (owner is GenericInstanceTypeAnalysisContext genericInstance)
        {
            var layout = GenericInstanceFieldLayout.GetConcreteFieldLayout(genericInstance);
            return layout?.Select(entry => (entry.Field, entry.Offset)).ToList() ?? [];
        }
        var declared = new List<(FieldAnalysisContext Field, long Offset)>();
        for (var current = owner; current != null; current = current.BaseType)
            declared.AddRange(current.Fields
                .Where(field => !field.IsStatic && (field.Attributes & FieldAttributes.Literal) == 0)
                .Select(field => (field, (long)field.Offset)));
        return declared;
    }

    private static bool IsSimdRegister(LocalVariable local)
        => local.Register.Name.Length > 1
           && local.Register.Name[0] == 'V'
           && int.TryParse(local.Register.Name.Substring(1), out _);
}
