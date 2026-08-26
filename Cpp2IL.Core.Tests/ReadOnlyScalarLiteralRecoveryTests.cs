using System;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class ReadOnlyScalarLiteralRecoveryTests
{
    [Test]
    [Category("基本功能")]
    public void 小端UInt16只读表恢复为字符常量表()
    {
        byte[] raw = [0x61, 0, 0x7A, 0];
        Assert.That(ReadOnlyScalarLiteralRecovery.TryDecodeUInt16Table(raw, false, 2, out var value), Is.True);
        Assert.That(value, Is.EqualTo("az"));
    }

    [Test]
    [Category("边界值")]
    public void 大端UInt16只读表保持字节序与末端元素()
    {
        byte[] raw = [0, 0x61, 0, 0x7A];
        Assert.That(ReadOnlyScalarLiteralRecovery.TryDecodeUInt16Table(raw, true, 2, out var value), Is.True);
        Assert.That(value, Is.EqualTo("az"));
    }

    [TestCase(0, 0)]
    [TestCase(1, 2)]
    [Category("异常输入")]
    public void 空表或越界字节范围失败关闭(int byteLength, int elementCount)
    {
        Assert.That(ReadOnlyScalarLiteralRecovery.TryDecodeUInt16Table(new byte[byteLength], false, elementCount, out _), Is.False);
    }

    [Test]
    [Category("基本功能")]
    public void 非二十六项表按实际上界完整解码()
    {
        Assert.That(ReadOnlyScalarLiteralRecovery.TryDecodeUInt16Table([0x61, 0, 0x62, 0, 0x63, 0], false, 3, out var value), Is.True);
        Assert.That(value, Is.EqualTo("abc"));
    }

    [TestCase(0x1001UL, true, true, 0L, TestName = "奇地址失败关闭")]
    [TestCase(0x1000UL, false, true, 0L, TestName = "可写段失败关闭")]
    [TestCase(0x1000UL, true, false, 0L, TestName = "不可映射地址失败关闭")]
    [TestCase(0x1000UL, true, true, 15L, TestName = "原始范围越界失败关闭")]
    [Category("异常输入")]
    public void 非法只读映射均被拒绝(ulong address, bool readOnly, bool mapped, long offset)
    {
        var accepted = ReadOnlyScalarLiteralRecovery.TryValidateUInt16TableRange(
            address, 4, 16, (_, _) => readOnly,
            (ulong _, out long raw) => { raw = offset; return mapped; }, out _);
        Assert.That(accepted, Is.False);
    }

    [Test]
    [Category("边界值")]
    public void 默认分支重新汇合表块时可达性成立()
    {
        var defaultBlock = new Block();
        var merge = new Block();
        var table = new Block();
        defaultBlock.Successors.Add(merge);
        merge.Successors.Add(table);
        Assert.That(ReadOnlyScalarLiteralRecovery.CanReach(defaultBlock, table), Is.True);
    }

    [Test]
    [Category("基本功能")]
    public void 复合只读表操作数保留索引生命周期()
    {
        var index = new LocalVariable("index", new Register(null, "X8", 1));
        var instruction = new Instruction(0, OpCode.Return, new ReadOnlyUInt16TableLookup("abc", index));
        Assert.That(instruction.Sources, Does.Contain(index));
    }

    [Test]
    [Category("异常输入")]
    public void 溢出元素计数失败关闭而不分配()
    {
        Assert.That(ReadOnlyScalarLiteralRecovery.TryDecodeUInt16Table([], false, int.MaxValue, out _), Is.False);
    }
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
