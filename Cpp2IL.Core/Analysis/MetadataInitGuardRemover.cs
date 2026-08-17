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

    // Il2CppMethodInfo::rgctx_data。泛型方法在首次读取该槽位前会调用
    // 原生初始化函数，该函数的共享地址有时会被误绑定为当前托管方法。
    private const long MethodRgctxOffset64 = 0x38;
    private const long MethodRgctxOffset32 = 0x1C;

    // Byte holding Il2CppClass's bitfield, of which bit 0 is initialized_and_no_error.
    // TODO this is almost certainly not correct on every version... but which?
    private const long InitialisedFlagOffset64 = 0x135;
    private const long InitialisedFlagOffset32 = 0xBD;

    public static void Run(MethodAnalysisContext method)
    {
        var cfg = method.ControlFlowGraph!;
        var is32Bit = method.AppContext.Binary.is32Bit;
        var removedOrdinaryGuard = RemoveGuards(
            cfg,
            is32Bit ? InitialisedFlagOffset32 : InitialisedFlagOffset64);
        var foldedRuntimeClassComparisons = FoldRuntimeClassNullComparisons(cfg);
        if (removedOrdinaryGuard || foldedRuntimeClassComparisons > 0)
            DeadCodeEliminator.Run(method);
    }

    /// <summary>
    /// 在方法 rgctx 尾调用和调用实参均已定型后删除泛型方法初始化保护区。
    /// 该阶段必须晚于 <see cref="MetadataResolver.ResolveMethodRgctxCalls"/>，否则合流块仍是未解析
    /// 间接调用，无法同时证明初始化分支和真实业务尾调用。
    /// </summary>
    internal static void RunMethodRgctxInitGuards(MethodAnalysisContext method)
    {
        var offset = method.AppContext.Binary.is32Bit ? MethodRgctxOffset32 : MethodRgctxOffset64;
        if (RemoveMethodRgctxInitGuards(method, method.ControlFlowGraph!, offset))
            DeadCodeEliminator.Run(method);
    }

    /// <summary>
    /// 运行时元数据槽完成解析后，折叠此前仍以原生地址形式存在的类型空值比较。
    /// </summary>
    internal static void RunRuntimeClassNullComparisons(MethodAnalysisContext method)
    {
        if (FoldRuntimeClassNullComparisons(method.ControlFlowGraph!) > 0)
            DeadCodeEliminator.Run(method);
    }

    public static void Run(ISILControlFlowGraph cfg, long initialisedFlagOffset)
    {
        var removedOrdinaryGuard = RemoveGuards(cfg, initialisedFlagOffset);
        var foldedRuntimeClassComparisons = FoldRuntimeClassNullComparisons(cfg);
        if (removedOrdinaryGuard || foldedRuntimeClassComparisons > 0)
            DeadCodeEliminator.Run(cfg);
    }

    private static bool RemoveGuards(ISILControlFlowGraph cfg, long initialisedFlagOffset)
    {
        var removedAny = false;
        foreach (var guard in cfg.Blocks.ToList())
            removedAny |= TryRemoveGuard(cfg, guard, initialisedFlagOffset);
        return removedAny;
    }

    /// <summary>
    /// 删除已由完整 CFG 证明的泛型方法 rgctx 初始化保护区。判定同时约束
    /// MethodInfo::rgctx_data 零值测试、唯一的初始化分支、被误绑定的当前方法以及
    /// 作为首个调用源的同一 MethodInfo 载体，不依赖某个游戏方法名或硬编码函数地址。
    /// </summary>
    internal static bool RemoveMethodRgctxInitGuards(
        MethodAnalysisContext method,
        ISILControlFlowGraph cfg,
        long methodRgctxOffset)
    {
        var definitions = cfg.Instructions
            .Where(instruction => instruction.Destination is LocalVariable)
            .ToLookup(instruction => (LocalVariable)instruction.Destination!);
        var blockDepths = ComputeBlockDepths(cfg);
        var removedAny = false;

        // 泛型方法常把“初始化运行时元数据”和“初始化当前 MethodInfo::rgctx_data”嵌套。
        // 深层守卫先折叠后，外层区域才只剩唯一初始化调用；深度目录只计算一次，避免重复扫描CFG。
        foreach (var guard in cfg.Blocks
                     .OrderByDescending(block => blockDepths.TryGetValue(block, out var depth) ? depth : -1)
                     .ThenByDescending(block => block.ID)
                     .ToList())
        {
            if (!cfg.Blocks.Contains(guard))
                continue;
            if (!TryGetMethodRgctxGuardCarrier(
                    method,
                    guard,
                    methodRgctxOffset,
                    definitions,
                    out var guardCarrier,
                    out var guardedMethod))
                continue;

            var first = guard.Successors[0];
            var second = guard.Successors[1];
            if (TryExciseMethodRgctxGuard(method, cfg, guard, first, second, guardCarrier, guardedMethod, definitions)
                || TryExciseMethodRgctxGuard(method, cfg, guard, second, first, guardCarrier, guardedMethod, definitions))
                removedAny = true;
        }

        return removedAny;
    }

    private static IReadOnlyDictionary<Block, int> ComputeBlockDepths(ISILControlFlowGraph cfg)
    {
        var depths = new Dictionary<Block, int> { [cfg.EntryBlock] = 0 };
        var queue = new Queue<Block>();
        queue.Enqueue(cfg.EntryBlock);
        while (queue.Count > 0)
        {
            var block = queue.Dequeue();
            var successorDepth = depths[block] + 1;
            foreach (var successor in block.Successors)
            {
                if (depths.ContainsKey(successor))
                    continue;
                depths.Add(successor, successorDepth);
                queue.Enqueue(successor);
            }
        }

        return depths;
    }

    private static bool TryGetMethodRgctxGuardCarrier(
        MethodAnalysisContext method,
        Block guard,
        long methodRgctxOffset,
        ILookup<LocalVariable, Instruction> definitions,
        out LocalVariable carrier,
        out MethodAnalysisContext guardedMethod)
    {
        carrier = null!;
        guardedMethod = null!;
        if (guard.BlockType != BlockType.TwoWay
            || guard.Successors.Count != 2
            || guard.Instructions.LastOrDefault() is not
            {
                OpCode: OpCode.ConditionalJump,
                Operands: [Block _, LocalVariable condition]
            })
        {
            if (guard.Instructions.LastOrDefault()?.OpCode == OpCode.ConditionalJump)
                Logger.VerboseNewline(
                    $"方法 rgctx 守卫跳过：method={method.FullName}，guard=b{guard.ID}，" +
                    $"blockType={guard.BlockType}，successors={guard.Successors.Count}，reason=控制流形态不匹配",
                    nameof(MetadataInitGuardRemover));
            return false;
        }

        var conditionDefinitions = GuardPrefixBlocks(guard)
            .SelectMany(block => block.Instructions)
            .Where(instruction => ReferenceEquals(instruction.Destination, condition))
            .Take(2)
            .ToArray();
        if (conditionDefinitions is not
            [
                {
                    OpCode: OpCode.CheckEqual or OpCode.CheckNotEqual,
                    Operands: [LocalVariable _, { } left, { } right]
                }
            ])
        {
            Logger.VerboseNewline(
                $"方法 rgctx 守卫跳过：method={method.FullName}，guard=b{guard.ID}，" +
                $"conditionDefinitions={conditionDefinitions.Length}，reason=条件定义不唯一",
                nameof(MetadataInitGuardRemover));
            return false;
        }

        var testedOperand = IsZero(right) ? left : IsZero(left) ? right : null;
        if (testedOperand == null)
            return false;

        if (ResolveMemoryOperand(testedOperand, UniqueDefinitions(definitions)) is not
            {
                Base: LocalVariable methodInfo,
                Index: null,
                Scale: 0,
                Addend: var addend
            }
            || addend != methodRgctxOffset)
        {
            Logger.VerboseNewline(
                $"方法 rgctx 守卫跳过：method={method.FullName}，guard=b{guard.ID}，reason=槽地址形态不匹配",
                nameof(MetadataInitGuardRemover));
            return false;
        }

        if (ResolveRuntimeMethodInfo(methodInfo, definitions) is not { } represented)
        {
            Logger.VerboseNewline(
                $"方法 rgctx 守卫跳过：method={method.FullName}，guard=b{guard.ID}，" +
                $"carrier={methodInfo}，type={methodInfo.Type?.FullName ?? "<未定型>"}，reason=载体不是MethodInfo",
                nameof(MetadataInitGuardRemover));
            return false;
        }

        carrier = methodInfo;
        guardedMethod = represented.RepresentedMethod;
        return true;
    }

    private static bool TryExciseMethodRgctxGuard(
        MethodAnalysisContext method,
        ISILControlFlowGraph cfg,
        Block guard,
        Block initEntry,
        Block merge,
        LocalVariable guardCarrier,
        MethodAnalysisContext guardedMethod,
        ILookup<LocalVariable, Instruction> definitions)
    {
        if (merge == cfg.EntryBlock || merge == cfg.ExitBlock
            || !TryCollectMethodRgctxInitRegion(
                method,
                cfg,
                guard,
                 initEntry,
                 merge,
                 guardCarrier,
                 guardedMethod,
                 definitions,
                 out var region))
            return false;

        Logger.VerboseNewline(
            $"方法 rgctx 初始化保护段删除：method={method.FullName}，guard=b{guard.ID}，region={region.Count}",
            nameof(MetadataInitGuardRemover));
        Excise(cfg, guard, initEntry, merge, region, false);
        return true;
    }

    private static bool TryCollectMethodRgctxInitRegion(
        MethodAnalysisContext method,
        ISILControlFlowGraph cfg,
        Block guard,
        Block initEntry,
        Block merge,
        LocalVariable guardCarrier,
        MethodAnalysisContext guardedMethod,
        ILookup<LocalVariable, Instruction> definitions,
        out HashSet<Block> region)
    {
        region = [];
        if (initEntry == merge || initEntry == guard)
        {
            Logger.VerboseNewline(
                $"方法 rgctx 初始化区域跳过：method={method.FullName}，guard=b{guard.ID}，reason=入口与合流重叠",
                nameof(MetadataInitGuardRemover));
            return false;
        }

        var initCallCount = 0;
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

            if (block == cfg.EntryBlock || block == cfg.ExitBlock || block == guard || !region.Add(block))
            {
                Logger.VerboseNewline(
                    $"方法 rgctx 初始化区域跳过：method={method.FullName}，guard=b{guard.ID}，" +
                    $"block=b{block.ID}，reason=区域触及边界或重复进入",
                    nameof(MetadataInitGuardRemover));
                return false;
            }

            foreach (var instruction in block.Instructions)
            {
                if (instruction.OpCode is OpCode.Nop or OpCode.Jump)
                    continue;

                // 原生初始化分支会先把保存寄存器搬到调用参数寄存器。只放行
                // 结果写入局部的纯计算；内存写入、额外调用或其他控制转移仍使区域失配。
                if (IsSideEffectFree(instruction))
                    continue;

                if (!IsMethodRgctxInitCall(method, instruction, guardCarrier, guardedMethod, definitions)
                    || ++initCallCount != 1)
                {
                    Logger.VerboseNewline(
                        $"方法 rgctx 初始化区域跳过：method={method.FullName}，guard=b{guard.ID}，" +
                        $"instruction={instruction}，count={initCallCount}，reason=副作用调用不匹配",
                        nameof(MetadataInitGuardRemover));
                    return false;
                }
            }

            foreach (var successor in block.Successors)
                queue.Enqueue(successor);
        }

        if (!reconverges || initCallCount != 1)
        {
            Logger.VerboseNewline(
                $"方法 rgctx 初始化区域跳过：method={method.FullName}，guard=b{guard.ID}，" +
                $"reconverges={reconverges}，count={initCallCount}，reason=区域未唯一合流",
                nameof(MetadataInitGuardRemover));
            return false;
        }

        var collected = region;
        var closed = collected.All(block =>
            block.Predecessors.All(predecessor => predecessor == guard || collected.Contains(predecessor))
            && block.Successors.All(successor => successor == merge || collected.Contains(successor)));
        if (!closed)
            Logger.VerboseNewline(
                $"方法 rgctx 初始化区域跳过：method={method.FullName}，guard=b{guard.ID}，reason=区域存在外部边",
                nameof(MetadataInitGuardRemover));
        return closed;
    }

    private static bool IsMethodRgctxInitCall(
        MethodAnalysisContext containingMethod,
        Instruction instruction,
        LocalVariable guardCarrier,
        MethodAnalysisContext guardedMethod,
        ILookup<LocalVariable, Instruction> definitions)
    {
        if (!instruction.IsCall)
            return false;

        // 当前 MethodInfo::rgctx_data 的精确零值守卫已经由调用方证明；该分支内唯一的
        // 运行时元数据初始化入口就是新版 IL2CPP 的正常形态。它不携带 MethodInfo 实参，
        // 因而不能套用下方“伪递归调用载体同源”规则，但同名调用若脱离该守卫仍不会命中。
        if (instruction.Operands.FirstOrDefault() is StringLiteral
            {
                Value: InitializeRuntimeMetadata
            })
            return true;

        if (instruction.Operands.FirstOrDefault() is not MethodAnalysisContext called
            || !SameMethod(called, guardedMethod))
            return false;

        if (instruction.Sources.FirstOrDefault() is LocalVariable callCarrier
            && ResolveRuntimeMethodInfo(callCarrier, definitions) is { } represented
            && SameMethod(represented.RepresentedMethod, guardedMethod))
        {
            return ReferenceEquals(
                ResolveUniqueMoveRoot(callCarrier, definitions),
                ResolveUniqueMoveRoot(guardCarrier, definitions));
        }

        // 某些共享泛型零参方法把“rgctx初始化入口”误绑定为该方法自身，但调用只携带
        // 被丢弃的返回值，没有可见MethodInfo实参。零显式参数、静态目标、无调用源且
        // 返回局部在整个方法中零消费，联合守卫中已证明的MethodInfo身份后才能闭合。
        if (!called.IsStatic
            || called.Parameters.Count != 0
            || instruction.Sources.Count != 0
            || instruction.Destination is not LocalVariable discardedResult)
            return false;

        return containingMethod.ControlFlowGraph!.Instructions.All(candidate =>
            ReferenceEquals(candidate, instruction)
            || candidate.Sources.All(source => !ReferenceEquals(source, discardedResult)));
    }

    private static RuntimeMethodInfoAnalysisContext? ResolveRuntimeMethodInfo(
        LocalVariable carrier,
        ILookup<LocalVariable, Instruction> definitions) =>
        carrier.Type as RuntimeMethodInfoAnalysisContext
        ?? RgctxResolver.ResolveForwardedRuntimeMetadataType(carrier, definitions) as RuntimeMethodInfoAnalysisContext;

    private static LocalVariable ResolveUniqueMoveRoot(
        LocalVariable carrier,
        ILookup<LocalVariable, Instruction> definitions)
    {
        var visited = new HashSet<LocalVariable>();
        var current = carrier;
        while (visited.Add(current)
               && definitions[current].Take(2).ToArray() is
               [
                   {
                       OpCode: OpCode.Move,
                       Operands: [LocalVariable _, LocalVariable previous]
                   }
               ])
            current = previous;
        return current;
    }

    private static IReadOnlyDictionary<LocalVariable, Instruction> UniqueDefinitions(
        ILookup<LocalVariable, Instruction> definitions) =>
        definitions
            .Where(group => group.Take(2).Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());

    private static bool SameMethod(MethodAnalysisContext left, MethodAnalysisContext right)
    {
        // 已闭合泛型调用使用 ConcreteGenericMethodAnalysisContext，而当前分析与
        // 隐藏 MethodInfo 可能指向基方法。比较前统一回到基方法身份。
        while (left is ConcreteGenericMethodAnalysisContext concreteLeft)
            left = concreteLeft.BaseMethodContext;
        while (right is ConcreteGenericMethodAnalysisContext concreteRight)
            right = concreteRight.BaseMethodContext;

        return ReferenceEquals(left, right)
               || left.Definition != null && ReferenceEquals(left.Definition, right.Definition);
    }

    private static bool IsZero(IOperand operand) => operand is Immediate { Value: 0 };

    /// <summary>
    /// 把直接类型元数据地址与原生零值的比较折叠为托管布尔常量。
    /// IL2CPP 的 <c>typeof(T)</c> 操作数在这里代表已解析的 Il2CppClass 指针，
    /// 并非待构造的托管对象；已成功解析的直接元数据地址恒为非零。
    /// </summary>
    internal static int FoldRuntimeClassNullComparisons(ISILControlFlowGraph cfg)
    {
        var definitions = UniqueDefinitions(cfg.Instructions
            .Where(instruction => instruction.Destination is LocalVariable)
            .ToLookup(instruction => (LocalVariable)instruction.Destination!));
        var folded = 0;
        foreach (var instruction in cfg.Instructions)
        {
            if (instruction.OpCode is not (OpCode.CheckEqual or OpCode.CheckNotEqual)
                || instruction.Operands is not [LocalVariable destination, { } left, { } right])
                continue;

            var metadata = IsZero(right) ? left : IsZero(left) ? right : null;
            var resolvedMetadata = ResolveDirectRuntimeClassMetadata(metadata, definitions);
            if (resolvedMetadata is not TypeAnalysisContext
                || resolvedMetadata is RuntimeMethodInfoAnalysisContext)
                continue;

            var comparisonResult = instruction.OpCode == OpCode.CheckNotEqual ? 1 : 0;
            instruction.OpCode = OpCode.Move;
            instruction.SetOperands(destination, new Immediate(comparisonResult));
            folded++;
        }

        return folded;
    }

    /// <summary>
    /// 沿唯一 Move 定义追溯运行时类载体；Phi、调用结果和多定义局部变量均在原位停止。
    /// </summary>
    private static IOperand? ResolveDirectRuntimeClassMetadata(
        IOperand? operand,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions)
    {
        var current = operand;
        var visited = new HashSet<LocalVariable>();
        while (current is LocalVariable local
               && visited.Add(local)
               && definitions.TryGetValue(local, out var definition)
               && definition is { OpCode: OpCode.Move, Operands: [LocalVariable _, { } source] })
            current = source;
        return current;
    }

    private static bool TryRemoveGuard(ISILControlFlowGraph cfg, Block guard, long initialisedFlagOffset)
    {
        if (guard.BlockType != BlockType.TwoWay || guard.Successors.Count != 2
            || guard.Instructions.Count == 0 || guard.Instructions[^1].OpCode != OpCode.ConditionalJump)
            return false;

        // see if we're checking Il2CppClass::initialized_and_no_error
        // that means this is runtime_init boilerplate and we can drop the block
        var initialisedFlagTest = HasInitialisedFlagTest(guard, initialisedFlagOffset);
        var constantMetadataFlagTest = GuardPrefixBlocks(guard)
            .Any(HasConstantMetadataFlagTest);

        // Either successor could be the init entry; the other is then the merge.
        var first = guard.Successors[0];
        var second = guard.Successors[1];

        return TryExcise(cfg, guard, first, second, initialisedFlagTest, constantMetadataFlagTest)
            || TryExcise(cfg, guard, second, first, initialisedFlagTest, constantMetadataFlagTest)
            || TryExciseThroughTrivialBypass(
                cfg,
                guard,
                first,
                second,
                initialisedFlagTest,
                constantMetadataFlagTest)
            || TryExciseThroughTrivialBypass(
                cfg,
                guard,
                second,
                first,
                initialisedFlagTest,
                constantMetadataFlagTest);
    }

    /// <summary>
    /// ARM64 的条件跳转经常先落到只含无条件跳转的代理块，再与初始化分支合流。
    /// 此处只接受由当前保护块独占、且仅含 Nop/Jump 的单后继代理块；这样既能恢复
    /// 原始类初始化菱形，也不会越过带业务计算或共享入口的真实控制流。
    /// </summary>
    private static bool TryExciseThroughTrivialBypass(
        ISILControlFlowGraph cfg,
        Block guard,
        Block initEntry,
        Block bypass,
        bool initialisedFlagTest,
        bool constantMetadataFlagTest)
    {
        if (!TryGetTrivialBypassMerge(guard, bypass, out var merge)
            || merge == cfg.EntryBlock
            || merge == cfg.ExitBlock
            || !TryCollectRegion(cfg, guard, initEntry, merge, initialisedFlagTest, out var region))
            return false;

        var guardSuccessorIndex = guard.Successors.IndexOf(bypass);
        var mergePredecessorIndex = merge.Predecessors.IndexOf(bypass);
        if (guardSuccessorIndex < 0 || mergePredecessorIndex < 0)
            return false;

        // 中文注释：用保护块替换代理块在合流点的位置，保持 Phi 输入与前驱索引一一对应。
        guard.Successors[guardSuccessorIndex] = merge;
        merge.Predecessors[mergePredecessorIndex] = guard;
        bypass.Predecessors.Clear();
        bypass.Successors.Clear();
        cfg.Blocks.Remove(bypass);

        Excise(cfg, guard, initEntry, merge, region, constantMetadataFlagTest);
        return true;
    }

    private static bool TryGetTrivialBypassMerge(Block guard, Block bypass, out Block merge)
    {
        merge = null!;
        if (bypass == guard
            || bypass.Predecessors.Count != 1
            || bypass.Predecessors[0] != guard
            || bypass.Successors.Count != 1
            || bypass.Instructions.Count == 0
            || bypass.Instructions.Any(instruction => instruction.OpCode is not (OpCode.Nop or OpCode.Jump))
            || bypass.Instructions[^1].OpCode != OpCode.Jump)
            return false;

        merge = bypass.Successors[0];
        return merge != bypass && merge != guard;
    }

    private static bool IsOne(IOperand operand) => operand is Immediate { Value: 1 };

    /// <summary>
    /// 判断保护块是否读取 <c>Il2CppClass::initialized_and_no_error</c>。
    /// ARM64 既会直接读取 <c>[class+0x135]</c>，也会先用 ADD 生成字段地址再读取
    /// <c>[address]</c>；两种严格等价的形态必须由同一处判定，避免把类初始化调用
    /// 错认成共享原生地址上的托管方法。
    /// </summary>
    internal static bool HasInitialisedFlagTest(Block guard, long initialisedFlagOffset)
    {
        var prefixInstructions = GuardPrefixBlocks(guard)
            .SelectMany(block => block.Instructions)
            .ToList();
        var definitions = guard.Instructions
            .Select(instruction => (instruction, destination: instruction.Destination as LocalVariable))
            .Where(pair => pair.destination != null)
            .GroupBy(pair => pair.destination!)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().instruction);

        foreach (var instruction in prefixInstructions)
        {
            if (instruction.OpCode != OpCode.And
                || instruction.Operands is not [_, { } flagOperand, { } mask]
                || !IsOne(mask))
                continue;

            var resolvedFlag = ResolveMemoryOperand(flagOperand, definitions);
            if (resolvedFlag is not { Index: null, Scale: 0 } flag)
                continue;

            if (flag.Base is LocalVariable && flag.Addend == initialisedFlagOffset)
                return true;

            if (flag is not { Base: LocalVariable address, Addend: 0 })
                continue;

            definitions.TryGetValue(address, out var addressDefinition);
            if (addressDefinition?.Operands is [_, _, Immediate offset]
                && offset.Value == initialisedFlagOffset)
                return true;
        }

        return false;
    }

    private static MemoryOperand? ResolveMemoryOperand(
        IOperand operand,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions)
    {
        for (var depth = 0; depth < 8 && operand is LocalVariable local; depth++)
        {
            if (!definitions.TryGetValue(local, out var definition)
                || definition is not { OpCode: OpCode.Move, Operands: [_, { } source] })
                return null;
            operand = source;
        }

        return operand is MemoryOperand memory ? memory : null;
    }

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

    internal static void Excise(
        ISILControlFlowGraph cfg,
        Block guard,
        Block initEntry,
        Block merge,
        HashSet<Block> region,
        bool constantMetadataFlagTest = false)
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
