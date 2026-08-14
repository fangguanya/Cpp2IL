using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Logging;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 从已绑定调用被裁掉的隐藏MethodInfo实参反向证明运行时元数据槽，
/// 并断开这些槽与后续物理寄存器重用所形成的混合Phi。
/// </summary>
public static class RuntimeMetadataSlotResolver
{
    private const string InitializeRuntimeMetadata = "il2cpp_codegen_initialize_runtime_metadata";
    private const string InitializeMethod = "il2cpp_codegen_initialize_method";

    private readonly record struct SlotCandidate(
        ulong Address,
        LocalVariable Origin,
        string MethodIdentity);

    public static HashSet<ulong> CaptureInitializedSlotAddresses(MethodAnalysisContext method)
    {
        var instructions = method.ControlFlowGraph!.Instructions;
        return CollectInitializedSlotAddresses(instructions, BuildDefinitions(instructions));
    }

    public static bool Run(MethodAnalysisContext method)
        => Run(method, CaptureInitializedSlotAddresses(method));

    public static bool Run(
        MethodAnalysisContext method,
        IReadOnlyCollection<ulong> capturedInitializedSlots)
    {
        var instructions = method.ControlFlowGraph!.Instructions;
        var definitions = BuildDefinitions(instructions);

        var candidates = new List<SlotCandidate>();
        foreach (var call in instructions)
        {
            if (!call.IsCall || call.Operands[0] is not MethodAnalysisContext called)
                continue;

            var expected = CallArgumentTrimmer.ExpectedOperandCount(call, called);
            for (var index = expected; index < call.Operands.Count; index++)
            {
                var origins = FindSlotOrigins(call.Operands[index], definitions);
                if (origins.Count > 0)
                {
                    foreach (var (address, origin) in origins)
                    {
                        candidates.Add(new(address, origin, MethodIdentity(called)));
                        Logger.VerboseNewline(
                            $"运行时元数据槽证据：method={method.Name}，call={called.Name}，" +
                            $"operand={index}，slot=0x{address:X}，origin={origin}");
                    }
                }
                else
                {
                    var definitionDetail = call.Operands[index] is LocalVariable unmatchedLocal
                                           && definitions.TryGetValue(unmatchedLocal, out var unmatchedDefinition)
                        ? unmatchedDefinition.ToString()
                        : "<无局部定义>";
                    Logger.VerboseNewline(
                        $"运行时元数据槽未匹配隐参：method={method.Name}，call={called.Name}，" +
                        $"operand={index}，value={call.Operands[index]}，definition={definitionDetail}");
                }
            }
        }

        var provenOrigins = new HashSet<LocalVariable>(candidates
            .GroupBy(candidate => candidate.Address)
            // 同一槽若指向两个不同托管方法，说明定义链证据发生冲突，整组保持原样。
            .Where(group => group.Select(candidate => candidate.MethodIdentity).Distinct().Count() == 1)
            .SelectMany(group => group.Select(candidate => candidate.Origin)));

        var initializedSlots = new HashSet<ulong>(capturedInitializedSlots);
        var libContext = method.AppContext.LibCpp2IlContext;
        foreach (var instruction in instructions)
        {
            if (instruction.OpCode == OpCode.Move
                && instruction.Operands is
                [LocalVariable destination, MemoryOperand
                    {
                        Base: null,
                        Index: null,
                        Scale: 0,
                        Addend: >= 0
                    } absolute]
                && initializedSlots.Contains((ulong)absolute.Addend)
                && IsHiddenMethodInfoUsage(MetadataResolver.ResolveAbsoluteSlotUsage(
                    (ulong)absolute.Addend,
                    libContext.GetAnyGlobalByAddress,
                    libContext.CheckForPost27GlobalTableEntryAt)?.Type))
            {
                // 初始化保护区只证明该地址属于运行时元数据目录；继续以元数据类型
                // 证明它确实是隐藏MethodInfo，字符串、类型和字段槽均保留为业务定义。
                provenOrigins.Add(destination);
            }
        }

        var changed = RepairMixedPhis(instructions, provenOrigins);
        // 该加载已被“绑定调用签名之外的隐藏实参”证明为 MethodInfo 槽值。
        // 在裁参前精确删除其唯一定义，可避免后续退 SSA/副本合并把同一物理寄存器
        // 的业务布尔值重新与元数据加载合并；调用上的多余引用随后由统一裁参器删除。
        changed |= RemoveProvenLoads(definitions, provenOrigins);
        if (candidates.Count > 0 || initializedSlots.Count > 0)
        {
            Logger.VerboseNewline(
                $"运行时元数据槽汇总：method={method.Name}，candidates={candidates.Count}，" +
                $"initializedSlots={initializedSlots.Count}，proven={provenOrigins.Count}，changed={changed}");
        }
        return changed;
    }

    /// <summary>
    /// 仅方法定义和泛型方法引用可作为托管调用签名之外的隐藏MethodInfo实参。
    /// </summary>
    internal static bool IsHiddenMethodInfoUsage(MetadataUsageType? usageType)
        => usageType is MetadataUsageType.MethodDef or MetadataUsageType.MethodRef;

    private static Dictionary<LocalVariable, Instruction> BuildDefinitions(
        IReadOnlyList<Instruction> instructions)
    {
        var definitions = new Dictionary<LocalVariable, Instruction>();
        foreach (var instruction in instructions)
        {
            if (instruction.Destination is LocalVariable destination)
                definitions[destination] = instruction;
        }

        return definitions;
    }

    internal static HashSet<ulong> CollectInitializedSlotAddresses(
        IReadOnlyList<Instruction> instructions,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions)
    {
        var slots = new HashSet<ulong>();
        foreach (var instruction in instructions)
        {
            if (!instruction.IsCall
                || instruction.Operands.Count < 2
                || instruction.Operands[0] is not StringLiteral { Value: var name }
                || name is not (InitializeRuntimeMetadata or InitializeMethod))
                continue;

            // 目标与可能的返回局部变量都不会形成槽证据；从其余操作数反向遍历
            // Move/Phi，可同时覆盖 ARM64 直接绝对加载与间接槽指针形态。
            var firstArgument = instruction.OpCode == OpCode.Call ? 2 : 1;
            for (var index = firstArgument; index < instruction.Operands.Count; index++)
            {
                foreach (var (address, _) in FindSlotOrigins(instruction.Operands[index], definitions))
                    slots.Add(address);
            }
        }

        return slots;
    }

    internal static bool RemoveProvenLoads(
        IReadOnlyDictionary<LocalVariable, Instruction> definitions,
        IReadOnlyCollection<LocalVariable> provenOrigins)
    {
        var changed = false;
        foreach (var origin in provenOrigins)
        {
            if (!definitions.TryGetValue(origin, out var definition)
                || definition.OpCode != OpCode.Move
                || definition.Operands.Count < 2
                || definition.Operands[1] is not MemoryOperand)
                continue;

            definition.OpCode = OpCode.Nop;
            definition.SetOperands();
            changed = true;
        }

        return changed;
    }

    internal static bool TryFindSlotOrigin(
        IOperand operand,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions,
        out ulong address,
        out LocalVariable origin)
    {
        var origins = FindSlotOrigins(operand, definitions);
        if (origins.Count > 0)
        {
            (address, origin) = origins[0];
            return true;
        }

        address = 0;
        origin = null!;
        return false;
    }

    internal static IReadOnlyList<(ulong Address, LocalVariable Origin)> FindSlotOrigins(
        IOperand operand,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions)
    {
        var results = new List<(ulong Address, LocalVariable Origin)>();
        var visited = new HashSet<LocalVariable>();
        var pending = new Stack<IOperand>();
        pending.Push(operand);

        void AddResult(ulong address, LocalVariable origin)
        {
            if (!results.Any(result => result.Address == address && ReferenceEquals(result.Origin, origin)))
                results.Add((address, origin));
        }

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is MemoryOperand
                {
                    Base: LocalVariable argumentSlotPointer,
                    Index: null,
                    Scale: 0,
                    Addend: 0
                }
                && definitions.TryGetValue(argumentSlotPointer, out var argumentSlotDefinition)
                && argumentSlotDefinition.OpCode == OpCode.Move
                && argumentSlotDefinition.Operands.Count >= 2
                && argumentSlotDefinition.Operands[1] is MemoryOperand
                {
                    Base: null,
                    Index: null,
                    Scale: 0,
                    Addend: >= 0
                } argumentAbsolute)
            {
                // LDR Xn,[绝对槽] 后直接以 [Xn] 作为 MethodInfo 实参时，调用操作数
                // 自身就是第二次解引用，不会再生成承接结果的局部变量。
                AddResult((ulong)argumentAbsolute.Addend, argumentSlotPointer);
                continue;
            }

            if (current is not LocalVariable local || !visited.Add(local)
                || !definitions.TryGetValue(local, out var definition))
                continue;

            if (definition.OpCode == OpCode.Phi)
            {
                // 隐藏实参可能经过循环汇合；遍历全部来源才能找到被业务寄存器重用
                // 包裹住的元数据槽，而不是把整个 Phi 误当成一个普通局部变量。
                for (var index = 1; index < definition.Operands.Count; index++)
                    pending.Push(definition.Operands[index]);
                continue;
            }

            if (definition.OpCode != OpCode.Move || definition.Operands.Count < 2)
                continue;

            var source = definition.Operands[1];
            if (source is MemoryOperand
                {
                    Base: null,
                    Index: null,
                    Scale: 0,
                    Addend: >= 0
                } directSlot)
            {
                // ARM64 常见形态是 ADRP+LDR 已折叠成一次绝对地址加载；该局部变量
                // 本身就是传给托管调用的 MethodInfo，而不是槽地址指针。
                AddResult((ulong)directSlot.Addend, local);
                continue;
            }

            if (source is MemoryOperand
                {
                    Base: LocalVariable slotPointer,
                    Index: null,
                    Scale: 0,
                    Addend: 0
                }
                && definitions.TryGetValue(slotPointer, out var slotDefinition)
                && slotDefinition.OpCode == OpCode.Move
                && slotDefinition.Operands.Count >= 2
                && slotDefinition.Operands[1] is MemoryOperand
                {
                    Base: null,
                    Index: null,
                    Scale: 0,
                    Addend: >= 0
                } absolute)
            {
                AddResult((ulong)absolute.Addend, local);
            }

            pending.Push(source);
        }

        return results;
    }

    internal static bool RepairMixedPhis(
        IReadOnlyList<Instruction> instructions,
        IReadOnlyCollection<LocalVariable> provenOrigins)
    {
        var tainted = new HashSet<LocalVariable>(provenOrigins);
        var changed = false;
        var progress = true;

        while (progress)
        {
            progress = false;
            foreach (var instruction in instructions)
            {
                if (instruction.OpCode == OpCode.Move
                    && instruction.Operands is [LocalVariable destination, LocalVariable source]
                    && tainted.Contains(source)
                    && tainted.Add(destination))
                {
                    progress = true;
                    continue;
                }

                if (instruction.OpCode != OpCode.Phi
                    || instruction.Operands.Count < 3
                    || instruction.Operands[0] is not LocalVariable phiDestination)
                    continue;

                var sources = instruction.Operands.Skip(1).ToArray();
                var taintedSources = sources
                    .Select((source, index) => (source, index))
                    .Where(item => item.source is LocalVariable local && tainted.Contains(local))
                    .ToArray();
                if (taintedSources.Length == 0)
                    continue;

                if (taintedSources.Length == sources.Length)
                {
                    if (tainted.Add(phiDestination))
                        progress = true;
                    continue;
                }

                // 混合Phi中的运行时槽值没有托管业务含义；以同一汇合点的非槽输入
                // 代表该未定义寄存器路径，保留边数和前驱位置关系不变。
                var replacement = sources.First(source =>
                    source is not LocalVariable local || !tainted.Contains(local));
                var repairedThisPhi = false;
                foreach (var (phiSource, sourceIndex) in taintedSources)
                {
                    if (ReferenceEquals(phiSource, replacement))
                        continue;

                    instruction.SetOperand(1 + sourceIndex, replacement);
                    repairedThisPhi = true;
                }

                if (repairedThisPhi)
                {
                    changed = true;
                    progress = true;
                }
            }
        }

        return changed;
    }

    private static string MethodIdentity(MethodAnalysisContext method)
        => $"{method.DeclaringType?.FullName}::{method.Name}({string.Join(",", method.Parameters.Select(parameter => parameter.ParameterType.FullName))})->{method.ReturnType.FullName}";
}
