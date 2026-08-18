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

                if (!TryResolveFloatingType(
                        instruction,
                        operandIndex,
                        method.AppContext,
                        out var floatingType,
                        out var widthBits))
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
                if (instruction.OpCode is
                        OpCode.Move or OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide
                    && instruction.Destination is LocalVariable moveDestination)
                    moveDestination.Type = floatingType;
            }
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
        int operandIndex,
        ApplicationAnalysisContext appContext,
        out TypeAnalysisContext floatingType,
        out int widthBits)
    {
        TypeAnalysisContext? expectedType = null;
        if (instruction.IsCall
            && instruction.Operands[0] is MethodAnalysisContext called
            && TryResolveCallParameterIndex(instruction, called, operandIndex, out var parameterIndex))
        {
            expectedType = called.Parameters[parameterIndex].ParameterType;
        }
        else if (instruction is { OpCode: OpCode.Move, Operands: [LocalVariable destination, ..] }
                 && operandIndex == 1)
        {
            expectedType = destination.Type;
        }
        else if (instruction is { OpCode: OpCode.Move, Operands: [FieldReference field, ..] }
                 && operandIndex == 1)
        {
            expectedType = field.Field.FieldType;
        }
        else if (instruction.OpCode is
                 OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide)
        {
            var types = instruction.Operands
                .OfType<LocalVariable>()
                .Select(local => local.Type)
                .Where(type => type?.FullName is "System.Single" or "System.Double")
                .DistinctBy(type => type!.FullName)
                .ToArray();
            if (types.Length == 1)
                expectedType = types[0];
        }

        if (expectedType?.FullName is not ("System.Single" or "System.Double"))
        {
            floatingType = null!;
            widthBits = 0;
            return false;
        }

        widthBits = expectedType.FullName == "System.Single" ? 32 : 64;
        floatingType = widthBits == 32
            ? appContext.SystemTypes.SystemSingleType
            : appContext.SystemTypes.SystemDoubleType;
        return true;
    }

    /// <summary>
    /// 根据 ISIL 调用操作数布局把当前操作数映射回托管形参。实例接收者和非 void
    /// 调用的返回值槽位均不属于形参；越界、静态/实例布局不一致时保持未知。
    /// </summary>
    internal static bool TryResolveCallParameterIndex(
        Instruction call,
        MethodAnalysisContext called,
        int operandIndex,
        out int parameterIndex)
    {
        var firstParameter = (call.OpCode == OpCode.Call ? 2 : 1)
                             + (called.IsStatic ? 0 : 1);
        parameterIndex = operandIndex - firstParameter;
        return parameterIndex >= 0 && parameterIndex < called.Parameters.Count;
    }
}
