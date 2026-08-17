using System;
using System.Buffers.Binary;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using LibCpp2IL.Elf;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 把只读 ELF 段中的绝对地址标量加载恢复为 Single/Double 字面量。仅当同一算术指令
/// 已有唯一浮点类型证据时执行，且拒绝可写段、越界、未对齐和混合精度输入。
/// </summary>
public static class ReadOnlyScalarLiteralRecovery
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.AppContext.Binary is not ElfFile elf)
            return;

        var binary = method.AppContext.Binary;
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode is not (
                    OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide)
                || !TryResolveFloatingType(instruction, method.AppContext, out var floatingType, out var widthBits))
                continue;

            var changed = false;
            for (var operandIndex = 1; operandIndex < instruction.Operands.Count; operandIndex++)
            {
                if (instruction.Operands[operandIndex] is not MemoryOperand
                    {
                        Base: null,
                        Index: null,
                        Scale: 0,
                        Addend: >= 0
                    } memory)
                    continue;

                var address = checked((ulong)memory.Addend);
                var byteCount = widthBits / 8;
                if ((address & (ulong)(byteCount - 1)) != 0
                    || !elf.IsReadOnlyFileBackedVirtualRange(address, byteCount)
                    || !binary.TryMapVirtualAddressToRaw(address, out var rawOffset)
                    || rawOffset < 0
                    || rawOffset > binary.RawLength - byteCount)
                    continue;

                var raw = binary.GetRawBinaryContent().Slice(checked((int)rawOffset), byteCount);
                if (!TryDecodeScalarLiteral(raw, binary.IsBigEndian, widthBits, out var literal))
                    continue;

                instruction.SetOperand(operandIndex, literal);
                changed = true;
            }

            if (changed && instruction.Destination is LocalVariable destination)
                destination.Type = floatingType;
        }
    }

    internal static bool TryDecodeScalarLiteral(
        ReadOnlySpan<byte> raw,
        bool isBigEndian,
        int widthBits,
        out IOperand literal)
    {
        if (widthBits == 32 && raw.Length >= sizeof(int))
        {
            var bits = isBigEndian
                ? BinaryPrimitives.ReadInt32BigEndian(raw)
                : BinaryPrimitives.ReadInt32LittleEndian(raw);
            literal = new FloatLiteral(FloatingPointBitHelper.Int32BitsToSingle(bits));
            return true;
        }

        if (widthBits == 64 && raw.Length >= sizeof(long))
        {
            var bits = isBigEndian
                ? BinaryPrimitives.ReadInt64BigEndian(raw)
                : BinaryPrimitives.ReadInt64LittleEndian(raw);
            literal = new DoubleLiteral(BitConverter.Int64BitsToDouble(bits));
            return true;
        }

        literal = null!;
        return false;
    }

    private static bool TryResolveFloatingType(
        Instruction instruction,
        ApplicationAnalysisContext appContext,
        out TypeAnalysisContext floatingType,
        out int widthBits)
    {
        var types = instruction.Operands
            .OfType<LocalVariable>()
            .Select(local => local.Type)
            .Where(type => type?.FullName is "System.Single" or "System.Double")
            .GroupBy(type => type!.FullName)
            .Select(group => group.First())
            .ToArray();
        if (types.Length != 1)
        {
            floatingType = null!;
            widthBits = 0;
            return false;
        }

        widthBits = types[0]!.FullName == "System.Single" ? 32 : 64;
        floatingType = widthBits == 32
            ? appContext.SystemTypes.SystemSingleType
            : appContext.SystemTypes.SystemDoubleType;
        return true;
    }
}
