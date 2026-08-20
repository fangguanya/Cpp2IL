using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Logging;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 恢复同一已初始化元数据槽在方法不同控制流位置上的运行时类型身份。
/// </summary>
/// <remarks>
/// ARM64 会把 TypeInfo 槽地址长期保存在 X19-X29，并在多个循环块中重复执行二次解引用。
/// 其中一处读取可由类初始化或静态字段闭合得到精确 Il2CppClass 类型，而同址的后续读取仍可能
/// 保持原生指针。槽地址和零偏移二次读取共同构成确定身份；只有整组已有唯一运行时类型时才传播。
/// </remarks>
public static class RuntimeClassSlotIdentityRecovery
{
    public sealed record LoadEvidence(
        Instruction Instruction,
        LocalVariable Destination);

    public sealed record SlotGroup(
        ulong Address,
        IReadOnlyList<LoadEvidence> EvidenceLoads,
        IReadOnlyList<LoadEvidence> TargetLoads)
    {
        public IReadOnlyList<LoadEvidence> Loads => TargetLoads;
    }

    /// <summary>
    /// 在任何元数据操作数改写前冻结“绝对槽 -&gt; 零偏移二次读取”的完整对应关系。
    /// </summary>
    internal static IReadOnlyList<SlotGroup> CaptureCandidates(MethodAnalysisContext method)
        => CaptureCandidates(method.ControlFlowGraph!.Instructions);

    internal static IReadOnlyList<SlotGroup> Capture(
        IReadOnlyList<Instruction> instructions,
        IReadOnlyCollection<ulong> initializedRuntimeMetadataSlots)
        => FilterInitialized(CaptureCandidates(instructions), initializedRuntimeMetadataSlots);

    private static IReadOnlyList<SlotGroup> CaptureCandidates(
        IReadOnlyList<Instruction> instructions)
    {
        var definitions = MetadataResolver.BuildConvergedDefinitionIndex(instructions);
        var resolvedAddresses = new Dictionary<LocalVariable, ulong?>();
        var groups = new Dictionary<ulong, List<LoadEvidence>>();

        foreach (var instruction in instructions)
        {
            if (instruction is not
                {
                    OpCode: OpCode.Move,
                    Operands:
                    [
                        LocalVariable destination,
                        MemoryOperand
                        {
                            Base: LocalVariable tableBase,
                            Index: null,
                            Scale: 0,
                            Addend: 0,
                        },
                    ],
                }
                || MetadataResolver.ResolveConvergedAbsoluteSlotAddress(
                    tableBase,
                    definitions,
                    [],
                    resolvedAddresses) is not { } address)
                continue;

            if (!groups.TryGetValue(address, out var loads))
                groups[address] = loads = [];
            loads.Add(new LoadEvidence(instruction, destination));
        }

        var candidates = groups
            .OrderBy(group => group.Key)
            .Select(group => new SlotGroup(group.Key, group.Value, group.Value))
            .ToArray();
        if (candidates.Length > 0)
        {
            Logger.VerboseNewline(
                $"运行时类型槽候选：groups={candidates.Length}，" +
                string.Join(",", candidates.Select(group => $"0x{group.Address:X}:{group.Loads.Count}")),
                nameof(RuntimeClassSlotIdentityRecovery));
        }
        return candidates;
    }

    /// <summary>
    /// 将早期冻结的类型种子与 SSA 简化后的当前 CFG 读取按绝对槽地址配对。
    /// </summary>
    internal static IReadOnlyList<SlotGroup> BuildCurrentPlan(
        IReadOnlyList<SlotGroup> evidenceCandidates,
        MethodAnalysisContext method,
        IReadOnlyCollection<ulong> initializedRuntimeMetadataSlots)
    {
        if (evidenceCandidates.Count == 0 || initializedRuntimeMetadataSlots.Count == 0)
            return [];

        var currentByAddress = CaptureCandidates(method)
            .ToDictionary(group => group.Address);
        var plan = evidenceCandidates
            .Where(group => initializedRuntimeMetadataSlots.Contains(group.Address)
                            && currentByAddress.ContainsKey(group.Address))
            .Select(group => new SlotGroup(
                group.Address,
                group.EvidenceLoads,
                currentByAddress[group.Address].TargetLoads))
            .ToArray();
        LogCaptured(plan);
        return plan;
    }

    internal static IReadOnlyList<SlotGroup> FilterInitialized(
        IReadOnlyList<SlotGroup> candidates,
        IReadOnlyCollection<ulong> initializedRuntimeMetadataSlots)
    {
        if (candidates.Count == 0 || initializedRuntimeMetadataSlots.Count == 0)
            return [];

        var captured = candidates
            .Where(group => initializedRuntimeMetadataSlots.Contains(group.Address))
            .ToArray();
        LogCaptured(captured);
        return captured;
    }

    private static void LogCaptured(IReadOnlyList<SlotGroup> captured)
    {
        if (captured.Count > 0)
        {
            Logger.VerboseNewline(
                $"运行时类型槽冻结：groups={captured.Count}，" +
                string.Join(",", captured.Select(group => $"0x{group.Address:X}:{group.Loads.Count}")),
                nameof(RuntimeClassSlotIdentityRecovery));
        }
    }

    /// <summary>
    /// 将同址组中的唯一 Il2CppClass 类型传播到未定型或仅带原生 ABI 占位类型的读取。
    /// </summary>
    internal static int Run(IReadOnlyList<SlotGroup>? groups)
    {
        if (groups == null || groups.Count == 0)
            return 0;

        var recovered = 0;
        foreach (var group in groups)
        {
            var representedTypes = group.EvidenceLoads
                .Concat(group.TargetLoads)
                .Select(load => load.Destination.Type)
                .OfType<RuntimeClassTypeAnalysisContext>()
                .Select(runtimeClass => runtimeClass.RepresentedType)
                .GroupBy(type => type.FullName, System.StringComparer.Ordinal)
                .Select(types => types.First())
                .ToArray();
            if (group.Loads.Count > 1)
            {
                Logger.VerboseNewline(
                    $"运行时类型槽收敛：slot=0x{group.Address:X}，loads={group.Loads.Count}，" +
                    $"types={string.Join(",", group.EvidenceLoads.Concat(group.TargetLoads).Select(load => load.Destination.Type?.FullName ?? "<null>"))}",
                    nameof(RuntimeClassSlotIdentityRecovery));
            }
            if (representedTypes.Length != 1)
                continue;

            var representedType = representedTypes[0];
            foreach (var load in group.TargetLoads)
            {
                if (!IsReplaceableRuntimeClassCarrier(load.Destination.Type, representedType))
                    continue;

                var changed = false;
                if (load.Destination.Type is not RuntimeClassTypeAnalysisContext)
                {
                    load.Destination.Type = new RuntimeClassTypeAnalysisContext(
                        representedType,
                        representedType.DeclaringAssembly);
                    changed = true;
                }

                if (load.Instruction.Operands.Count >= 2
                    && load.Instruction.Operands[1] is MemoryOperand)
                {
                    load.Instruction.SetOperand(1, representedType);
                    changed = true;
                }

                if (changed)
                    recovered++;
            }
        }

        return recovered;
    }

    private static bool IsReplaceableRuntimeClassCarrier(
        TypeAnalysisContext? existing,
        TypeAnalysisContext representedType)
    {
        if (existing == null)
            return true;
        if (existing is RuntimeClassTypeAnalysisContext runtimeClass)
            return GenericCallRebinder.TypesEquivalent(runtimeClass.RepresentedType, representedType);
        return existing.FullName is "System.Object" or "System.IntPtr" or "System.UIntPtr";
    }
}
