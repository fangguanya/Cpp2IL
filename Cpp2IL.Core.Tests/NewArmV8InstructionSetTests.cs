using System;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
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

    [TestCase(Arm64ConditionCode.GT, OpCode.CheckGreater, TestName = "有符号大于映射到有符号比较")]
    [TestCase(Arm64ConditionCode.LT, OpCode.CheckLess, TestName = "有符号小于映射到有符号比较")]
    [TestCase(Arm64ConditionCode.GE, OpCode.CheckGreaterOrEqual, TestName = "有符号大于等于映射到有符号比较")]
    [TestCase(Arm64ConditionCode.LE, OpCode.CheckLessOrEqual, TestName = "有符号小于等于映射到有符号比较")]
    [TestCase(Arm64ConditionCode.HI, OpCode.CheckGreaterUnsigned, TestName = "无符号大于映射到无符号比较")]
    [TestCase(Arm64ConditionCode.CC, OpCode.CheckLessUnsigned, TestName = "无符号低于映射到无符号比较")]
    [TestCase(Arm64ConditionCode.CS, OpCode.CheckGreaterOrEqualUnsigned, TestName = "无符号大于等于映射到无符号比较")]
    [TestCase(Arm64ConditionCode.LS, OpCode.CheckLessOrEqualUnsigned, TestName = "无符号小于等于映射到无符号比较")]
    [Category("基本功能")]
    public void RelationalConditionsMapToExactComparison(
        Arm64ConditionCode conditionCode,
        OpCode expectedOpCode)
    {
        Assert.That(
            NewArmV8InstructionSet.GetRelationalBranchOpCode(conditionCode),
            Is.EqualTo(expectedOpCode));
    }

    [TestCase(Arm64ConditionCode.NONE)]
    [TestCase(Arm64ConditionCode.AL)]
    [TestCase(Arm64ConditionCode.NV)]
    [Category("边界值")]
    public void AlwaysBranchCodesRemainUnconditional(Arm64ConditionCode conditionCode)
    {
        Assert.That(
            NewArmV8InstructionSet.IsUnconditionalBranchCode(conditionCode),
            Is.True);
    }

    [Test]
    [Category("边界值")]
    public void EqualityBranchAcceptsZeroOnlyFlagProducer()
    {
        Assert.That(
            NewArmV8InstructionSet.CanEmitConditionalBranch(
                Arm64ConditionCode.EQ,
                Arm64FlagState.ZeroOnly),
            Is.True);
    }

    [Test]
    [Category("边界值")]
    public void RelationalBranchRequiresComparisonOperands()
    {
        Assert.That(
            NewArmV8InstructionSet.CanEmitConditionalBranch(
                Arm64ConditionCode.LT,
                Arm64FlagState.ZeroOnly),
            Is.False);
    }

    [Test]
    [Category("异常输入")]
    public void ConditionalBranchWithoutFlagProducerIsRejected()
    {
        Assert.That(
            NewArmV8InstructionSet.CanEmitConditionalBranch(
                Arm64ConditionCode.NE,
                Arm64FlagState.None),
            Is.False);
    }

    [Test]
    [Category("异常输入")]
    public void UnsupportedOverflowConditionIsNotMappedToSignedComparison()
    {
        Assert.That(
            NewArmV8InstructionSet.GetRelationalBranchOpCode(Arm64ConditionCode.VS),
            Is.Null);
    }

    [Test]
    [Category("基本功能")]
    public void BlrMapsToIndirectCall()
    {
        Assert.That(
            NewArmV8InstructionSet.GetIndirectBranchOpCode(Arm64Mnemonic.BLR),
            Is.EqualTo(OpCode.IndirectCall));
    }

    [Test]
    [Category("边界值")]
    public void BrMapsToIndirectTailJump()
    {
        Assert.That(
            NewArmV8InstructionSet.GetIndirectBranchOpCode(Arm64Mnemonic.BR),
            Is.EqualTo(OpCode.IndirectJump));
    }

    [Test]
    [Category("异常输入")]
    public void RetIsRejectedByIndirectBranchMapping()
    {
        Assert.That(
            NewArmV8InstructionSet.GetIndirectBranchOpCode(Arm64Mnemonic.RET),
            Is.Null);
    }
}
