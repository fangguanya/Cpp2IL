using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Extensions;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 从完整定义及使用闭包证明含 GC 引用的字段快照。每个分量保持托管类型，
/// 不把物理 SIMD 名称、对象大小或首字段类型当作整个原生宽值的类型。
/// </summary>
internal static class ManagedReferenceSpanRecovery
{
    internal sealed record Component(TypeAnalysisContext Type, int OffsetBytes, int SizeBytes,
        ManagedFieldSpanRecoveryHelper.ScalarKind Kind, FieldAnalysisContext? Field = null,
        IReadOnlyList<FieldAnalysisContext>? ParentFields = null);

    internal sealed record Access(LocalVariable Value, IOperand Reference, int WidthBytes,
        IReadOnlyList<Component> Components, bool IsLoad, bool IsWholeValue = false);

    internal static IReadOnlyDictionary<Instruction, Access> Describe(ISILControlFlowGraph graph, int pointerSize)
    {
        var positions = new Dictionary<Instruction, (Block Block, int Position)>();
        var definitions = new Dictionary<LocalVariable, List<Instruction>>();
        var uses = new Dictionary<LocalVariable, HashSet<Instruction>>();
        var accesses = new Dictionary<Instruction, Access>();
        foreach (var block in graph.Blocks)
        for (var position = 0; position < block.Instructions.Count; position++)
        {
            var instruction = block.Instructions[position];
            positions.Add(instruction, (block, position));
            // 无操作数的异常指令由原有 CIL 严格门分类；取证索引不得先访问不存在的目标。
            if (instruction.Operands.Count == 0)
                continue;
            if (instruction.Destination is LocalVariable destination)
            {
                if (!definitions.TryGetValue(destination, out var list))
                    definitions.Add(destination, list = []);
                list.Add(instruction);
            }
            foreach (var local in DeadCodeEliminator.EnumerateUsedLocals(instruction))
            {
                if (!uses.TryGetValue(local, out var set))
                    uses.Add(local, set = []);
                set.Add(instruction);
            }
            if (instruction.OpCode != OpCode.Move || instruction.MemoryAccessWidthBits is not (64 or 128))
                continue;
            if (instruction.Operands is [LocalVariable arrayValue, ArrayAccess array]
                && TryDescribeArrayLoad(arrayValue, array, instruction.MemoryAccessWidthBits / 8,
                    pointerSize, out var arrayLoad))
            {
                accesses.Add(instruction, arrayLoad);
                continue;
            }
            var load = instruction.Operands is [LocalVariable, FieldReference];
            LocalVariable? value = load ? (LocalVariable)instruction.Operands[0]
                : instruction.Operands is [FieldReference, LocalVariable source] ? source : null;
            if (value == null)
                continue;
            var reference = (FieldReference)instruction.Operands[load ? 1 : 0];
            if (!ManagedFieldSpanRecoveryHelper.TryDescribe(reference, pointerSize,
                    instruction.MemoryAccessWidthBits / 8, out var layout, out _, includeManagedReferences: true))
                continue;
            var whole = layout.Segments.Count == 1
                        && layout.Segments[0].Kind == ManagedFieldSpanRecoveryHelper.ScalarKind.Aggregate
                        && GenericCallRebinder.TypesEquivalent(value.Type, reference.Field.FieldType, requireDefinitionIdentity: true);
            if (whole || layout.Segments.Any(segment => segment.Kind == ManagedFieldSpanRecoveryHelper.ScalarKind.ManagedReference))
                accesses.Add(instruction, new Access(value, reference, layout.WidthBytes,
                    layout.Segments.Select(segment => new Component(segment.Field.FieldType,
                        segment.RelativeOffsetBytes, segment.SizeBytes, segment.Kind, segment.Field, segment.ParentFields)).ToArray(), load, whole));
        }

        var result = new Dictionary<Instruction, Access>();
        DominatorInfo? dominance = null;
        foreach (var pair in accesses.Where(pair => pair.Value.IsLoad))
        {
            var instruction = pair.Key;
            var load = pair.Value;
            if (!definitions.TryGetValue(load.Value, out var defining) || defining.Count != 1
                || !uses.TryGetValue(load.Value, out var consuming) || consuming.Count == 0)
                continue;
            var origin = positions[instruction];
            var valid = true;
            foreach (var use in consuming)
            {
                // 整字段聚合值允许精确类型装箱；数值向量和部分字段访问不满足这个合同。
                var exactBox = load.IsWholeValue && use.OpCode == OpCode.Box
                    && use.Operands is [LocalVariable, LocalVariable boxed, TypeAnalysisContext boxedType]
                    && ReferenceEquals(boxed, load.Value) && GenericCallRebinder.TypesEquivalent(boxedType, load.Value.Type, requireDefinitionIdentity: true);
                var exactCall = load.IsWholeValue && IsExactWholeValueCall(use, load.Value);
                if (!exactBox && !exactCall && (!accesses.TryGetValue(use, out var store) || store.IsLoad
                    || !ReferenceEquals(store.Value, load.Value)
                    || store.IsWholeValue != load.IsWholeValue
                    || !CanStoreComponents(load, store)))
                {
                    valid = false;
                    break;
                }
                var target = positions[use];
                // 唯一原生读取必须支配每一次写回，避免以 CLR 零初始化掩盖未定义路径。
                if (origin.Block == target.Block ? origin.Position >= target.Position
                    : !(dominance ??= new DominatorInfo(graph)).Dominates(origin.Block, target.Block))
                {
                    valid = false;
                    break;
                }
            }
            if (!valid)
                continue;
            result.Add(instruction, load);
            foreach (var use in consuming)
                if (accesses.TryGetValue(use, out var access))
                    result.Add(use, access);
        }
        return result;
    }

    internal static bool IsExactWholeValueCall(Instruction instruction, LocalVariable value)
    {
        if (instruction.OpCode is not (OpCode.Call or OpCode.CallVoid)
            || instruction.Operands.Count == 0
            || instruction.Operands[0] is not MethodAnalysisContext target
            || value.Type is not { IsValueType: true } valueType)
            return false;
        var receiverIndex = instruction.OpCode == OpCode.Call ? 2 : 1;
        var firstParameter = receiverIndex + (target.IsStatic ? 0 : 1);
        if (instruction.Operands.Count != firstParameter + target.Parameters.Count)
            return false;
        var found = false;
        for (var index = receiverIndex; index < instruction.Operands.Count; index++)
        {
            var operand = instruction.Operands[index];
            if (!DeadCodeEliminator.EnumerateUsedLocals(operand).Any(local => ReferenceEquals(local, value)))
                continue;
            var direct = ReferenceEquals(operand, value);
            var address = operand is AddressOf { Target: LocalVariable local } && ReferenceEquals(local, value);
            // 使用完整操作数索引识别嵌套内存、字段或组合实参，拒绝把部分值误作整个结构。
            if (!direct && !address)
                return false;
            if (!target.IsStatic && index == receiverIndex)
            {
                if (!GenericCallRebinder.TypesEquivalent(target.DeclaringType, valueType, requireDefinitionIdentity: true))
                    return false;
            }
            else
            {
                var expected = target.Parameters[index - firstParameter].ParameterType;
                // 原生取址只有与真实 ref/out 签名一致时才保留为托管地址。
                if (address ? expected is not ByRefTypeAnalysisContext byRef || !GenericCallRebinder.TypesEquivalent(byRef.ElementType, valueType, requireDefinitionIdentity: true)
                    : !GenericCallRebinder.TypesEquivalent(expected, valueType, requireDefinitionIdentity: true))
                    return false;
            }
            found = true;
        }
        return found;
    }

    private static bool TryDescribeArrayLoad(LocalVariable value, ArrayAccess array, int widthBytes,
        int pointerSize, out Access access)
    {
        access = null!;
        if (pointerSize is not (4 or 8) || widthBytes % pointerSize != 0
            || array.Array.Type is not SzArrayTypeAnalysisContext { ElementType: var element }
            || !IsManagedReference(element))
            return false;
        var count = widthBytes / pointerSize;
        // 索引必须是已证明的整型值；动态索引在原始读取位置只求值一次，后续消费者只读取快照。
        if (array.Index is Immediate immediate
            ? immediate.Value < 0 || immediate.Value > int.MaxValue - (count - 1)
            : array.Index is not LocalVariable { Type: { } indexType }
              || !ReferenceEquals(indexType, element.AppContext.SystemTypes.SystemInt32Type))
            return false;
        var components = Enumerable.Range(0, count).Select(index => new Component(element,
            index * pointerSize, pointerSize, ManagedFieldSpanRecoveryHelper.ScalarKind.ManagedReference)).ToArray();
        access = new Access(value, array, widthBytes, components, true);
        return true;
    }

    private static bool IsManagedReference(TypeAnalysisContext type)
        => !type.IsValueType && type is not (ByRefTypeAnalysisContext or PointerTypeAnalysisContext
            or GenericParameterTypeAnalysisContext or StaticFieldStorageTypeAnalysisContext);

    private static bool CanStoreComponents(Access left, Access right)
    {
        if (left.WidthBytes != right.WidthBytes || left.Components.Count != right.Components.Count)
            return false;
        for (var index = 0; index < left.Components.Count; index++)
        {
            var source = left.Components[index];
            var destination = right.Components[index];
            if (source.OffsetBytes != destination.OffsetBytes
                || source.SizeBytes != destination.SizeBytes || source.Kind != destination.Kind)
                return false;
            if (GenericCallRebinder.TypesEquivalent(source.Type, destination.Type, requireDefinitionIdentity: true))
                continue;
            // 只接受同一原始类型图证明的引用向上赋值，绝不以显示名称或强制转换跨越字段类型。
            if (source.Kind != ManagedFieldSpanRecoveryHelper.ScalarKind.ManagedReference
                || !ReferenceEquals(source.Type.AppContext, destination.Type.AppContext)
                || !source.Type.IsAssignableTo(destination.Type))
                return false;
        }
        return true;
    }
}
