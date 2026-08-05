using System;
using Cpp2IL.Core.InstructionSets;
using Disarm;

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

    [TestCase(Arm64ConditionCode.EQ, false, TestName = "CSET与CSEL的EQ直接读取零标志")]
    [TestCase(Arm64ConditionCode.NE, true, TestName = "CSET与CSEL的NE反转零标志")]
    [Category("基本功能")]
    public void EqualityConditionsMapToZeroFlagPolarity(
        Arm64ConditionCode conditionCode,
        bool expectedInversion)
    {
        Assert.That(
            NewArmV8InstructionSet.ShouldInvertZeroFlag(conditionCode),
            Is.EqualTo(expectedInversion));
    }

    [Test]
    [Category("边界值")]
    public void AlwaysConditionIsNotMisclassifiedAsEqualityCondition()
    {
        Assert.That(
            NewArmV8InstructionSet.ShouldInvertZeroFlag(Arm64ConditionCode.AL),
            Is.Null);
    }

    [Test]
    [Category("异常输入")]
    public void MissingConditionIsRejectedByEqualityConditionMapping()
    {
        Assert.That(
            NewArmV8InstructionSet.ShouldInvertZeroFlag(Arm64ConditionCode.NONE),
            Is.Null);
    }

    [TestCase(Arm64Mnemonic.LDRH, TestName = "半字无符号加载进入统一标量加载路径")]
    [TestCase(Arm64Mnemonic.LDRSW, TestName = "有符号字加载进入统一标量加载路径")]
    [TestCase(Arm64Mnemonic.LDUR, TestName = "非缩放加载进入统一标量加载路径")]
    [Category("基本功能")]
    public void MissingScalarLoadVariantsUseManagedValueMove(Arm64Mnemonic mnemonic)
    {
        Assert.That(NewArmV8InstructionSet.IsScalarLoadMnemonic(mnemonic), Is.True);
    }

    [Test]
    [Category("边界值")]
    public void ExistingByteLoadRemainsInScalarLoadFamily()
    {
        Assert.That(
            NewArmV8InstructionSet.IsScalarLoadMnemonic(Arm64Mnemonic.LDRB),
            Is.True);
    }

    [Test]
    [Category("异常输入")]
    public void StoreMnemonicIsRejectedByScalarLoadFamily()
    {
        Assert.That(
            NewArmV8InstructionSet.IsScalarLoadMnemonic(Arm64Mnemonic.STR),
            Is.False);
    }

    [Test]
    [Category("基本功能")]
    public void ZeroVectorImmediateIsExactlyRepresentable()
    {
        Assert.That(
            NewArmV8InstructionSet.IsExactlyRepresentableMovi(
                Arm64OperandKind.Immediate,
                0),
            Is.True);
    }

    [Test]
    [Category("边界值")]
    public void NonZeroVectorImmediateRequiresElementReplicationSemantics()
    {
        Assert.That(
            NewArmV8InstructionSet.IsExactlyRepresentableMovi(
                Arm64OperandKind.Immediate,
                1),
            Is.False);
    }

    [Test]
    [Category("异常输入")]
    public void RegisterOperandIsRejectedAsVectorImmediate()
    {
        Assert.That(
            NewArmV8InstructionSet.IsExactlyRepresentableMovi(
                Arm64OperandKind.Register,
                0),
            Is.False);
    }
}
