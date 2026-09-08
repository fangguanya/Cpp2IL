using System;
using Cpp2IL.Core.Utils;
using static Cpp2IL.Core.Utils.MetadataOwnershipHelper;

namespace Cpp2IL.Core.Tests;

public class MetadataOwnershipHelperTests
{
    [Test]
    [Category("基本功能")]
    public void BuildsOwnersIndependentlyOfDeclarationOrder()
    {
        Assert.That(BuildOwners(4, [new(8, 2, 2), new(3, 0, 2)]), Is.EqualTo(new[] { 3, 3, 8, 8 }));
    }

    [Test]
    [Category("边界值")]
    public void EmptyDeclarationsAndFinalBoundaryRemainValid()
    {
        Assert.That(BuildOwners(1, [new(1, -1, 0), new(2, 0, 1), new(3, 1, 0)]), Is.EqualTo(new[] { 2 }));
        Assert.That(BuildOwners(0, [new(0, -1, 0)]), Is.Empty);
    }

    [Test]
    [Category("异常输入")]
    public void RejectsGapsOverlapsAndDuplicateOwners()
    {
        Assert.Throws<InvalidOperationException>(() => BuildOwners(2, [new(0, 0, 1)]));
        Assert.Throws<InvalidOperationException>(() => BuildOwners(2, [new(0, 0, 2), new(1, 1, 1)]));
        Assert.Throws<InvalidOperationException>(() => BuildOwners(2, [new(0, 0, 1), new(0, 1, 1)]));
    }

    [TestCase(-1, 0, 1)]
    [TestCase(0, -2, 0)]
    [TestCase(0, -1, 1)]
    [TestCase(0, 0, -1)]
    [TestCase(0, 2, 0)]
    [TestCase(0, int.MaxValue, int.MaxValue)]
    [Category("异常输入")]
    public void RejectsInvalidRangesWithoutArithmeticOverflow(int owner, int start, int count)
    {
        Assert.Throws<InvalidOperationException>(() => BuildOwners(1, [new(owner, start, count)]));
    }

    [Test]
    [Category("异常输入")]
    public void RejectsNegativeTableSize()
    {
        Assert.Throws<InvalidOperationException>(() => BuildOwners(-1, []));
    }
}
