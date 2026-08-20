using System;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Tests;

public class Arm64MethodBodyReaderTests
{
    [Test]
    [Category("基本功能")]
    public void ResolveRawRangeMapsVirtualRangeToRawSlice()
    {
        var range = Arm64MethodBodyReader.ResolveRawRange(
            0x1000_4000,
            0x1000_4040,
            0x2000,
            0x2040,
            0x8000);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(range.Start, Is.EqualTo(0x2000));
            Assert.That(range.Length, Is.EqualTo(0x40));
        }
    }

    [Test]
    [Category("边界值")]
    public void ResolveRawRangeAcceptsLoadGameVarsBoundary()
    {
        const int expectedLength = 0x4EC8;
        var range = Arm64MethodBodyReader.ResolveRawRange(
            0x305B50C,
            0x305B50C + expectedLength,
            0x305750C,
            0x305750C + expectedLength,
            0x4000000);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(range.Start, Is.EqualTo(0x305750C));
            Assert.That(range.Length, Is.EqualTo(20168));
        }
    }

    [TestCase(0x1004ul, 0x1000ul, 0x2004L, 0x2000L, TestName = "拒绝倒置区间")]
    [TestCase(0x1000ul, 0x1004ul, 0x2000L, 0x2000L, TestName = "拒绝空文件区间")]
    [TestCase(0x1000ul, 0x1004ul, -1L, 0x2004L, TestName = "拒绝负文件偏移")]
    [Category("异常输入")]
    public void ResolveRawRangeRejectsInvalidRanges(
        ulong startVirtualAddress,
        ulong endVirtualAddress,
        long rawStart,
        long rawEnd)
    {
        Assert.Catch<ArgumentException>(() => Arm64MethodBodyReader.ResolveRawRange(
            startVirtualAddress,
            endVirtualAddress,
            rawStart,
            rawEnd,
            0x8000));
    }

    [Test]
    [Category("异常输入")]
    public void ResolveRawRangeRejectsUnalignedArm64Boundary()
    {
        Assert.Throws<ArgumentException>(() => Arm64MethodBodyReader.ResolveRawRange(
            0x1002,
            0x1008,
            0x2000,
            0x2006,
            0x8000));
    }
}

public class MethodAnalysisSizePolicyTests
{
    [Test]
    [Category("基本功能")]
    public void DefaultBudgetAnalyzesLoadGameVars()
    {
        Assert.That(
            MethodAnalysisSizePolicy.ExceedsMaximum(20168, MethodAnalysisSizePolicy.DefaultMaximumBytes),
            Is.False);
    }

    [Test]
    [Category("边界值")]
    public void ExactBudgetBoundaryIsInclusive()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(MethodAnalysisSizePolicy.ExceedsMaximum(20168, 20168), Is.False);
            Assert.That(MethodAnalysisSizePolicy.ExceedsMaximum(20169, 20168), Is.True);
        }
    }

    [Test]
    [Category("边界值")]
    public void UnlimitedBudgetAcceptsLargeMethod()
    {
        Assert.That(MethodAnalysisSizePolicy.ExceedsMaximum(int.MaxValue, MethodAnalysisSizePolicy.Unlimited), Is.False);
    }

    [TestCase(0)]
    [TestCase(-2)]
    [Category("异常输入")]
    public void InvalidBudgetIsRejected(int maximumBytes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MethodAnalysisSizePolicy.ValidateMaximumBytes(maximumBytes));
    }

    [Test]
    [Category("异常输入")]
    public void NegativeMethodSizeIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MethodAnalysisSizePolicy.ExceedsMaximum(-1, 20168));
    }
}
