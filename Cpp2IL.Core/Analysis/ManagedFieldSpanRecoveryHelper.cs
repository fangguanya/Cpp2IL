using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 将一次原生连续访问描述为若干完整托管字段。只有字段偏移、标量尺寸和整个访问区间都能精确闭合时才成功，
/// 从而允许 CIL 按字段读写重建位模式，而不依赖 CLR 在运行时继续把相邻字段放在连续地址中。
/// </summary>
internal static class ManagedFieldSpanRecoveryHelper
{
    internal enum ScalarKind
    {
        Integer,
        Single,
        Double,
        NativeInteger,
        ManagedReference,
        Aggregate,
    }

    internal readonly record struct Segment(
        FieldAnalysisContext Field,
        int RelativeOffsetBytes,
        int SizeBytes,
        ScalarKind Kind,
        IReadOnlyList<FieldAnalysisContext>? ParentFields = null)
    {
        internal FieldAnalysisContext RootField => ParentFields is { Count: > 0 } ? ParentFields[0] : Field;
    }

    internal readonly record struct Description(
        int WidthBytes,
        IReadOnlyList<Segment> Segments,
        FieldAnalysisContext? WholeAggregateField = null);

    private readonly record struct PositionedField(
        FieldAnalysisContext Field,
        long Offset,
        int Size,
        ScalarKind Kind,
        IReadOnlyList<FieldAnalysisContext>? ParentFields = null)
    {
        internal long End => checked(Offset + Size);
    }

    internal static bool IsExactScalarFloatingAccess(Instruction instruction, int pointerSize)
    {
        if (instruction.OpCode != OpCode.Move || instruction.MemoryAccessWidthBits is not (32 or 64)
            || instruction.Operands.Count != 2)
            return false;
        var local = instruction.Operands[0] as LocalVariable ?? instruction.Operands[1] as LocalVariable;
        var field = instruction.Operands[0] as FieldReference ?? instruction.Operands[1] as FieldReference;
        if (local == null || field == null
            || !GenericCallRebinder.TypesEquivalent(local.Type, field.Field.FieldType, requireDefinitionIdentity: true)
            || !TryDescribe(field, pointerSize, instruction.MemoryAccessWidthBits / 8,
                out var layout, out _))
            return false;
        // 同宽的完整浮点字段具有托管标量语义；部分聚合、跨字段和异身份都不满足此合同。
        return layout.Segments.Count == 1
               && layout.Segments[0].Kind is ScalarKind.Single or ScalarKind.Double;
    }

    internal static bool TryDescribe(
        FieldReference reference,
        int pointerSize,
        int widthBytes,
        out Description description,
        out string failure,
        bool includeManagedReferences = false,
        bool allowOpaqueSmallAggregate = true)
    {
        description = default;
        failure = string.Empty;
        if (pointerSize is not (4 or 8) || widthBytes <= 0)
        {
            failure = $"指针或访问宽度无效：pointerSize={pointerSize}, widthBytes={widthBytes}。";
            return false;
        }

        long end;
        try
        {
            end = checked((long)reference.Offset + widthBytes);
        }
        catch (OverflowException)
        {
            failure = "字段跨度终点溢出。";
            return false;
        }

        if (reference.Offset < 0 || end <= reference.Offset)
        {
            failure = $"字段跨度起点无效：offset={reference.Offset}。";
            return false;
        }

        if (!TryGetPositionedFields(reference, pointerSize, end, includeManagedReferences, allowOpaqueSmallAggregate,
                out var positioned, out var wholeAggregate, out failure))
            return false;

        var overlapping = positioned
            .Where(field => field.Offset < end && reference.Offset < field.End)
            .OrderBy(field => field.Offset)
            .ThenBy(field => field.End)
            .ToArray();
        if (overlapping.Length == 0)
        {
            failure = "访问区间没有对应字段。";
            return false;
        }

        var cursor = (long)reference.Offset;
        var segments = new List<Segment>(overlapping.Length);
        foreach (var field in overlapping)
        {
            if (field.Offset != cursor)
            {
                failure = field.Offset < cursor
                    ? $"字段布局重叠：cursor={cursor}, field={field.Field.Name}@{field.Offset}。"
                    : $"字段布局存在间隙：cursor={cursor}, field={field.Field.Name}@{field.Offset}。";
                return false;
            }

            if (field.End > end)
            {
                failure = $"访问只覆盖字段的一部分：field={field.Field.Name}, fieldEnd={field.End}, accessEnd={end}。";
                return false;
            }

            if (segments.Count == 0 && !SameField(field.ParentFields is { Count: > 0 } ? field.ParentFields[0] : field.Field, reference.Field))
            {
                failure = $"跨度首字段身份与已解析字段不一致：resolved={reference.Field.Name}, layout={field.Field.Name}。";
                return false;
            }

            segments.Add(new(
                field.Field,
                checked((int)(field.Offset - reference.Offset)),
                field.Size,
                field.Kind, field.ParentFields));
            cursor = field.End;
        }

        if (cursor != end)
        {
            failure = $"字段布局存在间隙，未完整覆盖访问区间：coveredEnd={cursor}, accessEnd={end}。";
            return false;
        }

        description = new(widthBytes, segments, wholeAggregate);
        return true;
    }

    private static bool TryGetPositionedFields(
        FieldReference reference,
        int pointerSize,
        long accessEnd,
        bool includeManagedReferences,
        bool allowOpaqueSmallAggregate,
        out IReadOnlyList<PositionedField> fields,
        out FieldAnalysisContext? wholeAggregateField,
        out string failure)
    {
        fields = [];
        wholeAggregateField = null;
        failure = string.Empty;
        var owner = ResolveLayoutOwner(reference);
        if (owner == null)
        {
            failure = "字段接收者缺少可验证的布局类型。";
            return false;
        }

        if (!TryGetDeclaredPositions(owner, reference.Field.IsStatic, out var positioned, out failure))
            return false;
        var result = new List<PositionedField>(positioned.Count);
        var active = new HashSet<TypeAnalysisContext>();
        FieldAnalysisContext? completeRoot = null;
        foreach (var (field, offset) in positioned)
            if (!Append(field, offset, [], null, out failure)) return false;
        fields = result;
        wholeAggregateField = completeRoot;
        return true;

        bool Append(FieldAnalysisContext field, long offset, IReadOnlyList<FieldAnalysisContext> parents,
            long? containingEnd, out string error)
        {
            error = string.Empty;
            if (offset < 0)
            {
                error = "字段布局存在负偏移。";
                return false;
            }
            if (offset >= accessEnd) return true;
            // 引用大小仍取指针槽；即使数值分支也须检测重叠引用，不能忽略后伪造覆盖。
            if (!TryGetScalarLayout(field.FieldType, pointerSize, true, out var size, out var kind))
            {
                error = $"访问区间含有未闭合的字段布局：{field.Name}@{offset}。";
                return false;
            }
            var fieldEnd = checked(offset + size);
            // 同一次原始布局遍历登记整字段边界，后续 CIL 不再次计算该布局或猜测泛型字段偏移。
            if (parents.Count == 0 && kind == ScalarKind.Aggregate && SameField(field, reference.Field)
                && offset == reference.Offset && fieldEnd == accessEnd)
                completeRoot = field;
            if (containingEnd is { } bound && fieldEnd > bound)
            {
                error = "嵌套字段越过原始值类型尺寸。";
                return false;
            }
            if (fieldEnd <= reference.Offset) return true;
            if (kind == ScalarKind.Aggregate && (!includeManagedReferences || offset < reference.Offset || fieldEnd > accessEnd))
            {
                if (allowOpaqueSmallAggregate && offset >= reference.Offset && fieldEnd <= accessEnd
                    && size is > 0 and <= 8
                    && ByRefMemoryAccessHelper.HasExactKnownUnmanagedLayout(field.FieldType, pointerSize, size))
                {
                    // 小型无托管聚合已由原始尺寸、字段类型和布局合同闭合，保留为单一位模式段，
                    // 避免显式布局的重叠成员被错误展开成互相覆盖的标量字段。
                    var aggregateParents = parents;
                    if (parents.Count == 0 && SameField(field, reference.Field)
                        && offset == reference.Offset && fieldEnd == accessEnd)
                        aggregateParents = [field];
                    result.Add(new(field, offset, size, kind,
                        aggregateParents.Count == 0 ? null : aggregateParents));
                    return true;
                }

                // 部分聚合只展开原始未装箱字段偏移；不读取尾部填充，也不把结构体直接当位整数。
                if (parents.Count >= 32 || !active.Add(field.FieldType))
                {
                    error = "嵌套值字段存在循环或超过布局深度。";
                    return false;
                }
                try
                {
                    if (!TryGetDeclaredPositions(field.FieldType, false, out var nested, out error)) return false;
                    if (nested.Count == 0)
                    {
                        error = "嵌套值字段缺少原始成员布局。";
                        return false;
                    }
                    var path = parents.Append(field).ToArray();
                    foreach (var child in nested)
                    {
                        if (child.Offset < 0)
                        {
                            error = "嵌套字段布局存在负偏移。";
                            return false;
                        }
                        if (!Append(child.Field, checked(offset + child.Offset), path, fieldEnd, out error)) return false;
                    }
                    return true;
                }
                finally { active.Remove(field.FieldType); }
            }
            if (kind == ScalarKind.ManagedReference && !includeManagedReferences)
            {
                error = "数值跨度与托管引用字段重叠。";
                return false;
            }
            result.Add(new(field, offset, size, kind, parents.Count == 0 ? null : parents));
            return true;
        }
    }

    private static bool TryGetDeclaredPositions(TypeAnalysisContext owner, bool isStatic,
        out List<(FieldAnalysisContext Field, long Offset)> positioned, out string failure)
    {
        positioned = [];
        failure = string.Empty;
        if (isStatic)
        {
            var declaredOwner = owner is GenericInstanceTypeAnalysisContext generic ? generic.GenericType : owner;
            foreach (var field in declaredOwner.Fields.Where(field =>
                         field.IsStatic && (field.Attributes & FieldAttributes.Literal) == 0))
            {
                var concrete = owner is GenericInstanceTypeAnalysisContext instance
                    ? new ConcreteGenericFieldAnalysisContext(field, instance)
                    : field;
                positioned.Add((concrete, field.Offset));
            }
        }
        else if (GenericInstanceFieldLayout.CreateLayoutOwner(owner) is { } genericOwner)
        {
            var layout = GenericInstanceFieldLayout.GetConcreteFieldLayout(genericOwner);
            if (layout == null)
            {
                failure = $"泛型实例字段布局未知：{owner.FullName}。";
                return false;
            }

            positioned.AddRange(layout.Select(field => (field.Field, field.Offset)));
        }
        else
        {
            for (var current = owner; current != null; current = current.BaseType)
                positioned.AddRange(current.Fields
                    .Where(field => !field.IsStatic && (field.Attributes & FieldAttributes.Literal) == 0)
                    .Select(field => (field, (long)field.Offset)));
        }

        return true;
    }

    private static TypeAnalysisContext? ResolveLayoutOwner(FieldReference reference)
    {
        if (reference.Field.IsStatic)
        {
            return reference.Local.Type is StaticFieldStorageTypeAnalysisContext storage
                ? storage.OwnerType
                : reference.Field.DeclaringType;
        }

        var receiver = reference.Local.Type;
        if (receiver is ByRefTypeAnalysisContext byRef)
            receiver = byRef.ElementType;
        return receiver is null or PointerTypeAnalysisContext
            ? reference.Field.DeclaringType
            : receiver;
    }

    internal static bool TryGetScalarLayout(
        TypeAnalysisContext type,
        int pointerSize,
        bool includeManagedReferences,
        out int size,
        out ScalarKind kind)
    {
        if (type.IsEnumType && type.EnumUnderlyingType is { } underlying)
            type = underlying;

        // 复用应用上下文唯一的核心类型身份表；同名用户声明不得取得标量尺寸或失去 GC 引用属性。
        var primitive = type.AppContext.SystemTypes.TryGetIl2CppTypeEnum(type, out var resolved)
            ? resolved : Il2CppTypeEnum.IL2CPP_TYPE_END;
        (size, kind) = primitive switch
        {
            Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN or Il2CppTypeEnum.IL2CPP_TYPE_U1 or Il2CppTypeEnum.IL2CPP_TYPE_I1 => (1, ScalarKind.Integer),
            Il2CppTypeEnum.IL2CPP_TYPE_CHAR or Il2CppTypeEnum.IL2CPP_TYPE_I2 or Il2CppTypeEnum.IL2CPP_TYPE_U2 => (2, ScalarKind.Integer),
            Il2CppTypeEnum.IL2CPP_TYPE_I4 or Il2CppTypeEnum.IL2CPP_TYPE_U4 => (4, ScalarKind.Integer),
            Il2CppTypeEnum.IL2CPP_TYPE_I8 or Il2CppTypeEnum.IL2CPP_TYPE_U8 => (8, ScalarKind.Integer),
            Il2CppTypeEnum.IL2CPP_TYPE_R4 => (4, ScalarKind.Single),
            Il2CppTypeEnum.IL2CPP_TYPE_R8 => (8, ScalarKind.Double),
            Il2CppTypeEnum.IL2CPP_TYPE_I or Il2CppTypeEnum.IL2CPP_TYPE_U => (pointerSize, ScalarKind.NativeInteger),
            _ when type is PointerTypeAnalysisContext => (pointerSize, ScalarKind.NativeInteger),
            // 托管引用只记录指针槽的存储跨度，绝不使用引用对象的未装箱大小。
            _ when includeManagedReferences && !type.IsValueType
                   && type is not (ByRefTypeAnalysisContext or GenericParameterTypeAnalysisContext
                       or StaticFieldStorageTypeAnalysisContext)
                => (pointerSize, ScalarKind.ManagedReference),
            _ => (0, default),
        };
        if (size == 0 && includeManagedReferences && type.IsValueType
            && type is not GenericParameterTypeAnalysisContext
            && (type is GenericInstanceTypeAnalysisContext
                ? !LocalVariables.ContainsUninstantiatedGenericParameter(type)
                : type.GenericParameters.Count == 0))
        {
            // 封闭泛型消费既有具体字段布局；普通结构只接受原始实例尺寸，不按名称猜尺寸。
            var aggregateSize = type is GenericInstanceTypeAnalysisContext
                ? GenericInstanceFieldLayout.GetSizeAndAlignment(type, pointerSize)?.Size ?? 0
                : TypeSizes.UnboxedSize(type, pointerSize);
            if (aggregateSize is > 0 and <= int.MaxValue)
                (size, kind) = ((int)aggregateSize, ScalarKind.Aggregate);
        }
        return size > 0;
    }

    private static bool SameField(FieldAnalysisContext left, FieldAnalysisContext right)
    {
        static FieldAnalysisContext Base(FieldAnalysisContext field) =>
            field is ConcreteGenericFieldAnalysisContext concrete ? concrete.BaseFieldContext : field;
        return ReferenceEquals(Base(left), Base(right));
    }
}
