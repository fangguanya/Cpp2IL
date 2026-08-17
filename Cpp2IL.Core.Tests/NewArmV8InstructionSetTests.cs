using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Disarm;
using Disarm.InternalDisassembly;

namespace Cpp2IL.Core.Tests;

public class NewArmV8InstructionSetTests
{
    /// <summary>
    /// 将四字节夹具解码成唯一 ARM64 指令；测试输入若不是单指令必须立即失败。
    /// </summary>
    private static Arm64Instruction DecodeSingleInstruction(byte[] machineCode, ulong address)
    {
        var decoded = new List<Arm64Instruction>();
        foreach (var instruction in Disassembler.Disassemble(
                     machineCode,
                     address,
                     new Disassembler.Options(true, true, false)))
            decoded.Add(instruction);

        return decoded.Single();
    }

    [TestCase(Arm64Mnemonic.STRB, Arm64Register.W0, 8, TestName = "基本_字节存储保留8位覆盖宽度")]
    [TestCase(Arm64Mnemonic.STURH, Arm64Register.W0, 16, TestName = "边界_无符号半字节存储保留16位覆盖宽度")]
    [TestCase(Arm64Mnemonic.STUR, Arm64Register.X0, 64, TestName = "边界_64位寄存器存储保留64位覆盖宽度")]
    [Category("基本功能")]
    public void 标量存储宽度由操作码与源寄存器共同确定(
        Arm64Mnemonic mnemonic,
        Arm64Register sourceRegister,
        int expectedWidthBits)
    {
        Assert.That(
            NewArmV8InstructionSet.GetScalarStoreWidthBits(mnemonic, sourceRegister),
            Is.EqualTo(expectedWidthBits));
    }

    [Test]
    [Category("异常输入")]
    public void 非存储操作码不生成伪内存写宽度()
    {
        Assert.That(
            NewArmV8InstructionSet.GetScalarStoreWidthBits(Arm64Mnemonic.LDR, Arm64Register.X0),
            Is.Zero);
    }

    [Test]
    [Category("基本功能")]
    public void 标量加载保留动态寄存器索引与左移步长()
    {
        // LDR X20, [X26, X23, LSL #3]
        var decoded = new List<Arm64Instruction>();
        foreach (var instruction in Disassembler.Disassemble(
                     [0x54, 0x7B, 0x77, 0xF8],
                     0x025D6414,
                     new Disassembler.Options(true, true, false)))
            decoded.Add(instruction);

        Assert.That(decoded, Has.Count.EqualTo(1));

        var memory = NewArmV8InstructionSet.CreateMemoryOperand(decoded[0]);

        Assert.Multiple(() =>
        {
            Assert.That(memory.Base, Is.InstanceOf<Register>());
            Assert.That(((Register)memory.Base!).Name, Is.EqualTo("X26"));
            Assert.That(memory.Index, Is.InstanceOf<Register>());
            Assert.That(((Register)memory.Index!).Name, Is.EqualTo("X23"));
            Assert.That(memory.Scale, Is.EqualTo(8));
            Assert.That(memory.IndexExtension, Is.EqualTo(MemoryIndexExtension.None));
        });
    }

    [Test]
    [Category("边界值")]
    public void 零位移寄存器索引保持单位步长()
    {
        var decoded = NewArmV8InstructionSet.TryDecodeMemoryIndex(
            Arm64ShiftType.LSL,
            Arm64ExtendType.NONE,
            0,
            out var scale,
            out var extension);

        Assert.Multiple(() =>
        {
            Assert.That(decoded, Is.True);
            Assert.That(scale, Is.EqualTo(1));
            Assert.That(extension, Is.EqualTo(MemoryIndexExtension.None));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 非法右移寻址拒绝生成错误内存表达式()
    {
        Assert.That(
            NewArmV8InstructionSet.TryDecodeMemoryIndex(
                Arm64ShiftType.LSR,
                Arm64ExtendType.NONE,
                3,
                out _,
                out _),
            Is.False);
    }

    [Test]
    [Category("基本功能")]
    public void InterfaceLookupSlotRecognizesRecentZeroImmediate()
    {
        Instruction[] instructions =
        [
            new(0, OpCode.Move, new Register(null, "X2"), new Immediate(0)),
            new(1, OpCode.CheckEqual, new Register(null, "Z"), new Register(null, "X2"), new Immediate(0)),
        ];

        Assert.That(
            NewArmV8InstructionSet.HasRecentSmallImmediateArgument(instructions, "X2", ushort.MaxValue),
            Is.True);
    }

    [Test]
    [Category("边界值")]
    public void InterfaceLookupSlotAcceptsUnsignedShortMaximum()
    {
        Instruction[] instructions =
        [
            new(0, OpCode.Move, new Register(null, "X2"), new Immediate(ushort.MaxValue)),
        ];

        Assert.That(
            NewArmV8InstructionSet.HasRecentSmallImmediateArgument(instructions, "X2", ushort.MaxValue),
            Is.True);
    }

    [Test]
    [Category("异常输入")]
    public void InterfaceLookupSlotRejectsMethodInfoPointerAndCallBoundary()
    {
        Instruction[] pointerInstructions =
        [
            new(0, OpCode.Move, new Register(null, "X2"), new Register(null, "X19")),
        ];
        Instruction[] crossedCallInstructions =
        [
            new(0, OpCode.Move, new Register(null, "X2"), new Immediate(1)),
            new(1, OpCode.CallVoid, new Immediate(0x1234)),
        ];

        Assert.Multiple(() =>
        {
            Assert.That(
                NewArmV8InstructionSet.HasRecentSmallImmediateArgument(pointerInstructions, "X2", ushort.MaxValue),
                Is.False);
            Assert.That(
                NewArmV8InstructionSet.HasRecentSmallImmediateArgument(crossedCallInstructions, "X2", ushort.MaxValue),
                Is.False);
        });
    }

    [TestCase(Arm64Mnemonic.SUB, 0x100L, -0x100, TestName = "基本_建立256字节ARM64栈帧")]
    [TestCase(Arm64Mnemonic.ADD, 0x100L, 0x100, TestName = "基本_释放256字节ARM64栈帧")]
    [Category("基本功能")]
    public void StackPointerImmediateArithmeticBecomesStackShift(
        Arm64Mnemonic mnemonic,
        long amount,
        int expectedDelta)
    {
        var decoded = NewArmV8InstructionSet.TryDecodeStackPointerAdjustment(
            mnemonic,
            Arm64OperandKind.Register,
            Arm64Register.X31,
            Arm64OperandKind.Register,
            Arm64Register.X31,
            Arm64OperandKind.Immediate,
            amount,
            out var stackDelta);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded, Is.True);
            Assert.That(stackDelta, Is.EqualTo(expectedDelta));
        }
    }

    [TestCase(0L, TestName = "边界_栈顶零偏移")]
    [TestCase(-0x100L, TestName = "边界_栈顶负256偏移")]
    [TestCase(0x100L, TestName = "边界_栈顶正256偏移")]
    [Category("边界值")]
    public void DirectStackMemoryBecomesStableStackSlot(long byteOffset)
    {
        var decoded = NewArmV8InstructionSet.TryCreateStackOffset(
            Arm64Register.X31,
            Arm64Register.INVALID,
            byteOffset,
            out var stackOffset);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded, Is.True);
            Assert.That(stackOffset.Offset, Is.EqualTo(byteOffset));
        }
    }

    [Test]
    [Category("异常输入")]
    public void IndexedStackMemoryIsNotCollapsedIntoSingleSlot()
    {
        Assert.That(
            NewArmV8InstructionSet.TryCreateStackOffset(
                Arm64Register.X31,
                Arm64Register.X8,
                0x20,
                out _),
            Is.False);
    }

    [Test]
    [Category("异常输入")]
    public void OrdinaryRegisterArithmeticIsNotClassifiedAsStackShift()
    {
        Assert.That(
            NewArmV8InstructionSet.TryDecodeStackPointerAdjustment(
                Arm64Mnemonic.SUB,
                Arm64OperandKind.Register,
                Arm64Register.X8,
                Arm64OperandKind.Register,
                Arm64Register.X8,
                Arm64OperandKind.Immediate,
                0x100,
                out _),
            Is.False);
    }

    [Test]
    [Category("基本功能")]
    public void 后索引Ldp释放栈帧形成正向栈增量()
    {
        var decoded = NewArmV8InstructionSet.TryDecodePostIndexedStackAdjustment(
            Arm64MemoryIndexMode.PostIndex,
            Arm64Register.X31,
            0x20,
            out var stackDelta);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded, Is.True);
            Assert.That(stackDelta, Is.EqualTo(0x20));
        }
    }

    [Test]
    [Category("边界值")]
    public void 后索引栈访问接受零增量()
    {
        Assert.That(
            NewArmV8InstructionSet.TryDecodePostIndexedStackAdjustment(
                Arm64MemoryIndexMode.PostIndex,
                Arm64Register.X31,
                0,
                out var stackDelta),
            Is.True);
        Assert.That(stackDelta, Is.Zero);
    }

    [Test]
    [Category("异常输入")]
    public void 普通基址的后索引不得修改托管栈状态()
    {
        Assert.That(
            NewArmV8InstructionSet.TryDecodePostIndexedStackAdjustment(
                Arm64MemoryIndexMode.PostIndex,
                Arm64Register.X19,
                0x20,
                out _),
            Is.False);
    }

    [Test]
    [Category("基本功能")]
    public void AdrpRelativePageBecomesAbsolutePage()
    {
        Assert.That(
            NewArmV8InstructionSet.ResolveAdrpPageAddress(0x02D379A4, 0x0313D000),
            Is.EqualTo(0x05E74000UL));
    }

    [Test]
    [Category("边界值")]
    public void AdrpStoreAcceptsZeroByteOffset()
    {
        var pages = new Dictionary<Arm64Register, ulong>
        {
            [Arm64Register.X20] = 0x05E74000,
        };

        var resolved = NewArmV8InstructionSet.TryCreateAdrpMemoryOperand(
            pages,
            Arm64Register.X20,
            Arm64Register.INVALID,
            0,
            out var memory);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolved, Is.True);
            Assert.That(memory.IsConstant, Is.True);
            Assert.That(memory.Addend, Is.EqualTo(0x05E74000));
        }
    }

    [Test]
    [Category("基本功能")]
    public void Adrp标量加载接受零页内偏移()
    {
        var pages = new Dictionary<Arm64Register, ulong>
        {
            [Arm64Register.X8] = 0x059F3000,
        };

        var resolved = NewArmV8InstructionSet.TryCreateAdrpMemoryOperand(
            pages,
            Arm64Register.X8,
            Arm64Register.INVALID,
            0,
            out var memory);

        Assert.Multiple(() =>
        {
            Assert.That(resolved, Is.True);
            Assert.That(memory.IsConstant, Is.True);
            Assert.That(memory.Addend, Is.EqualTo(0x059F3000));
        });
    }

    [Test]
    [Category("边界值")]
    public void Adrp标量加载合并最大页内偏移()
    {
        var pages = new Dictionary<Arm64Register, ulong>
        {
            [Arm64Register.X8] = 0x059F3000,
        };

        var resolved = NewArmV8InstructionSet.TryCreateAdrpMemoryOperand(
            pages,
            Arm64Register.X8,
            Arm64Register.INVALID,
            0xFFF,
            out var memory);

        Assert.Multiple(() =>
        {
            Assert.That(resolved, Is.True);
            Assert.That(memory.Addend, Is.EqualTo(0x059F3FFF));
        });
    }

    [Test]
    [Category("异常输入")]
    public void Adrp标量加载带动态索引时保持寄存器寻址()
    {
        var pages = new Dictionary<Arm64Register, ulong>
        {
            [Arm64Register.X8] = 0x059F3000,
        };

        Assert.That(
            NewArmV8InstructionSet.TryCreateAdrpMemoryOperand(
                pages,
                Arm64Register.X8,
                Arm64Register.X9,
                0,
                out _),
            Is.False);
    }

    [Test]
    [Category("异常输入")]
    public void IndexedAdrpStoreKeepsDynamicAddressing()
    {
        var pages = new Dictionary<Arm64Register, ulong>
        {
            [Arm64Register.X20] = 0x05E74000,
        };

        Assert.That(
            NewArmV8InstructionSet.TryCreateAdrpMemoryOperand(
                pages,
                Arm64Register.X20,
                Arm64Register.X8,
                0x11F,
                out _),
            Is.False);
    }

    [TestCase(Arm64Register.X0, 0L, 0, TestName = "基本_ADD恢复栈顶地址")]
    [TestCase(Arm64Register.X8, 0x28L, 0x28, TestName = "基本_ADD恢复结构返回缓冲区地址")]
    [TestCase(Arm64Register.X30, int.MaxValue, int.MaxValue, TestName = "边界_ADD接受最大栈地址偏移")]
    [Category("基本功能")]
    public void AddImmediateFromStackPointerBecomesStackAddress(
        Arm64Register destination,
        long amount,
        int expectedOffset)
    {
        var decoded = NewArmV8InstructionSet.TryCreateStackAddressOffset(
            Arm64Mnemonic.ADD,
            Arm64OperandKind.Register,
            destination,
            Arm64OperandKind.Register,
            Arm64Register.X31,
            Arm64OperandKind.Immediate,
            amount,
            out var stackOffset);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded, Is.True);
            Assert.That(stackOffset.Offset, Is.EqualTo(expectedOffset));
        }
    }

    [TestCase(Arm64Mnemonic.SUB, Arm64Register.X8, Arm64Register.X31, 0x28L, TestName = "异常_SUB不是栈地址形成")]
    [TestCase(Arm64Mnemonic.ADD, Arm64Register.X31, Arm64Register.X31, 0x28L, TestName = "异常_SP目标属于栈调整")]
    [TestCase(Arm64Mnemonic.ADD, Arm64Register.X8, Arm64Register.X7, 0x28L, TestName = "异常_普通源寄存器不是SP")]
    [TestCase(Arm64Mnemonic.ADD, Arm64Register.X8, Arm64Register.X31, -1L, TestName = "异常_负立即数不属于ADD立即数地址")]
    [Category("异常输入")]
    public void NonStackAddressFormsAreRejected(
        Arm64Mnemonic mnemonic,
        Arm64Register destination,
        Arm64Register source,
        long amount)
    {
        Assert.That(
            NewArmV8InstructionSet.TryCreateStackAddressOffset(
                mnemonic,
                Arm64OperandKind.Register,
                destination,
                Arm64OperandKind.Register,
                source,
                Arm64OperandKind.Immediate,
                amount,
                out _),
            Is.False);
    }

    [Test]
    [Category("基本功能")]
    public void LatestX8StackAddressIsObservedAsIndirectReturnBuffer()
    {
        var instructions = new List<Instruction>
        {
            new(0, OpCode.Move, new Register(null, "X8"), new AddressOf(new StackOffset(0x28))),
            new(1, OpCode.Move, new Register(null, "X1"), new Register(null, "X23")),
        };

        var observed = NewArmV8InstructionSet.TryFindObservedIndirectReturnBuffer(
            instructions,
            out var stackOffset);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(observed, Is.True);
            Assert.That(stackOffset.Offset, Is.EqualTo(0x28));
        }
    }

    [Test]
    [Category("边界值")]
    public void PriorCallInvalidatesStaleX8StackAddress()
    {
        var instructions = new List<Instruction>
        {
            new(0, OpCode.Move, new Register(null, "X8"), new AddressOf(new StackOffset(0x28))),
            new(1, OpCode.Call, new Immediate(0x1000), new Register(null, "X0")),
        };

        Assert.That(
            NewArmV8InstructionSet.TryFindObservedIndirectReturnBuffer(instructions, out _),
            Is.False);
    }

    [Test]
    [Category("异常输入")]
    public void NewerX8OverwriteRejectsOldStackAddress()
    {
        var instructions = new List<Instruction>
        {
            new(0, OpCode.Move, new Register(null, "X8"), new AddressOf(new StackOffset(0x28))),
            new(1, OpCode.Move, new Register(null, "X8"), new Immediate(7)),
        };

        Assert.That(
            NewArmV8InstructionSet.TryFindObservedIndirectReturnBuffer(instructions, out _),
            Is.False);
    }

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
    [Category("基本功能")]
    public void 方法体内部跳回自身入口识别为自尾调用()
    {
        Assert.That(
            NewArmV8InstructionSet.IsSelfTailBranch(0x1000, 0x1000, 0x1100),
            Is.True);
    }

    [Test]
    [Category("边界值")]
    public void 方法入口自身分支不构成递归证据()
    {
        Assert.That(
            NewArmV8InstructionSet.IsSelfTailBranch(0x1000, 0x1000, 0x1000),
            Is.False);
    }

    [Test]
    [Category("异常输入")]
    public void 跳到序言后的局部标签保持普通回边()
    {
        Assert.That(
            NewArmV8InstructionSet.IsSelfTailBranch(0x1010, 0x1000, 0x1100),
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
    public void WordCmnImmediateDecodesSignedSwitchThreshold()
    {
        var decoded = NewArmV8InstructionSet.TryDecodeCmnSignedThreshold(
            Arm64Register.W31,
            Arm64OperandKind.Immediate,
            2,
            out var registerWidthBits,
            out var signedThreshold);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded, Is.True);
            Assert.That(registerWidthBits, Is.EqualTo(32));
            Assert.That(signedThreshold, Is.EqualTo(-2));
        }
    }

    [Test]
    [Category("边界值")]
    public void WideCmnZeroImmediateKeepsZeroThreshold()
    {
        var decoded = NewArmV8InstructionSet.TryDecodeCmnSignedThreshold(
            Arm64Register.X31,
            Arm64OperandKind.Immediate,
            0,
            out var registerWidthBits,
            out var signedThreshold);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded, Is.True);
            Assert.That(registerWidthBits, Is.EqualTo(64));
            Assert.That(signedThreshold, Is.Zero);
        }
    }

    [Test]
    [Category("异常输入")]
    public void NonZeroDestinationIsRejectedAsCmnAlias()
    {
        Assert.That(
            NewArmV8InstructionSet.TryDecodeCmnSignedThreshold(
                Arm64Register.W20,
                Arm64OperandKind.Immediate,
                2,
                out _,
                out _),
            Is.False);
    }

    [Test]
    [Category("基本功能")]
    public void SinglePrecisionFloatingNegateIsExactlySupported()
    {
        Assert.That(
            NewArmV8InstructionSet.CanEmitScalarFloatingNegate(
                Arm64OperandKind.Register,
                Arm64Register.S0,
                Arm64OperandKind.Register,
                Arm64Register.S9),
            Is.True);
    }

    [Test]
    [Category("边界值")]
    public void DoublePrecisionFloatingNegateIsExactlySupported()
    {
        Assert.That(
            NewArmV8InstructionSet.CanEmitScalarFloatingNegate(
                Arm64OperandKind.Register,
                Arm64Register.D31,
                Arm64OperandKind.Register,
                Arm64Register.D0),
            Is.True);
    }

    [Test]
    [Category("异常输入")]
    public void MismatchedFloatingNegateWidthsAreRejected()
    {
        Assert.That(
            NewArmV8InstructionSet.CanEmitScalarFloatingNegate(
                Arm64OperandKind.Register,
                Arm64Register.S0,
                Arm64OperandKind.Register,
                Arm64Register.D0),
            Is.False);
    }

    [Test]
    [Category("基本功能")]
    public void 标量浮点指令接受全部单精度寄存器()
    {
        var accepted = NewArmV8InstructionSet.TryGetMatchingScalarFloatingWidth(
            [
                (Arm64OperandKind.Register, Arm64Register.S0),
                (Arm64OperandKind.Register, Arm64Register.S1),
                (Arm64OperandKind.Register, Arm64Register.S8),
            ],
            out var widthBits);

        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.True);
            Assert.That(widthBits, Is.EqualTo(32));
        });
    }

    [Test]
    [Category("边界值")]
    public void 标量浮点指令接受最高编号双精度寄存器()
    {
        var accepted = NewArmV8InstructionSet.TryGetMatchingScalarFloatingWidth(
            [
                (Arm64OperandKind.Register, Arm64Register.D31),
                (Arm64OperandKind.Register, Arm64Register.D0),
            ],
            out var widthBits);

        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.True);
            Assert.That(widthBits, Is.EqualTo(64));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 标量浮点指令拒绝混合精度和非寄存器操作数()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                NewArmV8InstructionSet.TryGetMatchingScalarFloatingWidth(
                    [
                        (Arm64OperandKind.Register, Arm64Register.S0),
                        (Arm64OperandKind.Register, Arm64Register.D0),
                    ],
                    out _),
                Is.False);
            Assert.That(
                NewArmV8InstructionSet.TryGetMatchingScalarFloatingWidth(
                    [
                        (Arm64OperandKind.Register, Arm64Register.S0),
                        (Arm64OperandKind.Immediate, Arm64Register.S1),
                    ],
                    out _),
                Is.False);
        });
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
    [Category("基本功能")]
    public void SmullAliasExpandsToSignedWordMultiplyAndWideAdd()
    {
        // 真实出生地方法指令：SMULL X8, W10, W8，即 SMADDL X8, W10, W8, XZR。
        var native = DecodeSingleInstruction([0x48, 0x7D, 0x28, 0x9B], 0x0271BCA8);

        var recognized = NewArmV8InstructionSet.TryCreateSignedMultiplyAddLongInstructions(
            native,
            native.Address,
            out var recovered);

        Assert.That(
            recognized,
            Is.True,
            $"解码形态：{native.Mnemonic} {native.Op0Kind}/{native.Op0Reg} "
            + $"{native.Op1Kind}/{native.Op1Reg} {native.Op2Kind}/{native.Op2Reg} "
            + $"{native.Op3Kind}/{native.Op3Reg}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recovered.Select(item => item.OpCode), Is.EqualTo(new[]
            {
                OpCode.ConvertSignedIntegerWidth,
                OpCode.ConvertSignedIntegerWidth,
                OpCode.Multiply,
                OpCode.Add,
            }));
            Assert.That(recovered[0].Operands[1], Is.EqualTo(new Register(null, "X10")));
            Assert.That(recovered[0].Operands[2], Is.EqualTo(new Immediate(64)));
            Assert.That(recovered[0].Operands[3], Is.EqualTo(new Immediate(32)));
            Assert.That(recovered[1].Operands[1], Is.EqualTo(new Register(null, "X8")));
            Assert.That(recovered[2].IntegerWidthBits, Is.EqualTo(64));
            Assert.That(recovered[3].Operands[1], Is.EqualTo(new Immediate(0)));
            Assert.That(recovered[3].IntegerWidthBits, Is.EqualTo(64));
        }
    }

    [Test]
    [Category("边界值")]
    public void SmaddlPreservesNonZeroWideAccumulatorAsFirstAddend()
    {
        // SMADDL X8, W10, W8, X9；累加器必须保持为64位 X9，且不能与乘数换槽。
        var native = DecodeSingleInstruction([0x48, 0x25, 0x28, 0x9B], 0x1000);

        var recognized = NewArmV8InstructionSet.TryCreateSignedMultiplyAddLongInstructions(
            native,
            native.Address,
            out var recovered);

        Assert.That(
            recognized,
            Is.True,
            $"解码形态：{native.Mnemonic} {native.Op0Kind}/{native.Op0Reg} "
            + $"{native.Op1Kind}/{native.Op1Reg} {native.Op2Kind}/{native.Op2Reg} "
            + $"{native.Op3Kind}/{native.Op3Reg}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recovered[3].OpCode, Is.EqualTo(OpCode.Add));
            Assert.That(recovered[3].Operands[0], Is.EqualTo(new Register(null, "X8")));
            Assert.That(recovered[3].Operands[1], Is.EqualTo(new Register(null, "X9")));
            Assert.That(recovered[3].Operands[2], Is.EqualTo(recovered[2].Operands[0]));
        }
    }

    [Test]
    [Category("异常输入")]
    public void OrdinaryWideMaddIsNotMisclassifiedAsSignedWordMultiplyAdd()
    {
        // MADD X8, X10, X8, X9 使用64位乘数，不属于 SMADDL 的 W×W 符号拓宽语义。
        var native = DecodeSingleInstruction([0x48, 0x25, 0x08, 0x9B], 0x1000);

        Assert.That(
            NewArmV8InstructionSet.TryCreateSignedMultiplyAddLongInstructions(
                native,
                native.Address,
                out var recovered),
            Is.False);
        Assert.That(recovered, Is.Empty);
    }

    [TestCase(new byte[] { 0x09, 0xFD, 0x7F, 0xD3 }, 63L, "X9", "X8",
        TestName = "基本_出生地余数链逻辑右移63位")]
    [TestCase(new byte[] { 0x08, 0xFD, 0x60, 0xD3 }, 32L, "X8", "X8",
        TestName = "基本_出生地余数链逻辑右移32位")]
    [Category("基本功能")]
    public void LsrAliasBecomesOneUnsignedRightShift(
        byte[] machineCode,
        long expectedShift,
        string expectedDestination,
        string expectedSource)
    {
        var native = DecodeSingleInstruction(machineCode, 0x0271BCAC);

        var recognized = NewArmV8InstructionSet.TryCreateUnsignedBitfieldMoveInstructions(
            native,
            out var recovered);

        Assert.That(
            recognized,
            Is.True,
            $"解码形态：{native.Mnemonic} {native.Op0Kind}/{native.Op0Reg} "
            + $"{native.Op1Kind}/{native.Op1Reg} {native.Op2Kind}/{native.Op2Imm} "
            + $"{native.Op3Kind}/{native.Op3Imm}");
        Assert.Multiple(() =>
        {
            Assert.That(recovered, Has.Length.EqualTo(1));
            Assert.That(recovered[0].OpCode, Is.EqualTo(OpCode.ShiftRightUnsigned));
            Assert.That(recovered[0].Operands[0], Is.EqualTo(new Register(null, expectedDestination)));
            Assert.That(recovered[0].Operands[1], Is.EqualTo(new Register(null, expectedSource)));
            Assert.That(recovered[0].Operands[2], Is.EqualTo(new Immediate(expectedShift)));
            Assert.That(recovered[0].IntegerWidthBits, Is.EqualTo(64));
        });
    }

    [Test]
    [Category("边界值")]
    public void UbfizWrapAroundBitfieldMasksThenMovesIntoHighRange()
    {
        // UBFIZ X9, X8, #1, #1 等价于 UBFM X9, X8, #63, #0。
        var native = DecodeSingleInstruction([0x09, 0x01, 0x7F, 0xD3], 0x1000);

        var recognized = NewArmV8InstructionSet.TryCreateUnsignedBitfieldMoveInstructions(
            native,
            out var recovered);

        Assert.That(recognized, Is.True, $"解码形态：{native.Mnemonic}");
        Assert.Multiple(() =>
        {
            Assert.That(recovered.Select(item => item.OpCode), Is.EqualTo(new[]
            {
                OpCode.And,
                OpCode.ShiftLeft,
            }));
            Assert.That(recovered[0].Operands[2], Is.EqualTo(new Immediate(1)));
            Assert.That(recovered[1].Operands[2], Is.EqualTo(new Immediate(1)));
            Assert.That(recovered.All(item => item.IntegerWidthBits == 64), Is.True);
        });
    }

    [Test]
    [Category("异常输入")]
    public void AsrAliasIsNotMisclassifiedAsUnsignedBitfieldMove()
    {
        // ASR 属于 SBFM，有符号补位语义不得进入 UBFM 的逻辑右移路径。
        var native = DecodeSingleInstruction([0x09, 0xFD, 0x7F, 0x93], 0x1000);

        Assert.That(
            NewArmV8InstructionSet.TryCreateUnsignedBitfieldMoveInstructions(native, out var recovered),
            Is.False);
        Assert.That(recovered, Is.Empty);
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

    [TestCase(
        Arm64Register.S0,
        Arm64Register.W8,
        OpCode.ReinterpretIntegerBitsAsFloat,
        32,
        TestName = "基本_FMOV把W寄存器位模式解释为Single")]
    [TestCase(
        Arm64Register.D31,
        Arm64Register.X30,
        OpCode.ReinterpretIntegerBitsAsFloat,
        64,
        TestName = "边界_FMOV把X寄存器位模式解释为Double")]
    [TestCase(
        Arm64Register.W30,
        Arm64Register.S31,
        OpCode.ReinterpretFloatBitsAsInteger,
        32,
        TestName = "基本_FMOV把Single位模式解释为Int32")]
    [TestCase(
        Arm64Register.X0,
        Arm64Register.D0,
        OpCode.ReinterpretFloatBitsAsInteger,
        64,
        TestName = "边界_FMOV把Double位模式解释为Int64")]
    public void FmovGeneralAndFloatingRegistersPreserveRawBits(
        Arm64Register destination,
        Arm64Register source,
        OpCode expectedOpCode,
        int expectedWidth)
    {
        var recognized = NewArmV8InstructionSet.TryGetFmovBitReinterpretation(
            Arm64OperandKind.Register,
            destination,
            Arm64OperandKind.Register,
            source,
            out var opCode,
            out var widthBits);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recognized, Is.True);
            Assert.That(opCode, Is.EqualTo(expectedOpCode));
            Assert.That(widthBits, Is.EqualTo(expectedWidth));
        }
    }

    [TestCase(Arm64Register.S0, Arm64Register.X0, TestName = "异常_FMOV拒绝32位浮点与64位整数混宽")]
    [TestCase(Arm64Register.V0, Arm64Register.X0, TestName = "异常_FMOV拒绝完整向量寄存器")]
    [TestCase(Arm64Register.W0, Arm64Register.X1, TestName = "异常_FMOV拒绝两个通用寄存器")]
    public void InvalidFmovRegisterPairsAreNotReinterpreted(
        Arm64Register destination,
        Arm64Register source)
    {
        Assert.That(
            NewArmV8InstructionSet.TryGetFmovBitReinterpretation(
                Arm64OperandKind.Register,
                destination,
                Arm64OperandKind.Register,
                source,
                out _,
                out _),
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

    [Test]
    [Category("基本功能")]
    public void ConditionalComparisonMapsSignedGreaterForCsel()
    {
        Assert.That(
            NewArmV8InstructionSet.GetConditionalComparisonOpCode(Arm64ConditionCode.GT),
            Is.EqualTo(OpCode.CheckGreater));
    }

    [Test]
    [Category("边界值")]
    public void ConditionalComparisonFallbackHonorsZeroFlagForGreater()
    {
        var supported = NewArmV8InstructionSet.TryEvaluateConditionFromNzcv(
            Arm64ConditionCode.GT,
            0b0100,
            out var result);

        Assert.Multiple(() =>
        {
            Assert.That(supported, Is.True);
            Assert.That(result, Is.False);
        });
    }

    [Test]
    [Category("异常输入")]
    public void ConditionalComparisonRejectsMissingConditionCode()
    {
        Assert.That(
            NewArmV8InstructionSet.TryEvaluateConditionFromNzcv(
                Arm64ConditionCode.NONE,
                0,
                out _),
            Is.False);
    }

    [Test]
    [Category("基本功能")]
    public void AdrAndByteJumpTableResolveExactTargets()
    {
        var branchBase = NewArmV8InstructionSet.ResolveAdrAddress(0x02D22914, 0x10);
        var resolved = NewArmV8InstructionSet.TryResolveByteJumpTableTargets(
            [0, 2, 5],
            branchBase,
            0x02D228C0,
            0x02D229B8,
            out var targets);

        Assert.Multiple(() =>
        {
            Assert.That(branchBase, Is.EqualTo(0x02D22924));
            Assert.That(resolved, Is.True);
            Assert.That(targets, Is.EqualTo(new ulong[] { 0x02D22924, 0x02D2292C, 0x02D22938 }));
        });
    }

    [Test]
    [Category("边界值")]
    public void ByteJumpTableRejectsTargetAtMethodEnd()
    {
        Assert.That(
            NewArmV8InstructionSet.TryResolveByteJumpTableTargets(
                [2],
                0x1000,
                0x1000,
                0x1008,
                out var targets),
            Is.False);
        Assert.That(targets, Is.Empty);
    }

    [Test]
    [Category("异常输入")]
    public void EmptyByteJumpTableIsRejected()
    {
        Assert.That(
            NewArmV8InstructionSet.TryResolveByteJumpTableTargets(
                [],
                0x1000,
                0x1000,
                0x2000,
                out var targets),
            Is.False);
        Assert.That(targets, Is.Empty);
    }

    [Test]
    [Category("基本功能")]
    public void ConditionalNegateMapsFalseValueToArithmeticNegation()
    {
        Assert.That(
            NewArmV8InstructionSet.GetConditionalFalseTransformOpCode(Arm64Mnemonic.CSNEG),
            Is.EqualTo(OpCode.Negate));
    }

    [Test]
    [Category("边界值")]
    public void ConditionalInvertMapsFalseValueToBitwiseNot()
    {
        Assert.That(
            NewArmV8InstructionSet.GetConditionalFalseTransformOpCode(Arm64Mnemonic.CSINV),
            Is.EqualTo(OpCode.Not));
    }

    [Test]
    [Category("异常输入")]
    public void OrdinaryConditionalSelectHasNoFalseValueTransform()
    {
        Assert.That(
            NewArmV8InstructionSet.GetConditionalFalseTransformOpCode(Arm64Mnemonic.CSEL),
            Is.Null);
    }

    [TestCase(Arm64ConditionCode.HI, TestName = "基本_CCMP无符号大于可由C与Z精确恢复")]
    [TestCase(Arm64ConditionCode.LS, TestName = "边界_CCMP无符号小于等于可由C与Z精确恢复")]
    [TestCase(Arm64ConditionCode.CS, TestName = "边界_CCMP进位条件可直接读取C")]
    [TestCase(Arm64ConditionCode.CC, TestName = "边界_CCMP无进位条件可反转C")]
    [Category("基本功能")]
    public void CarryZeroProducerAcceptsOnlyUnsignedConditions(Arm64ConditionCode conditionCode)
    {
        Assert.That(
            NewArmV8InstructionSet.CanEmitCarryZeroCondition(
                conditionCode,
                Arm64FlagState.CarryAndZero),
            Is.True);
    }

    [Test]
    [Category("异常输入")]
    public void CarryZeroProducerRejectsSignedConditionAndWrongProducer()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                NewArmV8InstructionSet.CanEmitCarryZeroCondition(
                    Arm64ConditionCode.GT,
                    Arm64FlagState.CarryAndZero),
                Is.False);
            Assert.That(
                NewArmV8InstructionSet.CanEmitCarryZeroCondition(
                    Arm64ConditionCode.HI,
                    Arm64FlagState.ZeroOnly),
                Is.False);
        }
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

    [Test]
    [Category("基本功能")]
    public void ReplicatedHalfwordMoveDecodesFourOnes()
    {
        var decoded = NewArmV8InstructionSet.TryDecodeReplicatedVectorMoveImmediate16(
            0x0F008420u,
            out var vectorWidth,
            out var laneCount,
            out var elementBits);

        Assert.Multiple(() =>
        {
            Assert.That(decoded, Is.True);
            Assert.That(vectorWidth, Is.EqualTo(64));
            Assert.That(laneCount, Is.EqualTo(4));
            Assert.That(elementBits, Is.EqualTo(1));
        });
    }

    [Test]
    [Category("边界值")]
    public void ReplicatedHalfwordMoveReportsEightLanesForQRegister()
    {
        var decoded = NewArmV8InstructionSet.TryDecodeReplicatedVectorMoveImmediate16(
            0x4F008420u,
            out var vectorWidth,
            out var laneCount,
            out var elementBits);

        Assert.Multiple(() =>
        {
            Assert.That(decoded, Is.True);
            Assert.That(vectorWidth, Is.EqualTo(128));
            Assert.That(laneCount, Is.EqualTo(8));
            Assert.That(elementBits, Is.EqualTo(1));
        });
    }

    [Test]
    [Category("异常输入")]
    public void ReplicatedHalfwordMoveRejectsSinglePrecisionEncoding()
    {
        Assert.That(
            NewArmV8InstructionSet.TryDecodeReplicatedVectorMoveImmediate16(
                0x0F000420u,
                out _,
                out _,
                out _),
            Is.False);
    }

    [Test]
    [Category("基本功能")]
    public void VectorQMemoryDecoderRestoresScaledByteOffset()
    {
        var decoded = NewArmV8InstructionSet.TryDecodeUnsignedVector128Memory(
            0x3DC00BE0u,
            out var isLoad,
            out var vectorRegister,
            out var baseRegister,
            out var byteOffset);

        Assert.Multiple(() =>
        {
            Assert.That(decoded, Is.True);
            Assert.That(isLoad, Is.True);
            Assert.That(vectorRegister, Is.Zero);
            Assert.That(baseRegister, Is.EqualTo(31));
            Assert.That(byteOffset, Is.EqualTo(0x20));
        });
    }

    [Test]
    [Category("边界值")]
    public void PackedHalfwordPredicateMatchesSchoolCompilerIdiom()
    {
        uint[] machineCodes =
        [
            0x4E040FA0u, 0x3DC12901u, 0x4EA03422u, 0x4EA13400u,
            0x0E612841u, 0x0E612800u, 0x6E061401u, 0x3DC00BE0u,
            0x0EA11C00u, 0x0F1F5400u, 0x0E60A800u, 0x2E71A800u,
            0x1E260008u,
        ];

        var decoded = NewArmV8InstructionSet.TryDecodePackedHalfwordPredicatePattern(
            machineCodes,
            out var pattern);

        Assert.Multiple(() =>
        {
            Assert.That(decoded, Is.True);
            Assert.That(pattern.ScalarSourceRegister, Is.EqualTo(29));
            Assert.That(pattern.ResultRegister, Is.EqualTo(8));
            Assert.That(pattern.ConstantBaseRegister, Is.EqualTo(8));
            Assert.That(pattern.ConstantByteOffset, Is.EqualTo(0x4A0));
            Assert.That(pattern.AccumulatorBaseRegister, Is.EqualTo(31));
            Assert.That(pattern.AccumulatorByteOffset, Is.EqualTo(0x20));
            Assert.That(pattern.ReverseLaneMask, Is.EqualTo(2));
        });
    }

    [Test]
    [Category("异常输入")]
    public void PackedHalfwordPredicateRejectsChangedReductionOpcode()
    {
        uint[] machineCodes =
        [
            0x4E040FA0u, 0x3DC12901u, 0x4EA03422u, 0x4EA13400u,
            0x0E612841u, 0x0E612800u, 0x6E061401u, 0x3DC00BE0u,
            0x0EA11C00u, 0x0F1F5400u, 0x0E60A800u, 0x2E71A801u,
            0x1E260008u,
        ];

        Assert.That(
            NewArmV8InstructionSet.TryDecodePackedHalfwordPredicatePattern(machineCodes, out _),
            Is.False);
    }

    [Test]
    [Category("基本功能")]
    public void UnsignedHalfwordMoveDecodesSecondPackedLane()
    {
        var decoded = NewArmV8InstructionSet.TryDecodeUnsignedHalfwordMoveToGeneral(
            0x0E063C08u,
            out var generalRegister,
            out var vectorRegister,
            out var laneIndex);

        Assert.Multiple(() =>
        {
            Assert.That(decoded, Is.True);
            Assert.That(generalRegister, Is.EqualTo(8));
            Assert.That(vectorRegister, Is.Zero);
            Assert.That(laneIndex, Is.EqualTo(1));
        });
    }

    [Test]
    [Category("边界值")]
    public void UnsignedHalfwordMoveAcceptsHighestPackedLane()
    {
        Assert.That(
            NewArmV8InstructionSet.TryDecodeUnsignedHalfwordMoveToGeneral(
                0x0E0E3C00u,
                out _,
                out _,
                out var laneIndex),
            Is.True);
        Assert.That(laneIndex, Is.EqualTo(3));
    }

    [Test]
    [Category("异常输入")]
    public void UnsignedHalfwordMoveRejectsLaneOutsidePackedLowerHalf()
    {
        Assert.That(
            NewArmV8InstructionSet.TryDecodeUnsignedHalfwordMoveToGeneral(
                0x0E123C00u,
                out _,
                out _,
                out _),
            Is.False);
    }
}
