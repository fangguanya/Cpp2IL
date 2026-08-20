using System;
using Cpp2IL.Core.Utils;
using Disarm.InternalDisassembly;

namespace Cpp2IL.Core.Tests;

public class Arm64RegisterHelperTests
{
    [TestCase(Arm64Register.W2, "X2", TestName = "W2与X2共享整数参数槽")]
    [TestCase(Arm64Register.S7, "V7", TestName = "S7与V7共享浮点参数槽")]
    [TestCase(Arm64Register.D0, "V0", TestName = "D0与V0共享浮点返回槽")]
    [Category("基本功能")]
    public void AliasesUseCanonicalPhysicalRegister(Arm64Register register, string expected)
    {
        Assert.That(Arm64RegisterHelper.CanonicalName(register), Is.EqualTo(expected));
    }

    [Test]
    [Category("边界值")]
    public void RegisterThirtyOneKeepsStackOrZeroRegisterIdentity()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Arm64RegisterHelper.CanonicalName(Arm64Register.W31), Is.EqualTo("W31"));
            Assert.That(Arm64RegisterHelper.CanonicalName(Arm64Register.X31), Is.EqualTo("X31"));
        });
    }

    [TestCase(Arm64Register.W0, 4)]
    [TestCase(Arm64Register.X0, 8)]
    [TestCase(Arm64Register.V0, 16)]
    [Category("边界值")]
    public void PairElementSizeUsesOriginalRegisterWidth(Arm64Register register, int expected)
    {
        Assert.That(Arm64RegisterHelper.SizeBytes(register), Is.EqualTo(expected));
    }

    [Test]
    [Category("异常输入")]
    public void InvalidRegisterWidthIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Arm64RegisterHelper.SizeBytes(Arm64Register.INVALID));
    }

    [Test]
    [Category("基本功能")]
    public void RegisterThirtyOneIsZeroInMoveAndStoreSourcePositions()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Arm64RegisterHelper.IsZeroRegister(Arm64Register.W31), Is.True);
            Assert.That(Arm64RegisterHelper.IsZeroRegister(Arm64Register.X31), Is.True);
            Assert.That(Arm64RegisterHelper.IsZeroRegister(Arm64Register.X30), Is.False);
        });
    }
}
