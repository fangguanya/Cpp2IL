using System;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class ReadOnlyScalarLiteralRecoveryTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 小端单精度字节恢复为一百万分之一()
    {
        var raw = BitConverter.GetBytes(1e-6f);

        var decoded = ReadOnlyScalarLiteralRecovery.TryDecodeScalarLiteral(
            raw,
            isBigEndian: false,
            widthBits: 32,
            out var literal);

        Assert.Multiple(() =>
        {
            Assert.That(decoded, Is.True);
            Assert.That(literal, Is.TypeOf<FloatLiteral>());
            Assert.That(((FloatLiteral)literal).Value, Is.EqualTo(1e-6f));
        });
    }

    [Test]
    [Category("边界值")]
    public void 大端双精度字节保持负零位模式()
    {
        var bits = BitConverter.DoubleToInt64Bits(-0d);
        var raw = BitConverter.GetBytes(bits);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(raw);

        var decoded = ReadOnlyScalarLiteralRecovery.TryDecodeScalarLiteral(
            raw,
            isBigEndian: true,
            widthBits: 64,
            out var literal);

        Assert.Multiple(() =>
        {
            Assert.That(decoded, Is.True);
            Assert.That(literal, Is.TypeOf<DoubleLiteral>());
            Assert.That(
                BitConverter.DoubleToInt64Bits(((DoubleLiteral)literal).Value),
                Is.EqualTo(bits));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 非法位宽和短缓冲区均拒绝解码()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                ReadOnlyScalarLiteralRecovery.TryDecodeScalarLiteral(
                    [0, 0, 0, 0],
                    isBigEndian: false,
                    widthBits: 16,
                    out _),
                Is.False);
            Assert.That(
                ReadOnlyScalarLiteralRecovery.TryDecodeScalarLiteral(
                    [0, 0, 0],
                    isBigEndian: false,
                    widthBits: 32,
                    out _),
                Is.False);
        });
    }

    [Test]
    [Category("基本功能")]
    public void 实例无返回调用的浮点实参正确映射到托管形参()
    {
        var (target, receiver, firstArgument, secondArgument) = CreateCallFixture();
        var call = new Instruction(
            0,
            OpCode.CallVoid,
            target,
            receiver,
            firstArgument,
            secondArgument);

        var firstMapped = ReadOnlyScalarLiteralRecovery.TryResolveCallParameterIndex(
            call,
            target,
            operandIndex: 2,
            out var firstParameterIndex);
        var secondMapped = ReadOnlyScalarLiteralRecovery.TryResolveCallParameterIndex(
            call,
            target,
            operandIndex: 3,
            out var secondParameterIndex);

        Assert.Multiple(() =>
        {
            Assert.That(firstMapped, Is.True);
            Assert.That(firstParameterIndex, Is.Zero);
            Assert.That(secondMapped, Is.True);
            Assert.That(secondParameterIndex, Is.EqualTo(1));
        });
    }

    [Test]
    [Category("边界值")]
    public void 实例接收者不被映射为第一个托管形参()
    {
        var (target, receiver, firstArgument, secondArgument) = CreateCallFixture();
        var call = new Instruction(
            0,
            OpCode.CallVoid,
            target,
            receiver,
            firstArgument,
            secondArgument);

        var mapped = ReadOnlyScalarLiteralRecovery.TryResolveCallParameterIndex(
            call,
            target,
            operandIndex: 1,
            out _);

        Assert.That(mapped, Is.False);
    }

    [Test]
    [Category("异常输入")]
    public void 调用形参范围之外的操作数保持未知()
    {
        var (target, receiver, firstArgument, secondArgument) = CreateCallFixture();
        var call = new Instruction(
            0,
            OpCode.CallVoid,
            target,
            receiver,
            firstArgument,
            secondArgument);

        var mapped = ReadOnlyScalarLiteralRecovery.TryResolveCallParameterIndex(
            call,
            target,
            operandIndex: 4,
            out _);

        Assert.That(mapped, Is.False);
    }

    private static (MethodAnalysisContext Target, LocalVariable Receiver, MemoryOperand First, MemoryOperand Second)
        CreateCallFixture()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var assembly = app.Assemblies[0];
        var owner = new InjectedTypeAnalysisContext(
            assembly,
            "Cpp2IL.Core.Tests",
            "ReadOnlyFloatOwner",
            app.SystemTypes.SystemObjectType,
            TypeAttributes.Public | TypeAttributes.Class);
        var target = new InjectedMethodAnalysisContext(
            owner,
            "Update",
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Public,
            [app.SystemTypes.SystemSingleType, app.SystemTypes.SystemSingleType],
            ["first", "second"]);
        var receiver = new LocalVariable("receiver", new Register(null, "X0", 1), owner);
        return (
            target,
            receiver,
            new MemoryOperand(addend: 0x1000),
            new MemoryOperand(addend: 0x1000));
    }
}
