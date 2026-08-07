using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Logging;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 从已绑定调用被裁掉的隐藏MethodInfo实参反向证明运行时元数据槽，
/// 并断开这些槽与后续物理寄存器重用所形成的混合Phi。
/// </summary>
public static class RuntimeMetadataSlotResolver
{
    private readonly record struct SlotCandidate(
        ulong Address,
        LocalVariable Origin,
        string MethodIdentity);

    public static bool Run(MethodAnalysisContext method)
    {
        var instructions = method.ControlFlowGraph!.Instructions;
        var definitions = new Dictionary<LocalVariable, Instruction>();
        foreach (var instruction in instructions)
        {
            if (instruction.Destination is LocalVariable destination)
                definitions[destination] = instruction;
        }

        var candidates = new List<SlotCandidate>();
        foreach (var call in instructions)
        {
            if (!call.IsCall || call.Operands[0] is not MethodAnalysisContext called)
                continue;

            var expected = CallArgumentTrimmer.ExpectedOperandCount(call, called);
            for (var index = expected; index < call.Operands.Count; index++)
            {
                if (TryFindSlotOrigin(call.Operands[index], definitions, out var address, out var origin))
                {
                    candidates.Add(new(address, origin, MethodIdentity(called)));
                    Logger.VerboseNewline(
                        $"运行时元数据槽证据：method={method.Name}，call={called.Name}，" +
                        $"operand={index}，slot=0x{address:X}，origin={origin}");
                }
                else
                {
                    Logger.VerboseNewline(
                        $"运行时元数据槽未匹配隐参：method={method.Name}，call={called.Name}，" +
                        $"operand={index}，value={call.Operands[index]}");
                }
            }
        }

        var provenOrigins = new HashSet<LocalVariable>(candidates
            .GroupBy(candidate => candidate.Address)
            // 同一槽若指向两个不同托管方法，说明定义链证据发生冲突，整组保持原样。
            .Where(group => group.Select(candidate => candidate.MethodIdentity).Distinct().Count() == 1)
            .SelectMany(group => group.Select(candidate => candidate.Origin)));

        var changed = RepairMixedPhis(instructions, provenOrigins);
        // 该加载已被“绑定调用签名之外的隐藏实参”证明为 MethodInfo 槽值。
        // 在裁参前精确删除其唯一定义，可避免后续退 SSA/副本合并把同一物理寄存器
        // 的业务布尔值重新与元数据加载合并；调用上的多余引用随后由统一裁参器删除。
        changed |= RemoveProvenLoads(definitions, provenOrigins);
        if (candidates.Count > 0)
        {
            Logger.VerboseNewline(
                $"运行时元数据槽汇总：method={method.Name}，candidates={candidates.Count}，" +
                $"proven={provenOrigins.Count}，changed={changed}");
        }
        return changed;
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
        if (operand is MemoryOperand
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
            address = (ulong)argumentAbsolute.Addend;
            origin = argumentSlotPointer;
            return true;
        }

        var visited = new HashSet<LocalVariable>();
        while (operand is LocalVariable local && visited.Add(local))
        {
            if (!definitions.TryGetValue(local, out var definition)
                || definition.OpCode != OpCode.Move
                || definition.Operands.Count < 2)
                break;

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
                address = (ulong)directSlot.Addend;
                origin = local;
                return true;
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
                address = (ulong)absolute.Addend;
                origin = local;
                return true;
            }

            operand = source;
        }

        address = 0;
        origin = null!;
        return false;
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
