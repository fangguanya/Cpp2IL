using System;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Disarm;
using Disarm.InternalDisassembly;

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

    [TestCase(Arm64ConditionCode.MI, false, TestName = "MI直接读取整数减法的负标志")]
    [TestCase(Arm64ConditionCode.PL, true, TestName = "PL反转整数减法的负标志")]
    [Category("基本功能")]
    public void SignConditionsMapToNegativeFlagPolarity(
        Arm64ConditionCode conditionCode,
        bool expectedInversion)
    {
        Assert.That(
            NewArmV8InstructionSet.ShouldInvertSignFlag(conditionCode),
            Is.EqualTo(expectedInversion));
    }

    [Test]
    [Category("异常输入")]
    public void OverflowConditionIsRejectedBySignFlagMapping()
    {
        Assert.That(
            NewArmV8InstructionSet.ShouldInvertSignFlag(Arm64ConditionCode.VS),
            Is.Null);
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
    [TestCase(Arm64Mnemonic.LDURH, TestName = "非缩放半字加载进入统一标量加载路径")]
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

    [TestCase(Arm64Mnemonic.STRH, TestName = "半字存储进入统一标量存储路径")]
    [TestCase(Arm64Mnemonic.STURH, TestName = "非缩放半字存储进入统一标量存储路径")]
    [Category("基本功能")]
    public void HalfWordStoreVariantsUseManagedValueMove(Arm64Mnemonic mnemonic)
    {
        Assert.That(NewArmV8InstructionSet.IsScalarStoreMnemonic(mnemonic), Is.True);
    }

    [Test]
    [Category("边界值")]
    public void ExistingByteStoreRemainsInScalarStoreFamily()
    {
        Assert.That(
            NewArmV8InstructionSet.IsScalarStoreMnemonic(Arm64Mnemonic.STRB),
            Is.True);
    }

    [Test]
    [Category("异常输入")]
    public void LoadMnemonicIsRejectedByScalarStoreFamily()
    {
        Assert.That(
            NewArmV8InstructionSet.IsScalarStoreMnemonic(Arm64Mnemonic.LDRH),
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

    [Test]
    [Category("基本功能")]
    public void ReplicatedVectorMoveDecodesBleedFollowersNegativeSaturationValue()
    {
        var decoded = NewArmV8InstructionSet.TryDecodeReplicatedVectorMoveImmediate32(
            0x0F0665E3u,
            out var vectorWidthBits,
            out var laneCount,
            out var elementBits);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded, Is.True);
            Assert.That(vectorWidthBits, Is.EqualTo(64));
            Assert.That(laneCount, Is.EqualTo(2));
            Assert.That(elementBits, Is.EqualTo(0xCF000000u));
            Assert.That(BitConverter.Int32BitsToSingle(unchecked((int)elementBits)), Is.EqualTo(-2_147_483_648f));
        }
    }

    [Test]
    [Category("边界值")]
    public void ReplicatedVectorMoveDecodesFourLanesAndLargestShift()
    {
        var decoded = NewArmV8InstructionSet.TryDecodeReplicatedVectorMoveImmediate32(
            0x4F0767E0u,
            out var vectorWidthBits,
            out var laneCount,
            out var elementBits);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded, Is.True);
            Assert.That(vectorWidthBits, Is.EqualTo(128));
            Assert.That(laneCount, Is.EqualTo(4));
            Assert.That(elementBits, Is.EqualTo(0xFF000000u));
        }
    }

    [Test]
    [Category("异常输入")]
    public void ReplicatedVectorMoveRejectsShiftingOnesVariant()
    {
        Assert.That(
            NewArmV8InstructionSet.TryDecodeReplicatedVectorMoveImmediate32(
                0x0F06C5E3u,
                out _,
                out _,
                out _),
            Is.False);
    }

    [Test]
    [Category("基本功能")]
    public void MoveKeepDecodesUiManagerOneDayThresholdHalfword()
    {
        var decoded = NewArmV8InstructionSet.TryDecodeMoveKeepImmediate(
            0x72A00028u,
            out var registerWidth,
            out var halfwordShift,
            out var clearMask,
            out var shiftedImmediate);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded, Is.True);
            Assert.That(registerWidth, Is.EqualTo(32));
            Assert.That(halfwordShift, Is.EqualTo(16));
            Assert.That(clearMask, Is.EqualTo(0x0000FFFFul));
            Assert.That(shiftedImmediate, Is.EqualTo(0x00010000ul));
            Assert.That((0x5180ul & clearMask) | shiftedImmediate, Is.EqualTo(86_400ul));
        }
    }

    [Test]
    [Category("边界值")]
    public void MoveKeepCanClearHighestHalfwordOfSixtyFourBitRegister()
    {
        var decoded = NewArmV8InstructionSet.TryDecodeMoveKeepImmediate(
            0xF2F579A0u,
            out var registerWidth,
            out var halfwordShift,
            out var clearMask,
            out var shiftedImmediate);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded, Is.True);
            Assert.That(registerWidth, Is.EqualTo(64));
            Assert.That(halfwordShift, Is.EqualTo(48));
            Assert.That(clearMask, Is.EqualTo(0x0000FFFFFFFFFFFFul));
            Assert.That(shiftedImmediate, Is.EqualTo(0xABCD000000000000ul));
        }
    }

    [Test]
    [Category("异常输入")]
    public void MoveKeepRejectsMoveZeroEncoding()
    {
        Assert.That(
            NewArmV8InstructionSet.TryDecodeMoveKeepImmediate(
                0x528A3008u,
                out _,
                out _,
                out _,
                out _),
            Is.False);
    }

    [TestCase(0x1E749000u, 64, -10.0d, TestName = "恢复JWT刷新窗口的双精度负十分钟")]
    [TestCase(0x1E349000u, 32, -10.0d, TestName = "单精度编码保持同一精确数值")]
    [Category("基本功能")]
    public void ScalarFloatingImmediateDecodesExactNativeValue(
        uint machineCode,
        int expectedPrecisionBits,
        double expectedImmediate)
    {
        var decoded = NewArmV8InstructionSet.TryDecodeScalarFloatingPointImmediate(
            machineCode,
            out var precisionBits,
            out var immediate);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded, Is.True);
            Assert.That(precisionBits, Is.EqualTo(expectedPrecisionBits));
            Assert.That(immediate, Is.EqualTo(expectedImmediate));
        }
    }

    [Test]
    [Category("边界值")]
    public void ScalarFloatingImmediateDecodesSmallestPositiveEncodedMagnitude()
    {
        var decoded = NewArmV8InstructionSet.TryDecodeScalarFloatingPointImmediate(
            0x1E681000u,
            out var precisionBits,
            out var immediate);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded, Is.True);
            Assert.That(precisionBits, Is.EqualTo(64));
            Assert.That(immediate, Is.EqualTo(0.125d));
        }
    }

    [Test]
    [Category("异常输入")]
    public void ScalarFloatingImmediateRejectsReservedPrecisionEncoding()
    {
        Assert.That(
            NewArmV8InstructionSet.TryDecodeScalarFloatingPointImmediate(
                0x1EA01000u,
                out _,
                out _),
            Is.False);
    }

    [Test]
    [Category("基本功能")]
    public void FloatingRegisterWidthsMatchScalarPrecision()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                NewArmV8InstructionSet.TryGetFloatingPointPrecisionBits(Arm64Register.S8, out var singleBits),
                Is.True);
            Assert.That(singleBits, Is.EqualTo(32));
            Assert.That(
                NewArmV8InstructionSet.TryGetFloatingPointPrecisionBits(Arm64Register.D9, out var doubleBits),
                Is.True);
            Assert.That(doubleBits, Is.EqualTo(64));
        }
    }

    [Test]
    [Category("边界值")]
    public void SignedIntegerRegisterWidthsCoverLargestAllocatableRegisters()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                NewArmV8InstructionSet.TryGetSignedIntegerWidthBits(Arm64Register.W30, out var wordBits),
                Is.True);
            Assert.That(wordBits, Is.EqualTo(32));
            Assert.That(
                NewArmV8InstructionSet.TryGetSignedIntegerWidthBits(Arm64Register.X30, out var wideBits),
                Is.True);
            Assert.That(wideBits, Is.EqualTo(64));
        }
    }

    [Test]
    [Category("异常输入")]
    public void VectorRegisterIsRejectedAsScalarNumericWidth()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                NewArmV8InstructionSet.TryGetFloatingPointPrecisionBits(Arm64Register.V0, out _),
                Is.False);
            Assert.That(
                NewArmV8InstructionSet.TryGetSignedIntegerWidthBits(Arm64Register.S0, out _),
                Is.False);
        }
    }

    [Test]
    [Category("基本功能")]
    public void ScalarSimdRegistersExposeSignedIntegerPayloadWidthsForConversions()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                NewArmV8InstructionSet.TryGetSignedIntegerPayloadWidthBits(Arm64Register.S1, out var singleBits),
                Is.True);
            Assert.That(singleBits, Is.EqualTo(32));
            Assert.That(
                NewArmV8InstructionSet.TryGetSignedIntegerPayloadWidthBits(Arm64Register.D31, out var doubleBits),
                Is.True);
            Assert.That(doubleBits, Is.EqualTo(64));
        }
    }

    [Test]
    [Category("边界值")]
    public void GeneralRegistersRemainValidSignedIntegerPayloads()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                NewArmV8InstructionSet.TryGetSignedIntegerPayloadWidthBits(Arm64Register.W30, out var wordBits),
                Is.True);
            Assert.That(wordBits, Is.EqualTo(32));
            Assert.That(
                NewArmV8InstructionSet.TryGetSignedIntegerPayloadWidthBits(Arm64Register.X30, out var wideBits),
                Is.True);
            Assert.That(wideBits, Is.EqualTo(64));
        }
    }

    [Test]
    [Category("异常输入")]
    public void FullVectorRegisterIsRejectedAsSignedIntegerPayload()
    {
        Assert.That(
            NewArmV8InstructionSet.TryGetSignedIntegerPayloadWidthBits(Arm64Register.V0, out _),
            Is.False);
    }

    [Test]
    [Category("基本功能")]
    public void VectorFloatingMultiplyDecodesUiManagerPixelAverageInstruction()
    {
        var decoded = NewArmV8InstructionSet.TryDecodeVectorFloatingMultiply(
            0x6E22DC00u,
            out var vectorWidthBits,
            out var elementWidthBits,
            out var laneCount,
            out var destinationRegister,
            out var leftRegister,
            out var rightRegister);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded, Is.True);
            Assert.That(vectorWidthBits, Is.EqualTo(128));
            Assert.That(elementWidthBits, Is.EqualTo(32));
            Assert.That(laneCount, Is.EqualTo(4));
            Assert.That(destinationRegister, Is.EqualTo(0));
            Assert.That(leftRegister, Is.EqualTo(0));
            Assert.That(rightRegister, Is.EqualTo(2));
        }
    }

    [Test]
    [Category("边界值")]
    public void VectorFloatingMultiplySupportsTwoDoubleLanes()
    {
        var decoded = NewArmV8InstructionSet.TryDecodeVectorFloatingMultiply(
            0x6E62DC00u,
            out var vectorWidthBits,
            out var elementWidthBits,
            out var laneCount,
            out _,
            out _,
            out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded, Is.True);
            Assert.That(vectorWidthBits, Is.EqualTo(128));
            Assert.That(elementWidthBits, Is.EqualTo(64));
            Assert.That(laneCount, Is.EqualTo(2));
        }
    }

    [TestCase(0x4E22DC00u, TestName = "FMULX编码不会冒充FMUL")]
    [TestCase(0x2E62DC00u, TestName = "非法单通道双精度向量被拒绝")]
    [Category("异常输入")]
    public void VectorFloatingMultiplyRejectsDifferentOrReservedEncodings(uint machineCode)
    {
        Assert.That(
            NewArmV8InstructionSet.TryDecodeVectorFloatingMultiply(
                machineCode,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _),
            Is.False);
    }

    [TestCase(
        0x0E020D00u,
        (int)Arm64RecoveredVectorOperation.DuplicateInt16,
        0,
        8,
        -1,
        TestName = "恢复UIStats四半字复制")]
    [TestCase(
        0x2F10A400u,
        (int)Arm64RecoveredVectorOperation.WidenUnsignedInt16ToInt32,
        0,
        0,
        -1,
        TestName = "恢复UIStats四通道无符号拓宽")]
    [TestCase(
        0x4F3F5400u,
        (int)Arm64RecoveredVectorOperation.ShiftLeftInt32,
        0,
        0,
        -1,
        TestName = "恢复UIStats四通道左移")]
    [TestCase(
        0x4EA0A800u,
        (int)Arm64RecoveredVectorOperation.CompareLessThanZeroInt32,
        0,
        0,
        -1,
        TestName = "恢复UIStats四通道负值比较")]
    [TestCase(
        0x6E631C80u,
        (int)Arm64RecoveredVectorOperation.BitwiseSelect128,
        0,
        4,
        3,
        TestName = "恢复UIStats一百二十八位选择")]
    [TestCase(
        0x4F829000u,
        (int)Arm64RecoveredVectorOperation.MultiplyFloat32ByElement,
        0,
        0,
        2,
        TestName = "恢复UIStats四通道单元素浮点乘法")]
    [Category("基本功能")]
    public void RecoveredUiStatsVectorInstructionsDecodeExactRegisters(
        uint machineCode,
        int expectedOperation,
        int expectedDestination,
        int expectedFirstSource,
        int expectedSecondSource)
    {
        var decoded = NewArmV8InstructionSet.TryDecodeRecoveredVectorInstruction(
            machineCode,
            out var instruction);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded, Is.True);
            Assert.That((int)instruction.Operation, Is.EqualTo(expectedOperation));
            Assert.That(instruction.DestinationRegister, Is.EqualTo(expectedDestination));
            Assert.That(instruction.FirstSourceRegister, Is.EqualTo(expectedFirstSource));
            Assert.That(instruction.SecondSourceRegister, Is.EqualTo(expectedSecondSource));
        }
    }

    [Test]
    [Category("边界值")]
    public void RecoveredBitwiseSelectFormatterKeepsMaskAndBothInputs()
    {
        var formatted = NewArmV8InstructionSet.TryFormatRecoveredVectorInstruction(
            0x6E611C80u,
            0x02E684F4u,
            out var text);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(formatted, Is.True);
            Assert.That(
                text,
                Is.EqualTo("0x02E684F4 BSL V0.16B, V4.16B, V1.16B"));
        }
    }

    [Test]
    [Category("异常输入")]
    public void RecoveredVectorDecoderRejectsUnrelatedMachineCode()
    {
        Assert.That(
            NewArmV8InstructionSet.TryDecodeRecoveredVectorInstruction(
                0xFFFFFFFFu,
                out _),
            Is.False);
    }

    [Test]
    [Category("基本功能")]
    public void InvalidDisassemblerAddressUsesMethodRelativeInstructionAddress()
    {
        Assert.That(
            NewArmV8InstructionSet.ResolveInstructionAddress(
                0x02E7B454,
                345,
                Arm64Mnemonic.INVALID,
                0),
            Is.EqualTo(0x02E7B9B8));
    }

    [Test]
    [Category("边界值")]
    public void ReportedInstructionAddressRemainsAuthoritative()
    {
        Assert.That(
            NewArmV8InstructionSet.ResolveInstructionAddress(
                0x1000,
                int.MaxValue,
                Arm64Mnemonic.FMUL,
                0x4321),
            Is.EqualTo(0x4321));
    }

    [Test]
    [Category("异常输入")]
    public void NegativeInvalidInstructionIndexIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NewArmV8InstructionSet.ResolveInstructionAddress(
                0x1000,
                -1,
                Arm64Mnemonic.INVALID,
                0));
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

    [Test]
    [Category("基本功能")]
    public void ConditionalSetUsesUnsignedGreaterAfterComparison()
    {
        Assert.That(
            NewArmV8InstructionSet.GetConditionalSetRelationalOpCode(
                Arm64ConditionCode.HI,
                Arm64FlagState.Comparison),
            Is.EqualTo(OpCode.CheckGreaterUnsigned));
    }

    [Test]
    [Category("边界值")]
    public void ConditionalSetRejectsRelationalConditionAfterZeroOnlyProducer()
    {
        Assert.That(
            NewArmV8InstructionSet.GetConditionalSetRelationalOpCode(
                Arm64ConditionCode.HI,
                Arm64FlagState.ZeroOnly),
            Is.Null);
    }

    [Test]
    [Category("异常输入")]
    public void ConditionalSetRejectsUnsupportedOverflowCondition()
    {
        Assert.That(
            NewArmV8InstructionSet.GetConditionalSetRelationalOpCode(
                Arm64ConditionCode.VS,
                Arm64FlagState.Comparison),
            Is.Null);
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
    public void FloatingComparisonAllowsExactPositiveOrZeroBranch()
    {
        Assert.That(
            NewArmV8InstructionSet.CanEmitConditionalBranch(
                Arm64ConditionCode.PL,
                Arm64FlagState.FloatingComparison),
            Is.True);
    }

    [Test]
    [Category("基本功能")]
    public void FloatingComparisonAllowsExactNegativeCondition()
    {
        Assert.That(
            NewArmV8InstructionSet.CanEmitConditionalBranch(
                Arm64ConditionCode.MI,
                Arm64FlagState.FloatingComparison),
            Is.True);
    }

    [Test]
    [Category("基本功能")]
    public void IntegerComparisonAllowsExactNegativeBranch()
    {
        Assert.That(
            NewArmV8InstructionSet.CanEmitConditionalBranch(
                Arm64ConditionCode.MI,
                Arm64FlagState.Comparison),
            Is.True);
    }

    [Test]
    [Category("边界值")]
    public void IntegerComparisonAllowsExactPositiveOrZeroBranch()
    {
        Assert.That(
            NewArmV8InstructionSet.CanEmitConditionalBranch(
                Arm64ConditionCode.PL,
                Arm64FlagState.Comparison),
            Is.True);
    }

    [Test]
    [Category("边界值")]
    public void ZeroOnlyProducerDoesNotExposeNegativeFlag()
    {
        Assert.That(
            NewArmV8InstructionSet.CanEmitConditionalBranch(
                Arm64ConditionCode.MI,
                Arm64FlagState.ZeroOnly),
            Is.False);
    }

    [Test]
    [Category("异常输入")]
    public void MissingFlagProducerRejectsPositiveOrZeroBranch()
    {
        Assert.That(
            NewArmV8InstructionSet.CanEmitConditionalBranch(
                Arm64ConditionCode.PL,
                Arm64FlagState.None),
            Is.False);
    }

    [Test]
    [Category("异常输入")]
    public void MissingFlagProducerRejectsNegativeBranch()
    {
        Assert.That(
            NewArmV8InstructionSet.CanEmitConditionalBranch(
                Arm64ConditionCode.MI,
                Arm64FlagState.None),
            Is.False);
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
