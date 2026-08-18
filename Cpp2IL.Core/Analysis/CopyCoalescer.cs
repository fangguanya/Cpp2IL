using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

// Merge the copies left behind by SSA destruction.
public static class CopyCoalescer
{
    public static void Run(MethodAnalysisContext method) => Run(method.ControlFlowGraph!);

    public static void Run(ISILControlFlowGraph cfg)
    {
        var copies = FindSameSlotCopies(cfg);
        var escapedSlots = FindEscapedSlotGroups(cfg);

        if (copies.Count == 0 && escapedSlots.Count == 0)
            return;

        var candidates = new HashSet<LocalVariable>();
        foreach (var (destination, source, _) in copies)
        {
            candidates.Add(destination);
            candidates.Add(source);
        }
        foreach (var group in escapedSlots)
            candidates.UnionWith(group);

        var interference = BuildInterference(cfg, candidates);
        var groups = new DisjointSet(candidates);

        foreach (var group in escapedSlots)
        {
            for (var i = 1; i < group.Count; i++)
            {
                var a = groups.Find(group[0]);
                var b = groups.Find(group[i]);

                // this 是 CIL 参数而不是可复用的物理寄存器槽。ARM64 在把实例保存到 X19 后
                // 会立即复用 X0 承载数组等调用结果；若与未定型结果合并，后续数组恢复会把
                // this 改写成数组局部，并把数组元素存储误解析为状态机字段写入。
                if (a.IsThis || b.IsThis
                    || a == b
                    || (a.Type != null && b.Type != null && !ReferenceEquals(a.Type, b.Type)))
                    continue;

                groups.Union(a, b);
            }
        }

        foreach (var (destination, source, _) in copies)
        {
            var a = groups.Find(destination);
            var b = groups.Find(source);

            // this 的参数身份必须贯穿整个方法；即使活跃区间不重叠，也不得把后续 X0
            // 返回值并入 this，否则 CIL 固定参数类型会被后期结果类型覆盖。
            if (a.IsThis || b.IsThis)
                continue;

            // 两个独立构造的具体泛型上下文可能不是同一对象，但只要结构化类型一致，
            // 副本合并就不需要任何转换；不同具体泛型仍保持独立。
            if (a.Type != null
                && b.Type != null
                && !GenericCallRebinder.TypesEquivalent(a.Type, b.Type))
                continue;

            if (a == b || Interferes(interference, groups, a, b))
                continue;

            groups.Union(a, b);
        }

        Rewrite(cfg, groups);
    }

    /// <summary>
    /// 删除退 SSA 后由晚期聚合/地址恢复最终证明为跨语义类型的 Phi 复制。该门必须位于
    /// 所有局部类型和栈槽重写完成之后；指令索引 -1 精确限定 Phi 生成物，不触碰原生 Move。
    /// </summary>
    internal static void PruneIncompatiblePhiCopies(ISILControlFlowGraph cfg)
    {
        foreach (var instruction in cfg.Instructions.Where(instruction =>
                     instruction is
                     {
                         Index: < 0,
                         OpCode: OpCode.Move,
                         Operands: [LocalVariable destination, LocalVariable source]
                     }
                     && !SsaForm.ShouldEmitPhiCopy(destination, source)))
        {
            instruction.OpCode = OpCode.Nop;
            instruction.SetOperands();
        }
    }

    /// <summary>
    /// 退 SSA 后，引用 Phi 的空入边已经被常量传播为整数零。若同一目标的其余入边全部是
    /// 同一个具体引用类型，则把零解释为 null 并恢复目标类型；这样后续字段偏移解析可以把
    /// 上一节点到当前节点的原生内存写入还原成真实托管字段赋值。
    /// </summary>
    internal static bool ResolveNullReferencePhiCopyTypes(ISILControlFlowGraph cfg)
    {
        var changed = false;
        var groups = cfg.Instructions
            .Where(instruction => instruction is
            {
                Index: < 0,
                OpCode: OpCode.Move,
                Operands: [LocalVariable, _]
            })
            .GroupBy(instruction => (LocalVariable)instruction.Operands[0]);

        foreach (var group in groups)
        {
            var sources = group.Select(instruction => instruction.Operands[1]).ToArray();
            if (sources.Length < 2
                || !sources.Any(source => source is Immediate { Value: 0 })
                || sources.Any(source => source is not LocalVariable
                    && source is not Immediate { Value: 0 }))
                continue;

            var referenceSources = sources.OfType<LocalVariable>().ToArray();
            if (referenceSources.Length == 0
                || referenceSources.Any(source => source.Type is not { IsValueType: false }))
                continue;

            var consensusType = referenceSources[0].Type!;
            if (referenceSources.Skip(1).Any(source =>
                    !GenericCallRebinder.TypesEquivalent(source.Type, consensusType)))
                continue;

            var destination = group.Key;
            if (destination.Type != null
                && !GenericCallRebinder.TypesEquivalent(destination.Type, consensusType)
                && !GenericCallRebinder.IsSharedObjectPlaceholder(destination.Type, consensusType))
                continue;

            if (!GenericCallRebinder.TypesEquivalent(destination.Type, consensusType))
            {
                destination.Type = consensusType;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// 删除“已有真实定义的托管目标 &lt;- 无任何来源的未定型局部”退 SSA 复制。该形态来自
    /// 异常清理边合并 X0 返回寄存器；源值既非参数也无定义，保留它只会把 object/I4 伪值
    /// 写入真实调用结果。目标必须另有定义，避免把唯一业务赋值误删。
    /// </summary>
    internal static int PruneUndefinedSourcePhiCopies(MethodAnalysisContext method)
    {
        var instructions = method.ControlFlowGraph!.Instructions;
        var definitionCounts = instructions
            .Where(instruction => instruction.Destination is LocalVariable)
            .GroupBy(instruction => (LocalVariable)instruction.Destination!)
            .ToDictionary(group => group.Key, group => group.Count());
        var definedLocals = new HashSet<LocalVariable>(definitionCounts.Keys);
        var pruned = 0;

        foreach (var instruction in instructions.Where(instruction => instruction is
                 {
                     Index: < 0,
                     OpCode: OpCode.Move,
                     Operands: [LocalVariable, LocalVariable]
                 }))
        {
            var destination = (LocalVariable)instruction.Operands[0];
            var source = (LocalVariable)instruction.Operands[1];
            var destinationDefinitionCount = definitionCounts.TryGetValue(destination, out var count) ? count : 0;
            if (destination.Type == null
                || source.Type != null
                || destinationDefinitionCount < 2
                || definedLocals.Contains(source)
                || method.ParameterLocals.Contains(source))
                continue;

            instruction.OpCode = OpCode.Nop;
            instruction.SetOperands();
            pruned++;
        }

        return pruned;
    }

    private static List<(LocalVariable Destination, LocalVariable Source, Instruction Instruction)> FindSameSlotCopies(ISILControlFlowGraph cfg)
    {
        var copies = new List<(LocalVariable, LocalVariable, Instruction)>();

        foreach (var instruction in cfg.Instructions)
        {
            if (instruction.OpCode == OpCode.Move
                && instruction.Operands[0] is LocalVariable destination
                && instruction.Operands[1] is LocalVariable source
                && !ReferenceEquals(destination, source)
                && destination.Register.Number == source.Register.Number)
                copies.Add((destination, source, instruction));
        }

        return copies;
    }

    private static List<List<LocalVariable>> FindEscapedSlotGroups(ISILControlFlowGraph cfg)
    {
        var escapedSlotNumbers = new HashSet<int>();
        foreach (var instruction in cfg.Instructions)
            foreach (var operand in instruction.Operands)
                if (operand is AddressOf { Target: LocalVariable addressed })
                    escapedSlotNumbers.Add(addressed.Register.Number);

        if (escapedSlotNumbers.Count == 0)
            return [];

        var bySlot = new Dictionary<int, List<LocalVariable>>();
        var seen = new HashSet<LocalVariable>();

        foreach (var instruction in cfg.Instructions)
        {
            var locals = Used(instruction);
            if (Defined(instruction) is { } defined)
                locals = locals.Append(defined);

            foreach (var local in locals)
                if (escapedSlotNumbers.Contains(local.Register.Number) && seen.Add(local))
                {
                    if (!bySlot.TryGetValue(local.Register.Number, out var versions))
                        bySlot[local.Register.Number] = versions = [];
                    versions.Add(local);
                }
        }

        return bySlot.Values.Where(versions => versions.Count > 1).ToList();
    }

    private static bool Interferes(Dictionary<LocalVariable, HashSet<LocalVariable>> interference, DisjointSet groups, LocalVariable a, LocalVariable b)
    {
        foreach (var member in groups.Members(a))
        {
            if (!interference.TryGetValue(member, out var edges))
                continue;

            foreach (var edge in edges)
                if (groups.Find(edge) == b)
                    return true;
        }

        return false;
    }

    
    // interference edges between the candidates.
    // whatever is alive where a local is assigned has to keep its own storage, because both values are wanted at once.
    // except: the source of a copy is exempt against its own destination, so after <c>a = b</c> the two agree, because that is, after all, the whole point.
    private static Dictionary<LocalVariable, HashSet<LocalVariable>> BuildInterference(ISILControlFlowGraph cfg, HashSet<LocalVariable> candidates)
    {
        var liveOut = ComputeBlockLiveOut(cfg);
        var interference = new Dictionary<LocalVariable, HashSet<LocalVariable>>();

        void Connect(LocalVariable a, LocalVariable b)
        {
            if (ReferenceEquals(a, b))
                return;

            if (!interference.TryGetValue(a, out var edges))
                interference[a] = edges = [];
            edges.Add(b);

            if (!interference.TryGetValue(b, out var otherEdges))
                interference[b] = otherEdges = [];
            otherEdges.Add(a);
        }

        foreach (var block in cfg.Blocks)
        {
            var live = new HashSet<LocalVariable>(liveOut[block]);

            for (var i = block.Instructions.Count - 1; i >= 0; i--)
            {
                var instruction = block.Instructions[i];
                var defined = Defined(instruction);

                if (defined != null && candidates.Contains(defined))
                {
                    var copySource = instruction.OpCode == OpCode.Move ? instruction.Operands[1] as LocalVariable : null;

                    foreach (var other in live)
                        if (candidates.Contains(other) && !ReferenceEquals(other, copySource))
                            Connect(defined, other);
                }

                if (defined != null)
                    live.Remove(defined);

                foreach (var used in Used(instruction))
                    live.Add(used);
            }
        }

        return interference;
    }

    private static Dictionary<Block, HashSet<LocalVariable>> ComputeBlockLiveOut(ISILControlFlowGraph cfg)
    {
        var liveIn = new Dictionary<Block, HashSet<LocalVariable>>();
        var liveOut = new Dictionary<Block, HashSet<LocalVariable>>();

        foreach (var block in cfg.Blocks)
        {
            liveIn[block] = [];
            liveOut[block] = [];
        }

        var remaining = new Stack<Block>(cfg.Blocks);

        while (remaining.Count > 0)
        {
            var block = remaining.Pop();

            var outSet = new HashSet<LocalVariable>();
            foreach (var successor in block.Successors)
                outSet.UnionWith(liveIn[successor]);

            var inSet = new HashSet<LocalVariable>(outSet);
            for (var i = block.Instructions.Count - 1; i >= 0; i--)
            {
                var instruction = block.Instructions[i];

                if (Defined(instruction) is { } defined)
                    inSet.Remove(defined);

                foreach (var used in Used(instruction))
                    inSet.Add(used);
            }

            if (!outSet.SetEquals(liveOut[block]) || !inSet.SetEquals(liveIn[block]))
            {
                liveOut[block] = outSet;
                liveIn[block] = inSet;
                
                foreach (var predecessor in block.Predecessors)
                    remaining.Push(predecessor);
            }
        }

        return liveOut;
    }

    private static LocalVariable? Defined(Instruction instruction) => instruction.Destination as LocalVariable;

    private static IEnumerable<LocalVariable> Used(Instruction instruction)
    {
        var defined = Defined(instruction);

        foreach (var operand in instruction.Operands)
        {
            switch (operand)
            {
                case LocalVariable local when !ReferenceEquals(local, defined):
                    yield return local;
                    break;
                case MemoryOperand memory:
                    if (memory.Base is LocalVariable memoryBase)
                        yield return memoryBase;
                    if (memory.Index is LocalVariable memoryIndex)
                        yield return memoryIndex;
                    break;
                case FieldReference field:
                    yield return field.Local;
                    break;
                case ArrayAccess array:
                    yield return array.Array;
                    if (array.Index is LocalVariable arrayIndex)
                        yield return arrayIndex;
                    break;
                case ArrayLength length:
                    yield return length.Array;
                    break;
                case StringLength length:
                    yield return length.Value;
                    break;
                case ListCount count:
                    yield return count.Value;
                    break;
                case AddressOf { Target: LocalVariable addressed }:
                    yield return addressed;
                    break;
                case HomogeneousFloatingAggregateArgument aggregate:
                    foreach (var component in aggregate.Components.OfType<LocalVariable>())
                        yield return component;
                    break;
            }
        }
    }

    private static void Rewrite(ISILControlFlowGraph cfg, DisjointSet groups)
    {
        foreach (var block in cfg.Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                for (var i = 0; i < instruction.Operands.Count; i++)
                {
                    switch (instruction.Operands[i])
                    {
                        case LocalVariable local:
                            instruction.SetOperand(i, groups.Find(local));
                            break;
                        case MemoryOperand memory:
                            if (memory.Base is LocalVariable memoryBase)
                                memory.Base = groups.Find(memoryBase);
                            if (memory.Index is LocalVariable memoryIndex)
                                memory.Index = groups.Find(memoryIndex);
                            instruction.SetOperand(i, memory); // MemoryOperand is a struct, write the copy back
                            break;
                        case FieldReference field:
                            field.Local = groups.Find(field.Local);
                            break;
                        case ArrayAccess array:
                            array.Array = groups.Find(array.Array);
                            if (array.Index is LocalVariable arrayIndex)
                                array.Index = groups.Find(arrayIndex);
                            break;
                        case ArrayLength length:
                            length.Array = groups.Find(length.Array);
                            break;
                        case StringLength length:
                            length.Value = groups.Find(length.Value);
                            break;
                        case ListCount count:
                            count.Value = groups.Find(count.Value);
                            break;
                        case AddressOf { Target: LocalVariable addressed } addressOf:
                            addressOf.Target = groups.Find(addressed);
                            break;
                        case HomogeneousFloatingAggregateArgument aggregate:
                            for (var componentIndex = 0; componentIndex < aggregate.Components.Count; componentIndex++)
                                if (aggregate.Components[componentIndex] is LocalVariable component)
                                    aggregate.Components[componentIndex] = groups.Find(component);
                            break;
                    }
                }
            }

            // the copies that have become self-assignments are why we did this, they're now noise
            foreach (var instruction in block.Instructions)
            {
                if (instruction.OpCode == OpCode.Move
                    && instruction.Operands[0] is LocalVariable destination
                    && instruction.Operands[1] is LocalVariable source
                    && ReferenceEquals(destination, source))
                {
                    instruction.OpCode = OpCode.Nop;
                    instruction.SetOperands();
                }
            }
        }
    }

    private class DisjointSet(IEnumerable<LocalVariable> locals)
    {
        private readonly Dictionary<LocalVariable, LocalVariable> _parent = locals.ToDictionary(l => l, l => l);
        private readonly Dictionary<LocalVariable, List<LocalVariable>> _members = locals.ToDictionary(l => l, l => new List<LocalVariable> { l });

        public LocalVariable Find(LocalVariable local)
        {
            if (!_parent.TryGetValue(local, out var parent))
                return local;

            while (!ReferenceEquals(parent, _parent[parent]))
                parent = _parent[parent];

            _parent[local] = parent;
            return parent;
        }

        public IEnumerable<LocalVariable> Members(LocalVariable representative) => _members[representative];

        public void Union(LocalVariable a, LocalVariable b)
        {
            // keep whichever already carries a type
            var (keep, drop) = a.Type != null || b.Type == null ? (a, b) : (b, a);

            _parent[drop] = keep;
            _members[keep].AddRange(_members[drop]);
            _members.Remove(drop);

            keep.Type ??= drop.Type;
            keep.IsThis |= drop.IsThis;
            keep.IsReturn |= drop.IsReturn;
            keep.IsMethodInfo |= drop.IsMethodInfo;
        }
    }
}
