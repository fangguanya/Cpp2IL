using System;
using Cpp2IL.Core.InstructionSets;

namespace Cpp2IL.Core.Tests;

public class NewArmV8InstructionSetTests
{
    [Test]
    [Category("基本功能")]
    public void BranchInsideMethodRemainsLocalJump()
    {
        Assert.That(
            NewArmV8InstructionSet.IsBranchOutsideMethod(0x1010, 0x1000, 0x20),
            Is.False);
    }

    [TestCase(0x0FFCUL, TestName = "方法首地址之前属于外部跳转")]
    [TestCase(0x1020ul, TestName = "方法尾后地址属于外部跳转")]
    [Category("边界值")]
    public void BranchOutsideOpenIntervalBecomesTailCall(ulong target)
    {
        Assert.That(
            NewArmV8InstructionSet.IsBranchOutsideMethod(target, 0x1000, 0x20),
            Is.True);
    }

    [Test]
    [Category("边界值")]
    public void BranchAtLastInstructionRemainsLocalJump()
    {
        Assert.That(
            NewArmV8InstructionSet.IsBranchOutsideMethod(0x101C, 0x1000, 0x20),
            Is.False);
    }

    [Test]
    [Category("异常输入")]
    public void NegativeMethodLengthIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NewArmV8InstructionSet.IsBranchOutsideMethod(0x1000, 0x1000, -1));
    }

    [Test]
    [Category("异常输入")]
    public void OverflowingMethodEndIsRejected()
    {
        Assert.Throws<OverflowException>(() =>
            NewArmV8InstructionSet.IsBranchOutsideMethod(
                ulong.MaxValue,
                ulong.MaxValue - 3,
                8));
    }
}
