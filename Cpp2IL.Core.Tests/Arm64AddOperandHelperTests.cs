using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Utils;
using Disarm;

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

    private static LocalVariable Local(string name, string registerName, int version)
        => new(name, new Register(null, registerName, version));
}
