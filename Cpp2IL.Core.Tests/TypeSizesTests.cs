using System;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Tests;

public class TypeSizesTests
{
    [Test]
    [Category("基本功能")]
    public void BoxedFortyByteValueHasTwentyFourBytePayload()
    {
        Assert.That(TypeSizes.UnboxedSizeFromBoxedSize(40, 8), Is.EqualTo(24));
    }

    [Test]
    [Category("边界值")]
    public void SixteenBytePayloadStaysAtDirectReturnBoundary()
    {
        Assert.That(TypeSizes.UnboxedSizeFromBoxedSize(32, 8), Is.EqualTo(16));
    }

    [TestCase(0L)]
    [TestCase(16L)]
    [Category("异常输入")]
    public void MissingOrHeaderOnlyBoxedSizeReportsUnknown(long boxedSize)
    {
        Assert.That(TypeSizes.UnboxedSizeFromBoxedSize(boxedSize, 8), Is.Zero);
    }

    [Test]
    [Category("异常输入")]
    public void NonPositivePointerSizeIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TypeSizes.UnboxedSizeFromBoxedSize(40, 0));
    }
}
