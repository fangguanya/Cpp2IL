using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Analysis;

// 从原始布局证据恢复泛型实例字段位置；泛型声明偏移本身不代表具体实例。
public static class GenericInstanceFieldLayout
{
    internal readonly record struct ConcreteFieldLayout(
        FieldAnalysisContext Field,
        long Offset,
        long Size,
        long Alignment);

    // 每次布局查询独立持有缓存和递归路径，不跨应用保留类型图。
    private sealed class LayoutTraversal
    {
        internal readonly HashSet<GenericInstanceTypeAnalysisContext> Active = [];
        internal readonly HashSet<TypeAnalysisContext> OriginalActive = [];
        internal readonly HashSet<TypeAnalysisContext> ReferenceActive = [];
        internal readonly Dictionary<TypeAnalysisContext, (long Size, long Alignment)?> OriginalCompleted = [];
        internal readonly Dictionary<GenericInstanceTypeAnalysisContext, IReadOnlyList<ConcreteFieldLayout>?> Completed = [];
    }

    /// <summary>
    /// 将具体泛型实例或开放泛型声明统一转换为可计算字段布局的泛型实例。
    /// 开放声明使用自身类型参数，既保留 T 等精确字段签名，又按 IL2CPP 共享泛型布局将其视为指针宽度。
    /// </summary>
    internal static GenericInstanceTypeAnalysisContext? CreateLayoutOwner(TypeAnalysisContext owner)
    {
        if (owner is GenericInstanceTypeAnalysisContext genericInstance)
            return genericInstance;

        return owner.GenericParameters.Count == 0
            ? null
            : owner.MakeGenericInstanceType(owner.GenericParameters);
    }

    public static FieldAnalysisContext? FindFieldAtOffset(GenericInstanceTypeAnalysisContext type, long targetOffset)
    {
        var layout = GetConcreteFieldLayout(type);
        var field = layout?.FirstOrDefault(entry => entry.Offset == targetOffset).Field;
        return field is ConcreteGenericFieldAnalysisContext concrete
            ? concrete.BaseFieldContext
            : field;
    }

    /// <summary>
    /// 返回泛型实例的单一权威字段布局。字段偏移恢复与ARM64聚合参数投影共用此结果，
    /// 避免两条流水线分别计算对齐后产生不一致。
    /// </summary>
    internal static IReadOnlyList<ConcreteFieldLayout>? GetConcreteFieldLayout(
        GenericInstanceTypeAnalysisContext type)
        => GetConcreteFieldLayout(type, type.AppContext.Binary.PointerSizeBytes, new LayoutTraversal());

    private static IReadOnlyList<ConcreteFieldLayout>? GetConcreteFieldLayout(
        GenericInstanceTypeAnalysisContext type, int pointerSize, LayoutTraversal traversal)
    {
        if (pointerSize is not (4 or 8)) return null;
        // 实例上下文保留全部泛型实参身份；显示名称不包含实参程序集，不能作为布局键。
        var key = type;
        if (traversal.Completed.TryGetValue(key, out var cached)) return cached;
        if (traversal.Active.Count >= 64 || !traversal.Active.Add(key)) return null;
        try
        {
            if (GetDeclaredFieldStart(type, pointerSize, traversal) is not { } offset)
                return null;

            // 显式布局及指定类尺寸需额外原始尺寸证据，不能套用顺序字段算法。
            var definition = type.GenericType.Definition;
            if (type.IsValueType
                && ((type.Attributes & System.Reflection.TypeAttributes.LayoutMask) == System.Reflection.TypeAttributes.ExplicitLayout
                    || definition is { ClassSizeIsDefault: false })) return null;
            var packing = definition?.PackingSize ?? 0;

            var layout = new List<ConcreteFieldLayout>();
            foreach (var field in type.GenericType.Fields.Where(field => !field.IsStatic))
            {
                var concreteFieldType = GenericInstantiation.Instantiate(field.FieldType, type.GenericArguments, []);
                if (GetSizeAndAlignment(concreteFieldType, pointerSize, traversal) is not var (size, alignment))
                    return null;
                if (packing != 0) alignment = System.Math.Min(alignment, packing);

                offset = checked(offset + alignment - 1) & ~(alignment - 1);
                layout.Add(new(
                    new ConcreteGenericFieldAnalysisContext(field, type),
                    offset,
                    size, alignment));
                offset = checked(offset + size);
            }

            traversal.Completed[key] = layout;
            return layout;
        }
        finally { traversal.Active.Remove(key); }
    }

    /// <summary>
    /// 使用继承链中已经落定的字段偏移计算当前类型首个声明字段的位置。
    /// 非泛型基类的字段偏移来自元数据；泛型基类则递归按其实例参数计算，避免把基类实例字段覆盖掉。
    /// </summary>
    private static long? GetDeclaredFieldStart(GenericInstanceTypeAnalysisContext type, int pointerSize, LayoutTraversal traversal)
    {
        if (type.IsValueType)
            return 0;

        if (type.GenericType.BaseType is not { } openBaseType)
            return 2L * pointerSize;

        var baseType = GenericInstantiation.Instantiate(openBaseType, type.GenericArguments, []);
        return GetReferenceTypeEnd(baseType, pointerSize, traversal);
    }

    private static long? GetReferenceTypeEnd(TypeAnalysisContext type, int pointerSize, LayoutTraversal traversal)
    {
        if (type is GenericInstanceTypeAnalysisContext genericInstance)
        {
            // 基类末尾复用唯一具体布局，保持打包规则一致，不再次计算全部字段。
            var layout = GetConcreteFieldLayout(genericInstance, pointerSize, traversal);
            if (layout is null) return null;
            return layout.Count == 0
                ? GetDeclaredFieldStart(genericInstance, pointerSize, traversal)
                : layout.Max(field => checked(field.Offset + field.Size));
        }

        if (traversal.ReferenceActive.Count >= 64 || !traversal.ReferenceActive.Add(type)) return null;
        try
        {
            var inheritedEnd = type.BaseType is { } baseType
                ? GetReferenceTypeEnd(baseType, pointerSize, traversal)
                : 2L * pointerSize;
            if (inheritedEnd is null) return null;
            var end = inheritedEnd.Value;
            foreach (var field in type.Fields.Where(field => !field.IsStatic))
            {
                if (field.Offset < 0 || GetSizeAndAlignment(field.FieldType, pointerSize, traversal) is not var (size, _))
                    return null;
                end = System.Math.Max(end, checked(field.Offset + size));
            }
            return end;
        }
        finally { traversal.ReferenceActive.Remove(type); }
    }

    /// <summary>
    /// 按具体泛型实例的未装箱偏移返回已经实例化字段类型的字段上下文。
    /// </summary>
    public static FieldAnalysisContext? FindConcreteFieldAtOffset(
        GenericInstanceTypeAnalysisContext type,
        long targetOffset)
    {
        return GetConcreteFieldLayout(type)?
            .FirstOrDefault(entry => entry.Offset == targetOffset)
            .Field;
    }

    internal static (long Size, long Alignment)? GetSizeAndAlignment(TypeAnalysisContext fieldType, int pointerSize)
        => GetSizeAndAlignment(fieldType, pointerSize, new LayoutTraversal());

    private static (long Size, long Alignment)? GetSizeAndAlignment(TypeAnalysisContext fieldType, int pointerSize,
        LayoutTraversal traversal)
    {
        if (pointerSize is not (4 or 8)) return null;
        if (fieldType is GenericParameterTypeAnalysisContext or PointerTypeAnalysisContext || !fieldType.IsValueType)
            return (pointerSize, pointerSize);

        if (fieldType.IsEnumType && fieldType.Fields.FirstOrDefault(f => !f.IsStatic) is { } underlying)
            return GetSizeAndAlignment(underlying.FieldType, pointerSize, traversal);

        if (fieldType is GenericInstanceTypeAnalysisContext generic)
        {
            var fields = GetConcreteFieldLayout(generic, pointerSize, traversal);
            if (fields is not { Count: > 0 }) return null;
            var alignment = fields.Max(field => field.Alignment);
            var end = fields.Max(field => checked(field.Offset + field.Size));
            // 数组步长及外层后续字段必须包含尾部对齐填充，不仅是最后字段末尾。
            return (checked(end + alignment - 1) & ~(alignment - 1), alignment);
        }

        // 与字段跨度共享核心声明身份和尺寸表；同名非核心值类型不能取得基元 ABI 布局。
        if (ManagedFieldSpanRecoveryHelper.TryGetScalarLayout(fieldType, pointerSize, false, out var size, out _))
            return (size, size);
        return GetOriginalValueLayout(fieldType, pointerSize, traversal);
    }

    private static (long Size, long Alignment)? GetOriginalValueLayout(
        TypeAnalysisContext type, int pointerSize, LayoutTraversal traversal)
    {
        if (traversal.OriginalCompleted.TryGetValue(type, out var cached)) return cached;
        if (traversal.OriginalActive.Count + traversal.Active.Count >= 64
            || !traversal.OriginalActive.Add(type)) return null;
        try
        {
            var definition = type.Definition;
            // 原始尺寸属于实际输入架构；指定尺寸、显式或自动布局均需另行证明。
            if (pointerSize != type.AppContext.Binary.PointerSizeBytes || definition is null
                || type.GenericParameters.Count != 0 || !definition.ClassSizeIsDefault
                || (type.Attributes & System.Reflection.TypeAttributes.LayoutMask)
                    != System.Reflection.TypeAttributes.SequentialLayout) return null;
            var originalSize = TypeSizes.UnboxedSize(type, pointerSize);
            if (originalSize <= 0) return null;
            var packing = definition.PackingSize;
            if (packing < 0 || (packing != 0 && (packing & (packing - 1)) != 0)) return null;
            long end = 0, maximumAlignment = 1;
            var count = 0;
            foreach (var field in type.Fields.Where(field => !field.IsStatic))
            {
                // 字段偏移已经去除装箱头；不再次减头，也不接受注入字段伪造原始证明。
                if (field.BackingData is null || field.Offset != field.DefaultOffset
                    || GetSizeAndAlignment(field.FieldType, pointerSize, traversal) is not var (size, alignment)) return null;
                if (packing != 0) alignment = System.Math.Min(alignment, packing);
                var offset = checked(end + alignment - 1) & ~(alignment - 1);
                if (offset != field.Offset || size <= 0) return null;
                end = checked(offset + size);
                if (end > originalSize) return null;
                maximumAlignment = System.Math.Max(maximumAlignment, alignment);
                count++;
            }
            var paddedEnd = checked(end + maximumAlignment - 1) & ~(maximumAlignment - 1);
            if (count == 0 || paddedEnd != originalSize) return null;
            var result = (originalSize, maximumAlignment);
            traversal.OriginalCompleted[type] = result;
            return result;
        }
        finally { traversal.OriginalActive.Remove(type); }
    }

}
