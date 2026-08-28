using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 终态整数控制状态计划。类型恢复与控制流恢复共享同一份载体和值域目录，禁止重复扫描方法图。
/// </summary>
public sealed class IntegerControlStatePlan
{
    private readonly Dictionary<LocalVariable, HashSet<long>> _domains;
    private static readonly IReadOnlySet<long> NoValues = new HashSet<long>();

    internal IntegerControlStatePlan(IReadOnlyDictionary<LocalVariable, HashSet<long>> domains)
    {
        _domains = domains.ToDictionary(
            pair => pair.Key,
            pair => new HashSet<long>(pair.Value));
    }

    internal IEnumerable<LocalVariable> Carriers => _domains.Keys;

    internal bool Contains(LocalVariable local) => _domains.ContainsKey(local);

    internal bool Accepts(LocalVariable local, IEnumerable<long> values) =>
        _domains.TryGetValue(local, out var domain)
        && values.All(domain.Contains);

    internal bool TryGetDomain(LocalVariable local, out IReadOnlySet<long> domain)
    {
        if (_domains.TryGetValue(local, out var values))
        {
            domain = values;
            return true;
        }

        domain = NoValues;
        return false;
    }
}

/// <summary>
/// 以有限整数值域和条件边约束折叠退 SSA 后可证明的条件边，并同步修正控制流图。
/// </summary>
/// <remarks>
/// 同一物理局部在退 SSA 后可有多个顺序定义；全方法定义共识会丢失“本块最后写入”和
/// 跳转边与顺序边的已知条件。本恢复器沿可执行边传播 Bottom/FiniteSet/Top，未知值始终失败关闭。
/// </remarks>
public static class ConstantControlFlowRecovery
{
    private const int MaxFiniteValueCount = 64;

    internal static int FiniteValueLimit => MaxFiniteValueCount;

    internal static int Run(ISILControlFlowGraph graph, IntegerControlStatePlan plan)
    {
        if (graph == null)
            throw new ArgumentNullException(nameof(graph));
        if (plan == null)
            throw new ArgumentNullException(nameof(plan));

        var decisions = new EdgeConstrainedAnalysis(graph, plan).Run();
        var rewritten = 0;
        foreach (var pair in decisions)
        {
            var block = pair.Key;
            if (block.Instructions.LastOrDefault() is not
                { OpCode: OpCode.ConditionalJump, Operands: [Block target, _] } branch
                || !TryGetFallthrough(block, target, out var fallthrough))
                continue;

            var destination = pair.Value ? target : fallthrough;
            var discarded = pair.Value ? fallthrough : target;
            branch.OpCode = OpCode.Jump;
            branch.SetOperands(destination);

            // 中文注释：删除控制流边时同步移除 Phi 的同索引输入；只改跳转操作数会留下
            // 悬空前驱，并使后续支配关系、发射顺序和栈验证观察到不同的图。
            block.Successors.RemoveAll(successor => ReferenceEquals(successor, discarded));
            ISILControlFlowGraph.RemovePredecessorAndPhiInputs(discarded, block);
            block.CalculateBlockType();
            rewritten++;
        }

        if (rewritten == 0)
            return 0;

        graph.RemoveUnreachableBlocks();
        graph.RemoveNops();
        graph.RemoveEmptyBlocks();
        foreach (var block in graph.Blocks)
            block.CalculateBlockType();
        return rewritten;
    }

    private static bool TryGetFallthrough(Block block, Block target, out Block fallthrough)
    {
        var candidates = block.Successors
            .Where(successor => !ReferenceEquals(successor, target))
            .Distinct()
            .ToArray();
        if (!block.Successors.Any(successor => ReferenceEquals(successor, target))
            || candidates.Length != 1)
        {
            fallthrough = null!;
            return false;
        }

        fallthrough = candidates[0];
        return true;
    }

    /// <summary>
    /// 统一计算有符号或无符号整数比较。状态传播与延迟谓词求值共用此入口，
    /// 避免两处比较语义发生漂移。
    /// </summary>
    private static bool TryEvaluateComparison(
        OpCode opCode,
        long left,
        long right,
        bool int32,
        out bool result)
    {
        result = opCode switch
        {
            OpCode.CheckEqual => left == right,
            OpCode.CheckNotEqual => left != right,
            OpCode.CheckGreater => left > right,
            OpCode.CheckGreaterOrEqual => left >= right,
            OpCode.CheckLess => left < right,
            OpCode.CheckLessOrEqual => left <= right,
            OpCode.CheckGreaterUnsigned => int32
                ? unchecked((uint)(int)left) > unchecked((uint)(int)right)
                : unchecked((ulong)left) > unchecked((ulong)right),
            OpCode.CheckGreaterOrEqualUnsigned => int32
                ? unchecked((uint)(int)left) >= unchecked((uint)(int)right)
                : unchecked((ulong)left) >= unchecked((ulong)right),
            OpCode.CheckLessUnsigned => int32
                ? unchecked((uint)(int)left) < unchecked((uint)(int)right)
                : unchecked((ulong)left) < unchecked((ulong)right),
            OpCode.CheckLessOrEqualUnsigned => int32
                ? unchecked((uint)(int)left) <= unchecked((uint)(int)right)
                : unchecked((ulong)left) <= unchecked((ulong)right),
            _ => false,
        };
        return opCode is OpCode.CheckEqual
            or OpCode.CheckNotEqual
            or OpCode.CheckGreater
            or OpCode.CheckGreaterOrEqual
            or OpCode.CheckLess
            or OpCode.CheckLessOrEqual
            or OpCode.CheckGreaterUnsigned
            or OpCode.CheckGreaterOrEqualUnsigned
            or OpCode.CheckLessUnsigned
            or OpCode.CheckLessOrEqualUnsigned;
    }

    private sealed class EdgeConstrainedAnalysis
    {
        private readonly ISILControlFlowGraph _graph;
        private readonly IntegerControlStatePlan _plan;
        private readonly HashSet<LocalVariable> _trackedLocals;
        private readonly Dictionary<Block, FlowState> _entryStates = [];
        private readonly Dictionary<Block, bool> _decisions = [];

        internal EdgeConstrainedAnalysis(
            ISILControlFlowGraph graph,
            IntegerControlStatePlan plan)
        {
            _graph = graph;
            _plan = plan;
            _trackedLocals = BuildTrackedLocalClosure(graph, plan);
        }

        internal IReadOnlyDictionary<Block, bool> Run()
        {
            var queue = new Queue<Block>();
            var queued = new HashSet<Block>();
            _entryStates[_graph.EntryBlock] = FlowState.Reachable();
            Enqueue(_graph.EntryBlock, queue, queued);

            while (queue.Count > 0)
            {
                var block = queue.Dequeue();
                queued.Remove(block);
                var state = _entryStates[block].Clone();
                foreach (var instruction in block.Instructions)
                {
                    Transfer(instruction, state);
                    if (state.IsBottom)
                        break;
                }

                if (state.IsBottom)
                    continue;

                if (block.Instructions.LastOrDefault() is
                        { OpCode: OpCode.ConditionalJump, Operands: [Block target, var condition] }
                        && TryGetFallthrough(block, target, out var fallthrough))
                {
                    var takenState = Constrain(state, condition, expectedTrue: true);
                    var fallthroughState = Constrain(state, condition, expectedTrue: false);
                    var canTake = !takenState.IsBottom;
                    var canFallThrough = !fallthroughState.IsBottom;

                    if (canTake != canFallThrough)
                        _decisions[block] = canTake;
                    else
                        _decisions.Remove(block);

                    if (canTake)
                        Propagate(target, takenState, queue, queued);
                    if (canFallThrough)
                        Propagate(fallthrough, fallthroughState, queue, queued);
                    continue;
                }

                _decisions.Remove(block);
                foreach (var successor in block.Successors.Distinct())
                    Propagate(successor, state, queue, queued);
            }

            return _decisions;
        }

        private static HashSet<LocalVariable> BuildTrackedLocalClosure(
            ISILControlFlowGraph graph,
            IntegerControlStatePlan plan)
        {
            var instructions = graph.Instructions;
            var definitions = new Dictionary<LocalVariable, List<Instruction>>();
            foreach (var instruction in instructions)
            {
                if (instruction.Destination is not LocalVariable destination)
                    continue;
                if (!definitions.TryGetValue(destination, out var localDefinitions))
                    definitions[destination] = localDefinitions = [];
                localDefinitions.Add(instruction);
            }

            var tracked = new HashSet<LocalVariable>(plan.Carriers);
            var pending = new Queue<LocalVariable>(tracked);
            foreach (var branchCondition in instructions
                         .Where(instruction => instruction.OpCode == OpCode.ConditionalJump)
                         .Select(instruction => instruction.Operands.Count > 1
                             ? instruction.Operands[1]
                             : null)
                         .OfType<LocalVariable>())
            {
                if (tracked.Add(branchCondition))
                    pending.Enqueue(branchCondition);
            }

            // 中文注释：只追踪能影响计划载体或条件跳转的纯值反向切片，避免大型方法为
            // 与控制流无关的常量局部复制整份边状态。
            while (pending.Count > 0)
            {
                var local = pending.Dequeue();
                if (!definitions.TryGetValue(local, out var localDefinitions))
                    continue;

                foreach (var definition in localDefinitions.Where(IsSupportedDefinition))
                {
                    foreach (var source in definition.Sources.OfType<LocalVariable>())
                    {
                        if (tracked.Add(source))
                            pending.Enqueue(source);
                    }
                }
            }

            return tracked;
        }

        private static bool IsSupportedDefinition(Instruction instruction) =>
            instruction.OpCode is OpCode.Move or OpCode.ConditionalSelect or OpCode.Not
            || IsComparison(instruction.OpCode);

        private void Transfer(Instruction instruction, FlowState state)
        {
            if (instruction.Destination is not LocalVariable destination
                || !_trackedLocals.Contains(destination))
                return;

            var domain = ValueDomain.Top;
            ComparisonPredicate? predicate = null;
            var supported = true;
            switch (instruction)
            {
                case { OpCode: OpCode.Move, Operands: [_, var source] }:
                    domain = Read(state, source);
                    if (source is LocalVariable sourceLocal
                        && state.Predicates.TryGetValue(sourceLocal, out var copiedPredicate))
                        predicate = copiedPredicate;
                    break;

                case
                {
                    OpCode: OpCode.ConditionalSelect,
                    Operands: [_, var condition, var whenTrue, var whenFalse]
                }:
                    domain = EvaluateConditionalSelect(state, condition, whenTrue, whenFalse);
                    predicate = SelectPredicate(state, condition, whenTrue, whenFalse);
                    break;

                case { OpCode: OpCode.Not, Operands: [_, var negated] }:
                    domain = EvaluateNot(state, destination, negated);
                    if (negated is LocalVariable negatedLocal
                        && state.Predicates.TryGetValue(negatedLocal, out var negatedPredicate))
                        predicate = negatedPredicate.Invert();
                    break;

                case { Operands.Count: 3 } comparison when IsComparison(comparison.OpCode):
                    domain = EvaluateComparison(state, comparison);
                    predicate = TryCreatePredicate(comparison);
                    break;

                default:
                    supported = false;
                    break;
            }

            InvalidatePredicatesForDefinition(state, destination);
            if (supported)
            {
                Write(state, destination, domain);
                if (predicate != null && _trackedLocals.Contains(destination))
                    state.Predicates[destination] = predicate;
            }
            else
            {
                SetTop(state, destination);
            }

        }

        private ValueDomain EvaluateConditionalSelect(
            FlowState state,
            IOperand condition,
            IOperand whenTrue,
            IOperand whenFalse)
        {
            var truth = Truth(Read(state, condition));
            return truth switch
            {
                TruthValue.True => Read(state, whenTrue),
                TruthValue.False => Read(state, whenFalse),
                _ => ValueDomain.Union(
                    Read(state, whenTrue),
                    Read(state, whenFalse),
                    MaxFiniteValueCount),
            };
        }

        private static ComparisonPredicate? SelectPredicate(
            FlowState state,
            IOperand condition,
            IOperand whenTrue,
            IOperand whenFalse)
        {
            var truth = Truth(ReadCore(state, condition));
            var truePredicate = whenTrue is LocalVariable trueLocal
                                && state.Predicates.TryGetValue(trueLocal, out var trueValue)
                ? trueValue
                : null;
            var falsePredicate = whenFalse is LocalVariable falseLocal
                                 && state.Predicates.TryGetValue(falseLocal, out var falseValue)
                ? falseValue
                : null;
            return truth switch
            {
                TruthValue.True => truePredicate,
                TruthValue.False => falsePredicate,
                _ when truePredicate != null && truePredicate.Equals(falsePredicate) => truePredicate,
                _ => null,
            };
        }

        private ValueDomain EvaluateNot(
            FlowState state,
            LocalVariable destination,
            IOperand source)
        {
            var sourceDomain = Read(state, source);
            if (sourceDomain.IsTop || sourceDomain.IsBottom)
                return sourceDomain;

            var logical = IsBoolean(destination)
                          || source is LocalVariable sourceLocal
                          && (IsBoolean(sourceLocal) || state.Predicates.ContainsKey(sourceLocal));
            return ValueDomain.Create(
                sourceDomain.Values!.Select(value => logical
                    ? value == 0 ? 1L : 0L
                    : ~value),
                MaxFiniteValueCount);
        }

        private ValueDomain EvaluateComparison(FlowState state, Instruction comparison)
        {
            var left = Read(state, comparison.Operands[1]);
            var right = Read(state, comparison.Operands[2]);
            if (left.IsBottom || right.IsBottom)
                return ValueDomain.Bottom;
            if (left.IsTop || right.IsTop)
                return ValueDomain.Top;

            var int32 = IsInt32ControlOperand(comparison.Operands[1])
                        || IsInt32ControlOperand(comparison.Operands[2]);
            var results = new HashSet<long>();
            foreach (var leftValue in left.Values!)
            foreach (var rightValue in right.Values!)
            {
                if (!TryEvaluateComparison(
                        comparison.OpCode,
                        leftValue,
                        rightValue,
                        int32,
                        out var result))
                    return ValueDomain.Top;
                results.Add(result ? 1 : 0);
            }

            return ValueDomain.Create(results, MaxFiniteValueCount);
        }

        private ComparisonPredicate? TryCreatePredicate(Instruction comparison)
        {
            if (comparison.Operands[1] is LocalVariable left
                && _plan.Contains(left)
                && comparison.Operands[2] is Immediate right
                && right.Value is >= int.MinValue and <= int.MaxValue)
            {
                return new ComparisonPredicate(left, comparison.OpCode, right.Value, Negated: false);
            }

            if (comparison.Operands[2] is LocalVariable rightLocal
                && _plan.Contains(rightLocal)
                && comparison.Operands[1] is Immediate leftImmediate
                && leftImmediate.Value is >= int.MinValue and <= int.MaxValue
                && TryReverseComparison(comparison.OpCode, out var reversed))
            {
                return new ComparisonPredicate(
                    rightLocal,
                    reversed,
                    leftImmediate.Value,
                    Negated: false);
            }

            return null;
        }

        private FlowState Constrain(FlowState source, IOperand condition, bool expectedTrue)
        {
            var state = source.Clone();
            if (condition is Immediate immediate)
                return (immediate.Value != 0) == expectedTrue ? state : FlowState.Bottom();

            if (condition is not LocalVariable conditionLocal)
                return state;

            var conditionDomain = Read(state, conditionLocal);
            if (!conditionDomain.IsTop)
            {
                var constrainedCondition = conditionDomain.Values!
                    .Where(value => (value != 0) == expectedTrue)
                    .ToArray();
                if (constrainedCondition.Length == 0)
                    return FlowState.Bottom();
                Write(state, conditionLocal, ValueDomain.Create(
                    constrainedCondition,
                    MaxFiniteValueCount));
            }
            else if (IsBoolean(conditionLocal))
            {
                Write(state, conditionLocal, ValueDomain.Create(
                    [expectedTrue ? 1L : 0L],
                    MaxFiniteValueCount));
            }

            if (!state.Predicates.TryGetValue(conditionLocal, out var predicate))
                return state;

            var carrierDomain = Read(state, predicate.Carrier);
            if (carrierDomain.IsTop)
                return state;

            var constrainedCarrier = carrierDomain.Values!
                .Where(value => predicate.Evaluate(value) == expectedTrue)
                .ToArray();
            if (constrainedCarrier.Length == 0)
                return FlowState.Bottom();
            Write(state, predicate.Carrier, ValueDomain.Create(
                constrainedCarrier,
                MaxFiniteValueCount));
            return state;
        }

        private ValueDomain Read(FlowState state, IOperand operand) =>
            operand switch
            {
                Immediate immediate => ValueDomain.Create(
                    [immediate.Value],
                    MaxFiniteValueCount),
                LocalVariable local => ReadLocal(state, local),
                _ => ValueDomain.Top,
            };

        private static ValueDomain ReadCore(FlowState state, IOperand operand) =>
            operand switch
            {
                Immediate immediate => ValueDomain.Create(
                    [immediate.Value],
                    MaxFiniteValueCount),
                LocalVariable local when state.Values.TryGetValue(local, out var value) => value,
                LocalVariable local when IsBoolean(local) => ValueDomain.Boolean,
                _ => ValueDomain.Top,
            };

        private ValueDomain ReadLocal(FlowState state, LocalVariable local)
        {
            if (!_trackedLocals.Contains(local))
                return ValueDomain.Top;
            if (state.Values.TryGetValue(local, out var value))
                return value;
            return IsBoolean(local) ? ValueDomain.Boolean : ValueDomain.Top;
        }

        private void Write(FlowState state, LocalVariable local, ValueDomain value)
        {
            if (value.IsBottom)
            {
                state.MakeBottom();
                return;
            }

            if (value.IsTop
                || _plan.Contains(local) && !_plan.Accepts(local, value.Values!))
            {
                state.Values.Remove(local);
                return;
            }

            state.Values[local] = value;
        }

        private void SetTop(FlowState state, LocalVariable local)
        {
            state.Values.Remove(local);
            state.Predicates.Remove(local);
        }

        private void InvalidatePredicatesForDefinition(
            FlowState state,
            LocalVariable destination)
        {
            // 中文注释：比较谓词描述的是“定义当时”的载体；载体随后被写入时，旧条件仍可
            // 保留自己的有限值，但绝不能再用旧关系收窄载体的新值。
            state.Predicates.Remove(destination);
            if (!_plan.Contains(destination) || state.Predicates.Count == 0)
                return;
            foreach (var dependent in state.Predicates
                         .Where(pair => ReferenceEquals(pair.Value.Carrier, destination))
                         .Select(pair => pair.Key)
                         .ToArray())
                state.Predicates.Remove(dependent);
        }

        private bool IsInt32ControlOperand(IOperand operand) =>
            operand is LocalVariable local
            && (_plan.Contains(local) || local.Type?.FullName == "System.Int32");

        private static bool IsBoolean(LocalVariable local) =>
            local.Type?.FullName == "System.Boolean";

        private static TruthValue Truth(ValueDomain domain)
        {
            if (domain.IsTop || domain.IsBottom)
                return TruthValue.Unknown;
            var hasFalse = domain.Values!.Contains(0);
            var hasTrue = domain.Values.Any(value => value != 0);
            return (hasFalse, hasTrue) switch
            {
                (true, false) => TruthValue.False,
                (false, true) => TruthValue.True,
                _ => TruthValue.Unknown,
            };
        }

        private static bool IsComparison(OpCode opCode) =>
            opCode is OpCode.CheckEqual
                or OpCode.CheckNotEqual
                or OpCode.CheckGreater
                or OpCode.CheckGreaterOrEqual
                or OpCode.CheckLess
                or OpCode.CheckLessOrEqual
                or OpCode.CheckGreaterUnsigned
                or OpCode.CheckGreaterOrEqualUnsigned
                or OpCode.CheckLessUnsigned
                or OpCode.CheckLessOrEqualUnsigned;

        private static bool TryReverseComparison(OpCode opCode, out OpCode reversed)
        {
            reversed = opCode switch
            {
                OpCode.CheckEqual => OpCode.CheckEqual,
                OpCode.CheckNotEqual => OpCode.CheckNotEqual,
                OpCode.CheckGreater => OpCode.CheckLess,
                OpCode.CheckGreaterOrEqual => OpCode.CheckLessOrEqual,
                OpCode.CheckLess => OpCode.CheckGreater,
                OpCode.CheckLessOrEqual => OpCode.CheckGreaterOrEqual,
                OpCode.CheckGreaterUnsigned => OpCode.CheckLessUnsigned,
                OpCode.CheckGreaterOrEqualUnsigned => OpCode.CheckLessOrEqualUnsigned,
                OpCode.CheckLessUnsigned => OpCode.CheckGreaterUnsigned,
                OpCode.CheckLessOrEqualUnsigned => OpCode.CheckGreaterOrEqualUnsigned,
                _ => OpCode.Invalid,
            };
            return reversed != OpCode.Invalid;
        }

        private void Propagate(
            Block successor,
            FlowState incoming,
            Queue<Block> queue,
            HashSet<Block> queued)
        {
            if (incoming.IsBottom)
                return;
            if (!_entryStates.TryGetValue(successor, out var current))
            {
                _entryStates[successor] = incoming.Clone();
                Enqueue(successor, queue, queued);
                return;
            }

            if (current.MergeFrom(incoming, MaxFiniteValueCount))
                Enqueue(successor, queue, queued);
        }

        private static void Enqueue(
            Block block,
            Queue<Block> queue,
            HashSet<Block> queued)
        {
            if (queued.Add(block))
                queue.Enqueue(block);
        }
    }

    private sealed class FlowState
    {
        private FlowState(bool isBottom)
        {
            IsBottom = isBottom;
        }

        internal bool IsBottom { get; private set; }
        internal Dictionary<LocalVariable, ValueDomain> Values { get; } = [];
        internal Dictionary<LocalVariable, ComparisonPredicate> Predicates { get; } = [];

        internal static FlowState Bottom() => new(isBottom: true);

        internal static FlowState Reachable() => new(isBottom: false);

        internal FlowState Clone()
        {
            if (IsBottom)
                return Bottom();
            var clone = Reachable();
            foreach (var pair in Values)
                clone.Values[pair.Key] = pair.Value;
            foreach (var pair in Predicates)
                clone.Predicates[pair.Key] = pair.Value;
            return clone;
        }

        internal void MakeBottom()
        {
            IsBottom = true;
            Values.Clear();
            Predicates.Clear();
        }

        internal bool MergeFrom(FlowState incoming, int finiteLimit)
        {
            if (incoming.IsBottom)
                return false;

            var changed = false;
            foreach (var local in Values.Keys.ToArray())
            {
                if (!incoming.Values.TryGetValue(local, out var incomingValue))
                {
                    Values.Remove(local);
                    changed = true;
                    continue;
                }

                var merged = ValueDomain.Union(Values[local], incomingValue, finiteLimit);
                if (merged.IsTop)
                {
                    Values.Remove(local);
                    changed = true;
                }
                else if (!Values[local].SetEquals(merged))
                {
                    Values[local] = merged;
                    changed = true;
                }
            }

            foreach (var local in Predicates.Keys.ToArray())
            {
                if (!incoming.Predicates.TryGetValue(local, out var incomingPredicate)
                    || !Predicates[local].Equals(incomingPredicate))
                {
                    Predicates.Remove(local);
                    changed = true;
                }
            }

            return changed;
        }
    }

    private sealed class ValueDomain
    {
        private ValueDomain(ValueDomainKind kind, HashSet<long>? values = null)
        {
            Kind = kind;
            Values = values;
        }

        internal static ValueDomain Bottom { get; } = new(ValueDomainKind.Bottom);
        internal static ValueDomain Top { get; } = new(ValueDomainKind.Top);
        internal static ValueDomain Boolean { get; } = new(
            ValueDomainKind.FiniteSet,
            [0, 1]);
        private ValueDomainKind Kind { get; }
        internal HashSet<long>? Values { get; }
        internal bool IsBottom => Kind == ValueDomainKind.Bottom;
        internal bool IsTop => Kind == ValueDomainKind.Top;

        internal static ValueDomain Create(IEnumerable<long> values, int finiteLimit)
        {
            var materialized = new HashSet<long>();
            foreach (var value in values)
            {
                materialized.Add(value);
                if (materialized.Count > finiteLimit)
                    return Top;
            }

            return materialized.Count == 0
                ? Bottom
                : new ValueDomain(ValueDomainKind.FiniteSet, materialized);
        }

        internal static ValueDomain Union(ValueDomain left, ValueDomain right, int finiteLimit)
        {
            if (left.IsTop || right.IsTop)
                return Top;
            if (left.IsBottom)
                return right;
            if (right.IsBottom)
                return left;
            return Create(left.Values!.Concat(right.Values!), finiteLimit);
        }

        internal bool SetEquals(ValueDomain other) =>
            Kind == other.Kind
            && (IsBottom || IsTop || Values!.SetEquals(other.Values!));
    }

    private enum ValueDomainKind
    {
        Bottom,
        FiniteSet,
        Top,
    }

    private sealed record ComparisonPredicate(
        LocalVariable Carrier,
        OpCode Comparison,
        long Constant,
        bool Negated)
    {
        internal ComparisonPredicate Invert() => this with { Negated = !Negated };

        internal bool Evaluate(long value)
        {
            TryEvaluateComparison(
                Comparison,
                value,
                Constant,
                int32: true,
                out var result);
            return Negated ? !result : result;
        }
    }

    private enum TruthValue
    {
        Unknown,
        False,
        True,
    }
}
