using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Utils;
using Disarm;
using Disarm.InternalDisassembly;

namespace Cpp2IL.Core.Tests;

public class Arm64AddOperandHelperTests
{
    [Test]
    [Category("基本功能")]
    public void SignedWordScaledOperandEmitsExactExpansion()
    {
        var source = new Register(null, "W9");
        var emitted = new List<Instruction>();
        var success = Arm64AddOperandHelper.TryEmit(
            Arm64ExtendType.SXTW,
            Arm64ShiftType.NONE,
            Arm64OperandKind.Immediate,
            4,
            64,
            source,
            (opCode, operands) => Add(emitted, opCode, operands),
            out var transformed);

        Assert.Multiple(() =>
        {
            Assert.That(success, Is.True);
            Assert.That(emitted.Select(item => item.OpCode), Is.EqualTo(new[]
            {
                OpCode.And,
                OpCode.Xor,
                OpCode.Subtract,
                OpCode.ShiftLeft,
            }));
            Assert.That(transformed, Is.TypeOf<Register>());
        });
    }

    [Test]
    [Category("边界值")]
    public void ZeroBitShiftKeepsOriginalOperand()
    {
        var source = new Register(null, "X10");
        var emitted = new List<Instruction>();
        var success = Arm64AddOperandHelper.TryEmit(
            Arm64ExtendType.NONE,
            Arm64ShiftType.LSL,
            Arm64OperandKind.Immediate,
            0,
            64,
            source,
            (opCode, operands) => Add(emitted, opCode, operands),
            out var transformed);

        Assert.Multiple(() =>
        {
            Assert.That(success, Is.True);
            Assert.That(emitted, Is.Empty);
            Assert.That(transformed, Is.EqualTo(source));
        });
    }

    [Test]
    [Category("异常输入")]
    public void RotateModifierIsRejectedWithoutPartialEmission()
    {
        var source = new Register(null, "X10");
        var emitted = new List<Instruction>();
        var success = Arm64AddOperandHelper.TryEmit(
            Arm64ExtendType.NONE,
            Arm64ShiftType.ROR,
            Arm64OperandKind.Immediate,
            3,
            64,
            source,
            (opCode, operands) => Add(emitted, opCode, operands),
            out _);

        Assert.Multiple(() =>
        {
            Assert.That(success, Is.False);
            Assert.That(emitted, Is.Empty);
        });
    }

    [Test]
    [Category("基本功能")]
    public void DecodedSignedWordWithoutExplicitShiftUsesImplicitZero()
    {
        // 原始扩展寄存器编码在移距为零时没有第四操作数；None 必须精确解释为隐式零。
        var instruction = DecodeSingle([0x88, 0xC2, 0x33, 0x8B]); // ADD X8, X20, W19, SXTW
        var emitted = new List<Instruction>();

        var success = Arm64AddOperandHelper.TryEmit(
            instruction,
            new Register(null, "W19"),
            (opCode, operands) => Add(emitted, opCode, operands),
            out var transformed);

        Assert.Multiple(() =>
        {
            Assert.That(success, Is.True);
            Assert.That(emitted.Select(item => item.OpCode), Is.EqualTo(new[]
            {
                OpCode.And,
                OpCode.Xor,
                OpCode.Subtract,
            }));
            // 扩展链保持索引域，最终 ADD 才携带目标 X 寄存器位宽，避免污染地址基址类型。
            Assert.That(emitted.Select(item => item.IntegerWidthBits), Is.All.Zero);
            Assert.That(transformed, Is.TypeOf<Register>());
        });
    }

    [TestCase(
        new byte[] { 0x9C, 0x07, 0x88, 0x0B },
        OpCode.ShiftRight,
        32,
        TestName = "基本_32位ADD算术右移保留符号语义")]
    [TestCase(
        new byte[] { 0xCE, 0x81, 0x8A, 0x8B },
        OpCode.ShiftRight,
        64,
        TestName = "边界_64位ADD算术右移32位")]
    [TestCase(
        new byte[] { 0x6D, 0x05, 0x4D, 0x0B },
        OpCode.ShiftRightUnsigned,
        32,
        TestName = "基本_32位ADD逻辑右移保留无符号语义")]
    [Category("基本功能")]
    public void DecodedRightShiftModifierUsesExactOpcodeAndWidth(
        byte[] machineCode,
        OpCode expectedOpCode,
        int expectedWidthBits)
    {
        var instruction = DecodeSingle(machineCode);
        var emitted = new List<Instruction>();

        var success = Arm64AddOperandHelper.TryEmit(
            instruction,
            new Register(null, Arm64RegisterHelper.CanonicalName(instruction.Op2Reg)),
            (opCode, operands) => Add(emitted, opCode, operands),
            out _);

        Assert.Multiple(() =>
        {
            Assert.That(success, Is.True);
            Assert.That(emitted, Has.Count.EqualTo(1));
            Assert.That(emitted[0].OpCode, Is.EqualTo(expectedOpCode));
            Assert.That(emitted[0].IntegerWidthBits, Is.EqualTo(expectedWidthBits));
        });
    }

    [TestCase(Arm64ExtendType.SXTW, Arm64ShiftType.NONE, 5, 64,
        TestName = "异常_扩展寄存器移距超过四位被拒绝")]
    [TestCase(Arm64ExtendType.NONE, Arm64ShiftType.ASR, 32, 32,
        TestName = "异常_32位移位寄存器移距达到位宽被拒绝")]
    [TestCase(Arm64ExtendType.NONE, Arm64ShiftType.LSR, 1, 48,
        TestName = "异常_非ARM64整数位宽被拒绝")]
    [Category("异常输入")]
    public void InvalidModifierRangeIsRejectedBeforeEmission(
        Arm64ExtendType extendType,
        Arm64ShiftType shiftType,
        long shift,
        int widthBits)
    {
        var emitted = new List<Instruction>();

        var success = Arm64AddOperandHelper.TryEmit(
            extendType,
            shiftType,
            Arm64OperandKind.Immediate,
            shift,
            widthBits,
            new Register(null, "W9"),
            (opCode, operands) => Add(emitted, opCode, operands),
            out _);

        Assert.Multiple(() =>
        {
            Assert.That(success, Is.False);
            Assert.That(emitted, Is.Empty);
        });
    }

    [Test]
    [Category("基本功能")]
    public void SignedWordExpansionCanBeUnwrappedForDispatchAnalysis()
    {
        var original = Local("original", "W9", 1);
        var masked = Local("masked", "TEMP", 1);
        var xorValue = Local("xorValue", "TEMP", 2);
        var transformed = Local("transformed", "TEMP", 3);
        var definitions = new Dictionary<IOperand, Instruction>
        {
            [masked] = new Instruction(0, OpCode.And, masked, original, new Immediate(0xFFFFFFFFL)),
            [xorValue] = new Instruction(1, OpCode.Xor, xorValue, masked, new Immediate(0x80000000L)),
            [transformed] = new Instruction(2, OpCode.Subtract, transformed, xorValue, new Immediate(0x80000000L)),
        };

        var result = Arm64AddOperandHelper.TryUnwrapSignedWordExtension(
            transformed,
            operand => definitions.TryGetValue(operand, out var definition) ? definition : null);

        Assert.That(result, Is.SameAs(original));
    }

    [Test]
    [Category("异常输入")]
    public void MalformedSignedWordExpansionIsNotUnwrapped()
    {
        var transformed = Local("transformed", "TEMP", 3);
        var definition = new Instruction(
            0,
            OpCode.Subtract,
            transformed,
            Local("xorValue", "TEMP", 2),
            new Immediate(1));

        var result = Arm64AddOperandHelper.TryUnwrapSignedWordExtension(
            transformed,
            operand => ReferenceEquals(operand, transformed) ? definition : null);

        Assert.That(result, Is.Null);
    }

    private static Instruction Add(List<Instruction> emitted, OpCode opCode, List<IOperand> operands)
    {
        var instruction = new Instruction(emitted.Count, opCode, operands);
        emitted.Add(instruction);
        return instruction;
    }

    private static Arm64Instruction DecodeSingle(byte[] machineCode)
        => Disassembler.Disassemble(
                machineCode,
                0x1000,
                new Disassembler.Options(true, true, false))
            .Single();

    private static LocalVariable Local(string name, string registerName, int version)
        => new(name, new Register(null, registerName, version));
}
