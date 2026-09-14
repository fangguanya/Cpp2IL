using System;
using System.Buffers.Binary;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests;

public class ReadOnlyBitPatternLiteralTests
{
    [TestCase(64, 32)]
    [TestCase(128, 32)]
    [TestCase(128, 64)]
    [Category("基本功能")]
    [Category("边界值")]
    public void 只读宽常量保留不同通道及最高位(int width, int scalarWidth)
    {
        var bytes = new byte[width / 8];
        const ulong low = 0x3F99999A3F800000UL;
        const ulong high = 0xFFF0000000000001UL;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, low);
        if (width == 128)
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), high);
        Assert.That(ReadOnlyScalarLiteralRecovery.TryDecodeMemoryLiteral(bytes, false, width, scalarWidth, out var literal), Is.True);
        Assert.That(literal, Is.EqualTo(new NativeBitPatternLiteral(low, width == 128 ? high : 0, width)));
    }

    [TestCase(0, 32)]
    [TestCase(32, 32)]
    [TestCase(64, 64)]
    [Category("基本功能")]
    public void 同宽标量保留既有浮点合同(int nativeWidth, int scalarWidth)
    {
        Assert.That(ReadOnlyScalarLiteralRecovery.TryDecodeMemoryLiteral(new byte[scalarWidth / 8], false,
            nativeWidth, scalarWidth, out var literal), Is.True);
        Assert.That(literal.GetType(), Is.EqualTo(scalarWidth == 32 ? typeof(FloatLiteral) : typeof(DoubleLiteral)));
    }

    [TestCase(64, 32, 4, false)]
    [TestCase(128, 32, 8, false)]
    [TestCase(64, 32, 16, false)]
    [TestCase(32, 64, 4, false)]
    [TestCase(256, 32, 32, false)]
    [TestCase(64, 16, 8, false)]
    [TestCase(64, 32, 8, true)]
    [Category("异常输入")]
    public void 只读宽常量拒绝截断额外字节及未证明端序(int width, int scalarWidth, int byteCount, bool bigEndian)
    {
        Assert.That(ReadOnlyScalarLiteralRecovery.TryDecodeMemoryLiteral(new byte[byteCount], bigEndian,
            width, scalarWidth, out _), Is.False);
    }
}
