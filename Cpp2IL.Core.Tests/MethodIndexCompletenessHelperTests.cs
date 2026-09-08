using System;
using Cpp2IL.Core.Utils;
using static Cpp2IL.Core.Utils.MethodIndexCompletenessHelper;

namespace Cpp2IL.Core.Tests;

public class MethodIndexCompletenessHelperTests
{
    [Test]
    [Category("基本功能")]
    public void PreservesIdenticalTokensAcrossAssembliesAndSharedAddresses()
    {
        var a = new Identity("A", 0x06000001);
        var b = new Identity("B", 0x06000001);
        var result = Validate([a, b], [new(a, 16), new(b, 16)]);
        Assert.That(result, Is.EqualTo(new Summary(2, 2, 0, 1, 2)));
    }

    [Test]
    [Category("边界值")]
    public void ZeroAddressIsNotAnExclusionOrSharedGroup()
    {
        var a = new Identity("A", 0x06000001);
        var b = new Identity("A", 0x06000002);
        Assert.That(Validate([a, b], [new(a, 0), new(b, 0)]), Is.EqualTo(new Summary(2, 2, 2, 0, 0)));
        Assert.That(Validate([], []), Is.EqualTo(new Summary(0, 0, 0, 0, 0)));
    }

    [Test]
    [Category("边界值")]
    public void ReorderingAndAddressRelocationPreserveIdentityCounts()
    {
        var a = new Identity("A", 0x06000001);
        var b = new Identity("A", 0x06000002);
        Assert.That(Validate([a, b], [new(b, 2048), new(a, 4096)]),
            Is.EqualTo(Validate([b, a], [new(a, 16), new(b, 32)])));
    }

    [Test]
    [Category("异常输入")]
    public void MissingDuplicateAndForeignMethodsAreRejected()
    {
        var a = new Identity("A", 0x06000001);
        var b = new Identity("B", 0x06000001);
        Assert.Throws<InvalidOperationException>(() => Validate([a], []));
        Assert.Throws<InvalidOperationException>(() => Validate([a, a], [new(a, 16)]));
        Assert.Throws<InvalidOperationException>(() => Validate([a], [new(a, 16), new(a, 32)]));
        Assert.Throws<InvalidOperationException>(() => Validate([a], [new(b, 16)]));
    }

    [TestCase("", 0x06000001u)]
    [TestCase("A", 0x06000000u)]
    [TestCase("A", 0x02000001u)]
    [Category("异常输入")]
    public void InvalidIdentityIsRejected(string assembly, uint token)
    {
        var identity = new Identity(assembly, token);
        Assert.Throws<InvalidOperationException>(() => Validate([identity], [new(identity, 0)]));
    }
}
