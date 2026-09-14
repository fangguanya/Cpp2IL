using System;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

internal static class ManagedArraySpanRecoveryHelper
{
    internal sealed record Description(TypeAnalysisContext ElementType, int ElementBytes,
        ManagedFieldSpanRecoveryHelper.ScalarKind Kind, int Count, bool NativeIndex);

    internal static bool TryDescribe(Instruction instruction, int pointerSize, out Description description)
    {
        description = null!;
        if (instruction is not { OpCode: OpCode.Move, MemoryAccessWidthBits: 64 or 128,
                Operands: [LocalVariable, ArrayAccess array] }
            || pointerSize is not (4 or 8)
            || array.Array.Type is not SzArrayTypeAnalysisContext { ElementType: var element }
            || !ManagedFieldSpanRecoveryHelper.TryGetScalarLayout(element, pointerSize, false, out var size, out var kind)
            || size is not (1 or 2 or 4 or 8) || instruction.MemoryAccessWidthBits % (size * 8) != 0)
            return false;
        var count = instruction.MemoryAccessWidthBits / (size * 8);
        var system = element.AppContext.SystemTypes;
        var nativeIndex = array.Index is LocalVariable index
            && (ReferenceEquals(index.Type, system.SystemIntPtrType)
                || ReferenceEquals(index.Type, system.SystemUIntPtrType));
        // 索引身份必须来自当前应用核心类型，原生整数不得截断为Int32。
        if (array.Index is Immediate immediate
                ? immediate.Value < 0 || immediate.Value > int.MaxValue - (count - 1)
                : array.Index is not LocalVariable local
                  || !(nativeIndex || ReferenceEquals(local.Type, system.SystemInt32Type)))
            return false;
        description = new Description(element, size, kind, count, nativeIndex);
        return true;
    }
}
