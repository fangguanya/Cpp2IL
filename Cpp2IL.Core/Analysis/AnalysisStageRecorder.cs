using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 显式启用的只读阶段取证器。快照脱离可变图；对象身份不依赖显示名或重复的Instruction.Index。
/// 原生区间表示提升时的产生窗口，后续新节点只登记首次出现阶段，不伪造转换血缘。
/// </summary>
internal sealed class AnalysisStageRecorder(Action<AnalysisStageRecorder.Snapshot> sink)
{
    internal sealed record NativeRange(ulong Start, ulong EndExclusive);
    internal sealed record Snapshot(int Sequence, string Stage, object Method, int PointerSize,
        object[] Instructions, object[] Locals, object[] Types, object[] Parameters,
        object[] Blocks, object[] FieldLayouts, int UnstructuredOperands, string? Failure);

    private sealed class IdentityComparer : IEqualityComparer<object>
    {
        public new bool Equals(object? left, object? right) => ReferenceEquals(left, right);
        public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
    }

    private readonly Dictionary<object, int> identities = new(new IdentityComparer());
    private readonly Dictionary<Instruction, NativeRange> origins = [];
    private readonly Dictionary<Instruction, string> firstStages = [];
    private readonly List<TypeAnalysisContext> types = [];
    private readonly HashSet<TypeAnalysisContext> knownTypes = [];
    private readonly Dictionary<LocalVariable, object> localViews = [];
    private readonly List<TypeAnalysisContext> layoutOwners = [];
    private readonly HashSet<TypeAnalysisContext> knownLayoutOwners = [];
    private readonly Dictionary<TypeAnalysisContext, int> layoutDepths = [];
    private int sequence;
    private int unstructured;
    private string currentStage = "";

    private int Id(object value)
    {
        if (!identities.TryGetValue(value, out var id))
            identities.Add(value, id = identities.Count + 1);
        return id;
    }

    private int? TypeId(TypeAnalysisContext? type)
    {
        if (type == null) return null;
        if (knownTypes.Add(type)) types.Add(type);
        return Id(type);
    }

    internal void RegisterNativeEmission(IReadOnlyList<Instruction> instructions, int first,
        ulong start, ulong endExclusive)
    {
        if (first < 0 || first > instructions.Count || endExclusive <= start)
            throw new ArgumentOutOfRangeException(nameof(first), "原生产生窗口边界无效。");
        for (var i = first; i < instructions.Count; i++)
        {
            if (origins.ContainsKey(instructions[i]))
                throw new InvalidOperationException("同一指令重复登记原生产生窗口。");
            origins.Add(instructions[i], new NativeRange(start, endExclusive));
        }
    }

    internal void Capture(string stage, MethodAnalysisContext method,
        IReadOnlyList<Instruction>? raw = null, string? failure = null)
    {
        currentStage = stage;
        unstructured = 0;
        localViews.Clear();
        layoutOwners.Clear();
        knownLayoutOwners.Clear();
        layoutDepths.Clear();
        // 对象 ID 跨阶段稳定，但当前类型表只包含当前快照实际引用的身份。
        types.Clear();
        knownTypes.Clear();
        var graph = raw == null ? method.ControlFlowGraph : null;
        var nodes = raw?.ToArray() ?? graph?.Instructions.ToArray()
            ?? method.ConvertedIsil?.ToArray() ?? origins.Keys.ToArray();
        var instructions = nodes.Select(DescribeInstruction).ToArray();
        var parameters = method.ParameterOperands.Select(operand => Operand(operand)).ToArray();
        foreach (var local in method.ParameterLocals) Local(local);
        var methodView = Method(method);
        foreach (var parameter in method.Parameters)
            if (parameter.ParameterType.IsValueType) ObserveLayout(parameter.ParameterType);
        if (method.DeclaringType is { IsValueType: true } valueOwner) ObserveLayout(valueOwner);
        var layoutViews = new List<object>();
        // 只展开本阶段实际访问的所有者、基类及内嵌值字段，不沿引用字段遍历整个应用。
        for (var i = 0; i < layoutOwners.Count; i++) layoutViews.Add(FieldLayout(layoutOwners[i]));
        var typeViews = new List<object>();
        // 类型包装和泛型参数可能补充新的真实身份；同一阶段只投影每个类型一次。
        for (var i = 0; i < types.Count; i++) typeViews.Add(Type(types[i]));
        var blocks = graph == null ? [] : graph.Blocks.Select(block => (object)new
        {
            id = block.ID,
            objectIdentityId = Id(block),
            predecessors = block.Predecessors.Select(parent => parent.ID).ToArray(),
            successors = block.Successors.Select(child => child.ID).ToArray(),
            instructions = block.Instructions.Select(Id).ToArray()
        }).ToArray();
        sink(new Snapshot(sequence++, stage, methodView, method.AppContext.Binary.PointerSizeBytes,
            instructions, localViews.Values.ToArray(), typeViews.ToArray(), parameters,
            blocks, layoutViews.ToArray(), unstructured, failure));
    }

    private object DescribeInstruction(Instruction instruction)
    {
        if (!firstStages.TryGetValue(instruction, out var first))
            firstStages.Add(instruction, first = currentStage);
        origins.TryGetValue(instruction, out var origin);
        // 不完整异常指令仍保留全部操作数；数据流投影失败单独记录，不掩盖原始分析错误。
        object? destination = null;
        int[] used = [];
        string? projectionFailure = null;
        try
        {
            if (instruction.Operands.Count > 0)
            {
                destination = instruction.Destination is { } target ? Operand(target) : null;
                used = DeadCodeEliminator.EnumerateUsedLocals(instruction).Select(Local).Distinct().ToArray();
            }
        }
        catch (Exception error)
        {
            projectionFailure = error.GetType().Name + ": " + error.Message;
        }
        return new
        {
            id = Id(instruction), index = instruction.Index, opcode = instruction.OpCode.ToString(),
            integerWidthBits = instruction.IntegerWidthBits, memoryAccessWidthBits = instruction.MemoryAccessWidthBits,
            firstObservedStage = first, nativeEmissionRange = origin,
            transformationAncestryProved = false, destination, usedLocalIds = used,
            implicitDefinition = instruction.ImplicitDefinition is { } register ? Operand(register) : null,
            operands = instruction.Operands.Select(operand => Operand(operand)).ToArray(), projectionFailure
        };
    }

    private int Local(LocalVariable local)
    {
        var id = Id(local);
        if (!localViews.ContainsKey(local))
            localViews.Add(local, new
            {
                id, name = local.Name, register = local.Register.Name, registerNumber = local.Register.Number, ssaVersion = local.Register.Version,
                typeId = TypeId(local.Type), local.IsThis, local.IsReturn, local.IsMethodInfo,
                sourceParameter = local.SourceParameter is not { } parameter ? null : new
                {
                    methodId = Id(parameter.DeclaringMethod), methodToken = parameter.DeclaringMethod.Token,
                    parameterIndex = parameter.ParameterIndex, parameterToken = parameter.Token,
                    typeId = TypeId(parameter.ParameterType)
                }
            });
        return id;
    }

    private object Method(MethodAnalysisContext method) => new
    {
        id = Id(method), token = method.Token, address = method.UnderlyingPointer,
        name = method.Name, ownerTypeId = TypeId(method.DeclaringType),
        returnTypeId = TypeId(method.ReturnType), attributes = (int)method.Attributes,
        parameterTypes = method.Parameters.Select(p => TypeId(p.ParameterType)).ToArray(),
        declaredParameters = method.Parameters.Select(p => new { p.ParameterIndex, p.Token, typeId = TypeId(p.ParameterType) }).ToArray()
    };

    private object Type(TypeAnalysisContext type) => new
    {
        id = Id(type), token = type.Token, assemblyId = Id(type.DeclaringAssembly),
        assembly = type.DeclaringAssembly.Name, contextKind = type.GetType().Name,
        displayName = type.FullName, type.IsValueType,
        primitive = type.AppContext.SystemTypes.TryGetIl2CppTypeEnum(type, out var primitive) ? primitive.ToString() : null,
        elementTypeId = type is WrappedTypeAnalysisContext wrapped ? TypeId(wrapped.ElementType) : null,
        genericDefinitionId = type is GenericInstanceTypeAnalysisContext instance ? TypeId(instance.GenericType) : null,
        genericArgumentIds = type is GenericInstanceTypeAnalysisContext generic ? generic.GenericArguments.Select(TypeId).ToArray() : [],
        genericParameterIndex = type is GenericParameterTypeAnalysisContext parameter ? (int?)parameter.Index : null
    };

    private void ObserveLayout(TypeAnalysisContext? type, int depth = 0)
    {
        if (type is ByRefTypeAnalysisContext byRef) type = byRef.ElementType;
        if (type is StaticFieldStorageTypeAnalysisContext storage) type = storage.OwnerType;
        if (type is null or WrappedTypeAnalysisContext or GenericParameterTypeAnalysisContext
            || type.AppContext.SystemTypes.TryGetIl2CppTypeEnum(type, out _)) return;
        TypeId(type);
        if (knownLayoutOwners.Add(type))
        {
            layoutOwners.Add(type);
            layoutDepths.Add(type, depth);
        }
    }

    private object FieldOperand(FieldReference field)
    {
        ObserveLayout(field.Field.DeclaringType);
        ObserveLayout(field.Local.Type);
        return new
        {
            kind = "Field", receiverId = Local(field.Local), accessOffset = field.Offset,
            fieldId = Id(field.Field), token = field.Field.Token, ownerTypeId = TypeId(field.Field.DeclaringType),
            fieldTypeId = TypeId(field.Field.FieldType), declaredOffset = field.Field.Offset, field.Field.IsStatic,
            baseFieldId = field.Field is ConcreteGenericFieldAnalysisContext concrete ? Id(concrete.BaseFieldContext) : Id(field.Field)
        };
    }

    private object FieldLayout(TypeAnalysisContext owner)
    {
        var pointerSize = owner.AppContext.Binary.PointerSizeBytes;
        var declaration = owner is GenericInstanceTypeAnalysisContext instance ? instance.GenericType : owner;
        var definition = declaration.Definition;
        var depth = layoutDepths[owner];
        if (depth > 64) return new { ownerTypeId = TypeId(owner), layoutProjectionFailure = "内嵌值布局递归超出证据合同", semanticLayoutAccepted = false };
        try
        {
            var baseType = owner is GenericInstanceTypeAnalysisContext generic && generic.GenericType.BaseType is { } openBase
                ? GenericInstantiation.Instantiate(openBase, generic.GenericArguments, []) : owner.BaseType;
            ObserveLayout(baseType, depth + 1);
            var rawSizes = definition?.RawSizes;
            var concrete = GenericInstanceFieldLayout.CreateLayoutOwner(owner);
            var layout = concrete == null ? null : GenericInstanceFieldLayout.GetConcreteFieldLayout(concrete);
            var positions = layout?.ToDictionary(item => item.Field is ConcreteGenericFieldAnalysisContext field
                ? field.BaseFieldContext : item.Field);
            var fields = declaration.Fields.Select(field =>
            {
                GenericInstanceFieldLayout.ConcreteFieldLayout? position = positions != null && positions.TryGetValue(field, out var entry) ? entry : null;
                var projected = position?.Field ?? (concrete == null ? field : new ConcreteGenericFieldAnalysisContext(field, concrete));
                var fieldType = projected.FieldType;
                if (fieldType.IsValueType) ObserveLayout(fieldType, depth + 1);
                return new
                {
                    fieldId = Id(field), field.Token, ownerTypeId = TypeId(owner), typeId = TypeId(fieldType),
                    field.IsStatic, attributes = (int)field.Attributes,
                    hasOriginalOffset = field.BackingData != null, originalDeclaredOffset = field.DefaultOffset,
                    observedDeclaredOffset = field.Offset,
                    concreteOffset = position?.Offset, concreteSize = position?.Size, concreteAlignment = position?.Alignment,
                    // 普通字段的偏移是原始未装箱偏移；开放泛型声明的零值不是实例偏移证明。
                    offsetBasis = concrete != null ? "CONCRETE_LAYOUT_OR_UNKNOWN" : field.BackingData != null ? field.IsStatic ? "ORIGINAL_STATIC_FIELD" : "ORIGINAL_UNBOXED_FIELD" : "INJECTED_UNPROVED"
                };
            }).Cast<object>().ToArray();
            return new
            {
                ownerTypeId = TypeId(owner), declarationTypeId = TypeId(declaration), baseTypeId = TypeId(baseType),
                attributes = (int)owner.Attributes, packing = definition?.PackingSize,
                classSizeIsDefault = definition?.ClassSizeIsDefault,
                originalSizeAddress = definition?.TypeIndex is { } index
                    ? (ulong?)owner.AppContext.Binary.TypeDefinitionSizePointers[index.Value] : null,
                boxedSize = rawSizes?.instance_size, nativeSize = rawSizes?.native_size,
                staticFieldsSize = rawSizes?.static_fields_size,
                originalUnboxedSize = rawSizes == null ? (long?)null : TypeSizes.UnboxedSizeFromBoxedSize(rawSizes.instance_size, pointerSize),
                originalSizeIsConcreteInstance = concrete == null,
                concreteLayoutAvailable = layout != null, fields,
                layoutProjectionFailure = (string?)null, semanticLayoutAccepted = false
            };
        }
        catch (Exception error)
        {
            // 取证缺口单独保留，不将布局投影异常伪装为算法成功，也不吞掉主分析异常。
            return new { ownerTypeId = TypeId(owner), declarationTypeId = TypeId(declaration),
                layoutProjectionFailure = error.GetType().Name + ": " + error.Message, semanticLayoutAccepted = false };
        }
    }

    private object Operand(IOperand operand, int depth = 0)
    {
        if (depth > 32)
        {
            unstructured++;
            return new { kind = operand.GetType().Name, structured = false, reason = "操作数递归深度超出取证合同" };
        }
        switch (operand)
        {
            case LocalVariable local: return new { kind = "Local", id = Local(local) };
            case Register register: return new { kind = "Register", register.Name, register.Number, register.Version };
            // 控制流目标以块对象身份关联同阶段 CFG；不递归展开循环，也不把显示 ID 当稳定对象身份。
            case Block block: return new { kind = "Block", id = block.ID, objectIdentityId = Id(block) };
            case Instruction target: return new { kind = "Instruction", id = Id(target) };
            case Immediate immediate: return new { kind = "Immediate", immediate.Value };
            case StackOffset stack: return new { kind = "StackOffset", stack.Offset };
            case MemoryOperand memory:
                if (memory.Base is LocalVariable memoryBase) ObserveLayout(memoryBase.Type);
                return new
            {
                kind = "Memory", baseOperand = memory.Base == null ? null : Operand(memory.Base, depth + 1),
                indexOperand = memory.Index == null ? null : Operand(memory.Index, depth + 1),
                memory.Addend, memory.Scale, extension = memory.IndexExtension.ToString()
            };
            case FieldReference field: return FieldOperand(field);
            case ArrayAccess array: return new { kind = "Array", arrayId = Local(array.Array), index = Operand(array.Index, depth + 1) };
            case ListCount count: return new { kind = "ListCount", valueId = Local(count.Value), typeId = TypeId(count.ListType) };
            case StringLength stringLength: return new { kind = "StringLength", valueId = Local(stringLength.Value) };
            case MetadataStringTableLookup table: return new
            {
                kind = "MetadataStringTable", index = Operand(table.Index, depth + 1),
                values = table.Values.Select(value => Operand(value, depth + 1)).ToArray(),
                defaultValue = Operand(table.DefaultValue, depth + 1)
            };
            case ReadOnlyUInt16TableLookup table: return new
            {
                kind = "ReadOnlyUInt16Table", index = Operand(table.Index, depth + 1),
                codeUnits = table.Values.Select(value => (int)value).ToArray()
            };
            case ArrayLength length: return new { kind = "ArrayLength", arrayId = Local(length.Array) };
            case AddressOf address: return new { kind = "AddressOf", target = Operand(address.Target, depth + 1) };
            case MethodAnalysisContext method: return new { kind = "Method", method = Method(method) };
            case TypeAnalysisContext type: return new { kind = "Type", typeId = TypeId(type) };
            case HomogeneousFloatingAggregateArgument aggregate: return new
            {
                kind = "HFA", typeId = TypeId(aggregate.AggregateType),
                components = aggregate.Components.Select(component => Operand(component, depth + 1)).ToArray()
            };
            case NativeBitPatternLiteral literal: return new { kind = "NativeBits", literal.Low, literal.High, literal.WidthBits };
            case FloatLiteral single: return new { kind = "Float", bits = BitConverter.ToInt32(BitConverter.GetBytes(single.Value), 0) };
            case DoubleLiteral floating: return new { kind = "Double", bits = BitConverter.DoubleToInt64Bits(floating.Value) };
            case StringLiteral text: return new { kind = "String", value = text.Value };
            default:
                unstructured++;
                return new { kind = operand.GetType().Name, structured = false, text = operand.ToString() };
        }
    }
}
