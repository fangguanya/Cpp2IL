using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Logging;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 将 IL2CPP 展开的 <c>List&lt;T&gt;.Add</c> 容量分支重新闭合为公开的托管调用。
/// </summary>
/// <remarks>
/// IL2CPP 会把快速路径展开为 <c>_items/_size/_version</c> 读写，只在容量不足时调用
/// <c>AddWithResize</c>。这些成员属于运行库私有实现，直接输出会生成既不可访问又带原生
/// 地址算术的 C#。本恢复器只接受完整的标准菱形：版本递增、容量判断、同值元素写入、
/// 大小递增以及同值慢路径必须同时成立，避免把业务分支误认成集合内联。
/// </remarks>
public static class ListAddRecovery
{
    public static int Run(MethodAnalysisContext method)
    {
        var graph = method.ControlFlowGraph!;
        // 中文注释：Newobj 的类型槽在元数据闭合后可能已从 List<object> 精确到 List<T>；
        // 容量菱形匹配前只校准 AddWithResize，使公开 Add 的元素类型直接继承真实接收者。
        RebindAddWithResizeCallsFromReceivers(graph);
        // 共享快尾会在逐项改写后只剩一个前驱，因此必须在任何图修改之前冻结其身份。
        var originalSharedFastTails = new HashSet<Block>(graph.Blocks
            .Where(block => block.Predecessors.Count >= 2));
        // 早期入口只处理原生内存路径和保留 1/1 地址证据的数组路径；末次死码
        // 删除后才出现的 0/0 紧凑数组形态由独立入口处理，避免改变既有候选顺序。
        var recovered = RunRecoveryPhase(
            graph,
            originalSharedFastTails,
            compactArrayAccessOnly: false);

        // 完整容量菱形已经优先闭合。剩余的开放 List<T>.AddWithResize 若以立即数零，
        // 或“地址载体唯一写零”作为接收者，只可能来自共享原生地址的错误托管绑定；
        // 真正的实例调用既不能以空接收者继续正常执行，也不能把 byref 地址当作 List 对象。
        var residualInstructions = graph.Blocks.SelectMany(block => block.Instructions).ToList();
        var suppressed = residualInstructions.Count(instruction =>
            TrySuppressResidualOpenGenericCall(instruction, residualInstructions));
        if (suppressed > 0)
            DeadCodeEliminator.Run(method);

        return recovered + suppressed;
    }

    /// <summary>
    /// 末次死码删除把已归一化数组写入的缩放和地址合成消除后，只恢复 0/0 证据的
    /// <see cref="ArrayAccess"/> 容量菱形；原生内存与 1/1 证据路径保持在早期入口。
    /// </summary>
    public static int RunCompactArrayAccess(MethodAnalysisContext method)
    {
        var graph = method.ControlFlowGraph!;
        var originalSharedFastTails = new HashSet<Block>(graph.Blocks
            .Where(block => block.Predecessors.Count >= 2));
        return RunRecoveryPhase(
            graph,
            originalSharedFastTails,
            compactArrayAccessOnly: true);
    }

    private static int RunRecoveryPhase(
        ISILControlFlowGraph graph,
        HashSet<Block> originalSharedFastTails,
        bool compactArrayAccessOnly)
    {
        var recovered = 0;
        while (true)
        {
            var passRecovered = RunRecoveryPass(
                graph,
                originalSharedFastTails,
                compactArrayAccessOnly);
            if (passRecovered == 0)
                return recovered;
            recovered += passRecovered;
        }
    }

    internal static int RebindAddWithResizeCallsFromReceivers(ISILControlFlowGraph graph)
    {
        var rebound = 0;
        foreach (var instruction in graph.Instructions)
        {
            if (instruction is not
                {
                    OpCode: OpCode.CallVoid,
                    Operands:
                    [
                        ConcreteGenericMethodAnalysisContext { Name: "AddWithResize" },
                        LocalVariable,
                        _,
                    ],
                })
                continue;

            if (GenericCallRebinder.TryRebind(instruction))
                rebound++;
        }

        return rebound;
    }

    /// <summary>
    /// 删除共享地址误绑定形成的开放泛型 AddWithResize。该规则同时要求：目标仍为 List&lt;T&gt;
    /// 的开放实例、调用形状精确为接收者加单值参数、接收者由唯一零证据证明为无效载体。
    /// </summary>
    internal static bool TrySuppressResidualOpenGenericCall(
        Instruction instruction,
        IReadOnlyList<Instruction> allInstructions)
    {
        if (instruction is not
            {
                OpCode: OpCode.CallVoid,
                Operands:
                [
                    ConcreteGenericMethodAnalysisContext target,
                    IOperand receiver,
                    _,
                ],
            }
            || target.Name != "AddWithResize"
            || target.BaseMethodContext.DeclaringType?.FullName != "System.Collections.Generic.List`1"
            || !target.TypeGenericParameters.Any(type => type is GenericParameterTypeAnalysisContext)
            || !IsProvenResidualReceiver(receiver, allInstructions))
            return false;

        Logger.VerboseNewline(
            $"ListAdd开放泛型残留删除：调用 {instruction.Index} 的接收者 {receiver} 具有唯一零载体证据。",
            nameof(ListAddRecovery));
        instruction.OpCode = OpCode.Nop;
        instruction.SetOperands();
        return true;
    }

    /// <summary>
    /// 只沿引用相同的局部 Move 定义回溯；每一层必须恰好一个定义，且最终为整数零。
    /// AddressOf 仅在其目标局部满足同一唯一零链时成立，避免删除真实对象或多定义合并路径。
    /// </summary>
    private static bool IsProvenResidualReceiver(
        IOperand receiver,
        IReadOnlyList<Instruction> allInstructions)
    {
        if (receiver is Immediate { Value: 0 })
            return true;

        var current = receiver switch
        {
            AddressOf { Target: LocalVariable addressed } => addressed,
            LocalVariable local => local,
            _ => null,
        };
        if (current == null)
            return false;

        var visited = new HashSet<LocalVariable>();
        while (visited.Add(current))
        {
            var definitions = allInstructions
                .Where(candidate => ReferenceEquals(candidate.Destination, current))
                .Take(2)
                .ToList();
            if (definitions.Count != 1
                || definitions[0] is not { OpCode: OpCode.Move, Operands.Count: 2 })
                return false;

            switch (definitions[0].Operands[1])
            {
                case Immediate { Value: 0 }:
                    return true;
                case LocalVariable source:
                    current = source;
                    break;
                default:
                    return false;
            }
        }

        return false;
    }

    /// <summary>
    /// 对当前 CFG 执行一轮唯一恢复规则；外层固定点迭代处理共享尾消除后新暴露的标准菱形。
    /// </summary>
    private static int RunRecoveryPass(
        ISILControlFlowGraph graph,
        HashSet<Block> originalSharedFastTails,
        bool compactArrayAccessOnly)
    {
        var recovered = 0;
        foreach (var slowBlock in graph.Blocks.ToList())
        {
            var sharedSlowRecovered = 0;
            while (graph.Blocks.Contains(slowBlock)
                   && TryRecoverSharedSlowTail(
                       graph,
                       slowBlock,
                       originalSharedFastTails,
                       compactArrayAccessOnly))
                sharedSlowRecovered++;
            if (sharedSlowRecovered > 0)
            {
                recovered += sharedSlowRecovered;
                continue;
            }

            if (!compactArrayAccessOnly
                && TryRecoverSharedFastTail(graph, slowBlock, originalSharedFastTails))
            {
                recovered++;
                continue;
            }

            if (!TryMatchSlowPath(
                    slowBlock,
                    out var slowCall,
                    out var addWithResize,
                    out var receiver,
                    out var value,
                    out var slowTail))
                continue;

            if (slowBlock.Predecessors is not [var head])
                continue;
            if (!TryGetMergeBlock(graph, slowBlock, out var merge))
                continue;
            if (!TryMatchHead(
                    head,
                    slowBlock,
                    receiver,
                    out var rewriteHead,
                    out var headPath,
                    out var rewriteStart,
                    out var items,
                    out var sizeState,
                    out var versionResult,
                    out var versionSource,
                    out var preservedHeadBusiness))
                continue;
            if (!TryGetFastRoute(
                    head,
                    slowBlock,
                    merge,
                    originalSharedFastTails,
                    out var fastEntry,
                    out var fastBody,
                    out var fastPrefix))
                continue;
            if (!TryMatchFastPath(
                    graph,
                    fastBody,
                    fastPrefix,
                    merge,
                    receiver,
                    items,
                    sizeState,
                    versionResult,
                    versionSource,
                    value,
                    slowTail,
                    addWithResize.AppContext,
                    addWithResize.TypeGenericParameters.Single(),
                    compactArrayAccessOnly,
                    out var publicValue,
                    out var preservedSlowTail))
                continue;
            if (!TryCreatePublicAddTarget(addWithResize, out var addTarget))
                continue;

            Rewrite(
                graph,
                rewriteHead,
                headPath,
                fastEntry,
                fastBody,
                slowBlock,
                merge,
                rewriteStart,
                slowCall,
                preservedSlowTail,
                preservedHeadBusiness,
                addTarget,
                receiver,
                publicValue);
            recovered++;
        }

        return recovered;
    }

    /// <summary>
    /// 恢复多个容量分支同时汇入共享快尾与共享慢调用块的 ARM64 形态。
    /// </summary>
    /// <remarks>
    /// 每个分支在快边构造数组地址与元素位模式，在慢边把只读常量载入共享参数；
    /// 两边各自汇合后再更新同一组 size/version 状态。改写按分支逐项进行，
    /// 只在最后一个前驱移除后才删除共享尾，避免破坏尚未恢复的同组元素。
    /// </remarks>
    private static bool TryRecoverSharedSlowTail(
        ISILControlFlowGraph graph,
        Block slowBlock,
        HashSet<Block> originalSharedFastTails,
        bool compactArrayAccessOnly)
    {
        if (!TryMatchSlowPath(
                slowBlock,
                out var slowCall,
                out var addWithResize,
                out var receiver,
                out var sharedSlowValue,
                out var slowTail)
            || sharedSlowValue is not LocalVariable
            || slowBlock.Predecessors.Count == 0
            || !TryGetMergeBlock(graph, slowBlock, out var merge)
            || !TryCreatePublicAddTarget(addWithResize, out var addTarget))
            return false;

        foreach (var slowEntry in slowBlock.Predecessors.ToList())
        {
            if (!TryMatchSharedSlowValueEntry(
                    slowEntry,
                    slowBlock,
                    sharedSlowValue,
                    out var branchSlowValue))
            {
                Logger.VerboseNewline(
                    $"ListAdd共享慢尾拒绝：慢入口 b{slowEntry.ID} 未形成对共享参数 {sharedSlowValue} 的唯一载入。");
                continue;
            }

            if (slowEntry.Predecessors is not [var head])
            {
                Logger.VerboseNewline(
                    $"ListAdd共享慢尾拒绝：慢入口 b{slowEntry.ID} 的容量头前驱数={slowEntry.Predecessors.Count}。");
                continue;
            }

            if (!TryMatchHead(
                    head,
                    slowEntry,
                    receiver,
                    out var rewriteHead,
                    out var headPath,
                    out var rewriteStart,
                    out var items,
                    out var sizeState,
                    out var versionResult,
                    out var versionSource,
                    out var preservedHeadBusiness))
            {
                Logger.VerboseNewline(
                    $"ListAdd共享慢尾拒绝：容量头 b{head.ID} 的 items/size/version 状态后缀未闭合。");
                continue;
            }

            if (!TryGetFastRoute(
                    head,
                    slowEntry,
                    merge,
                    originalSharedFastTails,
                    out var fastEntry,
                    out var fastBody,
                    out var fastPrefix))
            {
                Logger.VerboseNewline(
                    $"ListAdd共享慢尾拒绝：容量头 b{head.ID} 的快入口与共享快尾拓扑未闭合。");
                continue;
            }

            if (!TryMatchFastPath(
                    graph,
                    fastBody,
                    fastPrefix,
                    merge,
                    receiver,
                    items,
                    sizeState,
                    versionResult,
                    versionSource,
                    branchSlowValue,
                    slowTail,
                    addWithResize.AppContext,
                    addWithResize.TypeGenericParameters.Single(),
                    compactArrayAccessOnly,
                    out var publicValue,
                    out var preservedSlowTail))
            {
                Logger.VerboseNewline(
                    $"ListAdd共享慢尾拒绝：容量头 b{head.ID} 的快路径语义未闭合。");
                continue;
            }

            RewriteSharedSlowTail(
                graph,
                rewriteHead,
                headPath,
                fastEntry,
                fastBody,
                slowEntry,
                slowBlock,
                merge,
                rewriteStart,
                slowCall,
                preservedSlowTail,
                preservedHeadBusiness,
                addTarget,
                receiver,
                publicValue);
            return true;
        }

        return false;
    }

    private static bool TryMatchSharedSlowValueEntry(
        Block entry,
        Block slowBlock,
        IOperand sharedSlowValue,
        out IOperand branchValue)
    {
        branchValue = null!;
        if (entry.Successors is not [var successor]
            || !ReferenceEquals(successor, slowBlock))
            return false;

        var instructions = PatternInstructions(entry);
        if (instructions.LastOrDefault() is { OpCode: OpCode.Jump, Operands: [Block target] })
        {
            if (!ReferenceEquals(target, slowBlock))
                return false;
            instructions = instructions.Take(instructions.Count - 1).ToList();
        }

        if (instructions is not
            [
                {
                    OpCode: OpCode.Move,
                    Operands: [var destination, var source],
                }
            ]
            || !ReferenceEquals(destination, sharedSlowValue))
            return false;

        branchValue = source;
        return true;
    }

    /// <summary>
    /// 恢复多个容量分支把元素写入汇聚到同一个原生快速尾块的 ARM64 形态。
    /// </summary>
    /// <remarks>
    /// 该形态先在每条快边写入元素载体，再由共享块统一执行数组地址计算、大小递增和元素写入；
    /// 慢边则直接调用 <c>AddWithResize</c>。恢复必须同时证明公开接收者别名、两级空检查、
    /// 版本递增、容量判断、共享尾的原生布局以及快慢值完全等价，任一条件漂移都保留原图。
    /// </remarks>
    private static bool TryRecoverSharedFastTail(
        ISILControlFlowGraph graph,
        Block slowBlock,
        HashSet<Block> originalSharedFastTails)
    {
        if (!TryMatchSharedSlowPath(
                slowBlock,
                out var slowCall,
                out var addWithResize,
                out var publicReceiver,
                out var slowValue,
                out var slowCarrierTail))
            return false;
        if (slowBlock.Predecessors is not [var capacityHead])
            return false;
        if (!TryGetMergeBlock(graph, slowBlock, out var merge))
            return false;
        if (!TryGetSharedFastBlocks(
                capacityHead,
                slowBlock,
                merge,
                originalSharedFastTails,
                out var stagingBlock,
                out var sharedFastTail))
            return false;
        if (!TryMatchSharedFastTail(
                sharedFastTail,
                merge,
                out var sharedPattern))
            return false;
        if (!TryMatchSharedCapacityHead(
                capacityHead,
                slowBlock,
                publicReceiver,
                sharedPattern))
            return false;
        if (!TryMatchSharedStatePrefix(
                capacityHead,
                publicReceiver,
                sharedPattern.Items,
                out var rewriteHead,
                out var headPath,
                out var rewriteStart))
            return false;
        if (!TryMatchSharedStagingPath(
                graph,
                stagingBlock,
                sharedFastTail,
                merge,
                sharedPattern.StagedValue,
                slowValue,
                slowCarrierTail,
                addWithResize.AppContext,
                addWithResize.TypeGenericParameters.Single(),
                out var publicValue,
                out var preservedCarrierTail))
            return false;
        if (!TryCreatePublicAddTarget(addWithResize, out var addTarget))
            return false;

        RewriteSharedFastTail(
            graph,
            rewriteHead,
            headPath,
            stagingBlock,
            slowBlock,
            sharedFastTail,
            merge,
            rewriteStart,
            slowCall,
            preservedCarrierTail,
            addTarget,
            publicReceiver,
            publicValue);
        return true;
    }

    private static bool TryMatchSharedSlowPath(
        Block block,
        out Instruction call,
        out ConcreteGenericMethodAnalysisContext target,
        out IOperand receiver,
        out IOperand value,
        out List<Instruction> carrierTail)
    {
        call = null!;
        target = null!;
        receiver = null!;
        value = null!;
        carrierTail = [];

        var instructions = PatternInstructions(block);
        if (instructions.Count < 2
            || instructions[0] is not
            {
                OpCode: OpCode.CallVoid,
                Operands:
                [
                    ConcreteGenericMethodAnalysisContext method,
                    var candidateReceiver,
                    var candidateValue,
                ],
            } candidateCall
            || method.Name != "AddWithResize"
            || method.BaseMethodContext.DeclaringType?.FullName != "System.Collections.Generic.List`1"
            || candidateReceiver is not (LocalVariable or FieldReference)
            || instructions[^1] is not { OpCode: OpCode.Jump, Operands: [Block] }
            || instructions.Skip(1).Take(instructions.Count - 2).Any(instruction =>
                instruction is not { OpCode: OpCode.Move }))
            return false;

        call = candidateCall;
        target = method;
        receiver = candidateReceiver;
        value = candidateValue;
        carrierTail = instructions.Skip(1).Take(instructions.Count - 2).ToList();
        return true;
    }

    private static bool TryGetSharedFastBlocks(
        Block capacityHead,
        Block slowBlock,
        Block merge,
        HashSet<Block> originalSharedFastTails,
        out Block stagingBlock,
        out Block sharedFastTail)
    {
        stagingBlock = null!;
        sharedFastTail = null!;
        if (capacityHead.Successors.Count != 2)
            return false;

        stagingBlock = capacityHead.Successors.SingleOrDefault(block =>
            !ReferenceEquals(block, slowBlock))!;
        if (stagingBlock == null
            || stagingBlock.Predecessors is not [var stagingPredecessor]
            || !ReferenceEquals(stagingPredecessor, capacityHead)
            || stagingBlock.Successors is not [var candidateTail]
            || !originalSharedFastTails.Contains(candidateTail)
            || candidateTail.Successors is not [var candidateMerge]
            || !ReferenceEquals(candidateMerge, merge))
            return false;

        sharedFastTail = candidateTail;
        return true;
    }

    private static bool TryMatchSharedFastTail(
        Block block,
        Block merge,
        out SharedFastTailPattern pattern)
    {
        pattern = null!;
        var instructions = PatternInstructions(block);
        if (instructions.Count != 9
            || instructions[0] is not
            {
                OpCode: OpCode.And,
                Operands: [LocalVariable masked, var sizeState, Immediate { Value: 0xFFFFFFFFL }],
            }
            || instructions[1] is not
            {
                OpCode: OpCode.Xor,
                Operands: [LocalVariable biased, var maskSource, Immediate { Value: 0x80000000L }],
            }
            || instructions[2] is not
            {
                OpCode: OpCode.Subtract,
                Operands: [LocalVariable normalized, var biasedSource, Immediate { Value: 0x80000000L }],
            }
            || instructions[3] is not
            {
                OpCode: OpCode.ShiftLeft,
                Operands: [LocalVariable elementOffset, var normalizedSource, Immediate scale],
            }
            || instructions[4] is not
            {
                OpCode: OpCode.Add,
                Operands: [LocalVariable elementAddress, var addressLeft, var addressRight],
            }
            || instructions[5] is not
            {
                OpCode: OpCode.Add,
                Operands: [LocalVariable newSize, var sizeAddSource, Immediate { Value: 1 }],
            }
            || instructions[6] is not
            {
                OpCode: OpCode.Move,
                Operands: [MemoryOperand sizeMemory, var sizeWriteSource],
            }
            || instructions[7] is not
            {
                OpCode: OpCode.Move,
                Operands: [MemoryOperand elementMemory, LocalVariable stagedValue],
            }
            || instructions[8] is not { OpCode: OpCode.Jump, Operands: [Block target] }
            || !ReferenceEquals(maskSource, masked)
            || !ReferenceEquals(biasedSource, biased)
            || !ReferenceEquals(normalizedSource, normalized)
            || scale.Value is not (1 or 2 or 3 or 4)
            || !ReferenceEquals(sizeAddSource, sizeState)
            || !ReferenceEquals(sizeWriteSource, newSize)
            || sizeMemory is not { Base: LocalVariable sizeAddress, Index: null, Addend: 0 }
            || elementMemory is not { Base: var storedElementAddress, Index: null, Addend: 0x20 }
            || !ReferenceEquals(storedElementAddress, elementAddress)
            || !ReferenceEquals(target, merge))
            return false;

        LocalVariable items;
        if (ReferenceEquals(addressLeft, elementOffset) && addressRight is LocalVariable rightItems)
            items = rightItems;
        else if (ReferenceEquals(addressRight, elementOffset) && addressLeft is LocalVariable leftItems)
            items = leftItems;
        else
            return false;

        pattern = new SharedFastTailPattern(items, sizeState, sizeAddress, sizeMemory, stagedValue);
        return true;
    }

    private static bool TryMatchSharedCapacityHead(
        Block block,
        Block slowBlock,
        IOperand publicReceiver,
        SharedFastTailPattern pattern)
    {
        var instructions = PatternInstructions(block);
        if (instructions is not
            [
                {
                    OpCode: OpCode.Add,
                    Operands: [var sizeAddress, var addressBase, Immediate { Value: 24 }],
                },
                {
                    OpCode: OpCode.Move,
                    Operands: [var sizeState, MemoryOperand sizeLoad],
                },
                {
                    OpCode: OpCode.CheckGreaterOrEqualUnsigned,
                    Operands: [LocalVariable condition, var checkedSize, ArrayLength length],
                },
                {
                    OpCode: OpCode.ConditionalJump,
                    Operands: [Block target, var branchCondition],
                }
            ]
            || !ReferenceEquals(sizeAddress, pattern.SizeAddress)
            || !AreEquivalentValue(addressBase, publicReceiver)
            || !ReferenceEquals(sizeState, pattern.SizeState)
            || !AreSameMemoryOperand(sizeLoad, pattern.SizeMemory)
            || !(ReferenceEquals(checkedSize, pattern.SizeState)
                 || checkedSize is MemoryOperand checkedMemory
                 && AreSameMemoryOperand(checkedMemory, pattern.SizeMemory))
            || !ReferenceEquals(length.Array, pattern.Items)
            || !ReferenceEquals(condition, branchCondition)
            || !ReferenceEquals(target, slowBlock))
            return false;

        return true;
    }

    private static bool TryMatchSharedStatePrefix(
        Block capacityHead,
        IOperand publicReceiver,
        LocalVariable items,
        out Block rewriteHead,
        out List<Block> headPath,
        out Instruction rewriteStart)
    {
        rewriteHead = null!;
        headPath = [];
        rewriteStart = null!;
        if (capacityHead.Predecessors is not [var itemsBlock]
            || itemsBlock.Predecessors is not [var aliasBlock])
            return false;

        var aliasInstructions = PatternInstructions(aliasBlock);
        var itemsInstructions = PatternInstructions(itemsBlock);
        if (aliasInstructions is not
            [
                { OpCode: OpCode.Move, Operands: [LocalVariable stateReceiver, var receiverSource] } aliasMove,
                { OpCode: OpCode.CheckEqual, Operands: [LocalVariable receiverNull, var checkedReceiver, Immediate { Value: 0 }] },
                { OpCode: OpCode.ConditionalJump, Operands: [Block receiverNullTarget, var receiverNullCondition] },
            ]
            || itemsInstructions is not
            [
                { OpCode: OpCode.Move, Operands: [var loadedItems, FieldReference itemsField] },
                { OpCode: OpCode.Add, Operands: [LocalVariable version, FieldReference versionSource, Immediate { Value: 1 }] },
                { OpCode: OpCode.Move, Operands: [FieldReference versionDestination, var writtenVersion] },
                { OpCode: OpCode.CheckEqual, Operands: [LocalVariable itemsNull, FieldReference checkedItems, Immediate { Value: 0 }] },
                { OpCode: OpCode.ConditionalJump, Operands: [Block itemsNullTarget, var itemsNullCondition] },
            ]
            || !AreEquivalentValue(receiverSource, publicReceiver)
            || !AreEquivalentValue(checkedReceiver, publicReceiver)
            || !ReferenceEquals(receiverNull, receiverNullCondition)
            || !ReferenceEquals(loadedItems, items)
            || !IsField(itemsField, stateReceiver, "_items")
            || !IsField(versionSource, stateReceiver, "_version")
            || !IsField(versionDestination, stateReceiver, "_version")
            || !ReferenceEquals(version, writtenVersion)
            || !IsField(checkedItems, stateReceiver, "_items")
            || !ReferenceEquals(itemsNull, itemsNullCondition)
            || !ReferenceEquals(receiverNullTarget, itemsNullTarget)
            || !ContainsNullReferenceThrow(receiverNullTarget)
            || aliasBlock.Successors.Count != 2
            || !aliasBlock.Successors.Contains(itemsBlock)
            || !aliasBlock.Successors.Contains(receiverNullTarget)
            || itemsBlock.Successors.Count != 2
            || !itemsBlock.Successors.Contains(capacityHead)
            || !itemsBlock.Successors.Contains(itemsNullTarget))
            return false;

        rewriteHead = aliasBlock;
        headPath = [aliasBlock, itemsBlock, capacityHead];
        rewriteStart = aliasMove;
        return true;
    }

    private static bool ContainsNullReferenceThrow(Block block)
        => SemanticInstructions(block).Any(instruction =>
            instruction is { OpCode: OpCode.Throw, Operands: [TypeAnalysisContext type] }
            && type.FullName == "System.NullReferenceException");

    private static bool TryMatchSharedStagingPath(
        ISILControlFlowGraph graph,
        Block stagingBlock,
        Block sharedFastTail,
        Block merge,
        LocalVariable stagedValue,
        IOperand slowValue,
        IReadOnlyList<Instruction> slowCarrierTail,
        ApplicationAnalysisContext appContext,
        TypeAnalysisContext elementType,
        out IOperand publicValue,
        out List<Instruction> preservedCarrierTail)
    {
        publicValue = null!;
        preservedCarrierTail = [];
        var instructions = PatternInstructions(stagingBlock);
        var hasExplicitJump = instructions.LastOrDefault() is
            { OpCode: OpCode.Jump, Operands: [Block target] }
            && ReferenceEquals(target, sharedFastTail);
        var stagingMoves = hasExplicitJump
            ? instructions.Take(instructions.Count - 1).ToList()
            : instructions;
        if (stagingMoves.Count < 1
            || stagingMoves.Any(instruction => instruction is not { OpCode: OpCode.Move }))
            return false;

        var valueMoves = stagingMoves.Where(instruction =>
            instruction is { OpCode: OpCode.Move, Operands: [var destination, _] }
            && ReferenceEquals(destination, stagedValue)).ToList();
        if (valueMoves.Count != 1
            || !TryReconcileElementValue(
                graph,
                valueMoves[0].Operands[1],
                slowValue,
                appContext,
                elementType,
                graph.Instructions.ToList(),
                out publicValue,
                out _))
            return false;

        var fastCarriers = stagingMoves
            .Where(instruction => !ReferenceEquals(instruction, valueMoves[0]))
            .ToList();
        return TryMatchRequiredSharedCarriers(
            fastCarriers,
            slowCarrierTail,
            merge,
            out preservedCarrierTail);
    }

    private static bool TryMatchRequiredSharedCarriers(
        IReadOnlyList<Instruction> fastCarriers,
        IReadOnlyList<Instruction> slowCarriers,
        Block merge,
        out List<Instruction> preservedSlowCarriers)
    {
        preservedSlowCarriers = [];
        if (fastCarriers.Concat(slowCarriers).Any(instruction =>
                instruction is not { OpCode: OpCode.Move, Operands: [LocalVariable, _] }))
            return false;

        var requiredDestinations = fastCarriers.Concat(slowCarriers)
            .Select(instruction => (LocalVariable)instruction.Operands[0])
            .Where(destination => IsReadBeforeDefinitionFromMerge(merge, destination))
            .Distinct()
            .ToList();
        foreach (var destination in requiredDestinations)
        {
            var fast = fastCarriers.Where(instruction =>
                ReferenceEquals(instruction.Operands[0], destination)).ToList();
            var slow = slowCarriers.Where(instruction =>
                ReferenceEquals(instruction.Operands[0], destination)).ToList();
            if (fast.Count != 1
                || slow.Count != 1
                || !AreEquivalentValue(fast[0].Operands[1], slow[0].Operands[1]))
                return false;
            preservedSlowCarriers.Add(slow[0]);
        }

        return true;
    }

    /// <summary>
    /// 判断汇合点之后是否存在“先读后写”路径；后继中只有覆盖写而没有先读时，边载体已经死亡。
    /// </summary>
    private static bool IsReadBeforeDefinitionFromMerge(Block merge, LocalVariable local)
    {
        var pending = new Stack<Block>();
        var visited = new HashSet<Block>();
        pending.Push(merge);

        while (pending.Count > 0)
        {
            var block = pending.Pop();
            if (!visited.Add(block))
                continue;

            var overwritten = false;
            foreach (var instruction in SemanticInstructions(block))
            {
                var destination = instruction.Destination;
                if (instruction.Operands.Any(operand =>
                        !(ReferenceEquals(operand, destination) && ReferenceEquals(operand, local))
                        && ReferencesLocal(operand, local)))
                    return true;

                if (ReferenceEquals(destination, local))
                {
                    overwritten = true;
                    break;
                }

                // Throw/Return 之后附着的原生异常辅助调用不属于同一条托管可达路径。
                if (instruction.OpCode is OpCode.Throw or OpCode.Return)
                {
                    overwritten = true;
                    break;
                }
            }

            if (overwritten)
                continue;
            foreach (var successor in block.Successors)
                pending.Push(successor);
        }

        return false;
    }

    private static bool AreEquivalentValue(IOperand left, IOperand right)
        => ReferenceEquals(left, right)
           || left is Immediate leftImmediate
           && right is Immediate rightImmediate
           && leftImmediate.Value == rightImmediate.Value
           || left is ArrayAccess leftArrayAccess
           && right is ArrayAccess rightArrayAccess
           && ReferenceEquals(leftArrayAccess.Array, rightArrayAccess.Array)
           && AreEquivalentValue(leftArrayAccess.Index, rightArrayAccess.Index)
           || left is FieldReference leftField
           && right is FieldReference rightField
           && AreSameFieldRead(leftField, rightField)
           || left is MemoryOperand leftMemory
           && right is MemoryOperand rightMemory
           && AreSameMemoryOperand(leftMemory, rightMemory);

    private static bool AreSameMemoryOperand(MemoryOperand left, MemoryOperand right)
        => left.Addend == right.Addend
           && AreEquivalentNullableOperand(left.Base, right.Base)
           && AreEquivalentNullableOperand(left.Index, right.Index);

    private static bool AreEquivalentNullableOperand(IOperand? left, IOperand? right)
        => left == null && right == null
           || left != null && right != null && AreEquivalentValue(left, right);

    private static void RewriteSharedFastTail(
        ISILControlFlowGraph graph,
        Block rewriteHead,
        IReadOnlyList<Block> headPath,
        Block stagingBlock,
        Block slowBlock,
        Block sharedFastTail,
        Block merge,
        Instruction rewriteStart,
        Instruction slowCall,
        IReadOnlyList<Instruction> preservedCarrierTail,
        ConcreteGenericMethodAnalysisContext addTarget,
        IOperand receiver,
        IOperand value)
    {
        var firstRemovedIndex = rewriteHead.Instructions.IndexOf(rewriteStart);
        rewriteHead.Instructions.RemoveRange(firstRemovedIndex, rewriteHead.Instructions.Count - firstRemovedIndex);
        rewriteHead.Instructions.Add(new Instruction(slowCall.Index, OpCode.CallVoid, addTarget, receiver, value));
        rewriteHead.Instructions.AddRange(preservedCarrierTail);

        foreach (var pathBlock in headPath.Skip(1).ToList())
            Detach(graph, pathBlock);
        Detach(graph, stagingBlock);
        Detach(graph, slowBlock);
        if (sharedFastTail.Predecessors.Count == 0)
            Detach(graph, sharedFastTail);

        foreach (var successor in rewriteHead.Successors.ToList())
            successor.Predecessors.Remove(rewriteHead);
        rewriteHead.Successors.Clear();
        rewriteHead.Successors.Add(merge);
        if (!merge.Predecessors.Contains(rewriteHead))
            merge.Predecessors.Add(rewriteHead);
        rewriteHead.CalculateBlockType();
    }

    private static void RewriteSharedSlowTail(
        ISILControlFlowGraph graph,
        Block rewriteHead,
        IReadOnlyList<Block> headPath,
        Block fastEntry,
        Block fastBody,
        Block slowEntry,
        Block slowBlock,
        Block merge,
        Instruction rewriteStart,
        Instruction slowCall,
        IReadOnlyList<Instruction> preservedSlowTail,
        IReadOnlyList<Instruction> preservedHeadBusiness,
        ConcreteGenericMethodAnalysisContext addTarget,
        LocalVariable receiver,
        IOperand value)
    {
        var firstRemovedIndex = rewriteHead.Instructions.IndexOf(rewriteStart);
        rewriteHead.Instructions.RemoveRange(firstRemovedIndex, rewriteHead.Instructions.Count - firstRemovedIndex);
        rewriteHead.Instructions.AddRange(preservedHeadBusiness);
        rewriteHead.Instructions.Add(new Instruction(slowCall.Index, OpCode.CallVoid, addTarget, receiver, value));
        rewriteHead.Instructions.AddRange(preservedSlowTail.Select(CloneInstruction));

        foreach (var pathBlock in headPath.Skip(1).ToList())
            Detach(graph, pathBlock);
        Detach(graph, fastEntry);
        Detach(graph, slowEntry);
        var removedSharedFastTail = false;
        if (!ReferenceEquals(fastBody, fastEntry) && fastBody.Predecessors.Count == 0)
        {
            Detach(graph, fastBody);
            removedSharedFastTail = true;
        }
        var removedSharedSlowTail = false;
        if (slowBlock.Predecessors.Count == 0)
        {
            Detach(graph, slowBlock);
            removedSharedSlowTail = true;
        }

        foreach (var successor in rewriteHead.Successors.ToList())
            successor.Predecessors.Remove(rewriteHead);
        rewriteHead.Successors.Clear();
        rewriteHead.Successors.Add(merge);
        if (!merge.Predecessors.Contains(rewriteHead))
            merge.Predecessors.Add(rewriteHead);
        rewriteHead.CalculateBlockType();
        if (removedSharedFastTail && removedSharedSlowTail)
            TryHoistIdenticalPredecessorTail(merge, preservedSlowTail);
    }

    /// <summary>
    /// 克隆共享慢尾指令对象，同时保留操作数的 SSA 引用身份。
    /// </summary>
    private static Instruction CloneInstruction(Instruction instruction)
        => new(instruction.Index, instruction.OpCode, [.. instruction.Operands]);

    /// <summary>
    /// 将所有汇合前驱末尾完全相同的集合状态刷新上提到汇合块，避免多路径重复计算。
    /// </summary>
    private static bool TryHoistIdenticalPredecessorTail(
        Block merge,
        IReadOnlyList<Instruction> tail)
    {
        if (tail.Count == 0 || merge.Predecessors.Count < 2)
            return false;

        var predecessorTails = new List<List<Instruction>>(merge.Predecessors.Count);
        foreach (var predecessor in merge.Predecessors)
        {
            var instructions = PatternInstructions(predecessor);
            if (instructions.Count < tail.Count)
                return false;
            var candidate = instructions.Skip(instructions.Count - tail.Count).ToList();
            if (!HaveIdenticalInstructionIdentity(candidate, tail))
                return false;
            predecessorTails.Add(candidate);
        }

        for (var predecessorIndex = 0; predecessorIndex < merge.Predecessors.Count; predecessorIndex++)
        {
            var predecessor = merge.Predecessors[predecessorIndex];
            var candidate = predecessorTails[predecessorIndex];
            foreach (var instruction in candidate)
                predecessor.Instructions.Remove(instruction);
            predecessor.CalculateBlockType();
        }

        merge.Instructions.InsertRange(0, tail.Select(CloneInstruction));
        merge.CalculateBlockType();
        return true;
    }

    private static bool HaveIdenticalInstructionIdentity(
        IReadOnlyList<Instruction> left,
        IReadOnlyList<Instruction> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var index = 0; index < left.Count; index++)
        {
            if (left[index].OpCode != right[index].OpCode
                || left[index].Operands.Count != right[index].Operands.Count)
                return false;
            for (var operandIndex = 0; operandIndex < left[index].Operands.Count; operandIndex++)
            {
                if (!ReferenceEquals(left[index].Operands[operandIndex], right[index].Operands[operandIndex]))
                    return false;
            }
        }

        return true;
    }

    private sealed record SharedFastTailPattern(
        LocalVariable Items,
        IOperand SizeState,
        LocalVariable SizeAddress,
        MemoryOperand SizeMemory,
        LocalVariable StagedValue);

    /// <summary>
    /// 容量不足分支有时会直接以AddWithResize结束方法，而快速分支跳到同一Return块。
    /// 此时慢块的图后继是Exit，但它的唯一托管Return仍是公开Add应接回的汇合块。
    /// </summary>
    private static bool TryGetMergeBlock(
        ISILControlFlowGraph graph,
        Block slowBlock,
        out Block merge)
    {
        merge = null!;
        if (slowBlock.Successors is not [var successor])
            return false;
        if (!ReferenceEquals(successor, graph.ExitBlock))
        {
            merge = successor;
            return true;
        }

        var slowInstructions = SemanticInstructions(slowBlock);
        if (slowInstructions is not
            [
                { OpCode: OpCode.CallVoid, Operands: [MethodAnalysisContext { Name: "AddWithResize" }, ..] },
                { OpCode: OpCode.Return, Operands.Count: 0 }
            ])
            return false;

        var candidates = graph.ExitBlock.Predecessors.Where(block =>
            !ReferenceEquals(block, slowBlock)
            && SemanticInstructions(block) is
            [{ OpCode: OpCode.Return, Operands.Count: 0 }]).ToList();
        if (candidates.Count != 1)
            return false;

        merge = candidates[0];
        return true;
    }

    private static bool TryMatchSlowPath(
        Block block,
        out Instruction call,
        out ConcreteGenericMethodAnalysisContext target,
        out LocalVariable receiver,
        out IOperand value,
        out List<Instruction> tail)
    {
        call = null!;
        target = null!;
        receiver = null!;
        value = null!;
        tail = [];

        var instructions = SemanticInstructions(block);
        var callIndex = instructions.FindIndex(instruction =>
            instruction is { OpCode: OpCode.CallVoid, Operands.Count: 3 }
            && instruction.Operands[0] is MethodAnalysisContext { Name: "AddWithResize" });
        var trailingInstructions = callIndex < 0
            ? []
            : instructions
                .Skip(callIndex + 1)
                .Where(instruction => !IsIgnorableRuntimeMetadataMove(instruction))
                .ToList();
        if (callIndex < 0
            || instructions.Count(instruction =>
                instruction.IsCall
                && instruction.Operands.FirstOrDefault() is MethodAnalysisContext { Name: "AddWithResize" }) != 1
            || instructions[callIndex] is not { OpCode: OpCode.CallVoid } candidate
            || candidate.Operands.Count != 3
            || candidate.Operands[0] is not ConcreteGenericMethodAnalysisContext method
            || candidate.Operands[1] is not LocalVariable list
            || method.Name != "AddWithResize"
            || method.BaseMethodContext.DeclaringType?.FullName != "System.Collections.Generic.List`1"
            || !IsValidSlowValuePrefix(
                instructions.Take(callIndex).ToList(),
                candidate.Operands[2])
            || !IsValidSlowTail(block, trailingInstructions))
            return false;

        call = candidate;
        target = method;
        receiver = list;
        value = candidate.Operands[2];
        tail = trailingInstructions
            .Where(instruction => instruction.OpCode is not (OpCode.Return or OpCode.Jump))
            .ToList();
        return true;
    }

    /// <summary>
    /// 验证慢路径调用前只包含其 HFA 实参的封闭常量构造链。
    /// </summary>
    private static bool IsValidSlowValuePrefix(
        IReadOnlyList<Instruction> prefix,
        IOperand value)
    {
        var semanticPrefix = prefix
            .Where(instruction => !IsIgnorableRuntimeMetadataMove(instruction))
            .ToList();
        if (semanticPrefix.Count == 0)
            return true;
        if (value is not HomogeneousFloatingAggregateArgument aggregate)
            return false;

        var construction = new HashSet<Instruction>();
        foreach (var component in aggregate.Components)
        {
            if (!TryCollectHfaComponentConstruction(
                    semanticPrefix,
                    component,
                    new HashSet<LocalVariable>(),
                    construction))
                return false;
        }

        return semanticPrefix.All(construction.Contains);
    }

    private static bool TryCollectHfaComponentConstruction(
        IReadOnlyList<Instruction> prefix,
        IOperand operand,
        ISet<LocalVariable> active,
        ISet<Instruction> construction)
    {
        if (operand is FloatLiteral or DoubleLiteral)
            return true;
        if (operand is not LocalVariable local || !active.Add(local))
            return false;

        try
        {
            var definitions = prefix
                .Where(instruction => ReferenceEquals(instruction.Destination, local))
                .Take(2)
                .ToList();
            if (definitions.Count != 1)
                return false;

            var definition = definitions[0];
            if (definition is { OpCode: OpCode.Move, Operands: [_, var source] })
            {
                construction.Add(definition);
                return TryCollectHfaComponentConstruction(
                    prefix,
                    source,
                    active,
                    construction);
            }

            if (definition is not
                {
                    OpCode: OpCode.ReinterpretIntegerBitsAsFloat,
                    Operands: [_, var integerBits, Immediate width],
                }
                || width.Value is not (32 or 64))
                return false;

            var integerConstruction = new List<Instruction>();
            if (!TryEvaluateUnsignedConstant(
                    prefix,
                    integerBits,
                    unchecked((int)width.Value),
                    new HashSet<LocalVariable>(),
                    integerConstruction,
                    out _))
                return false;

            construction.Add(definition);
            foreach (var instruction in integerConstruction)
                construction.Add(instruction);
            return true;
        }
        finally
        {
            active.Remove(local);
        }
    }

    /// <summary>
    /// 验证扩容调用后的载体尾部；显式跳转必须精确指向慢块唯一图后继。
    /// </summary>
    private static bool IsValidSlowTail(Block block, IReadOnlyList<Instruction> instructions)
    {
        if (instructions.All(IsPotentialSlowTailInstruction))
            return true;

        if (instructions.Count == 0
            || !instructions.Take(instructions.Count - 1).All(IsPotentialSlowTailInstruction))
            return false;

        return instructions[^1] is { OpCode: OpCode.Return, Operands.Count: 0 }
               || instructions[^1] is { OpCode: OpCode.Jump, Operands: [Block target] }
               && block.Successors is [var successor]
               && ReferenceEquals(target, successor);
    }

    /// <summary>
    /// 慢边预筛选只额外放行下一项版本递增的固定加法；接收者、字段回写和快边等价性稍后统一验证。
    /// </summary>
    private static bool IsPotentialSlowTailInstruction(Instruction instruction)
        => instruction.OpCode == OpCode.Move
           || instruction is
           {
               OpCode: OpCode.Add,
               Operands:
               [
                   LocalVariable,
                   FieldReference { Field.Name: "_version" },
                   Immediate { Value: 1 },
               ],
           };

    private static bool TryMatchHead(
        Block head,
        Block slowBlock,
        LocalVariable receiver,
        out Block rewriteHead,
        out List<Block> headPath,
        out Instruction rewriteStart,
        out LocalVariable items,
        out IOperand sizeState,
        out LocalVariable versionResult,
        out IOperand versionSource,
        out List<Instruction> preservedHeadBusiness)
    {
        rewriteHead = null!;
        headPath = [];
        rewriteStart = null!;
        items = null!;
        sizeState = null!;
        versionResult = null!;
        versionSource = null!;
        preservedHeadBusiness = [];

        // 首个 Add 直接读取字段，连续 Add 则会复用上一容量菱形在汇合边写入的
        // size/version 局部载体；ARM64 还允许把 items 读取排在版本加法之后。
        // 逐个扩大后缀窗口，只在全部指令都属于该标准状态机时接受。
        for (var instructionCount = 5; instructionCount <= 32; instructionCount++)
        {
            if (!TryCollectHeadSuffix(head, instructionCount, out var suffix, out var candidatePath))
                continue;
            if (!TryMatchHeadSuffix(
                    suffix,
                    slowBlock,
                    receiver,
                    out _,
                    out var loadedItems,
                    out var candidateSizeState,
                    out var candidateVersionResult,
                    out var candidateVersionSource,
                    out var candidateBusiness))
                continue;

            rewriteHead = suffix[0].Block;
            var rewriteBlockIndex = candidatePath.IndexOf(rewriteHead);
            headPath = candidatePath.GetRange(
                rewriteBlockIndex,
                candidatePath.Count - rewriteBlockIndex);
            rewriteStart = suffix[0].Instruction;
            items = loadedItems;
            sizeState = candidateSizeState;
            versionResult = candidateVersionResult;
            versionSource = candidateVersionSource;
            preservedHeadBusiness = candidateBusiness;
            return true;
        }

        return false;
    }

    private static bool TryMatchHeadSuffix(
        IReadOnlyList<(Block Block, Instruction Instruction)> suffix,
        Block slowBlock,
        LocalVariable receiver,
        out Instruction itemsLoad,
        out LocalVariable items,
        out IOperand sizeState,
        out LocalVariable versionResult,
        out IOperand versionSource,
        out List<Instruction> preservedHeadBusiness)
    {
        itemsLoad = null!;
        items = null!;
        sizeState = null!;
        versionResult = null!;
        versionSource = null!;
        preservedHeadBusiness = [];

        if (suffix.Count < 5
            || suffix[^1].Instruction is not
            {
                OpCode: OpCode.ConditionalJump,
                Operands: [Block target, var branchCondition]
            }
            || suffix[^2].Instruction is not
            {
                Operands: [LocalVariable condition, var checkedSize, ArrayLength length]
            } capacityCheck
            || capacityCheck.OpCode is not (OpCode.CheckGreaterOrEqualUnsigned or OpCode.CheckLessUnsigned)
            || !ReferenceEquals(condition, branchCondition)
            || !IsCapacityBranchTarget(
                suffix[^1].Block,
                slowBlock,
                target,
                capacityCheck.OpCode))
            return false;

        var prefix = suffix.Take(suffix.Count - 2).Select(entry => entry.Instruction).ToList();
        var itemLoads = prefix.Where(instruction =>
            instruction is { OpCode: OpCode.Move, Operands: [LocalVariable, FieldReference field] }
            && IsField(field, receiver, "_items")).ToList();
        var versionAdds = prefix.Where(instruction =>
            instruction is
            {
                OpCode: OpCode.Add,
                Operands: [LocalVariable, var candidateSource, Immediate { Value: 1 }]
            }
            && IsStateSource(prefix, instruction, candidateSource, receiver, "_version")).ToList();
        if (itemLoads.Count != 1
            || versionAdds.Count != 1
            || itemLoads[0].Operands[0] is not LocalVariable loadedItems
            || versionAdds[0].Operands[0] is not LocalVariable version)
            return false;
        var candidateOldVersionSource = versionAdds[0].Operands[1];

        var versionWrites = prefix.Where(instruction =>
            instruction is { OpCode: OpCode.Move, Operands: [FieldReference field, var source] }
            && IsField(field, receiver, "_version")
            && ReferenceEquals(source, version)).ToList();
        if (versionWrites.Count != 1
            || prefix.IndexOf(versionAdds[0]) >= prefix.IndexOf(versionWrites[0])
            || !ReferenceEquals(loadedItems, length.Array)
            || !IsStateSource(prefix, suffix[^2].Instruction, checkedSize, receiver, "_size"))
            return false;

        var allowed = new List<Instruction> { itemLoads[0], versionAdds[0], versionWrites[0] };
        if (!CollectStateLoad(prefix, versionAdds[0], candidateOldVersionSource, receiver, "_version", allowed)
            || !CollectStateLoad(prefix, suffix[^2].Instruction, checkedSize, receiver, "_size", allowed)
            )
            return false;

        // 容量比较可以直接重读 _size/Count，而快路索引复用稍早读取的局部载体。
        // 只有唯一、同接收者且位于容量比较前的读取才作为快路状态身份。
        var parallelSizeLoads = prefix.Where(instruction =>
            instruction is { OpCode: OpCode.Move, Operands: [LocalVariable, var source] }
            && IsDirectStateOperand(source, receiver, "_size")
            && IsBefore(prefix, instruction, suffix[^2].Instruction)
            && !allowed.Contains(instruction)).ToList();
        if (parallelSizeLoads.Count > 1
            || parallelSizeLoads.Count == 1
            && parallelSizeLoads[0].Operands[0] is not LocalVariable)
            return false;
        var candidateSizeState = checkedSize;
        if (parallelSizeLoads is [var parallelSizeLoad])
        {
            candidateSizeState = parallelSizeLoad.Operands[0];
            allowed.Add(parallelSizeLoad);
        }
        if (!CollectItemsNullGuards(prefix, receiver, versionWrites[0], allowed))
            return false;

        var itemsLoadIndex = prefix.IndexOf(itemLoads[0]);
        var business = prefix.Where(instruction => !allowed.Contains(instruction)).ToList();
        if (business.Any(instruction =>
                prefix.IndexOf(instruction) <= itemsLoadIndex
                || !IsPreservableHeadBusinessInstruction(
                    instruction,
                    receiver,
                    loadedItems,
                    version,
                    condition)))
            return false;

        itemsLoad = itemLoads[0];
        items = loadedItems;
        sizeState = candidateSizeState;
        versionResult = version;
        versionSource = candidateOldVersionSource;
        preservedHeadBusiness = business;
        return true;
    }

    /// <summary>
    /// 验证容量分支方向：标准 <c>size &gt;= length</c> 跳向慢边，
    /// 等价反向 <c>size &lt; length</c> 则跳向快边。
    /// </summary>
    private static bool IsCapacityBranchTarget(
        Block head,
        Block slowBlock,
        Block target,
        OpCode comparison)
    {
        if (head.Successors.Count != 2
            || !head.Successors.Contains(slowBlock)
            || !head.Successors.Contains(target))
            return false;

        return comparison == OpCode.CheckGreaterOrEqualUnsigned
            ? ReferenceEquals(target, slowBlock)
            : !ReferenceEquals(target, slowBlock);
    }

    /// <summary>
    /// 搬运 items 读取之后、容量比较之前的独立计算；它不得读取或写入集合接收者、
    /// 容量载体和分支条件，也不得包含调用或控制流。ARM64 可在 _version 回写后载入
    /// 与集合无关的迭代器或元数据载体；公开 Add 合并时将它们稳定地保留在调用之前。
    /// </summary>
    private static bool IsPreservableHeadBusinessInstruction(
        Instruction instruction,
        LocalVariable receiver,
        LocalVariable items,
        LocalVariable version,
        LocalVariable condition)
    {
        if (instruction.IsCall
            || instruction.OpCode is OpCode.Return
                or OpCode.Jump
                or OpCode.IndirectJump
                or OpCode.ConditionalJump
                or OpCode.Throw)
            return false;

        return instruction.Operands.All(operand =>
            !ReferencesLocal(operand, receiver)
            && !ReferencesLocal(operand, items)
            && !ReferencesLocal(operand, version)
            && !ReferencesLocal(operand, condition));
    }

    private static bool ReferencesLocal(IOperand operand, LocalVariable local)
        => ReferenceEquals(operand, local)
           || operand is FieldReference field && ReferenceEquals(field.Local, local)
           || operand is MemoryOperand memory
           && (memory.Base is not null && ReferencesLocal(memory.Base, local)
               || memory.Index is not null && ReferencesLocal(memory.Index, local))
           || operand is ArrayLength length && ReferencesLocal(length.Array, local)
           || operand is ArrayAccess access && ReferencesLocal(access.Array, local);

    private static bool IsStateSource(
        IReadOnlyList<Instruction> instructions,
        Instruction use,
        IOperand source,
        LocalVariable receiver,
        string fieldName)
    {
        if (IsPublicListSize(source, receiver, fieldName))
            return true;
        if (source is FieldReference direct)
            return IsField(direct, receiver, fieldName);
        if (source is not LocalVariable local)
            return false;

        return instructions.Count(instruction =>
            instruction is { OpCode: OpCode.Move, Operands: [var destination, FieldReference field] }
            && ReferenceEquals(destination, local)
            && IsField(field, receiver, fieldName)
            && IsBefore(instructions, instruction, use)) == 1;
    }

    private static bool CollectStateLoad(
        IReadOnlyList<Instruction> instructions,
        Instruction use,
        IOperand source,
        LocalVariable receiver,
        string fieldName,
        ICollection<Instruction> allowed)
    {
        if (IsPublicListSize(source, receiver, fieldName))
            return true;
        if (source is FieldReference direct)
            return IsField(direct, receiver, fieldName);
        if (source is not LocalVariable local)
            return false;

        var loads = instructions.Where(instruction =>
            instruction is { OpCode: OpCode.Move, Operands: [var destination, FieldReference field] }
            && ReferenceEquals(destination, local)
            && IsField(field, receiver, fieldName)
            && IsBefore(instructions, instruction, use)).ToList();
        if (loads.Count != 1)
            return false;
        allowed.Add(loads[0]);
        return true;
    }

    private static bool TryCollectHeadSuffix(
        Block head,
        int instructionCount,
        out List<(Block Block, Instruction Instruction)> suffix,
        out List<Block> path)
    {
        var reversePath = new List<Block>();
        var semanticCount = 0;
        var current = head;

        while (true)
        {
            reversePath.Add(current);
            semanticCount += PatternInstructions(current).Count;
            if (semanticCount >= instructionCount)
                break;

            if (current.Predecessors is not [var predecessor]
                || !IsLinearOrStrictItemsNullGuardEdge(predecessor, current)
                || predecessor.BlockType is BlockType.Entry or BlockType.Exit)
            {
                suffix = [];
                path = [];
                return false;
            }

            current = predecessor;
        }

        reversePath.Reverse();
        path = reversePath;
        var entries = path
            .SelectMany(block => PatternInstructions(block).Select(instruction => (block, instruction)))
            .ToList();
        suffix = entries.GetRange(entries.Count - instructionCount, instructionCount);
        return true;
    }

    /// <summary>
    /// 允许头部状态链跨过唯一线性边，或跨过失败边严格抛出空引用异常的
    /// <c>_items == null</c> 守卫。
    /// </summary>
    private static bool IsLinearOrStrictItemsNullGuardEdge(Block predecessor, Block continuation)
    {
        if (predecessor.Successors is [var successor])
            return ReferenceEquals(successor, continuation);
        if (predecessor.Successors.Count != 2)
            return false;

        var instructions = PatternInstructions(predecessor);
        if (instructions.Count < 2
            || instructions[^2] is not
            {
                OpCode: OpCode.CheckEqual,
                Operands:
                [
                    LocalVariable condition,
                    FieldReference { Field.Name: "_items" },
                    Immediate { Value: 0 },
                ],
            }
            || instructions[^1] is not
            {
                OpCode: OpCode.ConditionalJump,
                Operands: [Block failure, var branchCondition],
            }
            || !ReferenceEquals(condition, branchCondition)
            || ReferenceEquals(failure, continuation)
            || !predecessor.Successors.Contains(continuation)
            || !predecessor.Successors.Contains(failure))
            return false;

        return IsStrictNullReferenceThrowBlock(failure);
    }

    /// <summary>
    /// 将已由图路径证明的私有 items 空守卫纳入标准 List 状态前缀。
    /// </summary>
    private static bool CollectItemsNullGuards(
        IReadOnlyList<Instruction> prefix,
        LocalVariable receiver,
        Instruction versionWrite,
        ICollection<Instruction> allowed)
    {
        var versionWriteIndex = IndexOf(prefix, versionWrite);
        for (var index = 0; index + 1 < prefix.Count; index++)
        {
            if (prefix[index] is not
                {
                    OpCode: OpCode.CheckEqual,
                    Operands:
                    [
                        LocalVariable condition,
                        FieldReference field,
                        Immediate { Value: 0 },
                    ],
                }
                || prefix[index + 1] is not
                {
                    OpCode: OpCode.ConditionalJump,
                    Operands: [Block failure, var branchCondition],
                }
                || !ReferenceEquals(condition, branchCondition)
                || !IsField(field, receiver, "_items")
                || !IsStrictNullReferenceThrowBlock(failure))
                continue;

            if (index <= versionWriteIndex)
                return false;
            allowed.Add(prefix[index]);
            allowed.Add(prefix[index + 1]);
            index++;
        }

        return true;
    }

    /// <summary>
    /// 仅接受首条语义指令抛出 <c>NullReferenceException</c>、余下只含边载体与跳转的失败块。
    /// </summary>
    private static bool IsStrictNullReferenceThrowBlock(Block block)
    {
        var instructions = PatternInstructions(block);
        return instructions.Count > 0
               && instructions[0] is { OpCode: OpCode.Throw, Operands: [TypeAnalysisContext type] }
               && string.Equals(type.FullName, "System.NullReferenceException", StringComparison.Ordinal)
               && instructions.Skip(1).All(instruction => instruction.OpCode is OpCode.Move or OpCode.Jump);
    }

    /// <summary>
    /// 获取标准快边，同时支持边载体 staging 块进入多前驱共享快尾。
    /// </summary>
    private static bool TryGetFastRoute(
        Block head,
        Block slowBlock,
        Block merge,
        HashSet<Block> originalSharedFastTails,
        out Block fastEntry,
        out Block fastBody,
        out List<Instruction> fastPrefix)
    {
        fastEntry = null!;
        fastBody = null!;
        fastPrefix = [];
        if (head.Successors.Count != 2)
            return false;

        fastEntry = head.Successors.SingleOrDefault(block => !ReferenceEquals(block, slowBlock))!;
        if (fastEntry == null
            || fastEntry.Predecessors is not [var predecessor]
            || !ReferenceEquals(predecessor, head)
            || fastEntry.Successors is not [var successor])
            return false;

        if (ReferenceEquals(successor, merge))
        {
            fastBody = fastEntry;
            return true;
        }

        if (!originalSharedFastTails.Contains(successor)
            || successor.Successors is not [var sharedMerge]
            || !ReferenceEquals(sharedMerge, merge))
            return false;

        var stagingInstructions = PatternInstructions(fastEntry);
        var hasExplicitJump = false;
        if (stagingInstructions.LastOrDefault() is
            { OpCode: OpCode.Jump, Operands: [Block target] })
        {
            if (!ReferenceEquals(target, successor))
                return false;
            hasExplicitJump = true;
        }

        var prefix = hasExplicitJump
            ? stagingInstructions.Take(stagingInstructions.Count - 1).ToList()
            : stagingInstructions;
        // 纯跳转块也可能是容量分支到多前驱共享快尾的真实边中继。
        // 这里仅放行已经校验目标的显式跳转；共享快尾本体仍由后续完整
        // 数组写入、大小更新、快慢元素一致性与唯一汇合验证共同约束。
        if (prefix.Count == 0 && !hasExplicitJump)
            return false;

        fastBody = successor;
        fastPrefix = prefix;
        return true;
    }

    private static bool TryMatchFastPath(
        ISILControlFlowGraph graph,
        Block block,
        IReadOnlyList<Instruction> fastPrefix,
        Block merge,
        LocalVariable receiver,
        LocalVariable items,
        IOperand sizeState,
        LocalVariable versionResult,
        IOperand versionSource,
        IOperand value,
        IReadOnlyList<Instruction> slowTail,
        ApplicationAnalysisContext appContext,
        TypeAnalysisContext elementType,
        bool compactArrayAccessOnly,
        out IOperand publicValue,
        out List<Instruction> preservedSlowTail)
    {
        publicValue = null!;
        preservedSlowTail = [];
        var instructions = fastPrefix.Concat(PatternInstructions(block)).ToList();
        if (instructions.Count < 4
            || instructions[^1] is not { OpCode: OpCode.Jump, Operands: [Block target] }
            || !ReferenceEquals(target, merge))
        {
            Logger.VerboseNewline("ListAdd恢复拒绝：快路径未以指向汇合块的唯一跳转结束。");
            return false;
        }

        var stores = instructions.Where(instruction =>
            instruction is { OpCode: OpCode.Move, Operands: [MemoryOperand or ArrayAccess, _] }).ToList();
        var sizeAdds = instructions.Where(instruction =>
            instruction is { OpCode: OpCode.Add, Operands: [LocalVariable, var source, Immediate { Value: 1 }] }
            && IsSameStateOperand(source, sizeState, receiver, "_size")).ToList();
        if (stores.Count != 1
            || sizeAdds.Count != 1
            || sizeAdds[0].Operands[0] is not LocalVariable newSize)
        {
            Logger.VerboseNewline($"ListAdd恢复拒绝：快路径元素写入数={stores.Count}，大小递增数={sizeAdds.Count}。");
            return false;
        }
        MemoryOperand? memory = stores[0].Operands[0] is MemoryOperand rawMemory ? rawMemory : null;
        var arrayAccess = stores[0].Operands[0] as ArrayAccess;
        if (compactArrayAccessOnly && memory.HasValue)
            return false;
        if (!TryReconcileElementValue(
                graph,
                stores[0].Operands[1],
                value,
                appContext,
                elementType,
                instructions,
                out publicValue,
                out var valueConstruction))
        {
            Logger.VerboseNewline($"ListAdd恢复拒绝：快慢元素值不等价，元素类型={elementType.FullName}，快值={stores[0].Operands[1]}，慢值={value}。");
            return false;
        }

        var sizeWrites = instructions.Where(instruction =>
            instruction is { OpCode: OpCode.Move, Operands: [FieldReference field, var source] }
            && IsField(field, receiver, "_size")
            && ReferenceEquals(source, newSize)).ToList();
        if (sizeWrites.Count != 1)
        {
            Logger.VerboseNewline($"ListAdd恢复拒绝：大小回写数={sizeWrites.Count}。");
            return false;
        }

        var allowed = new List<Instruction>
        {
            stores[0],
            sizeAdds[0],
            sizeWrites[0],
            instructions[^1],
        };
        allowed.AddRange(valueConstruction);

        if (arrayAccess != null)
        {
            // ArrayRecovery 已经把原生的“缩放索引 + 基址加法 + 内存写入”折叠为
            // items[index]。较晚完成归一化时，原始缩放与地址指令已经变为 Nop；较早
            // 完成时两条证据仍保留。两种形态分别要求 0/0 或 1/1，拒绝半套地址链。
            if (!ReferenceEquals(arrayAccess.Array, items)
                || !IsSameStateOperand(arrayAccess.Index, sizeState, receiver, "_size")
                && !CollectUInt32IndexNormalization(
                    instructions,
                    stores[0],
                    receiver,
                    sizeState,
                    arrayAccess.Index,
                    allowed))
            {
                Logger.VerboseNewline(
                    $"ListAdd恢复拒绝：直接数组访问的数组或索引状态不匹配，访问={arrayAccess}。");
                return false;
            }

            var retainedScales = instructions.Where(instruction =>
                instruction is
                {
                    OpCode: OpCode.ShiftLeft or OpCode.Multiply,
                    Operands: [LocalVariable, _, Immediate]
                }).ToList();
            if (retainedScales.Count > 1)
            {
                Logger.VerboseNewline(
                    $"ListAdd恢复拒绝：直接数组访问保留的索引缩放数={retainedScales.Count}。");
                return false;
            }

            if (retainedScales.Count == 0 && !compactArrayAccessOnly
                || retainedScales.Count == 1 && compactArrayAccessOnly)
                return false;

            if (retainedScales is [var retainedScale]
                && retainedScale.Operands[0] is LocalVariable retainedElementOffset)
            {
                var retainedAddresses = instructions.Where(instruction =>
                    instruction is { OpCode: OpCode.Add, Operands: [LocalVariable, var left, var right] }
                    && ((IsItemsAddressBase(left, receiver, items)
                         && ReferenceEquals(right, retainedElementOffset))
                        || (IsItemsAddressBase(right, receiver, items)
                            && ReferenceEquals(left, retainedElementOffset)))).ToList();
                if (retainedAddresses.Count != 1)
                {
                    Logger.VerboseNewline(
                        $"ListAdd恢复拒绝：直接数组访问保留的元素地址证据数={retainedAddresses.Count}。");
                    return false;
                }

                allowed.Add(retainedScale);
                allowed.Add(retainedAddresses[0]);
                var retainedScaleSource = retainedScale.Operands[1];
                if (!IsSameStateOperand(retainedScaleSource, sizeState, receiver, "_size")
                    && !CollectUInt32IndexNormalization(
                        instructions,
                        retainedScale,
                        receiver,
                        sizeState,
                        retainedScaleSource,
                        allowed))
                {
                    Logger.VerboseNewline("ListAdd恢复拒绝：直接数组访问的保留索引归一化链未闭合。");
                    return false;
                }
            }
        }
        else
        {
            // 尚未归一化的原生内存写入继续要求唯一缩放和唯一地址合成，避免把普通
            // 指针写入误判为 List<T>.Add 快路径。
            var scales = instructions.Where(instruction =>
                instruction is
                {
                    OpCode: OpCode.ShiftLeft or OpCode.Multiply,
                    Operands: [LocalVariable, _, Immediate]
                }).ToList();
            if (scales.Count != 1
                || scales[0].Operands[0] is not LocalVariable elementOffset)
            {
                Logger.VerboseNewline($"ListAdd恢复拒绝：原生内存路径索引缩放数={scales.Count}。");
                return false;
            }

            var addresses = instructions.Where(instruction =>
                instruction is { OpCode: OpCode.Add, Operands: [LocalVariable destination, var left, var right] }
                && memory.HasValue
                && ReferenceEquals(destination, memory.Value.Base)
                && ((IsItemsAddressBase(left, receiver, items) && ReferenceEquals(right, elementOffset))
                    || (IsItemsAddressBase(right, receiver, items) && ReferenceEquals(left, elementOffset)))).ToList();
            if (addresses.Count != 1)
            {
                Logger.VerboseNewline($"ListAdd恢复拒绝：原生内存路径元素地址证据数={addresses.Count}。");
                return false;
            }

            allowed.Add(scales[0]);
            allowed.Add(addresses[0]);
            var scaleSource = scales[0].Operands[1];
            if (!IsSameStateOperand(scaleSource, sizeState, receiver, "_size")
                && !CollectUInt32IndexNormalization(
                    instructions,
                    scales[0],
                    receiver,
                    sizeState,
                    scaleSource,
                    allowed))
            {
                Logger.VerboseNewline("ListAdd恢复拒绝：索引归一化链未闭合。");
                return false;
            }
        }

        var slowStateRefreshes = slowTail.Where(instruction =>
            IsStateRefresh(instruction, receiver, "_size", newSize)
            || IsStateRefresh(instruction, receiver, "_version", versionResult)).ToList();
        if (slowTail.Any(instruction =>
                instruction is { OpCode: OpCode.Move, Operands: [_, FieldReference field] }
                && IsField(field, receiver, "_size")
                && !IsStateRefresh(instruction, receiver, "_size", newSize)
                || instruction is { OpCode: OpCode.Move, Operands: [_, FieldReference versionField] }
                && IsField(versionField, receiver, "_version")
                && !IsStateRefresh(instruction, receiver, "_version", versionResult)))
        {
            Logger.VerboseNewline("ListAdd恢复拒绝：慢路径含非等价的集合状态重载。");
            return false;
        }

        var fastTail = instructions.Where(instruction => !allowed.Contains(instruction)).ToList();
        // 连续内联 Add 会在汇合块继续消费上一菱形的 size/version 载体。公开 Add 已经完成
        // 集合状态更新，因此只有在后续仍引用载体时，才把慢路径的字段回读搬到公开调用之后；
        // 最后一项追加没有后续引用，仍删除全部私有状态刷新。
        var requiredStateRefreshes = new HashSet<Instruction>(slowStateRefreshes.Where(instruction =>
            instruction.Destination is LocalVariable destination
            && IsReferencedFromMerge(merge, destination)));
        preservedSlowTail = slowTail.Where(instruction =>
            !slowStateRefreshes.Contains(instruction)
            || requiredStateRefreshes.Contains(instruction)).ToList();
        var callClobberRefreshes = preservedSlowTail.Where(instruction =>
            IsStateRefresh(instruction, receiver, "_items", items)
            || IsRedundantCallClobberRefresh(graph, instruction)).ToList();
        var comparableSlowTail = preservedSlowTail.Where(instruction =>
            !requiredStateRefreshes.Contains(instruction)
            && !callClobberRefreshes.Contains(instruction)).ToList();
        var fastVersionMatched = TryCollectNextVersionAdvance(
            fastTail,
            receiver,
            versionResult,
            versionSource,
            out var fastVersionAdvance);
        var slowVersionMatched = TryCollectNextVersionAdvance(
            comparableSlowTail,
            receiver,
            carriedVersionState: null,
            fusedVersionSource: null,
            out var slowVersionAdvance);
        if (!fastVersionMatched
            || !slowVersionMatched
            || fastVersionAdvance.Count != slowVersionAdvance.Count)
        {
            Logger.VerboseNewline(
                $"ListAdd恢复拒绝：下一项版本预更新不等价，" +
                $"快路径={fastVersionAdvance.Count}，慢路径={slowVersionAdvance.Count}，" +
                $"头部旧版本源={versionSource}，" +
                $"快尾=[{string.Join(" | ", fastTail)}]，" +
                $"慢尾=[{string.Join(" | ", comparableSlowTail)}]。");
            return false;
        }

        // 连续内联 Add 还会把下一项的 _version++ 分别排到当前快慢边末尾。两边的 SSA
        // 结果局部不同，但都严格读取并回写同一集合版本字段；保留慢边的一份供下一菱形匹配，
        // 当前尾部比较则排除这组等价预更新。下一项提升为公开 Add 时会一并删除这份预更新。
        var comparableFastTail = fastTail.Where(instruction =>
            !fastVersionAdvance.Contains(instruction)
            && !IsDeadMistypedRuntimeMethodCarrier(instruction, merge)).ToList();
        comparableSlowTail = comparableSlowTail.Where(instruction =>
            !slowVersionAdvance.Contains(instruction)
            && !IsDeadMistypedRuntimeMethodCarrier(instruction, merge)).ToList();
        var sameCarriers = HaveIdenticalCarrierMoves(comparableFastTail, comparableSlowTail);
        if (!sameCarriers)
            Logger.VerboseNewline(
                $"ListAdd恢复拒绝：快慢路径载体尾不等价，快路径={comparableFastTail.Count}，" +
                $"慢路径={comparableSlowTail.Count}，快尾=[{string.Join(" | ", comparableFastTail)}]，" +
                $"慢尾=[{string.Join(" | ", comparableSlowTail)}]。");
        return sameCarriers;
    }

    /// <summary>
    /// ARM64 隐藏方法参数可能被类型传播误挂到通用调用寄存器局部；仅当该值在汇合点后
    /// 被覆盖前从未读取时，才把它视为死亡的原生方法句柄载体。
    /// </summary>
    private static bool IsDeadMistypedRuntimeMethodCarrier(Instruction instruction, Block merge)
        => instruction is
           {
               OpCode: OpCode.Move,
               Operands: [LocalVariable destination, RuntimeMethodInfoAnalysisContext],
           }
           && destination.Type is not RuntimeMethodInfoAnalysisContext
           && !IsReadBeforeDefinitionFromMerge(merge, destination);

    /// <summary>
    /// 收集快慢边末尾为下一次 List.Add 预排的唯一版本递增；没有该尾部也属于有效终项。
    /// </summary>
    private static bool TryCollectNextVersionAdvance(
        IReadOnlyList<Instruction> instructions,
        LocalVariable receiver,
        IOperand? carriedVersionState,
        IOperand? fusedVersionSource,
        out List<Instruction> advance)
    {
        advance = [];
        for (var index = 0; index + 1 < instructions.Count; index++)
        {
            if (instructions[index] is not
                {
                    OpCode: OpCode.Add,
                    Operands:
                    [
                        LocalVariable version,
                        var source,
                        Immediate increment,
                    ],
                }
                || increment.Value is not (1 or 2)
                || increment.Value == 1
                && !IsDirectStateOperand(source, receiver, "_version")
                && (carriedVersionState is null
                    || !ReferenceEquals(source, carriedVersionState))
                || increment.Value == 2
                && (fusedVersionSource is null
                    || !IsSameStateOperand(source, fusedVersionSource, receiver, "_version"))
                || instructions[index + 1] is not
                {
                    OpCode: OpCode.Move,
                    Operands: [FieldReference destination, var writtenVersion],
                }
                || !IsField(destination, receiver, "_version")
                || !ReferenceEquals(version, writtenVersion))
                continue;

            if (advance.Count != 0)
                return false;
            advance.Add(instructions[index]);
            advance.Add(instructions[index + 1]);
        }

        return true;
    }

    /// <summary>
    /// 判断容量菱形的汇合点及其后继是否继续使用指定状态载体。
    /// </summary>
    private static bool IsReferencedFromMerge(Block merge, LocalVariable local)
    {
        var pending = new Stack<Block>();
        var visited = new HashSet<Block>();
        pending.Push(merge);

        while (pending.Count > 0)
        {
            var block = pending.Pop();
            if (!visited.Add(block))
                continue;
            if (SemanticInstructions(block).Any(instruction =>
                    instruction.Operands.Any(operand => ReferencesLocal(operand, local))))
                return true;

            foreach (var successor in block.Successors)
                pending.Push(successor);
        }

        return false;
    }

    /// <summary>
    /// 统一快慢路径中等价的元素值表示。
    /// </summary>
    /// <remarks>
    /// ARM64 既会把 <c>Vector2</c> 等 HFA 打包为整元素只读常量，也会用
    /// <c>AND/OR</c> 拼出 <c>Single</c> 位模式，而慢路径从只读地址传入同一常量。
    /// 只在元素类型、地址映射和逐位结果同时闭合时才提升为公开 Add。
    /// </remarks>
    private static bool TryReconcileElementValue(
        ISILControlFlowGraph graph,
        IOperand fastValue,
        IOperand slowValue,
        ApplicationAnalysisContext appContext,
        TypeAnalysisContext elementType,
        IReadOnlyList<Instruction> valueScope,
        out IOperand publicValue,
        out IReadOnlyList<Instruction> valueConstruction)
    {
        publicValue = null!;
        valueConstruction = [];
        if (ReferenceEquals(fastValue, slowValue))
        {
            publicValue = slowValue;
            return true;
        }

        if (TryReconcileScalarFloatingConstant(
                graph,
                fastValue,
                slowValue,
                appContext,
                elementType,
                valueScope,
                out var scalar,
                out valueConstruction))
        {
            publicValue = scalar;
            return true;
        }

        // ARM64 会为快速路径的数组写入和慢路径的 AddWithResize 实参分别创建 Immediate。
        // 两个装箱实例的引用身份不同，但数值位完全相同时仍是同一个托管常量；数值不同则保留原菱形。
        if (fastValue is Immediate fastImmediate
            && slowValue is Immediate slowImmediate
            && fastImmediate.Value == slowImmediate.Value)
        {
            publicValue = slowValue;
            return true;
        }

        // 字段解析会为快路径数组写入与慢路径AddWithResize实参分别构造FieldReference。
        // 只有字段元数据、接收者局部量与原生偏移三者完全相同，才把它们视为同一次托管字段读取。
        if (fastValue is FieldReference fastField
            && slowValue is FieldReference slowField
            && AreSameFieldRead(fastField, slowField))
        {
            publicValue = slowValue;
            return true;
        }

        // 中文注释：ArrayRecovery 会分别为快速数组写入和慢速扩容调用重建数组元素读取；
        // 两个 ArrayAccess 实例即使引用身份不同，只要数组载体与索引表达完全相同，
        // 就代表同一次业务元素读取。数组或索引任一漂移时仍保留原容量分支。
        if (fastValue is ArrayAccess fastArrayAccess
            && slowValue is ArrayAccess slowArrayAccess
            && AreEquivalentValue(fastArrayAccess, slowArrayAccess))
        {
            publicValue = slowValue;
            return true;
        }

        if (fastValue is not MemoryOperand { IsConstant: true, Addend: > 0 } packed
            || slowValue is not HomogeneousFloatingAggregateArgument aggregate
            || !TryDecodePackedHfaConstant(appContext, packed, aggregate, out var decoded))
            return false;

        var unresolvedAbiComponents = aggregate.Components.Select((component, index) => (component, index)).All(entry =>
            entry.component is LocalVariable local
            && local.Register.Name == $"V{entry.index}"
            && graph.Instructions.All(instruction => !ReferenceEquals(instruction.Destination, local)));
        if (!unresolvedAbiComponents
            && !HaveSameResolvedFloatingBits(
                graph.Instructions,
                aggregate.Components,
                decoded.Components))
            return false;

        publicValue = decoded;
        return true;
    }

    /// <summary>
    /// 将快路径唯一 SSA 定义的 32 位常量表达式与慢路径只读地址中的 Single 逐位对齐。
    /// </summary>
    private static bool TryReconcileScalarFloatingConstant(
        ISILControlFlowGraph graph,
        IOperand fastValue,
        IOperand slowValue,
        ApplicationAnalysisContext appContext,
        TypeAnalysisContext elementType,
        IReadOnlyList<Instruction> valueScope,
        out FloatLiteral publicValue,
        out IReadOnlyList<Instruction> valueConstruction)
    {
        publicValue = default;
        valueConstruction = [];
        if (elementType.FullName != "System.Single"
            || slowValue is not MemoryOperand { IsConstant: true, Addend: > 0 } constant)
            return false;

        var construction = new List<Instruction>();
        if (!TryEvaluateUnsignedConstant(
                valueScope,
                fastValue,
                bitWidth: 32,
                new HashSet<LocalVariable>(),
                construction,
                out var evaluatedFastBits))
            return false;
        var fastBits = unchecked((uint)evaluatedFastBits);

        try
        {
            if (!appContext.Binary.TryMapVirtualAddressToRaw(
                    unchecked((ulong)constant.Addend),
                    out var rawAddress))
                return false;
            var bytes = appContext.Binary.Reader.ReadByteArrayAtRawAddress(rawAddress, sizeof(float));
            if (bytes.Length != sizeof(float)
                || BitConverter.ToUInt32(bytes, 0) != fastBits)
                return false;

            publicValue = new FloatLiteral(FloatingPointBitHelper.Int32BitsToSingle(unchecked((int)fastBits)));
            valueConstruction = construction.Distinct().ToList();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 求值 ARM64 已经降为 ISIL 的唯一 32 位常量构造链。
    /// </summary>
    /// <remarks>
    /// 仅接受已在真实产物中出现的 <c>Move/And/Or</c>；局部变量多定义、循环定义或
    /// 任一非常量操作均中止恢复，避免将运行时浮点数冻结为字面量。
    /// </remarks>
    private static bool TryEvaluateUnsignedConstant(
        IReadOnlyList<Instruction> valueScope,
        IOperand operand,
        int bitWidth,
        ISet<LocalVariable> active,
        ICollection<Instruction> construction,
        out ulong value)
    {
        if (bitWidth is not (32 or 64))
        {
            value = 0;
            return false;
        }

        var mask = bitWidth == 32 ? uint.MaxValue : ulong.MaxValue;
        if (operand is Immediate immediate)
        {
            value = immediate.UnsignedValue & mask;
            return true;
        }

        value = 0;
        if (operand is not LocalVariable local
            || !active.Add(local))
            return false;

        try
        {
            var definitions = valueScope.Where(instruction =>
                ReferenceEquals(instruction.Destination, local)).ToList();
            if (definitions.Count != 1)
                return false;

            var definition = definitions[0];
            var matched = definition switch
            {
                { OpCode: OpCode.Move, Operands: [_, var source] } =>
                    TryEvaluateUnsignedConstant(
                        valueScope,
                        source,
                        bitWidth,
                        active,
                        construction,
                        out value),
                {
                    OpCode: OpCode.And or OpCode.Or,
                    Operands: [_, var left, var right],
                } => TryEvaluateBinaryUnsignedConstant(
                    valueScope,
                    definition.OpCode,
                    left,
                    right,
                    bitWidth,
                    active,
                    construction,
                    out value),
                _ => false,
            };
            if (!matched)
                return false;

            construction.Add(definition);
            return true;
        }
        finally
        {
            active.Remove(local);
        }
    }

    private static bool TryEvaluateBinaryUnsignedConstant(
        IReadOnlyList<Instruction> valueScope,
        OpCode opCode,
        IOperand left,
        IOperand right,
        int bitWidth,
        ISet<LocalVariable> active,
        ICollection<Instruction> construction,
        out ulong value)
    {
        value = 0;
        if (!TryEvaluateUnsignedConstant(
                valueScope,
                left,
                bitWidth,
                active,
                construction,
                out var leftValue)
            || !TryEvaluateUnsignedConstant(
                valueScope,
                right,
                bitWidth,
                active,
                construction,
                out var rightValue))
            return false;

        var mask = bitWidth == 32 ? uint.MaxValue : ulong.MaxValue;
        value = (opCode == OpCode.And
            ? leftValue & rightValue
            : leftValue | rightValue) & mask;
        return true;
    }

    /// <summary>
    /// 识别 AddWithResize 对原生调用者保存寄存器造成的稳定存储载体重载。
    /// 稳定位置包括同一字段和同一内存槽；两次读取之间只允许出现这一处集合扩容调用，
    /// 避免吞掉真正的字段或栈槽更新。
    /// </summary>
    private static bool IsRedundantCallClobberRefresh(
        ISILControlFlowGraph graph,
        Instruction refresh)
    {
        if (refresh is not
            {
                Index: >= 0,
                OpCode: OpCode.Move,
                Operands: [LocalVariable destination, var source],
            })
            return false;
        if (!IsStableCallClobberSource(source))
            return false;

        foreach (var candidate in graph.Instructions)
        {
            if (ReferenceEquals(candidate, refresh)
                || candidate.Index < 0
                || candidate.Index >= refresh.Index
                || candidate is not
                {
                    OpCode: OpCode.Move,
                    Operands: [var priorDestination, var priorSource],
                }
                || !ReferenceEquals(priorDestination, destination)
                || !AreSameCallClobberSource(priorSource, source))
                continue;

            var interveningCalls = graph.Instructions.Where(instruction =>
                instruction.Index > candidate.Index
                && instruction.Index < refresh.Index
                && instruction.IsCall).ToList();
            if (interveningCalls is
                [
                    {
                        OpCode: OpCode.CallVoid,
                        Operands: [MethodAnalysisContext { Name: "AddWithResize" }, ..],
                    },
                ])
                return true;
        }

        return false;
    }

    /// <summary>
    /// 限定可跨调用重载的稳定源。局部量和立即数不代表可重读存储，保持失败关闭。
    /// </summary>
    private static bool IsStableCallClobberSource(IOperand source)
        => source is FieldReference or MemoryOperand or ArrayLength;

    /// <summary>
    /// 比较调用前后的稳定读取身份；字段要求字段、接收者和偏移一致，内存槽要求基址、索引
    /// 与附加偏移全部一致。
    /// </summary>
    private static bool AreSameCallClobberSource(IOperand left, IOperand right)
        => left is FieldReference leftField
           && right is FieldReference rightField
           && AreSameFieldRead(leftField, rightField)
           || left is MemoryOperand leftMemory
           && right is MemoryOperand rightMemory
           && AreSameMemoryOperand(leftMemory, rightMemory)
           || left is ArrayLength leftLength
           && right is ArrayLength rightLength
           && ReferenceEquals(leftLength.Array, rightLength.Array);

    private static bool AreSameFieldRead(FieldReference left, FieldReference right)
        => ReferenceEquals(left.Field, right.Field)
           && ReferenceEquals(left.Local, right.Local)
           && left.Offset == right.Offset;

    private static bool TryDecodePackedHfaConstant(
        ApplicationAnalysisContext appContext,
        MemoryOperand packed,
        HomogeneousFloatingAggregateArgument aggregate,
        out HomogeneousFloatingAggregateArgument decoded)
    {
        decoded = null!;
        if (!Arm64CallingConventionResolver.TryGetHomogeneousFloatingAggregateFields(
                aggregate.AggregateType,
                out var fields)
            || fields.Count != aggregate.Components.Count
            || fields.Count is < 1 or > 4)
            return false;

        var isSingle = fields.All(field => field.FieldType.FullName == "System.Single");
        var isDouble = fields.All(field => field.FieldType.FullName == "System.Double");
        if (!isSingle && !isDouble)
            return false;

        var elementSize = isSingle ? sizeof(float) : sizeof(double);
        var byteCount = checked(fields.Count * elementSize);
        try
        {
            if (!appContext.Binary.TryMapVirtualAddressToRaw(unchecked((ulong)packed.Addend), out var rawAddress))
                return false;
            var bytes = appContext.Binary.Reader.ReadByteArrayAtRawAddress(rawAddress, byteCount);
            var components = new List<IOperand>(fields.Count);
            for (var index = 0; index < fields.Count; index++)
            {
                var offset = index * elementSize;
                components.Add(isSingle
                    ? new FloatLiteral(BitConverter.ToSingle(bytes, offset))
                    : new DoubleLiteral(BitConverter.ToDouble(bytes, offset)));
            }

            decoded = new HomogeneousFloatingAggregateArgument(aggregate.AggregateType, components);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 比较已由 ARM64 位重解释指令定义的 HFA 分量与只读区解码结果。
    /// </summary>
    /// <remarks>
    /// 真实的 Vector2/Vector3 慢路径会先把整数位模式重解释为 V0-V3 浮点局部，
    /// 再把这些局部作为 AddWithResize 实参。每个局部必须只有一个 Move 或位重解释定义，
    /// 且最终位宽和值逐位相同；多定义、循环和运行时算术均保持失败关闭。
    /// </remarks>
    private static bool HaveSameResolvedFloatingBits(
        IReadOnlyList<Instruction> instructions,
        IReadOnlyList<IOperand> actual,
        IReadOnlyList<IOperand> expected)
    {
        if (actual.Count != expected.Count)
            return false;

        for (var index = 0; index < actual.Count; index++)
        {
            if (!TryResolveFloatingBits(
                    instructions,
                    actual[index],
                    new HashSet<LocalVariable>(),
                    out var actualWidth,
                    out var actualBits)
                || !TryGetLiteralFloatingBits(
                    expected[index],
                    out var expectedWidth,
                    out var expectedBits)
                || actualWidth != expectedWidth
                || actualBits != expectedBits)
                return false;
        }

        return true;
    }

    private static bool TryResolveFloatingBits(
        IReadOnlyList<Instruction> instructions,
        IOperand operand,
        ISet<LocalVariable> active,
        out int bitWidth,
        out ulong bits)
    {
        if (TryGetLiteralFloatingBits(operand, out bitWidth, out bits))
            return true;

        bitWidth = 0;
        bits = 0;
        if (operand is not LocalVariable local || !active.Add(local))
            return false;

        try
        {
            var definitions = instructions
                .Where(instruction => ReferenceEquals(instruction.Destination, local))
                .Take(2)
                .ToList();
            if (definitions.Count != 1)
                return false;

            var definition = definitions[0];
            if (definition is { OpCode: OpCode.Move, Operands: [_, var source] })
                return TryResolveFloatingBits(instructions, source, active, out bitWidth, out bits);

            if (definition is not
                {
                    OpCode: OpCode.ReinterpretIntegerBitsAsFloat,
                    Operands: [_, var integerBits, Immediate width],
                }
                || width.Value is not (32 or 64))
                return false;

            bitWidth = unchecked((int)width.Value);
            return TryEvaluateUnsignedConstant(
                instructions,
                integerBits,
                bitWidth,
                new HashSet<LocalVariable>(),
                new List<Instruction>(),
                out bits);
        }
        finally
        {
            active.Remove(local);
        }
    }

    private static bool TryGetLiteralFloatingBits(
        IOperand operand,
        out int bitWidth,
        out ulong bits)
    {
        switch (operand)
        {
            case FloatLiteral single:
                bitWidth = 32;
                bits = unchecked((uint)FloatingPointBitHelper.SingleToInt32Bits(single.Value));
                return true;
            case DoubleLiteral @double:
                bitWidth = 64;
                bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(@double.Value));
                return true;
            default:
                bitWidth = 0;
                bits = 0;
                return false;
        }
    }

    /// <summary>
    /// 收集 ARM64 将 Int32 集合索引规范化到原生寄存器宽度的唯一位运算链。
    /// </summary>
    /// <remarks>
    /// ArrayRecovery 可能已经把缩放和地址合成折叠为 ArrayAccess，此时归一化链与消费者之间
    /// 会夹着大小回写；因此按消费者之前的唯一数据定义回溯，而不是依赖三条指令紧邻缩放。
    /// </remarks>
    private static bool CollectUInt32IndexNormalization(
        IReadOnlyList<Instruction> instructions,
        Instruction consumer,
        LocalVariable receiver,
        IOperand sizeState,
        IOperand normalizedSource,
        ICollection<Instruction> allowed)
    {
        if (normalizedSource is not LocalVariable normalized
            || !TryGetUniqueDefinitionBefore(instructions, consumer, normalized, out var subtract)
            || subtract is not
            {
                OpCode: OpCode.Subtract,
                Operands: [_, LocalVariable biased, Immediate { Value: 0x80000000L }]
            }
            || !TryGetUniqueDefinitionBefore(instructions, subtract, biased, out var xor)
            || xor is not
            {
                OpCode: OpCode.Xor,
                Operands: [_, LocalVariable masked, Immediate { Value: 0x80000000L }]
            }
            || !TryGetUniqueDefinitionBefore(instructions, xor, masked, out var and)
            || and is not
            {
                OpCode: OpCode.And,
                Operands: [_, var maskSource, Immediate { Value: 0xFFFFFFFFL }]
            }
            || !IsSameStateOperand(maskSource, sizeState, receiver, "_size")
            || !ReferenceEquals(subtract.Operands[0], normalized)
            || !ReferenceEquals(xor.Operands[0], biased)
            || !ReferenceEquals(and.Operands[0], masked))
            return false;

        allowed.Add(and);
        allowed.Add(xor);
        allowed.Add(subtract);
        return true;
    }

    /// <summary>
    /// 在指定消费者之前读取局部量的唯一当前定义；多定义和逆序定义均保持失败关闭。
    /// </summary>
    private static bool TryGetUniqueDefinitionBefore(
        IReadOnlyList<Instruction> instructions,
        Instruction consumer,
        LocalVariable local,
        out Instruction definition)
    {
        definition = null!;
        var consumerIndex = IndexOf(instructions, consumer);
        if (consumerIndex < 0)
            return false;

        var definitions = instructions
            .Take(consumerIndex)
            .Where(instruction => ReferenceEquals(instruction.Destination, local))
            .Take(2)
            .ToList();
        if (definitions.Count != 1)
            return false;

        definition = definitions[0];
        return true;
    }

    private static int IndexOf(IReadOnlyList<Instruction> instructions, Instruction target)
    {
        for (var index = 0; index < instructions.Count; index++)
        {
            if (ReferenceEquals(instructions[index], target))
                return index;
        }

        return -1;
    }

    private static bool IsBefore(
        IReadOnlyList<Instruction> instructions,
        Instruction candidate,
        Instruction use)
    {
        var candidateIndex = IndexOf(instructions, candidate);
        var useIndex = IndexOf(instructions, use);
        return candidateIndex >= 0 && candidateIndex < (useIndex >= 0 ? useIndex : instructions.Count);
    }

    private static bool IsStateRefresh(
        Instruction instruction,
        LocalVariable receiver,
        string fieldName,
        LocalVariable destination)
        => instruction is { OpCode: OpCode.Move, Operands: [var target, FieldReference field] }
           && ReferenceEquals(target, destination)
           && IsField(field, receiver, fieldName);

    private static bool IsSameStateOperand(
        IOperand left,
        IOperand right,
        LocalVariable receiver,
        string fieldName)
        => ReferenceEquals(left, right)
           || IsDirectStateOperand(left, receiver, fieldName)
           && IsDirectStateOperand(right, receiver, fieldName);

    /// <summary>
    /// 判断操作数是否直接读取同一集合状态；<see cref="ListCount"/> 只等价于具体
    /// <c>List&lt;T&gt;</c> 接收者的 <c>_size</c> 读取，不参与版本或数组字段匹配。
    /// </summary>
    private static bool IsDirectStateOperand(
        IOperand operand,
        LocalVariable receiver,
        string fieldName)
        => operand is FieldReference field && IsField(field, receiver, fieldName)
           || IsPublicListSize(operand, receiver, fieldName);

    private static bool IsPublicListSize(
        IOperand operand,
        LocalVariable receiver,
        string fieldName)
        => fieldName == "_size"
           && operand is ListCount count
           && ReferenceEquals(count.Value, receiver)
           && string.Equals(count.ListType.FullName, receiver.Type?.FullName, StringComparison.Ordinal);

    private static bool TryCreatePublicAddTarget(
        ConcreteGenericMethodAnalysisContext addWithResize,
        out ConcreteGenericMethodAnalysisContext addTarget)
    {
        addTarget = null!;
        var listDefinition = addWithResize.BaseMethodContext.DeclaringType;
        var add = listDefinition?.Methods.SingleOrDefault(method =>
            method.Name == "Add"
            && !method.IsStatic
            && method.Parameters.Count == 1);
        if (add == null)
            return false;

        addTarget = new ConcreteGenericMethodAnalysisContext(add, addWithResize.TypeGenericParameters, []);
        return true;
    }

    private static void Rewrite(
        ISILControlFlowGraph graph,
        Block rewriteHead,
        List<Block> headPath,
        Block fastEntry,
        Block fastBody,
        Block slowBlock,
        Block merge,
        Instruction rewriteStart,
        Instruction slowCall,
        IReadOnlyList<Instruction> slowTail,
        IReadOnlyList<Instruction> preservedHeadBusiness,
        ConcreteGenericMethodAnalysisContext addTarget,
        LocalVariable receiver,
        IOperand value)
    {
        var firstRemovedIndex = rewriteHead.Instructions.IndexOf(rewriteStart);
        rewriteHead.Instructions.RemoveRange(firstRemovedIndex, rewriteHead.Instructions.Count - firstRemovedIndex);
        rewriteHead.Instructions.AddRange(preservedHeadBusiness);
        rewriteHead.Instructions.Add(new Instruction(slowCall.Index, OpCode.CallVoid, addTarget, receiver, value));
        rewriteHead.Instructions.AddRange(slowTail);

        foreach (var pathBlock in headPath.Skip(1).ToList())
            Detach(graph, pathBlock);
        Detach(graph, fastEntry);
        Detach(graph, slowBlock);
        if (!ReferenceEquals(fastBody, fastEntry) && fastBody.Predecessors.Count == 0)
            Detach(graph, fastBody);

        foreach (var successor in rewriteHead.Successors.ToList())
            successor.Predecessors.Remove(rewriteHead);
        rewriteHead.Successors.Clear();
        rewriteHead.Successors.Add(merge);
        if (!merge.Predecessors.Contains(rewriteHead))
            merge.Predecessors.Add(rewriteHead);
        rewriteHead.CalculateBlockType();
    }

    private static void Detach(ISILControlFlowGraph graph, Block block)
    {
        foreach (var predecessor in block.Predecessors.ToList())
            predecessor.Successors.Remove(block);
        foreach (var successor in block.Successors.ToList())
            successor.Predecessors.Remove(block);
        block.Predecessors.Clear();
        block.Successors.Clear();
        graph.Blocks.Remove(block);
    }

    private static List<Instruction> SemanticInstructions(Block block)
        => block.Instructions.Where(instruction => instruction.OpCode != OpCode.Nop).ToList();

    private static List<Instruction> PatternInstructions(Block block)
        => SemanticInstructions(block)
            .Where(instruction => !IsIgnorableRuntimeMetadataMove(instruction))
            .ToList();

    private static bool IsField(FieldReference field, LocalVariable receiver, string name)
        => ReferenceEquals(field.Local, receiver) && field.Field.Name == name;

    private static bool IsItemsAddressBase(IOperand operand, LocalVariable receiver, LocalVariable loadedItems)
        => ReferenceEquals(operand, loadedItems)
           || operand is FieldReference field && IsField(field, receiver, "_items");

    // 泛型List内联会夹带methodof、Il2CppClass和rgctx句柄装载；调用目标已经解析后，
    // 这些值只服务原生隐藏参数，不属于公开托管Add的可观察语义。
    private static bool IsIgnorableRuntimeMetadataMove(Instruction instruction)
        => instruction is { OpCode: OpCode.Move, Operands: [LocalVariable destination, var source] }
           && destination.Type is RuntimeClassTypeAnalysisContext
               or RgctxTableTypeAnalysisContext
               or RuntimeMethodInfoAnalysisContext
           && source is MemoryOperand
               or RuntimeMethodInfoAnalysisContext
               or RuntimeClassTypeAnalysisContext
               or RgctxTableTypeAnalysisContext
               or TypeAnalysisContext
               or Immediate;

    private static bool HaveIdenticalCarrierMoves(
        IReadOnlyList<Instruction> fastTail,
        IReadOnlyList<Instruction> slowTail)
    {
        if (fastTail.Count != slowTail.Count)
            return false;

        for (var index = 0; index < fastTail.Count; index++)
        {
            if (fastTail[index] is not { OpCode: OpCode.Move, Operands: [var fastDestination, var fastSource] }
                || slowTail[index] is not { OpCode: OpCode.Move, Operands: [var slowDestination, var slowSource] }
                || !ReferenceEquals(fastDestination, slowDestination)
                || !AreEquivalentValue(fastSource, slowSource))
                return false;
        }

        return true;
    }

}
