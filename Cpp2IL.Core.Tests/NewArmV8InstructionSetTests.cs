using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Utils;
using Disarm;
using Disarm.InternalDisassembly;

namespace Cpp2IL.Core.Tests;

public class NewArmV8InstructionSetTests
{
    // 合成映像把表页、半字站点、字节站点分开放置，同时让全部默认分支目标落在映像范围内。
    private const ulong SyntheticJumpTableImageStart = 0x1000;
    private const ulong SyntheticHalfwordJumpTableSite = 0x2000;
    private const ulong SyntheticByteJumpTableSite = 0x3000;
    private const ulong SyntheticJumpTableImageEnd = 0x5000;

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

    /// <summary>按给定虚拟地址解码连续 ARM64 机器码，供生产匹配器的编码字段测试使用。</summary>
    private static Arm64Instruction[] DecodeInstructions(byte[] machineCode, ulong address)
        => Disassembler.Disassemble(
                machineCode,
                address,
                new Disassembler.Options(true, true, false))
            .ToArray();

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
    public void 普通寄存器前索引存储识别字段地址写回()
    {
        // STR X0, [X19, #0x80]!；来自 MiniGameBurglary.GetMazeForIndex 的真实指令。
        var native = DecodeSingleInstruction([0x60, 0x0E, 0x08, 0xF8], 0x02BDC778);

        var decoded = NewArmV8InstructionSet.TryDecodePreIndexedRegisterWriteback(
            native.MemIndexMode,
            native.MemBase,
            native.MemAddendReg,
            out var writebackRegister);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(native.Mnemonic, Is.EqualTo(Arm64Mnemonic.STR));
            Assert.That(native.MemOffset, Is.EqualTo(0x80));
            Assert.That(decoded, Is.True);
            Assert.That(writebackRegister.Name, Is.EqualTo("X19"));
        }
    }

    [Test]
    [Category("边界值")]
    public void 普通寄存器前索引接受负立即数写回()
    {
        // STR X0, [X19, #-8]!；验证有符号 imm9 下界附近不会丢失写回身份。
        var native = DecodeSingleInstruction([0x60, 0x8E, 0x1F, 0xF8], 0x1000);

        var decoded = NewArmV8InstructionSet.TryDecodePreIndexedRegisterWriteback(
            native.MemIndexMode,
            native.MemBase,
            native.MemAddendReg,
            out var writebackRegister);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(native.MemOffset, Is.EqualTo(-8));
            Assert.That(decoded, Is.True);
            Assert.That(writebackRegister.Name, Is.EqualTo("X19"));
        }
    }

    [Test]
    [Category("异常输入")]
    public void 普通偏移存储不得伪造基址写回()
    {
        // STR X0, [X19, #0x80]；没有感叹号，基址必须保持不变。
        var native = DecodeSingleInstruction([0x60, 0x42, 0x00, 0xF9], 0x1000);

        Assert.That(
            NewArmV8InstructionSet.TryDecodePreIndexedRegisterWriteback(
                native.MemIndexMode,
                native.MemBase,
                native.MemAddendReg,
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
    [Category("基本功能")]
    public void 条件选择重写目标寄存器后清除陈旧Adrp页地址()
    {
        var pages = new Dictionary<Arm64Register, ulong>
        {
            [Arm64Register.X20] = 0x05E71000,
        };

        var invalidated = NewArmV8InstructionSet.InvalidateAdrpRegisterWrite(
            pages,
            Arm64OperandKind.Register,
            Arm64Register.X20);

        Assert.Multiple(() =>
        {
            Assert.That(invalidated, Is.True);
            Assert.That(pages, Does.Not.ContainKey(Arm64Register.X20));
            Assert.That(
                NewArmV8InstructionSet.TryCreateAdrpMemoryOperand(
                    pages,
                    Arm64Register.X20,
                    Arm64Register.INVALID,
                    0,
                    out _),
                Is.False);
        });
    }

    [Test]
    [Category("边界值")]
    public void 三十二位目标别名同样清除六十四位Adrp页地址()
    {
        var pages = new Dictionary<Arm64Register, ulong>
        {
            [Arm64Register.X20] = 0x05E71000,
            [Arm64Register.X21] = 0x059EF000,
        };

        NewArmV8InstructionSet.InvalidateAdrpRegisterWrite(
            pages,
            Arm64OperandKind.Register,
            Arm64Register.W20);

        Assert.Multiple(() =>
        {
            Assert.That(pages, Does.Not.ContainKey(Arm64Register.X20));
            Assert.That(pages[Arm64Register.X21], Is.EqualTo(0x059EF000));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 非寄存器目标不得污染Adrp页地址事实()
    {
        var pages = new Dictionary<Arm64Register, ulong>
        {
            [Arm64Register.X20] = 0x05E71000,
        };

        var invalidated = NewArmV8InstructionSet.InvalidateAdrpRegisterWrite(
            pages,
            Arm64OperandKind.Immediate,
            Arm64Register.X20);

        Assert.Multiple(() =>
        {
            Assert.That(invalidated, Is.False);
            Assert.That(pages[Arm64Register.X20], Is.EqualTo(0x05E71000));
        });
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

    [TestCase(Arm64Register.W31, TestName = "基本_CSINC的WZR值操作数规范化为零")]
    [TestCase(Arm64Register.X31, TestName = "边界_CSEL的XZR值操作数规范化为零")]
    [Category("基本功能")]
    public void ConditionalZeroRegisterValueBecomesImmediateZero(Arm64Register register)
    {
        var normalized = NewArmV8InstructionSet.NormalizeZeroRegisterValueOperand(
            Arm64OperandKind.Register,
            register,
            new Register(null, Arm64RegisterHelper.CanonicalName(register)));

        Assert.That(normalized, Is.EqualTo(new Immediate(0)));
    }

    [Test]
    [Category("异常输入")]
    public void NonRegisterOperandCannotBeReinterpretedAsZeroRegister()
    {
        var original = new Immediate(31);
        var normalized = NewArmV8InstructionSet.NormalizeZeroRegisterValueOperand(
            Arm64OperandKind.Immediate,
            Arm64Register.W31,
            original);

        Assert.That(normalized, Is.EqualTo(original));
    }

    [Test]
    [Category("异常输入")]
    public void OrdinaryConditionalRegisterKeepsItsIdentity()
    {
        var original = new Register(null, "W8");
        var normalized = NewArmV8InstructionSet.NormalizeZeroRegisterValueOperand(
            Arm64OperandKind.Register,
            Arm64Register.W8,
            original);

        Assert.That(normalized, Is.EqualTo(original));
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

    [Test]
    [Category("基本功能")]
    public void 三十二位MoveKeep展开后两条位操作均保留W寄存器位宽()
    {
        var destination = new Register(null, "W8");

        var recovered = NewArmV8InstructionSet.TryCreateMoveKeepInstructions(
            0x72A00028u,
            destination,
            out var instructions);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recovered, Is.True);
            Assert.That(instructions, Has.Length.EqualTo(2));
            Assert.That(instructions.Select(instruction => instruction.OpCode),
                Is.EqualTo(new[] { OpCode.And, OpCode.Or }));
            Assert.That(instructions.Select(instruction => instruction.IntegerWidthBits),
                Is.All.EqualTo(32));
            Assert.That(((Immediate)instructions[0].Operands[2]).UnsignedValue,
                Is.EqualTo(0x0000FFFFul));
            Assert.That(((Immediate)instructions[1].Operands[2]).UnsignedValue,
                Is.EqualTo(0x00010000ul));
        }
    }

    [Test]
    [Category("边界值")]
    public void 六十四位MoveKeep最高半字展开后保持X寄存器位宽与完整掩码()
    {
        var destination = new Register(null, "X0");

        var recovered = NewArmV8InstructionSet.TryCreateMoveKeepInstructions(
            0xF2F579A0u,
            destination,
            out var instructions);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recovered, Is.True);
            Assert.That(instructions, Has.Length.EqualTo(2));
            Assert.That(instructions.Select(instruction => instruction.IntegerWidthBits),
                Is.All.EqualTo(64));
            Assert.That(((Immediate)instructions[0].Operands[2]).UnsignedValue,
                Is.EqualTo(0x0000FFFFFFFFFFFFul));
            Assert.That(((Immediate)instructions[1].Operands[2]).UnsignedValue,
                Is.EqualTo(0xABCD000000000000ul));
        }
    }

    [Test]
    [Category("异常输入")]
    public void 非MoveKeep编码不生成任何带伪位宽的位操作()
    {
        var recovered = NewArmV8InstructionSet.TryCreateMoveKeepInstructions(
            0x528A3008u,
            new Register(null, "W8"),
            out var instructions);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recovered, Is.False);
            Assert.That(instructions, Is.Empty);
        }
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

        var recognized = NewArmV8InstructionSet.TryCreateMultiplyAddLongInstructions(
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

        var recognized = NewArmV8InstructionSet.TryCreateMultiplyAddLongInstructions(
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
            NewArmV8InstructionSet.TryCreateMultiplyAddLongInstructions(
                native,
                native.Address,
                out var recovered),
            Is.False);
        Assert.That(recovered, Is.Empty);
    }

    [Test]
    [Category("基本功能")]
    public void UmullAliasZeroExtendsBothWordSourcesBeforeWideMultiply()
    {
        // 冻结输入真实编码：UMULL X9, W8, W9；两个 W 源必须按 uint32 零扩展。
        var native = DecodeSingleInstruction([0x09, 0x7D, 0xA9, 0x9B], 0x1000);

        var recognized = NewArmV8InstructionSet.TryCreateMultiplyAddLongInstructions(
            native,
            native.Address,
            out var recovered);

        Assert.Multiple(() =>
        {
            Assert.That(recognized, Is.True);
            Assert.That(recovered.Select(item => item.OpCode), Is.EqualTo(new[]
            {
                OpCode.ConvertSignedIntegerWidth,
                OpCode.ConvertSignedIntegerWidth,
                OpCode.And,
                OpCode.And,
                OpCode.Multiply,
                OpCode.Add,
            }));
            Assert.That(recovered[2].Operands[2], Is.EqualTo(new Immediate(0xFFFFFFFFL)));
            Assert.That(recovered[3].Operands[2], Is.EqualTo(new Immediate(0xFFFFFFFFL)));
            Assert.That(recovered[4].IntegerWidthBits, Is.EqualTo(64));
            Assert.That(recovered[5].Operands[1], Is.EqualTo(new Immediate(0)));
            Assert.That(recovered[5].IntegerWidthBits, Is.EqualTo(64));
        });
    }

    [Test]
    [Category("边界值")]
    public void UmaddlPreservesWideAccumulatorAfterUnsignedWordExpansion()
    {
        // UMADDL X8, W9, W10, X11；累加器保持 X11，不参与 32 位零扩展。
        var native = DecodeSingleInstruction([0x28, 0x2D, 0xAA, 0x9B], 0x1000);

        var recognized = NewArmV8InstructionSet.TryCreateMultiplyAddLongInstructions(
            native,
            native.Address,
            out var recovered);

        Assert.Multiple(() =>
        {
            Assert.That(recognized, Is.True);
            Assert.That(recovered, Has.Length.EqualTo(6));
            Assert.That(recovered[5].Operands[0], Is.EqualTo(new Register(null, "X8")));
            Assert.That(recovered[5].Operands[1], Is.EqualTo(new Register(null, "X11")));
            Assert.That(recovered[5].Operands[2], Is.EqualTo(recovered[4].Operands[0]));
        });
    }

    [TestCase(new byte[] { 0xE9, 0x7F, 0xA9, 0x9B }, 0,
        TestName = "边界_UMULL左侧WZR按无符号零扩展")]
    [TestCase(new byte[] { 0x09, 0x7D, 0xBF, 0x9B }, 1,
        TestName = "边界_UMULL右侧WZR按无符号零扩展")]
    [Category("边界值")]
    public void UmullZeroWordSourceUsesExactZeroOperand(byte[] machineCode, int conversionIndex)
    {
        var native = DecodeSingleInstruction(machineCode, 0x1000);

        var recognized = NewArmV8InstructionSet.TryCreateMultiplyAddLongInstructions(
            native,
            native.Address,
            out var recovered);

        Assert.Multiple(() =>
        {
            Assert.That(recognized, Is.True);
            Assert.That(recovered[conversionIndex].Operands[1], Is.EqualTo(new Immediate(0)));
            Assert.That(recovered.SelectMany(item => item.Operands).OfType<Register>()
                .Any(register => register.Name is "W31" or "X31"), Is.False);
        });
    }

    [Test]
    [Category("边界值")]
    public void UmullZeroDestinationProducesNoDataFlowDefinition()
    {
        var native = DecodeSingleInstruction([0x1F, 0x7D, 0xA9, 0x9B], 0x1000);

        var recognized = NewArmV8InstructionSet.TryCreateMultiplyAddLongInstructions(
            native,
            native.Address,
            out var recovered);

        Assert.Multiple(() =>
        {
            Assert.That(recognized, Is.True);
            Assert.That(recovered, Is.Empty);
        });
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

    [TestCase(new byte[] { 0x25, 0xF1, 0x7D, 0xD3 }, 64, 3L, "X5", "X9",
        TestName = "基本_64位LSL立即数保留左移与宽度")]
    [TestCase(new byte[] { 0x13, 0x71, 0x1D, 0x53 }, 32, 3L, "X19", "X8",
        TestName = "基本_32位LSL立即数保留左移与宽度")]
    [TestCase(new byte[] { 0xD9, 0x7E, 0x60, 0xD3 }, 64, 32L, "X25", "X22",
        TestName = "边界_64位LSL立即数接受32位移距")]
    [Category("基本功能")]
    public void LslImmediateAliasBecomesSizedLeftShift(
        byte[] machineCode,
        int expectedWidth,
        long expectedShift,
        string expectedDestination,
        string expectedSource)
    {
        var native = DecodeSingleInstruction(machineCode, 0x1000);

        var recognized = NewArmV8InstructionSet.TryCreateLogicalShiftLeftInstructions(
            native,
            out var recovered);

        Assert.That(recognized, Is.True, $"解码形态：{native.Mnemonic}");
        Assert.Multiple(() =>
        {
            Assert.That(recovered, Has.Length.EqualTo(1));
            Assert.That(recovered[0].OpCode, Is.EqualTo(OpCode.ShiftLeft));
            Assert.That(recovered[0].Operands[0], Is.EqualTo(new Register(null, expectedDestination)));
            Assert.That(recovered[0].Operands[1], Is.EqualTo(new Register(null, expectedSource)));
            Assert.That(recovered[0].Operands[2], Is.EqualTo(new Immediate(expectedShift)));
            Assert.That(recovered[0].IntegerWidthBits, Is.EqualTo(expectedWidth));
        });
    }

    [TestCase(new byte[] { 0x29, 0x21, 0xC8, 0x1A }, 32, 31L, "X9", "X8",
        TestName = "边界_32位LSL寄存器移距显式掩码低5位")]
    [TestCase(new byte[] { 0x08, 0x23, 0xC8, 0x9A }, 64, 63L, "X8", "X8",
        TestName = "边界_64位LSL寄存器移距显式掩码低6位")]
    [Category("边界值")]
    public void LslRegisterShiftMasksAmountByNativeWidth(
        byte[] machineCode,
        int expectedWidth,
        long expectedMask,
        string expectedDestination,
        string expectedShiftSource)
    {
        var native = DecodeSingleInstruction(machineCode, 0x2000);

        var recognized = NewArmV8InstructionSet.TryCreateLogicalShiftLeftInstructions(
            native,
            out var recovered);

        Assert.That(recognized, Is.True, $"解码形态：{native.Mnemonic}");
        Assert.Multiple(() =>
        {
            Assert.That(recovered.Select(item => item.OpCode), Is.EqualTo(new[] { OpCode.And, OpCode.ShiftLeft }));
            Assert.That(recovered[0].Operands[1], Is.EqualTo(new Register(null, expectedShiftSource)));
            Assert.That(recovered[0].Operands[2], Is.EqualTo(new Immediate(expectedMask)));
            Assert.That(recovered[1].Operands[0], Is.EqualTo(new Register(null, expectedDestination)));
            Assert.That(recovered[1].Operands[2], Is.EqualTo(recovered[0].Operands[0]));
            Assert.That(recovered.All(item => item.IntegerWidthBits == expectedWidth), Is.True);
        });
    }

    [Test]
    [Category("边界值")]
    public void LslWriteToZeroRegisterHasNoDataFlowDefinition()
    {
        var native = DecodeSingleInstruction([0x3F, 0x20, 0xC2, 0x1A], 0x3000);

        var recognized = NewArmV8InstructionSet.TryCreateLogicalShiftLeftInstructions(
            native,
            out var recovered);

        Assert.That(recognized, Is.True, $"解码形态：{native.Mnemonic}");
        Assert.That(recovered.Select(item => item.OpCode), Is.EqualTo(new[] { OpCode.Nop }));
    }

    [Test]
    [Category("异常输入")]
    public void AsrMustNotEnterLogicalShiftLeftRecovery()
    {
        var native = DecodeSingleInstruction([0x09, 0xFD, 0x7F, 0x93], 0x4000);

        Assert.That(
            NewArmV8InstructionSet.TryCreateLogicalShiftLeftInstructions(native, out var recovered),
            Is.False);
        Assert.That(recovered, Is.Empty);
    }

    [TestCase(new byte[] { 0x00, 0x01, 0x20, 0x0A }, 32, OpCode.Xor,
        TestName = "基本_32位BIC无移位恢复取反与位与")]
    [TestCase(new byte[] { 0x49, 0x00, 0x20, 0x8A }, 64, OpCode.Xor,
        TestName = "基本_64位BIC无移位恢复取反与位与")]
    [TestCase(new byte[] { 0xA8, 0x06, 0x20, 0x0A }, 32, OpCode.ShiftLeft,
        TestName = "基本_32位BIC左移源保持移位语义")]
    [TestCase(new byte[] { 0x49, 0x11, 0x69, 0x0A }, 32, OpCode.ShiftRightUnsigned,
        TestName = "边界_32位BIC逻辑右移源保持无符号语义")]
    [TestCase(new byte[] { 0x16, 0x7D, 0xA8, 0x0A }, 32, OpCode.ShiftRight,
        TestName = "边界_32位BIC算术右移31位保持符号语义")]
    [TestCase(new byte[] { 0x08, 0xFD, 0xA8, 0x8A }, 64, OpCode.ShiftRight,
        TestName = "边界_64位BIC算术右移63位保持符号语义")]
    [Category("基本功能")]
    public void BicRestoresWidthAndShiftedBitClear(
        byte[] machineCode,
        int expectedWidth,
        OpCode expectedFirstOperation)
    {
        var native = DecodeSingleInstruction(machineCode, 0x5000);

        var recognized = NewArmV8InstructionSet.TryCreateBitClearInstructions(native, out var recovered);

        Assert.That(recognized, Is.True, $"解码形态：{native.Mnemonic} {native.Op3ShiftType}");
        Assert.Multiple(() =>
        {
            Assert.That(recovered[0].OpCode, Is.EqualTo(expectedFirstOperation));
            Assert.That(recovered[^2].OpCode, Is.EqualTo(OpCode.Xor));
            Assert.That(recovered[^2].Operands[2],
                Is.EqualTo(new Immediate(expectedWidth == 32 ? 0xFFFFFFFFL : -1L)));
            Assert.That(recovered[^1].OpCode, Is.EqualTo(OpCode.And));
            Assert.That(recovered.All(item => item.IntegerWidthBits == expectedWidth), Is.True);
        });
    }

    [Test]
    [Category("边界值")]
    public void BicRotateRightExpandsWithinWordWidth()
    {
        var native = DecodeSingleInstruction([0xF8, 0x54, 0xF5, 0x0A], 0x6000);

        var recognized = NewArmV8InstructionSet.TryCreateBitClearInstructions(native, out var recovered);

        Assert.That(recognized, Is.True, $"解码形态：{native.Mnemonic} {native.Op3ShiftType}");
        Assert.Multiple(() =>
        {
            Assert.That(recovered.Select(item => item.OpCode), Is.EqualTo(new[]
            {
                OpCode.ShiftRightUnsigned,
                OpCode.ShiftLeft,
                OpCode.Or,
                OpCode.Xor,
                OpCode.And,
            }));
            Assert.That(recovered[3].Operands[2], Is.EqualTo(new Immediate(0xFFFFFFFFL)));
            Assert.That(recovered[0].Operands[2], Is.EqualTo(new Immediate(21)));
            Assert.That(recovered[1].Operands[2], Is.EqualTo(new Immediate(11)));
            Assert.That(recovered.All(item => item.IntegerWidthBits == 32), Is.True);
        });
    }

    [TestCase(new byte[] { 0x1F, 0x01, 0x20, 0x0A }, OpCode.Nop,
        TestName = "边界_BIC写零寄存器不产生定义")]
    [TestCase(new byte[] { 0xE0, 0x03, 0x20, 0x0A }, OpCode.Move,
        TestName = "边界_BIC左源为零精确生成零值")]
    [TestCase(new byte[] { 0x00, 0x01, 0x3F, 0x0A }, OpCode.Move,
        TestName = "边界_BIC取反源为零保留左源")]
    [Category("边界值")]
    public void BicZeroRegisterSemanticsAreExplicit(byte[] machineCode, OpCode expectedOperation)
    {
        var native = DecodeSingleInstruction(machineCode, 0x7000);

        var recognized = NewArmV8InstructionSet.TryCreateBitClearInstructions(native, out var recovered);

        Assert.Multiple(() =>
        {
            Assert.That(recognized, Is.True);
            Assert.That(recovered.Select(item => item.OpCode), Is.EqualTo(new[] { expectedOperation }));
        });
    }

    [Test]
    [Category("异常输入")]
    public void BicsMustNotEnterFlaglessBitClearRecovery()
    {
        var native = DecodeSingleInstruction([0x00, 0x01, 0x20, 0x6A], 0x8000);

        Assert.That(NewArmV8InstructionSet.TryCreateBitClearInstructions(native, out var recovered), Is.False);
        Assert.That(recovered, Is.Empty);
    }

    [Test]
    [Category("基本功能")]
    public void MrsTpidrEl0KeepsExplicitSystemRegisterEvidence()
    {
        var native = DecodeSingleInstruction([0x59, 0xD0, 0x3B, 0xD5], 0x9000);

        var recognized = Arm64StackGuardHelper.TryCreateThreadPointerRead(
            native,
            0xD53BD059,
            out var recovered);

        Assert.Multiple(() =>
        {
            Assert.That(recognized, Is.True);
            Assert.That(recovered.OpCode, Is.EqualTo(OpCode.ReadSystemRegister));
            Assert.That(recovered.Operands[0], Is.EqualTo(new Register(null, "X25")));
            Assert.That(recovered.Operands[1], Is.EqualTo(new Immediate(Arm64StackGuardHelper.TpidrEl0Encoding)));
            Assert.That(recovered.IntegerWidthBits, Is.EqualTo(64));
        });
    }

    [TestCase(new byte[] { 0x59, 0xD0, 0x3B, 0xD5 }, 0xD53BD058u,
        TestName = "异常_MRS机器码目的寄存器与解码不一致")]
    [TestCase(new byte[] { 0x59, 0xD0, 0x3B, 0xD5 }, 0xD53BD079u,
        TestName = "异常_MRS系统寄存器编码不属于TPIDR_EL0")]
    [Category("异常输入")]
    public void MrsRequiresMatchingTpidrEl0Encoding(byte[] bytes, uint machineCode)
    {
        var native = DecodeSingleInstruction(bytes, 0xA000);

        Assert.That(
            Arm64StackGuardHelper.TryCreateThreadPointerRead(native, machineCode, out _),
            Is.False);
    }

    [Test]
    [Category("基本功能")]
    public void BirthPlaceWordAddCarriesThirtyTwoBitDestinationEvidence()
    {
        // ADD W8, W8, W9；W 目标要求二元运算后按32位写回，不能把前序 LSR 的 I8 直接存入 Int32。
        var native = DecodeSingleInstruction([0x08, 0x01, 0x09, 0x0B], 0x0271BCB4);

        var recognized = NewArmV8InstructionSet.TryGetSignedIntegerWidthBits(native.Op0Reg, out var widthBits);

        Assert.Multiple(() =>
        {
            Assert.That(native.Mnemonic, Is.EqualTo(Arm64Mnemonic.ADD));
            Assert.That(native.Op0Reg, Is.EqualTo(Arm64Register.W8));
            Assert.That(recognized, Is.True);
            Assert.That(widthBits, Is.EqualTo(32));
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
    public void 半字跳转表按小端表项恢复全部精确目标()
    {
        // 三个半字表项分别表示相对分支基址的 0、2、257 条 ARM64 指令偏移。
        var resolved = NewArmV8InstructionSet.TryResolveJumpTableTargets(
            [0x00, 0x00, 0x02, 0x00, 0x01, 0x01],
            sizeof(ushort),
            3,
            0x1000,
            0x1000,
            0x2000,
            out var targets);

        Assert.Multiple(() =>
        {
            Assert.That(resolved, Is.True);
            Assert.That(targets, Is.EqualTo(new ulong[] { 0x1000, 0x1008, 0x1404 }));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 合成机器码一次扫描恢复字节与半字两个跳转表站点()
    {
        // 两段均包含 SUB/CMP/B.HI、ADRP/ADD、ADR、索引加载、ADD/BR；第二段改用同编码族的 LDRB。
        var halfwordSite = DecodeInstructions(
            [
                0x68, 0xD2, 0x00, 0x51, 0x1F, 0x6D, 0x01, 0x71, 0x08, 0x3D, 0x00, 0x54,
                0xE9, 0xFF, 0xFF, 0xF0, 0x29, 0x01, 0x28, 0x91, 0xE0, 0x03, 0x1F, 0x2A,
                0x8A, 0x00, 0x00, 0x10, 0x2B, 0x79, 0x68, 0x78, 0x4A, 0x09, 0x0B, 0x8B,
                0x40, 0x01, 0x1F, 0xD6,
            ],
            SyntheticHalfwordJumpTableSite);
        var byteSite = DecodeInstructions(
            [
                0x88, 0xD2, 0x00, 0x51, 0x1F, 0x6D, 0x01, 0x71, 0x48, 0x6E, 0x00, 0x54,
                0xE9, 0xFF, 0xFF, 0xD0, 0x29, 0xE1, 0x2A, 0x91, 0xF4, 0x03, 0x1F, 0xAA,
                0x8A, 0x00, 0x00, 0x10, 0x2B, 0x79, 0x68, 0x38, 0x4A, 0x09, 0x0B, 0x8B,
                0x40, 0x01, 0x1F, 0xD6,
            ],
            SyntheticByteJumpTableSite);

        var matches = NewArmV8InstructionSet.FindJumpTableMatches(
            halfwordSite.Concat(byteSite).ToArray(),
            SyntheticJumpTableImageStart,
            SyntheticJumpTableImageEnd);
        var halfword = matches.Single(candidate =>
            candidate.DispatchAddress == SyntheticHalfwordJumpTableSite + 0x24);
        var bytes = matches.Single(candidate =>
            candidate.DispatchAddress == SyntheticByteJumpTableSite + 0x24);

        Assert.Multiple(() =>
        {
            Assert.That(matches, Has.Count.EqualTo(2));
            Assert.That(halfword.EntryWidthBytes, Is.EqualTo(sizeof(ushort)));
            Assert.That(halfword.TableAddress, Is.EqualTo(SyntheticJumpTableImageStart + 0xA00));
            Assert.That(halfword.BranchBaseAddress, Is.EqualTo(SyntheticHalfwordJumpTableSite + 0x28));
            Assert.That(halfword.Bounds.LowerBound, Is.EqualTo(52));
            Assert.That(halfword.Bounds.EntryCount, Is.EqualTo(92));
            Assert.That(halfword.Bounds.DefaultTarget, Is.EqualTo(SyntheticHalfwordJumpTableSite + 0x7A8));
            Assert.That(bytes.EntryWidthBytes, Is.EqualTo(sizeof(byte)));
            Assert.That(bytes.TableAddress, Is.EqualTo(SyntheticJumpTableImageStart + 0xAB8));
            Assert.That(bytes.Bounds.DefaultTarget, Is.EqualTo(SyntheticByteJumpTableSite + 0xDD0));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 跳转表接受边界后的单次索引寄存器复制()
    {
        // CMP/B.HI 验证 W0 后，MOV W8,W0 把同一值交给字节表寻址；这是 Clang 常见发射形状。
        var instructions = DecodeInstructions(
            [
                0x1F, 0x68, 0x00, 0x71, 0x48, 0x6E, 0x00, 0x54, 0xE8, 0x03, 0x00, 0x2A,
                0xE9, 0xFF, 0xFF, 0xD0, 0x29, 0xE1, 0x2A, 0x91, 0x8A, 0x00, 0x00, 0x10,
                0x2B, 0x79, 0x68, 0x38, 0x4A, 0x09, 0x0B, 0x8B, 0x40, 0x01, 0x1F, 0xD6,
            ],
            SyntheticByteJumpTableSite);

        var match = NewArmV8InstructionSet.FindJumpTableMatches(
            instructions,
            SyntheticJumpTableImageStart,
            SyntheticJumpTableImageEnd).Single();

        Assert.Multiple(() =>
        {
            Assert.That(match.AdjustedIndexRegister, Is.EqualTo(Arm64Register.X8));
            Assert.That(match.Bounds.EntryCount, Is.EqualTo(27));
            Assert.That(match.TableAddress, Is.EqualTo(SyntheticJumpTableImageStart + 0xAB8));
        });
    }

    [Test]
    [Category("边界值")]
    public void 跳转表保持比较寄存器直接寻址形状()
    {
        // W8 同时承担比较和表索引时不需要复制，既有严格形状必须继续成立。
        var instructions = DecodeInstructions(
            [
                0x1F, 0x69, 0x00, 0x71, 0x48, 0x6E, 0x00, 0x54,
                0xE9, 0xFF, 0xFF, 0xD0, 0x29, 0xE1, 0x2A, 0x91, 0x8A, 0x00, 0x00, 0x10,
                0x2B, 0x79, 0x68, 0x38, 0x4A, 0x09, 0x0B, 0x8B, 0x40, 0x01, 0x1F, 0xD6,
            ],
            SyntheticByteJumpTableSite);

        var matches = NewArmV8InstructionSet.FindJumpTableMatches(
            instructions,
            SyntheticJumpTableImageStart,
            SyntheticJumpTableImageEnd);

        Assert.That(matches, Has.Count.EqualTo(1));
    }

    [Test]
    [Category("异常输入")]
    public void 跳转表拒绝来自未验证寄存器的索引复制()
    {
        // MOV W8,W1 的来源不是 CMP W0，不能继承 W0 的边界证明。
        var instructions = DecodeInstructions(
            [
                0x1F, 0x68, 0x00, 0x71, 0x48, 0x6E, 0x00, 0x54, 0xE8, 0x03, 0x01, 0x2A,
                0xE9, 0xFF, 0xFF, 0xD0, 0x29, 0xE1, 0x2A, 0x91, 0x8A, 0x00, 0x00, 0x10,
                0x2B, 0x79, 0x68, 0x38, 0x4A, 0x09, 0x0B, 0x8B, 0x40, 0x01, 0x1F, 0xD6,
            ],
            SyntheticByteJumpTableSite);

        var matches = NewArmV8InstructionSet.FindJumpTableMatches(
            instructions,
            SyntheticJumpTableImageStart,
            SyntheticJumpTableImageEnd);

        Assert.That(matches, Is.Empty);
    }

    [Test]
    [Category("基本功能")]
    public void 跳转表允许目标累加与分派之间保存独立寄存器快照()
    {
        // ADD X10 后保存 X24 到 X20，最终 BR 仍只读取未被改写的 X10。
        var instructions = DecodeInstructions(
            [
                0x68, 0xD2, 0x00, 0x51, 0x1F, 0x6D, 0x01, 0x71, 0x08, 0x3D, 0x00, 0x54,
                0xE9, 0xFF, 0xFF, 0xF0, 0x29, 0x01, 0x28, 0x91, 0xF5, 0x03, 0x18, 0xAA,
                0xAA, 0x00, 0x00, 0x10, 0x2B, 0x79, 0x68, 0x78, 0x4A, 0x09, 0x0B, 0x8B,
                0xF4, 0x03, 0x18, 0xAA, 0x40, 0x01, 0x1F, 0xD6,
            ],
            SyntheticHalfwordJumpTableSite);

        var matches = NewArmV8InstructionSet.FindJumpTableMatches(
            instructions,
            SyntheticJumpTableImageStart,
            SyntheticJumpTableImageEnd);

        Assert.That(matches, Has.Count.EqualTo(1));
    }

    [Test]
    [Category("异常输入")]
    public void 跳转表拒绝快照指令改写最终分派寄存器()
    {
        // MOV X10,X24 覆盖了目标 ADD 的结果，BR X10 已不再由表项证明。
        var instructions = DecodeInstructions(
            [
                0x68, 0xD2, 0x00, 0x51, 0x1F, 0x6D, 0x01, 0x71, 0x08, 0x3D, 0x00, 0x54,
                0xE9, 0xFF, 0xFF, 0xF0, 0x29, 0x01, 0x28, 0x91, 0xF5, 0x03, 0x18, 0xAA,
                0xAA, 0x00, 0x00, 0x10, 0x2B, 0x79, 0x68, 0x78, 0x4A, 0x09, 0x0B, 0x8B,
                0xEA, 0x03, 0x18, 0xAA, 0x40, 0x01, 0x1F, 0xD6,
            ],
            SyntheticHalfwordJumpTableSite);

        var matches = NewArmV8InstructionSet.FindJumpTableMatches(
            instructions,
            SyntheticJumpTableImageStart,
            SyntheticJumpTableImageEnd);

        Assert.That(matches, Is.Empty);
    }

    [Test]
    [Category("基本功能")]
    public void 跳转表接受比较与边界之间的独立载荷()
    {
        // CMP W8 的 NZCV 跨过 LDR X20,[X22] 后仍由 B.HI 消费；载荷不触碰索引 W8。
        var instructions = DecodeInstructions(
            [
                0x1F, 0x69, 0x00, 0x71, 0xD4, 0x02, 0x40, 0xF9, 0x48, 0x6E, 0x00, 0x54,
                0xE9, 0xFF, 0xFF, 0xD0, 0x29, 0xE1, 0x2A, 0x91, 0x8A, 0x00, 0x00, 0x10,
                0x2B, 0x79, 0x68, 0x38, 0x4A, 0x09, 0x0B, 0x8B, 0x40, 0x01, 0x1F, 0xD6,
            ],
            SyntheticByteJumpTableSite);

        var matches = NewArmV8InstructionSet.FindJumpTableMatches(
            instructions,
            SyntheticJumpTableImageStart,
            SyntheticJumpTableImageEnd);

        Assert.That(matches, Has.Count.EqualTo(1));
    }

    [Test]
    [Category("边界值")]
    public void 跳转表接受目标累加后的独立立即数准备()
    {
        // ADD X10 后准备返回寄存器 W0；该立即数与跳转目标、表基址、表值和索引均无关。
        var instructions = DecodeInstructions(
            [
                0x1F, 0x69, 0x00, 0x71, 0x48, 0x6E, 0x00, 0x54,
                0xE9, 0xFF, 0xFF, 0xD0, 0x29, 0xE1, 0x2A, 0x91, 0x8A, 0x00, 0x00, 0x10,
                0x2B, 0x79, 0x68, 0x38, 0x4A, 0x09, 0x0B, 0x8B,
                0x00, 0xD0, 0x92, 0x52, 0x40, 0x01, 0x1F, 0xD6,
            ],
            SyntheticByteJumpTableSite);

        var matches = NewArmV8InstructionSet.FindJumpTableMatches(
            instructions,
            SyntheticJumpTableImageStart,
            SyntheticJumpTableImageEnd);

        Assert.That(matches, Has.Count.EqualTo(1));
    }

    [Test]
    [Category("基本功能")]
    public void 跳转表接受直接载荷提供零下界索引()
    {
        // LDR W8 是 CMP W8 的值来源，不是 SUB/ADD 归一化；零下界由无符号上界分支证明。
        var instructions = DecodeInstructions(
            [
                0x68, 0x12, 0x40, 0xB9, 0x1F, 0x69, 0x00, 0x71, 0x48, 0x6E, 0x00, 0x54,
                0xE9, 0xFF, 0xFF, 0xD0, 0x29, 0xE1, 0x2A, 0x91, 0x8A, 0x00, 0x00, 0x10,
                0x2B, 0x79, 0x68, 0x38, 0x4A, 0x09, 0x0B, 0x8B, 0x40, 0x01, 0x1F, 0xD6,
            ],
            SyntheticByteJumpTableSite);

        var matches = NewArmV8InstructionSet.FindJumpTableMatches(
            instructions,
            SyntheticJumpTableImageStart,
            SyntheticJumpTableImageEnd);

        Assert.That(matches, Has.Count.EqualTo(1));
    }

    [Test]
    [Category("基本功能")]
    public void 跳转表恢复双向条件选择夹紧的有符号索引()
    {
        // CMP/CSEL 把输入限制到[-6,6]，CMN/CSEL 完成下界夹紧，ADD #6 再映射到13项字节表。
        var instructions = DecodeInstructions(
            [
                0x7F, 0x1A, 0x00, 0x71, 0xC8, 0x00, 0x80, 0x52,
                0xA9, 0x00, 0x80, 0x12, 0x68, 0xB2, 0x88, 0x1A,
                0x60, 0x0C, 0x80, 0x12, 0x1F, 0x19, 0x00, 0x31,
                0x08, 0xC1, 0x89, 0x1A, 0xE9, 0xFF, 0xFF, 0xD0,
                0x29, 0xE1, 0x2A, 0x91, 0x08, 0x19, 0x00, 0x11,
                0x8A, 0x00, 0x00, 0x10, 0x2B, 0x79, 0x68, 0x38,
                0x4A, 0x09, 0x0B, 0x8B, 0x40, 0x01, 0x1F, 0xD6,
            ],
            SyntheticByteJumpTableSite);

        var matches = NewArmV8InstructionSet.FindJumpTableMatches(
            instructions,
            SyntheticJumpTableImageStart,
            SyntheticJumpTableImageEnd);
        Assert.That(
            matches,
            Has.Count.EqualTo(1),
            string.Join(
                Environment.NewLine,
                instructions.Select((instruction, index) =>
                    $"{index}: {instruction.Mnemonic} "
                    + $"O0={instruction.Op0Kind}/{instruction.Op0Reg}/{instruction.Op0Imm} "
                    + $"O1={instruction.Op1Kind}/{instruction.Op1Reg}/{instruction.Op1Imm} "
                    + $"O2={instruction.Op2Kind}/{instruction.Op2Reg}/{instruction.Op2Imm} "
                    + $"CC={instruction.FinalOpConditionCode}/{instruction.MnemonicConditionCode}")));
        var match = matches.Single();

        Assert.Multiple(() =>
        {
            Assert.That(match.UsesClampedIndex, Is.True);
            Assert.That(match.AdjustedIndexRegister, Is.EqualTo(Arm64Register.X8));
            Assert.That(match.Bounds.LowerBound, Is.EqualTo(-6));
            Assert.That(match.Bounds.EntryCount, Is.EqualTo(13));
            Assert.That(match.DispatchAddress, Is.EqualTo(SyntheticByteJumpTableSite + 0x34));
        });
    }

    [Test]
    [Category("边界值")]
    public void 夹紧跳转表拒绝与下界不一致的零基平移量()
    {
        // 下界仍由 CMN #6 和 MOV #-6 证明，但最终只 ADD #5，索引可能为-1，不能读取表前字节。
        var instructions = DecodeInstructions(
            [
                0x7F, 0x1A, 0x00, 0x71, 0xC8, 0x00, 0x80, 0x52,
                0xA9, 0x00, 0x80, 0x12, 0x68, 0xB2, 0x88, 0x1A,
                0x60, 0x0C, 0x80, 0x12, 0x1F, 0x19, 0x00, 0x31,
                0x08, 0xC1, 0x89, 0x1A, 0xE9, 0xFF, 0xFF, 0xD0,
                0x29, 0xE1, 0x2A, 0x91, 0x08, 0x15, 0x00, 0x11,
                0x8A, 0x00, 0x00, 0x10, 0x2B, 0x79, 0x68, 0x38,
                0x4A, 0x09, 0x0B, 0x8B, 0x40, 0x01, 0x1F, 0xD6,
            ],
            SyntheticByteJumpTableSite);

        var matches = NewArmV8InstructionSet.FindJumpTableMatches(
            instructions,
            SyntheticJumpTableImageStart,
            SyntheticJumpTableImageEnd);

        Assert.That(matches, Is.Empty);
    }

    [Test]
    [Category("异常输入")]
    public void 夹紧跳转表拒绝下界选择后再次覆盖索引()
    {
        // MOV W8,#0 破坏第二次 CSEL 的到达定义，即使后续 ADD 与表尾形状完整也必须关闭候选。
        var instructions = DecodeInstructions(
            [
                0x7F, 0x1A, 0x00, 0x71, 0xC8, 0x00, 0x80, 0x52,
                0xA9, 0x00, 0x80, 0x12, 0x68, 0xB2, 0x88, 0x1A,
                0x60, 0x0C, 0x80, 0x12, 0x1F, 0x19, 0x00, 0x31,
                0x08, 0xC1, 0x89, 0x1A, 0x08, 0x00, 0x80, 0x52,
                0xE9, 0xFF, 0xFF, 0xD0, 0x29, 0xE1, 0x2A, 0x91,
                0x08, 0x19, 0x00, 0x11, 0x8A, 0x00, 0x00, 0x10,
                0x2B, 0x79, 0x68, 0x38, 0x4A, 0x09, 0x0B, 0x8B,
                0x40, 0x01, 0x1F, 0xD6,
            ],
            SyntheticByteJumpTableSite);

        var matches = NewArmV8InstructionSet.FindJumpTableMatches(
            instructions,
            SyntheticJumpTableImageStart,
            SyntheticJumpTableImageEnd);

        Assert.That(matches, Is.Empty);
    }

    [Test]
    [Category("边界值")]
    public void 跳转表接受最终表基址覆盖较早寄存器生命期()
    {
        // 第一组 ADRP/LDR 使用 X9 读取无关常量；第二组 ADRP/ADD 才是表读取的最终到达定义。
        var instructions = DecodeInstructions(
            [
                0x1F, 0x69, 0x00, 0x71, 0x48, 0x6E, 0x00, 0x54,
                0x09, 0x00, 0x00, 0x90, 0x20, 0x0D, 0x4C, 0xBD,
                0xE9, 0xFF, 0xFF, 0xD0, 0x29, 0xE1, 0x2A, 0x91, 0x8A, 0x00, 0x00, 0x10,
                0x2B, 0x79, 0x68, 0x38, 0x4A, 0x09, 0x0B, 0x8B, 0x40, 0x01, 0x1F, 0xD6,
            ],
            SyntheticByteJumpTableSite);

        var matches = NewArmV8InstructionSet.FindJumpTableMatches(
            instructions,
            SyntheticJumpTableImageStart,
            SyntheticJumpTableImageEnd);

        Assert.That(matches, Has.Count.EqualTo(1));
    }

    [Test]
    [Category("边界值")]
    public void 跳转表允许目标形成后复用已消费的索引寄存器()
    {
        // 表读取与目标 ADD 已消费 W8；随后 MOV W8,#-1 不会改变最终 BR X10。
        var instructions = DecodeInstructions(
            [
                0x1F, 0x69, 0x00, 0x71, 0x48, 0x6E, 0x00, 0x54,
                0xE9, 0xFF, 0xFF, 0xD0, 0x29, 0xE1, 0x2A, 0x91, 0x8A, 0x00, 0x00, 0x10,
                0x2B, 0x79, 0x68, 0x38, 0x4A, 0x09, 0x0B, 0x8B,
                0x08, 0x00, 0x80, 0x12, 0x40, 0x01, 0x1F, 0xD6,
            ],
            SyntheticByteJumpTableSite);

        var matches = NewArmV8InstructionSet.FindJumpTableMatches(
            instructions,
            SyntheticJumpTableImageStart,
            SyntheticJumpTableImageEnd);

        Assert.That(matches, Has.Count.EqualTo(1));
    }

    [Test]
    [Category("异常输入")]
    public void 跳转表拒绝边界前载荷覆盖已比较索引()
    {
        // LDR X8,[X22] 会覆盖 CMP W8 已验证的值，B.HI 的标志不能证明后续表索引安全。
        var instructions = DecodeInstructions(
            [
                0x1F, 0x69, 0x00, 0x71, 0xC8, 0x02, 0x40, 0xF9, 0x48, 0x6E, 0x00, 0x54,
                0xE9, 0xFF, 0xFF, 0xD0, 0x29, 0xE1, 0x2A, 0x91, 0x8A, 0x00, 0x00, 0x10,
                0x2B, 0x79, 0x68, 0x38, 0x4A, 0x09, 0x0B, 0x8B, 0x40, 0x01, 0x1F, 0xD6,
            ],
            SyntheticByteJumpTableSite);

        var matches = NewArmV8InstructionSet.FindJumpTableMatches(
            instructions,
            SyntheticJumpTableImageStart,
            SyntheticJumpTableImageEnd);

        Assert.That(matches, Is.Empty);
    }

    [Test]
    [Category("基本功能")]
    public void 跳转表接受比较与边界之间的独立存储()
    {
        // STR WZR,[X20] 不改 NZCV，也不改索引 W8；B.HI 仍精确消费前一条 CMP 的标志。
        var instructions = DecodeInstructions(
            [
                0x1F, 0x69, 0x00, 0x71, 0x9F, 0x12, 0x00, 0xB9, 0x48, 0x6E, 0x00, 0x54,
                0xE9, 0xFF, 0xFF, 0xD0, 0x29, 0xE1, 0x2A, 0x91, 0x8A, 0x00, 0x00, 0x10,
                0x2B, 0x79, 0x68, 0x38, 0x4A, 0x09, 0x0B, 0x8B, 0x40, 0x01, 0x1F, 0xD6,
            ],
            SyntheticByteJumpTableSite);

        var matches = NewArmV8InstructionSet.FindJumpTableMatches(
            instructions,
            SyntheticJumpTableImageStart,
            SyntheticJumpTableImageEnd);

        Assert.That(matches, Has.Count.EqualTo(1));
    }

    [Test]
    [Category("边界值")]
    public void 跳转表接受边界前预计算的被调用方保存表基址()
    {
        // X19 的 ADRP/ADD 在边界检查前完成，之后没有重写；循环体可安全复用该持久表基址。
        var instructions = DecodeInstructions(
            [
                0xF3, 0xFF, 0xFF, 0xD0, 0x73, 0xE2, 0x2A, 0x91,
                0x1F, 0x69, 0x00, 0x71, 0x48, 0x6E, 0x00, 0x54,
                0x8A, 0x00, 0x00, 0x10, 0x6B, 0x7A, 0x68, 0x38,
                0x4A, 0x09, 0x0B, 0x8B, 0x40, 0x01, 0x1F, 0xD6,
            ],
            SyntheticByteJumpTableSite);

        var matches = NewArmV8InstructionSet.FindJumpTableMatches(
            instructions,
            SyntheticJumpTableImageStart,
            SyntheticJumpTableImageEnd);

        Assert.That(matches, Has.Count.EqualTo(1));
    }

    [Test]
    [Category("边界值")]
    public void 跳转表边界保留非零下界计数与独立默认目标()
    {
        var recovered = NewArmV8InstructionSet.TryCreateJumpTableBounds(
            52,
            91,
            0x1800,
            0x1000,
            0x2000,
            out var bounds);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.True);
            Assert.That(bounds.LowerBound, Is.EqualTo(52));
            Assert.That(bounds.EntryCount, Is.EqualTo(92));
            Assert.That(bounds.DefaultTarget, Is.EqualTo(0x1800));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 半字跳转表证据不足或目标未对齐时整体关闭()
    {
        var truncated = NewArmV8InstructionSet.TryResolveJumpTableTargets(
            [0x00, 0x00, 0x01],
            sizeof(ushort),
            2,
            0x1000,
            0x1000,
            0x2000,
            out var truncatedTargets);
        var unaligned = NewArmV8InstructionSet.TryResolveJumpTableTargets(
            [0x00, 0x00],
            sizeof(ushort),
            1,
            0x1002,
            0x1000,
            0x2000,
            out var unalignedTargets);
        // 把同一合成形状的 B.HI 改为 B.LS；默认边证据方向不闭合时匹配器必须拒绝。
        var invertedBoundary = DecodeInstructions(
            [
                0x68, 0xD2, 0x00, 0x51, 0x1F, 0x6D, 0x01, 0x71, 0x09, 0x3D, 0x00, 0x54,
                0xE9, 0xFF, 0xFF, 0xF0, 0x29, 0x01, 0x28, 0x91, 0xE0, 0x03, 0x1F, 0x2A,
                0x8A, 0x00, 0x00, 0x10, 0x2B, 0x79, 0x68, 0x78, 0x4A, 0x09, 0x0B, 0x8B,
                0x40, 0x01, 0x1F, 0xD6,
            ],
            SyntheticHalfwordJumpTableSite);
        var invalidMatches = NewArmV8InstructionSet.FindJumpTableMatches(
            invertedBoundary,
            SyntheticJumpTableImageStart,
            SyntheticJumpTableImageEnd);

        Assert.Multiple(() =>
        {
            Assert.That(truncated, Is.False);
            Assert.That(truncatedTargets, Is.Empty);
            Assert.That(unaligned, Is.False);
            Assert.That(unalignedTargets, Is.Empty);
            Assert.That(invalidMatches, Is.Empty);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 跳转表拒绝第二次累加基址并成组关闭单向跨站点入口()
    {
        // 在合法 ADRP+ADD 后再次写 X9；旧状态机会错误地仍以 pageAddress 为基准接受第二次 ADD。
        var doubleAdd = DecodeInstructions(
            [
                0x68, 0xD2, 0x00, 0x51, 0x1F, 0x6D, 0x01, 0x71, 0x08, 0x3D, 0x00, 0x54,
                0xE9, 0xFF, 0xFF, 0xF0, 0x29, 0x01, 0x28, 0x91, 0x29, 0x11, 0x00, 0x91,
                0xE0, 0x03, 0x1F, 0x2A, 0x8A, 0x00, 0x00, 0x10, 0x2B, 0x79, 0x68, 0x78,
                0x4A, 0x09, 0x0B, 0x8B, 0x40, 0x01, 0x1F, 0xD6,
            ],
            SyntheticHalfwordJumpTableSite);
        var doubleAddMatches = NewArmV8InstructionSet.FindJumpTableMatches(
            doubleAdd,
            SyntheticJumpTableImageStart,
            SyntheticJumpTableImageEnd);

        var dispatches = new Dictionary<int, Arm64JumpTableDispatch>
        {
            [10] = new(
                Arm64Register.W8,
                0x1100,
                new Arm64JumpTableBounds(0, 1, 0x1300),
                [0x1200]),
            [20] = new(
                Arm64Register.W9,
                0x1200,
                new Arm64JumpTableBounds(0, 1, 0x1500),
                [0x1400]),
        };
        var unsafeDispatches = NewArmV8InstructionSet.FindUnsafeJumpTableDispatches(
            dispatches,
            []);

        Assert.Multiple(() =>
        {
            Assert.That(doubleAddMatches, Is.Empty);
            Assert.That(unsafeDispatches, Is.EquivalentTo(new[] { 10, 20 }));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 普通直接分支按各自立即数字段关闭跳转表候选入口()
    {
        var directBranches = DecodeInstructions(
            [
                0x02, 0x00, 0x00, 0x14, // B +8
                0x40, 0x00, 0x00, 0xB4, // CBZ X0, +8
                0x40, 0x00, 0x00, 0xB5, // CBNZ X0, +8
                0x40, 0x00, 0x00, 0x36, // TBZ X0, #0, +8
                0x40, 0x00, 0x00, 0x37, // TBNZ X0, #0, +8
            ],
            0x1100);
        var dispatches = directBranches
            .Select((instruction, index) => new KeyValuePair<int, Arm64JumpTableDispatch>(
                index,
                new Arm64JumpTableDispatch(
                    Arm64Register.W8,
                    instruction.Address + 8,
                    new Arm64JumpTableBounds(0, 1, 0x2000),
                    [0x2100])))
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        var unsafeDispatches = NewArmV8InstructionSet.FindUnsafeJumpTableDispatches(
            dispatches,
            directBranches);

        Assert.That(unsafeDispatches, Is.EquivalentTo(dispatches.Keys));
    }

    [Test]
    [Category("边界值")]
    public void 普通直接分支保留后向位移并拒绝地址下溢()
    {
        var validBackward = DecodeSingleInstruction(
            [0xE0, 0xFF, 0xFF, 0xB4],
            0x1200); // CBZ X0, -4
        var underflow = DecodeSingleInstruction(
            [0xE0, 0xFF, 0xFF, 0xB4],
            0); // 同一位移在零地址会下溢

        var valid = NewArmV8InstructionSet.TryGetNonCallDirectBranchTarget(
            validBackward,
            out var validTarget);
        var invalid = NewArmV8InstructionSet.TryGetNonCallDirectBranchTarget(
            underflow,
            out var invalidTarget);

        Assert.Multiple(() =>
        {
            Assert.That(valid, Is.True);
            Assert.That(validTarget, Is.EqualTo(0x11FC));
            Assert.That(invalid, Is.False);
            Assert.That(invalidTarget, Is.Zero);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 非分支指令不产生普通直接分支目标()
    {
        var add = DecodeSingleInstruction([0x00, 0x04, 0x00, 0x91], 0x1300);

        var resolved = NewArmV8InstructionSet.TryGetNonCallDirectBranchTarget(
            add,
            out var target);

        Assert.Multiple(() =>
        {
            Assert.That(resolved, Is.False);
            Assert.That(target, Is.Zero);
        });
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
    [Category("基本功能")]
    public void DisassembledUmovUsesUnsignedHalfwordRecoveryRoute()
    {
        const uint machineCode = 0x0E063C08u;
        var instruction = DecodeSingleInstruction(BitConverter.GetBytes(machineCode), 0x024B4C7C);

        var decoded = NewArmV8InstructionSet.TryDecodeDisassembledUnsignedHalfwordMoveToGeneral(
            instruction.Mnemonic,
            machineCode,
            out var generalRegister,
            out var vectorRegister,
            out var laneIndex);

        Assert.Multiple(() =>
        {
            Assert.That(instruction.Mnemonic, Is.EqualTo(Arm64Mnemonic.UMOV));
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

    [Test]
    [Category("异常输入")]
    public void UnsignedHalfwordMoveRejectsUnrelatedRecognizedMnemonic()
    {
        Assert.That(
            NewArmV8InstructionSet.TryDecodeDisassembledUnsignedHalfwordMoveToGeneral(
                Arm64Mnemonic.MOV,
                0x0E063C08u,
                out _,
                out _,
                out _),
            Is.False);
    }
    [TestCase(0xF84107FEu, 0x1000UL, true, 16, 64)]
    [TestCase(0xF84107FEu, 0x8000UL, true, 16, 64)]
    [TestCase(0xF80107E0u, 0x2000UL, false, 16, 64)]
    [TestCase(0xF85F07FEu, 0x3000UL, true, -16, 64)]
    [TestCase(0xF84007FEu, 0x4000UL, true, 0, 64)]
    [TestCase(0x384017E0u, 0x5000UL, true, 1, 8)]
    [TestCase(0x380017E0u, 0x6000UL, false, 1, 8)]
    [Category("基本功能")]
    [Category("边界值")]
    public void 标量后索引栈访问先读写旧槽再调整SP(uint machineCode, ulong address, bool load, int delta, int width)
    {
        var native = DecodeSingleInstruction(BitConverter.GetBytes(machineCode), address);
        var decoded = new NewArmV8InstructionSet().TryCreatePostIndexedScalarStackAccess(native, out var instructions);
        Assert.That(decoded, Is.True, native.ToString());
        Assert.That(instructions.Select(i => i.OpCode), Is.EqualTo(new[] { OpCode.Move, OpCode.ShiftStack }));
        Assert.That(instructions[0].MemoryAccessWidthBits, Is.EqualTo(width));
        Assert.That(instructions[0].Operands[load ? 1 : 0], Is.TypeOf<StackOffset>());
        Assert.That(((StackOffset)instructions[0].Operands[load ? 1 : 0]).Offset, Is.Zero);
        Assert.That(((Immediate)instructions[1].Operands[0]).Value, Is.EqualTo(delta));
        Assert.That(instructions[1].MemoryAccessWidthBits, Is.Zero);
    }

    [TestCase(0xF8410660u)]
    [TestCase(0xF8410FFEu)]
    [TestCase(0xF94003FEu)]
    [Category("异常输入")]
    public void 非SP后索引或其他寻址模式不进入标量栈写回(uint machineCode)
    {
        var native = DecodeSingleInstruction(BitConverter.GetBytes(machineCode), 0x9000);
        Assert.That(new NewArmV8InstructionSet().TryCreatePostIndexedScalarStackAccess(native, out var instructions), Is.False);
        Assert.That(instructions, Is.Empty);
    }
    [TestCase(Arm64Mnemonic.LDR, Arm64Register.W8, 32)]
    [TestCase(Arm64Mnemonic.LDR, Arm64Register.X8, 64)]
    [TestCase(Arm64Mnemonic.LDUR, Arm64Register.W9, 32)]
    [TestCase(Arm64Mnemonic.LDRB, Arm64Register.W8, 8)]
    [TestCase(Arm64Mnemonic.LDRH, Arm64Register.W8, 16)]
    [TestCase(Arm64Mnemonic.LDURH, Arm64Register.W8, 16)]
    [TestCase(Arm64Mnemonic.LDRSW, Arm64Register.X8, 32)]
    [TestCase(Arm64Mnemonic.MOV, Arm64Register.X8, 0)]
    [TestCase(Arm64Mnemonic.STR, Arm64Register.X8, 0)]
    [Category("基本功能")]
    [Category("边界值")]
    [Category("异常输入")]
    public void 加载访问宽度不被目标扩展或普通移动污染(Arm64Mnemonic mnemonic, Arm64Register register, int expected)
    {
        Assert.That(NewArmV8InstructionSet.GetScalarLoadWidthBits(mnemonic, register), Is.EqualTo(expected));
    }

    [Test]
    [Category("基本功能")]
    public void 完整TpidrEl0栈保护序言与尾声被统一识别()
    {
        // 真实 Clang 序言/尾声形态：MRS -> [TP+0x28] -> [FP-8]，NE 边指向唯一末尾失败调用。
        var native = DecodeInstructions(
        [
            0x59, 0xD0, 0x3B, 0xD5, // MRS X25, TPIDR_EL0
            0xF4, 0x03, 0x02, 0xAA, // MOV X20, X2
            0xF3, 0x03, 0x01, 0xAA, // MOV X19, X1
            0x28, 0x17, 0x40, 0xF9, // LDR X8, [X25, #0x28]
            0xF6, 0x03, 0x00, 0xAA, // MOV X22, X0
            0xA8, 0x83, 0x1F, 0xF8, // STUR X8, [X29, #-8]
            0x28, 0x17, 0x40, 0xF9, // LDR X8, [X25, #0x28]
            0xA9, 0x83, 0x5F, 0xF8, // LDUR X9, [X29, #-8]
            0x1F, 0x01, 0x09, 0xEB, // CMP X8, X9
            0x01, 0x01, 0x00, 0x54, // B.NE +0x20
            0xBF, 0x03, 0x00, 0x91, // MOV SP, X29
            0xF4, 0x4F, 0x44, 0xA9, // LDP X20, X19, [SP, #0x40]
            0xF9, 0x0B, 0x40, 0xF9, // LDR X25, [SP, #0x10]
            0xF6, 0x57, 0x43, 0xA9, // LDP X22, X21, [SP, #0x30]
            0xF8, 0x5F, 0x42, 0xA9, // LDP X24, X23, [SP, #0x20]
            0xFD, 0x7B, 0xC5, 0xA8, // LDP X29, X30, [SP], #0x50
            0xC0, 0x03, 0x5F, 0xD6, // RET
            0x18, 0x4E, 0x6D, 0x94, // BL __stack_chk_fail
            0xFF, 0xC3, 0x00, 0xD1, // 相邻函数 SUB SP, SP, #0x30
            0xFD, 0x7B, 0x01, 0xA9, // 相邻函数 STP X29, X30, [SP, #0x10]
        ], 0x1000);

        var matched = Arm64StackGuardHelper.FindInjectedStackGuardInstructionIndices(native);

        Assert.That(matched.OrderBy(index => index), Is.EqualTo(new[] { 0, 3, 5, 6, 7, 8, 9, 17 }));
    }

    [Test]
    [Category("边界值")]
    public void 多个返回路径的栈保护检查全部指向同一失败调用()
    {
        // 同一方法可有多个尾声；不得只删除首个检查。
        var native = DecodeInstructions(
        [
            0x59, 0xD0, 0x3B, 0xD5, // MRS X25, TPIDR_EL0
            0x28, 0x17, 0x40, 0xF9, // LDR X8, [X25, #0x28]
            0xA8, 0x83, 0x1F, 0xF8, // STUR X8, [X29, #-8]
            0x28, 0x17, 0x40, 0xF9, // 第一个尾声
            0xA9, 0x83, 0x5F, 0xF8,
            0x1F, 0x01, 0x09, 0xEB,
            0x01, 0x01, 0x00, 0x54, // B.NE -> 索引14
            0x1F, 0x20, 0x03, 0xD5, // NOP
            0x28, 0x17, 0x40, 0xF9, // 第二个尾声
            0xA9, 0x83, 0x5F, 0xF8,
            0x1F, 0x01, 0x09, 0xEB,
            0x61, 0x00, 0x00, 0x54, // B.NE -> 索引14
            0x02, 0x00, 0x00, 0x14, // B -> 同一失败汇点；属于编译器生成的 no-return 落点
            0xC0, 0x03, 0x5F, 0xD6, // RET
            0x00, 0x00, 0x00, 0x94, // BL __stack_chk_fail
        ], 0x2000);

        var matched = Arm64StackGuardHelper.FindInjectedStackGuardInstructionIndices(native);

        Assert.That(
            matched.OrderBy(index => index),
            Is.EqualTo(new[] { 0, 1, 2, 3, 4, 5, 6, 8, 9, 10, 11, 12, 14 }));
    }

    [Test]
    [Category("边界值")]
    public void 异常路径经无条件分支汇入共享栈保护比较()
    {
        var native = DecodeInstructions(
        [
            0x54, 0xD0, 0x3B, 0xD5, // MRS X20, TPIDR_EL0
            0x88, 0x16, 0x40, 0xF9, // LDR X8, [X20, #0x28]
            0xA8, 0x83, 0x1F, 0xF8, // STUR X8, [X29, #-8]
            0x88, 0x16, 0x40, 0xF9, // 主返回路径当前 canary
            0xA9, 0x83, 0x5F, 0xF8,
            0x1F, 0x01, 0x09, 0xEB,
            0x21, 0x01, 0x00, 0x54, // B.NE -> 索引15
            0xC0, 0x03, 0x5F, 0xD6,
            0x88, 0x16, 0x40, 0xF9, // 异常路径先读取当前 canary
            0x03, 0x00, 0x00, 0x14, // B -> 索引12的共享保存值加载
            0x1F, 0x20, 0x03, 0xD5,
            0x08, 0x15, 0x40, 0xF9, // 另一异常路径经别名寄存器读取当前 canary
            0xA9, 0x83, 0x5F, 0xF8,
            0x1F, 0x01, 0x09, 0xEB,
            0x21, 0x00, 0x00, 0x54, // B.NE -> 索引15
            0x00, 0x00, 0x00, 0x94,
        ], 0x2800);

        var matched = Arm64StackGuardHelper.FindInjectedStackGuardInstructionIndices(native);

        Assert.That(
            matched.OrderBy(index => index),
            Is.EqualTo(new[] { 0, 1, 2, 3, 4, 5, 6, 8, 11, 12, 13, 14, 15 }));
    }

    [Test]
    [Category("边界值")]
    public void 托管返回值选择可位于Canary加载与比较之间()
    {
        // UnityEngine 返回结构体的真实形态：两个 CSEL 先组装返回值，随后才比较 canary。
        var native = DecodeInstructions(
        [
            0xFF, 0xC3, 0x00, 0xD1, 0xFE, 0x4F, 0x02, 0xA9,
            0x53, 0xD0, 0x3B, 0xD5, 0xE2, 0x03, 0x00, 0x91,
            0x68, 0x16, 0x40, 0xF9, 0xE8, 0x0F, 0x00, 0xF9,
            0xFF, 0x7F, 0x00, 0xA9, 0xFF, 0x0B, 0x00, 0xF9,
            0x53, 0xFE, 0xFF, 0x97, 0xE8, 0x07, 0x40, 0xF9,
            0xE9, 0x13, 0x40, 0xB9, 0x1F, 0x00, 0x00, 0x72,
            0x6A, 0x16, 0x40, 0xF9, 0xEB, 0x0F, 0x40, 0xF9,
            0x2C, 0x00, 0xC0, 0xD2, 0x00, 0x11, 0x9F, 0x9A,
            0x21, 0x11, 0x8C, 0x9A, 0x5F, 0x01, 0x0B, 0xEB,
            0x81, 0x00, 0x00, 0x54, 0xFE, 0x4F, 0x42, 0xA9,
            0xFF, 0xC3, 0x00, 0x91, 0xC0, 0x03, 0x5F, 0xD6,
            0x25, 0xAE, 0x0A, 0x94,
        ], 0x54F7A64);

        var matched = Arm64StackGuardHelper.FindInjectedStackGuardInstructionIndices(native);

        Assert.That(matched.OrderBy(index => index), Is.EqualTo(new[] { 2, 4, 5, 12, 13, 17, 18, 22 }));
    }

    [Test]
    [Category("异常输入")]
    public void 直接保存线程指针不被误判为栈保护()
    {
        // 孤立线程指针保存没有 [TP+0x28] 证据链。
        var native = DecodeInstructions(
        [
            0x59, 0xD0, 0x3B, 0xD5,
            0xB9, 0x83, 0x1F, 0xF8, // STUR X25, [X29, #-8]
            0xC0, 0x03, 0x5F, 0xD6,
        ], 0x3000);

        Assert.That(Arm64StackGuardHelper.FindInjectedStackGuardInstructionIndices(native), Is.Empty);
    }

    [Test]
    [Category("边界值")]
    public void 成对保存线程指针后仍按完整栈保护证据闭合()
    {
        // 大栈帧会把线程指针与业务参数成对保存；STP 读取源寄存器，不构成线程指针覆盖。
        var native = DecodeInstructions(
        [
            0x48, 0xD0, 0x3B, 0xD5, // MRS X8, TPIDR_EL0
            0xE8, 0x07, 0x00, 0xA9, // STP X8, X1, [SP]
            0x08, 0x15, 0x40, 0xF9, // LDR X8, [X8, #0x28]
            0xE8, 0x27, 0x00, 0xF9, // STR X8, [SP, #0x48]
            0xE8, 0x03, 0x40, 0xF9, // LDR X8, [SP]
            0x08, 0x15, 0x40, 0xF9, // LDR X8, [X8, #0x28]
            0xE9, 0x27, 0x40, 0xF9, // LDR X9, [SP, #0x48]
            0x1F, 0x01, 0x09, 0xEB, // CMP X8, X9
            0x41, 0x00, 0x00, 0x54, // B.NE -> 索引10
            0xC0, 0x03, 0x5F, 0xD6, // RET
            0x00, 0x00, 0x00, 0x94, // BL __stack_chk_fail
        ], 0x3500);

        var matched = Arm64StackGuardHelper.FindInjectedStackGuardInstructionIndices(native);

        Assert.That(matched.OrderBy(index => index), Is.EqualTo(new[] { 0, 2, 3, 5, 6, 7, 8, 10 }));
    }

    [Test]
    [Category("边界值")]
    public void 大栈帧在线程指针首次覆盖前按数据流寻找栈保护值()
    {
        // 参数搬运会把 canary 读取推迟到原固定窗口之外；线程指针定义仍保持不变。
        var native = DecodeInstructions(
        [
            0x48, 0xD0, 0x3B, 0xD5, // MRS X8, TPIDR_EL0
            0xA9, 0x37, 0x40, 0xF9, // LDR X9, [X29, #0x68]
            0xAA, 0x43, 0x00, 0xD1, // SUB X10, X29, #0x10
            0xA8, 0x83, 0x18, 0xF8, // STUR X8, [X29, #-0x78]：保存线程指针
            0xB3, 0x3F, 0x40, 0xF9, // LDR X19, [X29, #0x78]
            0xF8, 0x03, 0x01, 0xAA, // MOV X24, X1
            0x49, 0x01, 0x10, 0xF8, // STUR X9, [X10, #-0x100]
            0xA9, 0x33, 0x40, 0xF9, // LDR X9, [X29, #0x60]
            0xF7, 0x03, 0x00, 0xAA, // MOV X23, X0
            0xA9, 0x83, 0x10, 0xF8, // STUR X9, [X29, #-0xF8]
            0x08, 0x15, 0x40, 0xF9, // LDR X8, [X8, #0x28]
            0xA8, 0x03, 0x1F, 0xF8, // STUR X8, [X29, #-0x10]
            0xA8, 0x83, 0x58, 0xF8, // LDUR X8, [X29, #-0x78]
            0x08, 0x15, 0x40, 0xF9, // LDR X8, [X8, #0x28]
            0xA9, 0x03, 0x5F, 0xF8, // LDUR X9, [X29, #-0x10]
            0x1F, 0x01, 0x09, 0xEB, // CMP X8, X9
            0x41, 0x00, 0x00, 0x54, // B.NE -> 索引18
            0xC0, 0x03, 0x5F, 0xD6, // RET
            0x00, 0x00, 0x00, 0x94, // BL __stack_chk_fail
        ], 0x3800);

        var matched = Arm64StackGuardHelper.FindInjectedStackGuardInstructionIndices(native);

        Assert.That(matched.OrderBy(index => index), Is.EqualTo(new[] { 0, 10, 11, 13, 14, 15, 16, 18 }));
    }

    [Test]
    [Category("异常输入")]
    public void 线程指针覆盖后出现相同偏移读取不闭合为栈保护()
    {
        var native = DecodeInstructions(
        [
            0x48, 0xD0, 0x3B, 0xD5, // MRS X8, TPIDR_EL0
            0xE8, 0x03, 0x00, 0xAA, // MOV X8, X0：线程指针定义已被覆盖
            0x08, 0x15, 0x40, 0xF9, // LDR X8, [X8, #0x28]
            0xC0, 0x03, 0x5F, 0xD6,
        ], 0x3C00);

        Assert.That(Arm64StackGuardHelper.FindInjectedStackGuardInstructionIndices(native), Is.Empty);
    }

    [Test]
    [Category("边界值")]
    public void 栈保护线程指针寄存器被覆盖复用不影响闭合识别()
    {
        var bytes = new byte[]
        {
            0x59, 0xD0, 0x3B, 0xD5,
            0xF4, 0x03, 0x19, 0xAA, // MOV X20, X25：线性布局中寄存器可在其他路径被覆盖复用
            0xF3, 0x03, 0x01, 0xAA,
            0x28, 0x17, 0x40, 0xF9,
            0xF6, 0x03, 0x00, 0xAA,
            0xA8, 0x83, 0x1F, 0xF8,
            0x28, 0x17, 0x40, 0xF9,
            0xA9, 0x83, 0x5F, 0xF8,
            0x1F, 0x01, 0x09, 0xEB,
            0x01, 0x01, 0x00, 0x54,
            0xBF, 0x03, 0x00, 0x91,
            0xF4, 0x4F, 0x44, 0xA9,
            0xF9, 0x0B, 0x40, 0xF9,
            0xF6, 0x57, 0x43, 0xA9,
            0xF8, 0x5F, 0x42, 0xA9,
            0xFD, 0x7B, 0xC5, 0xA8,
            0xC0, 0x03, 0x5F, 0xD6,
            0x18, 0x4E, 0x6D, 0x94,
        };

        var native = DecodeInstructions(bytes, 0x4000);

        Assert.That(
            Arm64StackGuardHelper.FindInjectedStackGuardInstructionIndices(native).OrderBy(index => index),
            Is.EqualTo(new[] { 0, 3, 5, 6, 7, 8, 9, 17 }));
    }
}
