using Cpp2IL.Core;

namespace Cpp2IL.Core.Tests;

public class Il2CppDefaultsUsefulOffsetsTests
{
    [Test]
    [Category("基本功能")]
    public void 六十四位默认类型表的四十八偏移恢复为Int32()
    {
        Assert.That(
            Il2CppDefaultsUsefulOffsets.GetBoxedSystemTypeName(0x48, 8),
            Is.EqualTo("System.Int32"));
    }

    [Test]
    [Category("边界值")]
    public void 三十二位默认类型表按指针宽度缩放到二十四偏移()
    {
        Assert.That(
            Il2CppDefaultsUsefulOffsets.GetBoxedSystemTypeName(0x24, 4),
            Is.EqualTo("System.Int32"));
    }

    [Test]
    [Category("异常输入")]
    public void 未登记偏移和非法指针宽度均保持未知()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Il2CppDefaultsUsefulOffsets.GetBoxedSystemTypeName(0x40, 8), Is.Null);
            Assert.That(Il2CppDefaultsUsefulOffsets.GetBoxedSystemTypeName(0x48, 16), Is.Null);
        });
    }
}
