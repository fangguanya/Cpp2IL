using System.Linq;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Analysis;

/// <summary>统一验证原始非托管值类型的字节边界，供零块写与只读字段原生写共享。</summary>
internal static class ByRefMemoryAccessHelper
{
    internal static bool HasKnownUnmanagedRange(TypeAnalysisContext owner, int pointerSize, long offset, int bytes)
    {
        if (pointerSize is not (4 or 8) || !owner.IsValueType || owner is GenericInstanceTypeAnalysisContext
            || owner.GenericParameters.Count != 0 || owner.Definition == null || bytes <= 0)
            return false;
        long size = TypeSizes.UnboxedSize(owner, pointerSize);
        if (size <= 0 || offset < 0 || offset > size || bytes > size - offset) return false;
        foreach (var field in owner.Fields.Where(field => !field.IsStatic))
        {
            var type = field.FieldType;
            if (type is not PointerTypeAnalysisContext && !type.IsValueType
                || GenericInstanceFieldLayout.GetSizeAndAlignment(type, pointerSize) is not { } layout
                || field.Offset < 0 || field.Offset > size || layout.Size > size - field.Offset)
                return false;
        }
        return true;
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
