using System.IO;
using LibCpp2IL.Metadata;

namespace Cpp2IL.Core.Tests;

public class StaticArrayInitializationHelperTests
{
    [Test]
    [Category("基本功能")]
    public void UsesCompilerLengthOrNativeLayoutWithoutBusinessIdentity()
    {
        Assert.That(StaticArrayInitializationHelper.ResolveLength("__StaticArrayInitTypeSize=48", -1), Is.EqualTo(48));
        Assert.That(StaticArrayInitializationHelper.ResolveLength("SomeValueType", 8), Is.EqualTo(8));
        Assert.That(StaticArrayInitializationHelper.ResolvePointer(100, 64, 8, 16, 164), Is.EqualTo(108));
    }

    [Test]
    [Category("边界值")]
    public void IncludesFinalByteAndSupportsRelocation()
    {
        Assert.That(StaticArrayInitializationHelper.ResolvePointer(0, 1, 0, 1, 1), Is.Zero);
        Assert.That(StaticArrayInitializationHelper.ResolvePointer(int.MaxValue, 1, 0, 1, (long)int.MaxValue + 1), Is.EqualTo(int.MaxValue));
    }

    [TestCase(-1, 4, 0, 1, 4)]
    [TestCase(0, -1, 0, 1, 4)]
    [TestCase(0, 4, -1, 1, 4)]
    [TestCase(0, 4, 0, 0, 4)]
    [TestCase(0, 4, 3, 2, 4)]
    [TestCase(0, 4, 0, 1, 3)]
    [TestCase(0, int.MaxValue, int.MaxValue, 1, int.MaxValue)]
    [Category("异常输入")]
    public void RejectsOutOfSectionReads(int offset, int size, int index, int length, long sourceLength)
    {
        Assert.Throws<InvalidDataException>(() => StaticArrayInitializationHelper.ResolvePointer(offset, size, index, length, sourceLength));
    }

    [TestCase("__StaticArrayInitTypeSize=0", 8)]
    [TestCase("__StaticArrayInitTypeSize=-1", 8)]
    [TestCase("__StaticArrayInitTypeSize=2147483648", 8)]
    [TestCase("__StaticArrayInitTypeSize=unknown", 8)]
    [TestCase("SomeValueType", -1)]
    [Category("异常输入")]
    public void RejectsMissingOrMalformedLengthEvidence(string name, int nativeSize)
    {
        Assert.Throws<InvalidDataException>(() => StaticArrayInitializationHelper.ResolveLength(name, nativeSize));
    }
}
