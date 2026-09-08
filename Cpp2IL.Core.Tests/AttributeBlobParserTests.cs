using System.IO;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Tests;

public class AttributeBlobParserTests
{
    [Test]
    [Category("边界值")]
    public void AcceptsExplicitZeroAttributeCount()
    {
        using var stream = new MemoryStream([0]);
        Assert.That(V29AttributeUtils.ReadAttributeBlob(stream, TestGameLoader.LoadSimple2022Game()), Is.Empty);
        Assert.That(stream.Position, Is.EqualTo(stream.Length));
    }

    [Test]
    [Category("异常输入")]
    public void RejectsTruncatedConstructorTableBeforeResolution()
    {
        using var stream = new MemoryStream([1, 0, 0]);
        Assert.Throws<InvalidDataException>(() => V29AttributeUtils.ReadAttributeBlob(stream, null!));
    }

    [Test]
    [Category("异常输入")]
    public void RejectsUnconsumedBytes()
    {
        using var stream = new MemoryStream([0, 42]);
        Assert.Throws<InvalidDataException>(() => V29AttributeUtils.ReadAttributeBlob(stream, TestGameLoader.LoadSimple2022Game()));
    }
}
