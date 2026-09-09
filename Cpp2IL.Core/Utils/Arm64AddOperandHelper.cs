using System;
using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Disarm;
using Disarm.InternalDisassembly;

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
    {
        transformed = source;
        var extendType = instruction.FinalOpExtendType;
        var shiftType = instruction.FinalOpShiftType;
        if (extendType == Arm64ExtendType.NONE && shiftType == Arm64ShiftType.NONE)
            return true;

        if (instruction.Op0Kind != Arm64OperandKind.Register
            || instruction.Op1Kind != Arm64OperandKind.Register
            || instruction.Op2Kind != Arm64OperandKind.Register
            || !TryGetRegisterWidthBits(instruction.Op0Reg, out var widthBits)
            || !TryGetRegisterWidthBits(instruction.Op1Reg, out var leftWidthBits)
            || !TryGetRegisterWidthBits(instruction.Op2Reg, out var sourceWidthBits)
            || leftWidthBits != widthBits
            || !IsModifierSourceWidthValid(extendType, widthBits, sourceWidthBits))
            return false;

        return TryEmit(
            extendType,
            shiftType,
            instruction.Op3Kind,
            instruction.Op3Imm,
            widthBits,
            source,
            emit,
            out transformed);
    }

    /// <summary>
    /// 使用已解码的 ARM64 修饰符展开 ADD 操作数，供转换器与测试共享同一计算入口。
    /// </summary>
    public static bool TryEmit(
        Arm64ExtendType extendType,
        Arm64ShiftType shiftType,
        Arm64OperandKind shiftOperandKind,
        long shift,
        int integerWidthBits,
        IOperand source,
        Func<OpCode, List<IOperand>, Instruction> emit,
        out IOperand transformed)
    {
        if (emit == null)
            throw new ArgumentNullException(nameof(emit));

        transformed = source;

        if (integerWidthBits is not (32 or 64))
            return false;

        if (extendType == Arm64ExtendType.NONE && shiftType == Arm64ShiftType.NONE)
            return true;

        if (extendType != Arm64ExtendType.NONE && shiftType != Arm64ShiftType.NONE)
            return false;

        if (shiftOperandKind == Arm64OperandKind.None)
        {
            if (shift != 0)
                return false;
        }
        else if (shiftOperandKind != Arm64OperandKind.Immediate)
            return false;

        var temporary = new Register(null, "TEMP_ADD_OPERAND");
        Instruction EmitSizedShift(OpCode opCode, List<IOperand> operands)
        {
            var instruction = emit(opCode, operands);
            instruction.IntegerWidthBits = integerWidthBits;
            return instruction;
        }

        if (extendType != Arm64ExtendType.NONE)
        {
            // 扩展寄存器编码只允许 0..4 位左移；无显式第四操作数即移距零。
            if (shiftType != Arm64ShiftType.NONE || shift is < 0 or > 4
                || !TryEmitExtension(extendType, source, temporary, emit, out transformed))
                return false;
            if (shift == 0)
                return true;

            // 扩展链描述的是参与最终 ADD 的索引值，而不是独立的托管 Int64 载体；
            // 最终 ADD 自身保留目标寄存器位宽，避免在字段恢复前把未知地址基址定型成整数。
            emit(OpCode.ShiftLeft, [temporary, transformed, new Immediate(shift)]);
            transformed = temporary;
            return true;
        }

        // 移位寄存器编码按目标 W/X 位宽约束移距，并区分逻辑与算术右移。
        if (shift is < 0 || shift >= integerWidthBits)
            return false;
        var shiftOpCode = shiftType switch
        {
            Arm64ShiftType.LSL => OpCode.ShiftLeft,
            Arm64ShiftType.LSR => OpCode.ShiftRightUnsigned,
            Arm64ShiftType.ASR => OpCode.ShiftRight,
            _ => OpCode.NotImplemented,
        };
        if (shiftOpCode == OpCode.NotImplemented)
            return false;
        if (shift == 0)
            return true;

        EmitSizedShift(shiftOpCode, [temporary, transformed, new Immediate(shift)]);
        transformed = temporary;
        return true;
    }

    private static bool IsModifierSourceWidthValid(
        Arm64ExtendType extendType,
        int destinationWidthBits,
        int sourceWidthBits)
        => extendType switch
        {
            Arm64ExtendType.NONE => sourceWidthBits == destinationWidthBits,
            Arm64ExtendType.UXTB or Arm64ExtendType.UXTH or Arm64ExtendType.UXTW
                or Arm64ExtendType.SXTB or Arm64ExtendType.SXTH or Arm64ExtendType.SXTW
                => sourceWidthBits == 32,
            Arm64ExtendType.UXTX or Arm64ExtendType.SXTX
                => destinationWidthBits == 64 && sourceWidthBits == 64,
            _ => false,
        };

    private static bool TryGetRegisterWidthBits(Arm64Register register, out int widthBits)
    {
        if (register is >= Arm64Register.W0 and <= Arm64Register.W31)
        {
            widthBits = 32;
            return true;
        }

        if (register is >= Arm64Register.X0 and <= Arm64Register.X31)
        {
            widthBits = 64;
            return true;
        }

        widthBits = 0;
        return false;
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
