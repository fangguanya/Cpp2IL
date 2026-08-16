using System;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests;

public class ReadOnlyScalarLiteralRecoveryTests
{
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
}
