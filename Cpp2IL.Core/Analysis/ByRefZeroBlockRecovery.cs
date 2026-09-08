using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 对有原始尺寸证据且不含托管引用的值类型，保留零写的精确字节范围，包括填充。
/// 仅描述原指令，不合并访问、不推断未知布局，也不缩短到首个字段。
/// </summary>
internal static class ByRefZeroBlockRecovery
{
    internal readonly record struct ZeroBlock(LocalVariable Receiver, long Offset, int ByteCount);

    internal static bool TryDescribe(Instruction instruction, int pointerSize, out ZeroBlock block)
    {
        block = default;
        if (instruction is not { OpCode: OpCode.Move, MemoryAccessWidthBits: > 0,
                Operands: [MemoryOperand { Base: LocalVariable { Type: ByRefTypeAnalysisContext byRef } receiver,
                    Index: null, Scale: 0 } memory, Immediate { Value: 0 }] }
            || instruction.MemoryAccessWidthBits % 8 != 0
            || pointerSize is not (4 or 8)
            || !byRef.ElementType.IsValueType
            || byRef.ElementType is GenericInstanceTypeAnalysisContext
            || byRef.ElementType.GenericParameters.Count != 0
            || byRef.ElementType.Definition == null)
            return false;

        var owner = byRef.ElementType;
        var bytes = instruction.MemoryAccessWidthBits / 8;
        if (!ByRefMemoryAccessHelper.HasKnownUnmanagedRange(owner, pointerSize, memory.Addend, bytes))
            return false;

        block = new(receiver, memory.Addend, bytes);
        return true;
    }
}
