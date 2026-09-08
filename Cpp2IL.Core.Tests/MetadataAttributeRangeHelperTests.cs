using System.IO;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Tests;

public class MetadataAttributeRangeHelperTests
{
    [Test]
    [Category("基本功能")]
    public void PreservesAllRangesAndSeparatesSentinel()
    {
        Assert.That(MetadataAttributeRangeHelper.BuildLengths([0, 3, 8], 8), Is.EqualTo(new[] { 3, 5 }));
    }

    [Test]
    [Category("边界值")]
    public void PreservesEmptyRangesAndEmptySection()
    {
        Assert.That(MetadataAttributeRangeHelper.BuildLengths([0, 0, 1], 1), Is.EqualTo(new[] { 0, 1 }));
        Assert.That(MetadataAttributeRangeHelper.BuildLengths([0], 0), Is.Empty);
        Assert.That(MetadataAttributeRangeHelper.BuildLengths([0, 3], 4), Is.EqualTo(new[] { 3 }));
        Assert.That(MetadataAttributeRangeHelper.BuildLengths([0, int.MaxValue], int.MaxValue), Is.EqualTo(new[] { int.MaxValue }));
    }

    [Test]
    [Category("异常输入")]
    public void RejectsMissingSentinelGapsReversalAndOverflow()
    {
        Assert.Throws<InvalidDataException>(() => MetadataAttributeRangeHelper.BuildLengths([], 0));
        Assert.Throws<InvalidDataException>(() => MetadataAttributeRangeHelper.BuildLengths([0, 2], 1));
        Assert.Throws<InvalidDataException>(() => MetadataAttributeRangeHelper.BuildLengths([1, 2], 2));
        Assert.Throws<InvalidDataException>(() => MetadataAttributeRangeHelper.BuildLengths([0, 3, 2], 2));
        Assert.Throws<InvalidDataException>(() => MetadataAttributeRangeHelper.BuildLengths([0, uint.MaxValue, 2], 2));
        Assert.Throws<InvalidDataException>(() => MetadataAttributeRangeHelper.BuildLengths([0], -1));
    }
}
