using System;
using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Disarm;

namespace Cpp2IL.Core.Utils;

/// <summary>
/// 将 ARM64 ADD 的扩展寄存器或移位寄存器操作数展开为等价 ISIL。
/// </summary>
public static class Arm64AddOperandHelper
{
    private const long UInt32Mask = 0xFFFFFFFFL;
    private const long Int32SignBit = 0x80000000L;

    /// <summary>
    /// 精确展开 ADD 的第三个源操作数，并返回最终参与加法的 ISIL 操作数。
    /// </summary>
    public static bool TryEmit(
        Arm64Instruction instruction,
        IOperand source,
        Func<OpCode, List<IOperand>, Instruction> emit,
        out IOperand transformed)
        => TryEmit(
            instruction.FinalOpExtendType,
            instruction.FinalOpShiftType,
            instruction.Op3Kind,
            instruction.Op3Imm,
            source,
            emit,
            out transformed);

    /// <summary>
    /// 使用已解码的 ARM64 修饰符展开 ADD 操作数，供转换器与测试共享同一计算入口。
    /// </summary>
    public static bool TryEmit(
        Arm64ExtendType extendType,
        Arm64ShiftType shiftType,
        Arm64OperandKind shiftOperandKind,
        long shift,
        IOperand source,
        Func<OpCode, List<IOperand>, Instruction> emit,
        out IOperand transformed)
    {
        if (emit == null)
            throw new ArgumentNullException(nameof(emit));

        transformed = source;

        if (extendType == Arm64ExtendType.NONE && shiftType == Arm64ShiftType.NONE)
            return true;

        if (extendType != Arm64ExtendType.NONE && shiftType != Arm64ShiftType.NONE)
            return false;

        if (shiftOperandKind != Arm64OperandKind.Immediate || shift is < 0 or > 63)
            return false;

        var temporary = new Register(null, "TEMP_ADD_OPERAND");

        if (!TryEmitExtension(extendType, source, temporary, emit, out transformed))
            return false;

        // ARM64 扩展寄存器格式中的立即数同样表示左移位数。
        if (shift == 0)
            return true;

        if (shiftType is not (Arm64ShiftType.NONE or Arm64ShiftType.LSL))
            return false;

        emit(OpCode.ShiftLeft, [temporary, transformed, new Immediate(shift)]);
        transformed = temporary;
        return true;
    }

    /// <summary>
    /// 从本帮助器生成的 SXTW 表达式中取回原始 32 位操作数。
    /// </summary>
    public static IOperand? TryUnwrapSignedWordExtension(
        IOperand transformed,
        Func<IOperand, Instruction?> definition)
    {
        if (definition(transformed) is not
            {
                OpCode: OpCode.Subtract,
                Operands: [_, IOperand xorValue, Immediate { Value: Int32SignBit }],
            })
            return null;

        if (definition(xorValue) is not
            {
                OpCode: OpCode.Xor,
                Operands: [_, IOperand maskedValue, Immediate { Value: Int32SignBit }],
            })
            return null;

        return definition(maskedValue) is
        {
            OpCode: OpCode.And,
            Operands: [_, IOperand original, Immediate { Value: UInt32Mask }],
        }
            ? original
            : null;
    }

    private static bool TryEmitExtension(
        Arm64ExtendType extendType,
        IOperand source,
        Register temporary,
        Func<OpCode, List<IOperand>, Instruction> emit,
        out IOperand transformed)
    {
        transformed = source;

        if (extendType is Arm64ExtendType.NONE or Arm64ExtendType.UXTX or Arm64ExtendType.SXTX)
            return true;

        var bitWidth = extendType switch
        {
            Arm64ExtendType.UXTB or Arm64ExtendType.SXTB => 8,
            Arm64ExtendType.UXTH or Arm64ExtendType.SXTH => 16,
            Arm64ExtendType.UXTW or Arm64ExtendType.SXTW => 32,
            _ => 0,
        };

        if (bitWidth == 0)
            return false;

        var mask = bitWidth == 32 ? UInt32Mask : (1L << bitWidth) - 1;
        emit(OpCode.And, [temporary, source, new Immediate(mask)]);
        transformed = temporary;

        if (extendType is Arm64ExtendType.UXTB or Arm64ExtendType.UXTH or Arm64ExtendType.UXTW)
            return true;

        // (value ^ signBit) - signBit 在 ISIL 整数域中精确完成 N 位符号扩展。
        var signBit = bitWidth == 32 ? Int32SignBit : 1L << (bitWidth - 1);
        emit(OpCode.Xor, [temporary, transformed, new Immediate(signBit)]);
        emit(OpCode.Subtract, [temporary, temporary, new Immediate(signBit)]);
        transformed = temporary;
        return true;
    }
}
