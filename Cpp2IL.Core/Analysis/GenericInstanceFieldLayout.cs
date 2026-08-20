using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Analysis;

//Resolves field offsets on generic instances, which are all 0 in the metadata.
public static class GenericInstanceFieldLayout
{
    internal readonly record struct ConcreteFieldLayout(
        FieldAnalysisContext Field,
        long Offset,
        long Size);

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
    {
        var pointerSize = type.AppContext.Binary.PointerSizeBytes;
        if (GetDeclaredFieldStart(type, pointerSize) is not { } offset)
            return null;

        var layout = new List<ConcreteFieldLayout>();
        foreach (var field in type.GenericType.Fields.Where(field => !field.IsStatic))
        {
            var concreteFieldType = GenericInstantiation.Instantiate(field.FieldType, type.GenericArguments, []);
            if (GetSizeAndAlignment(concreteFieldType, pointerSize) is not var (size, alignment))
                return null;

            offset = (offset + alignment - 1) & ~(alignment - 1);
            layout.Add(new(
                new ConcreteGenericFieldAnalysisContext(field, type),
                offset,
                size));
            offset += size;
        }

        return layout;
    }

    /// <summary>
    /// 使用继承链中已经落定的字段偏移计算当前类型首个声明字段的位置。
    /// 非泛型基类的字段偏移来自元数据；泛型基类则递归按其实例参数计算，避免把基类实例字段覆盖掉。
    /// </summary>
    private static long? GetDeclaredFieldStart(GenericInstanceTypeAnalysisContext type, int pointerSize)
    {
        if (type.IsValueType)
            return 0;

        if (type.GenericType.BaseType is not { } openBaseType)
            return 2L * pointerSize;

        var baseType = GenericInstantiation.Instantiate(openBaseType, type.GenericArguments, []);
        return GetReferenceTypeEnd(baseType, pointerSize);
    }

    private static long? GetReferenceTypeEnd(TypeAnalysisContext type, int pointerSize)
    {
        if (type is GenericInstanceTypeAnalysisContext genericInstance)
            return GetGenericInstanceEnd(genericInstance, pointerSize);

        var inheritedEnd = type.BaseType is { } baseType
            ? GetReferenceTypeEnd(baseType, pointerSize)
            : 2L * pointerSize;
        if (inheritedEnd is null)
            return null;

        var end = inheritedEnd.Value;
        foreach (var field in type.Fields.Where(field => !field.IsStatic))
        {
            if (field.Offset < 0 || GetSizeAndAlignment(field.FieldType, pointerSize) is not var (size, _))
                return null;

            end = System.Math.Max(end, field.Offset + size);
        }

        return end;
    }

    private static long? GetGenericInstanceEnd(GenericInstanceTypeAnalysisContext type, int pointerSize)
    {
        if (GetDeclaredFieldStart(type, pointerSize) is not { } offset)
            return null;

        foreach (var field in type.GenericType.Fields.Where(field => !field.IsStatic))
        {
            var concreteFieldType = GenericInstantiation.Instantiate(field.FieldType, type.GenericArguments, []);
            if (GetSizeAndAlignment(concreteFieldType, pointerSize) is not var (size, alignment))
                return null;

            offset = (offset + alignment - 1) & ~(alignment - 1);
            offset += size;
        }

        return offset;
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
    {
        // TODO support user-defined value types
        if (fieldType is GenericParameterTypeAnalysisContext or PointerTypeAnalysisContext || !fieldType.IsValueType)
            return (pointerSize, pointerSize);

        if (fieldType.IsEnumType && fieldType.Fields.FirstOrDefault(f => !f.IsStatic) is { } underlying)
            return GetSizeAndAlignment(underlying.FieldType, pointerSize);

        return fieldType.FullName switch
        {
            "System.Boolean" or "System.Byte" or "System.SByte" => (1, 1),
            "System.Int16" or "System.UInt16" or "System.Char" => (2, 2),
            "System.Int32" or "System.UInt32" or "System.Single" => (4, 4),
            "System.Int64" or "System.UInt64" or "System.Double" => (8, 8),
            "System.IntPtr" or "System.UIntPtr" => (pointerSize, pointerSize),
            _ => null // an arbitrary struct needs its own layout computed, bail rather than guess
        };
    }
}
