using System.Collections.Generic;
using System.Linq;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.Analysis;

/// <summary>统一验证原始非托管值类型的字节边界，供零块写与只读字段原生写共享。</summary>
internal static class ByRefMemoryAccessHelper
{
    /// <summary>引用地址与字节偏移是不同栈种类；只接受真实 ByRef 来源，不把整数构造成引用。</summary>
    internal static bool TryDescribeByteOffset(Instruction instruction, int pointerSize,
        out LocalVariable receiver, out long offset)
    {
        receiver = null!;
        offset = 0;
        if (pointerSize is not (4 or 8)
            || instruction.IntegerWidthBits != 0 && instruction.IntegerWidthBits != pointerSize * 8
            || instruction.OpCode is not (OpCode.Add or OpCode.Subtract)
            || instruction.Operands.Count != 3
            || instruction.Operands[0] is not LocalVariable { Type: ByRefTypeAnalysisContext })
            return false;
        if (instruction.Operands[1] is LocalVariable { Type: ByRefTypeAnalysisContext } left
            && instruction.Operands[2] is Immediate right)
        {
            receiver = left;
            offset = right.Value;
            return true;
        }
        if (instruction.OpCode == OpCode.Add && instruction.Operands[1] is Immediate first
            && instruction.Operands[2] is LocalVariable { Type: ByRefTypeAnalysisContext } second)
        {
            receiver = second;
            offset = first.Value;
            return true;
        }
        return false;
    }

    internal static bool HasKnownUnmanagedRange(TypeAnalysisContext owner, int pointerSize, long offset, int bytes)
    {
        if (pointerSize is not (4 or 8) || !owner.IsValueType || owner is GenericInstanceTypeAnalysisContext
            || owner.GenericParameters.Count != 0 || owner.Definition == null || bytes <= 0)
            return false;
        long size = TypeSizes.UnboxedSize(owner, pointerSize);
        if (size <= 0 || offset < 0 || offset > size || bytes > size - offset) return false;
        var fields = owner.Fields.Where(field => !field.IsStatic).Select(field =>
        {
            var known = ManagedFieldSpanRecoveryHelper.TryGetScalarLayout(
                field.FieldType, pointerSize, includeManagedReferences: false,
                out var fieldSize, out _);
            return (field, offset: (long)field.Offset, fieldSize: known ? (long)fieldSize : 0L);
        });
        return HasKnownUnmanagedFieldLayout(fields, pointerSize, size, new() { owner });
    }

    /// <summary>
    /// 要求整个值类型都由已知非托管字段构成，并且实例尺寸与原生访问宽度完全一致。
    /// 封闭泛型必须复用具体字段布局；开放泛型和未落定布局继续保持失败。
    /// </summary>
    internal static bool HasExactKnownUnmanagedLayout(TypeAnalysisContext owner, int pointerSize, int bytes)
    {
        if (pointerSize is not (4 or 8) || !owner.IsValueType || bytes <= 0)
            return false;

        if (owner is GenericInstanceTypeAnalysisContext generic)
        {
            var layout = GenericInstanceFieldLayout.GetConcreteFieldLayout(generic);
            if (layout is null
                || GenericInstanceFieldLayout.GetSizeAndAlignment(generic, pointerSize)?.Size != bytes)
                return false;

            var concreteFields = layout.Select(field => (field.Field, field.Offset, field.Size));
            return HasKnownUnmanagedFieldLayout(concreteFields, pointerSize, bytes, new() { generic });
        }

        if (TypeSizes.UnboxedSize(owner, pointerSize) != bytes
            || owner.GenericParameters.Count != 0)
            return false;

        // 某些显式布局值类型只有字段上下文而没有可回溯的 Definition；根字段的精确尺寸与
        // 递归字段类型仍然足以证明整字段位模式，不能把 Definition 是否存在混入尺寸合同。
        var fields = owner.Fields.Where(field => !field.IsStatic).Select(field =>
        {
            var layout = GenericInstanceFieldLayout.GetSizeAndAlignment(field.FieldType, pointerSize);
            return (field, offset: (long)field.Offset, fieldSize: layout?.Size ?? 0);
        });
        return HasKnownUnmanagedFieldLayout(fields, pointerSize, bytes, new() { owner });
    }

    /// <summary>
    /// 检查字段尺寸、字段边界以及字段类型的递归无托管证明；只看外层 IsValueType 会放过嵌套引用字段。
    /// </summary>
    private static bool HasKnownUnmanagedFieldLayout(
        IEnumerable<(FieldAnalysisContext Field, long Offset, long FieldSize)> fields,
        int pointerSize,
        long ownerSize,
        HashSet<TypeAnalysisContext> active)
    {
        foreach (var (field, offset, fieldSize) in fields)
        {
            if (fieldSize <= 0 || offset < 0 || offset > ownerSize || fieldSize > ownerSize - offset
                || !HasKnownUnmanagedType(field.FieldType, pointerSize, active))
                return false;
        }

        return true;
    }

    /// <summary>
    /// 递归展开值类型字段；遇到引用、开放泛型、ByRef 或无法取得具体字段布局时保留失败。
    /// </summary>
    private static bool HasKnownUnmanagedType(
        TypeAnalysisContext type,
        int pointerSize,
        HashSet<TypeAnalysisContext> active)
    {
        if (type is PointerTypeAnalysisContext)
            return true;

        if (type.IsEnumType && type.EnumUnderlyingType is { } underlying)
            return HasKnownUnmanagedType(underlying, pointerSize, active);

        var primitive = type.AppContext.SystemTypes.TryGetIl2CppTypeEnum(type, out var resolved)
            ? resolved : Il2CppTypeEnum.IL2CPP_TYPE_END;
        if (primitive is Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN
            or Il2CppTypeEnum.IL2CPP_TYPE_U1 or Il2CppTypeEnum.IL2CPP_TYPE_I1
            or Il2CppTypeEnum.IL2CPP_TYPE_CHAR
            or Il2CppTypeEnum.IL2CPP_TYPE_U2 or Il2CppTypeEnum.IL2CPP_TYPE_I2
            or Il2CppTypeEnum.IL2CPP_TYPE_U4 or Il2CppTypeEnum.IL2CPP_TYPE_I4
            or Il2CppTypeEnum.IL2CPP_TYPE_U8 or Il2CppTypeEnum.IL2CPP_TYPE_I8
            or Il2CppTypeEnum.IL2CPP_TYPE_R4 or Il2CppTypeEnum.IL2CPP_TYPE_R8
            or Il2CppTypeEnum.IL2CPP_TYPE_U or Il2CppTypeEnum.IL2CPP_TYPE_I)
            return true;

        if (type is GenericParameterTypeAnalysisContext
            or ByRefTypeAnalysisContext
            or StaticFieldStorageTypeAnalysisContext
            || !type.IsValueType)
            return false;

        if (!active.Add(type))
            return false;

        try
        {
            if (type is GenericInstanceTypeAnalysisContext generic)
            {
                var layout = GenericInstanceFieldLayout.GetConcreteFieldLayout(generic);
                return layout is not null
                    && HasKnownUnmanagedFieldLayout(
                        layout.Select(field => (field.Field, field.Offset, field.Size)),
                        pointerSize,
                        GenericInstanceFieldLayout.GetSizeAndAlignment(generic, pointerSize)?.Size ?? 0,
                        active);
            }

            if (type.Definition is null || type.GenericParameters.Count != 0)
                return false;

            var size = TypeSizes.UnboxedSize(type, pointerSize);
            if (size <= 0)
                return false;

            var fields = type.Fields.Where(field => !field.IsStatic).Select(field =>
            {
                var known = ManagedFieldSpanRecoveryHelper.TryGetScalarLayout(
                    field.FieldType, pointerSize, includeManagedReferences: false,
                    out var fieldSize, out _);
                return (field, offset: (long)field.Offset, fieldSize: known ? (long)fieldSize : 0L);
            });
            return HasKnownUnmanagedFieldLayout(fields, pointerSize, size, active);
        }
        finally
        {
            active.Remove(type);
        }
    }

    internal static bool TryDescribeReadonlyStore(Instruction instruction, int pointerSize, out CilOpCode store)
    {
        store = default;
        if (instruction is not { OpCode: OpCode.Move, Operands: [FieldReference field, _] }
            || field.Field.IsStatic || (field.Field.Attributes & System.Reflection.FieldAttributes.InitOnly) == 0
            || field.Local.Type is not ByRefTypeAnalysisContext byRef
            || !byRef.ElementType.Fields.Contains(field.Field) || field.Offset != field.Field.Offset)
            return false;
        var type = field.Field.FieldType;
        if (type.IsEnumType) type = type.EnumUnderlyingType!;
        int bytes;
        if (type is PointerTypeAnalysisContext || type.FullName is "System.IntPtr" or "System.UIntPtr")
        { store = CilOpCodes.Stind_I; bytes = pointerSize; }
        else
        {
            (store, bytes) = type.FullName switch
            {
                "System.Boolean" or "System.Byte" or "System.SByte" => (CilOpCodes.Stind_I1, 1),
                "System.Char" or "System.Int16" or "System.UInt16" => (CilOpCodes.Stind_I2, 2),
                "System.Int32" or "System.UInt32" => (CilOpCodes.Stind_I4, 4),
                "System.Int64" or "System.UInt64" => (CilOpCodes.Stind_I8, 8),
                "System.Single" => (CilOpCodes.Stind_R4, 4),
                "System.Double" => (CilOpCodes.Stind_R8, 8),
                _ => (default, 0)
            };
        }
        return instruction.MemoryAccessWidthBits == bytes * 8
            && HasKnownUnmanagedRange(byRef.ElementType, pointerSize, field.Offset, bytes);
    }
}
