using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Disarm;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.Il2CppApiFunctions;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Disarm.InternalDisassembly;

namespace Cpp2IL.Core.InstructionSets;

internal enum Arm64FlagState
{
    None,
    ZeroOnly,
    CarryAndZero,
    Comparison,
    ConditionalComparison,
    FloatingComparison
}

internal enum Arm64RecoveredVectorOperation
{
    DuplicateInt16,
    WidenUnsignedInt16ToInt32,
    ShiftLeftInt32,
    CompareLessThanZeroInt32,
    BitwiseSelect128,
    MultiplyFloat32ByElement
}

internal readonly struct Arm64RecoveredVectorInstruction
{
    internal Arm64RecoveredVectorInstruction(
        Arm64RecoveredVectorOperation operation,
        int destinationRegister,
        int firstSourceRegister,
        int secondSourceRegister = -1,
        int laneCount = 0,
        int elementWidthBits = 0,
        int immediate = 0)
    {
        Operation = operation;
        DestinationRegister = destinationRegister;
        FirstSourceRegister = firstSourceRegister;
        SecondSourceRegister = secondSourceRegister;
        LaneCount = laneCount;
        ElementWidthBits = elementWidthBits;
        Immediate = immediate;
    }

    internal Arm64RecoveredVectorOperation Operation { get; }

    internal int DestinationRegister { get; }

    internal int FirstSourceRegister { get; }

    internal int SecondSourceRegister { get; }

    internal int LaneCount { get; }

    internal int ElementWidthBits { get; }

    internal int Immediate { get; }
}

internal readonly struct Arm64PackedHalfwordPredicatePattern
{
    internal Arm64PackedHalfwordPredicatePattern(
        int scalarSourceRegister,
        int resultRegister,
        int constantBaseRegister,
        int constantByteOffset,
        int accumulatorBaseRegister,
        int accumulatorByteOffset,
        int reverseLaneMask)
    {
        ScalarSourceRegister = scalarSourceRegister;
        ResultRegister = resultRegister;
        ConstantBaseRegister = constantBaseRegister;
        ConstantByteOffset = constantByteOffset;
        AccumulatorBaseRegister = accumulatorBaseRegister;
        AccumulatorByteOffset = accumulatorByteOffset;
        ReverseLaneMask = reverseLaneMask;
    }

    internal int ScalarSourceRegister { get; }

    internal int ResultRegister { get; }

    internal int ConstantBaseRegister { get; }

    internal int ConstantByteOffset { get; }

    internal int AccumulatorBaseRegister { get; }

    internal int AccumulatorByteOffset { get; }

    internal int ReverseLaneMask { get; }
}

/// <summary>只保存由原生边界分支证明的跳转表范围，不从表内容猜测数量或默认分支。</summary>
internal readonly record struct Arm64JumpTableBounds(
    long LowerBound,
    int EntryCount,
    ulong DefaultTarget);

/// <summary>单个证据闭合的 ARM64 跳转表站点。</summary>
internal sealed record Arm64JumpTableMatch(
    int BranchIndex,
    Arm64Register AdjustedIndexRegister,
    int EntryWidthBytes,
    ulong BoundaryAddress,
    ulong DispatchAddress,
    ulong TableAddress,
    ulong BranchBaseAddress,
    Arm64JumpTableBounds Bounds,
    bool UsesClampedIndex);

/// <summary>已读取并验证全部表项的 ARM64 跳转表站点。</summary>
internal sealed record Arm64JumpTableDispatch(
    Arm64Register AdjustedIndexRegister,
    ulong DispatchAddress,
    Arm64JumpTableBounds Bounds,
    ulong[] CaseTargets);

public class NewArmV8InstructionSet : Cpp2IlInstructionSet
{
    private const int MaximumJumpTableEntryCount = 65_536;

    private static readonly Arm64CallingConventionAdapter CallingConventions = new();

    public override BaseCallingConventionResolver CallingConventionResolver => CallingConventions;

    [ThreadStatic]
    private static Dictionary<Arm64Register, ulong> adrpOffsets = new();

    private static Immediate Imm(long value) => new(value);
    private static Immediate Imm(ulong value) => new(unchecked((long)value));

    /// <summary>
    /// 把只能表示值的 ARM64 31 号通用寄存器规范化为常量零。
    /// 该帮助器只用于 CSEL/CSINC 等明确按 WZR/XZR 解释的值操作数，
    /// 栈指针语境仍由帧地址恢复规则独立处理。
    /// </summary>
    internal static IOperand NormalizeZeroRegisterValueOperand(
        Arm64OperandKind kind,
        Arm64Register register,
        IOperand converted)
        => kind == Arm64OperandKind.Register
           && Arm64RegisterHelper.IsZeroRegister(register)
            ? Imm(0)
            : converted;

    internal static bool TryDecodeStackPointerAdjustment(
        Arm64Mnemonic mnemonic,
        Arm64OperandKind destinationKind,
        Arm64Register destinationRegister,
        Arm64OperandKind sourceKind,
        Arm64Register sourceRegister,
        Arm64OperandKind amountKind,
        long amount,
        out int stackDelta)
    {
        // ARM64把SP编码为31号寄存器；仅接受SP到SP的立即数ADD/SUB，避免把普通算术误判为栈调整。
        if (mnemonic is not (Arm64Mnemonic.ADD or Arm64Mnemonic.SUB)
            || destinationKind != Arm64OperandKind.Register
            || sourceKind != Arm64OperandKind.Register
            || amountKind != Arm64OperandKind.Immediate
            || destinationRegister != Arm64Register.X31
            || sourceRegister != Arm64Register.X31
            || amount < 0
            || amount > int.MaxValue)
        {
            stackDelta = 0;
            return false;
        }

        var magnitude = checked((int)amount);
        stackDelta = mnemonic == Arm64Mnemonic.SUB ? -magnitude : magnitude;
        return true;
    }

    /// <summary>
    /// LDP/STR 等后索引内存操作先访问旧 SP，再按立即数更新 SP；因此内存槽使用零偏移，
    /// 栈状态增量则取 MemOffset。普通偏移和前索引不得进入该路径。
    /// </summary>
    internal static bool TryDecodePostIndexedStackAdjustment(
        Arm64MemoryIndexMode indexMode,
        Arm64Register memoryBase,
        long byteOffset,
        out int stackDelta)
    {
        if (indexMode != Arm64MemoryIndexMode.PostIndex
            || memoryBase != Arm64Register.X31
            || byteOffset is < int.MinValue or > int.MaxValue)
        {
            stackDelta = 0;
            return false;
        }

        stackDelta = checked((int)byteOffset);
        return true;
    }

    /// <summary>
    /// 识别普通通用寄存器的前索引写回。
    /// 前索引寻址会先把立即数加到基址寄存器，再使用更新后的地址访问内存；
    /// SP 由独立的栈槽路径处理，寄存器索引形式则不属于 ARM64 前索引立即数编码。
    /// </summary>
    internal static bool TryDecodePreIndexedRegisterWriteback(
        Arm64MemoryIndexMode indexMode,
        Arm64Register memoryBase,
        Arm64Register addendRegister,
        out Register writebackRegister)
    {
        if (indexMode != Arm64MemoryIndexMode.PreIndex
            || memoryBase is < Arm64Register.X0 or > Arm64Register.X30
            || addendRegister != Arm64Register.INVALID)
        {
            writebackRegister = default;
            return false;
        }

        writebackRegister = new Register(
            null,
            Arm64RegisterHelper.CanonicalName(memoryBase));
        return true;
    }

    internal static bool TryCreateStackOffset(
        Arm64Register baseRegister,
        Arm64Register addendRegister,
        long byteOffset,
        out StackOffset stackOffset)
    {
        // 带索引寄存器的地址不是固定栈槽，必须继续走普通内存解析，禁止错误折叠。
        if (baseRegister != Arm64Register.X31
            || addendRegister != Arm64Register.INVALID
            || byteOffset < int.MinValue
            || byteOffset > int.MaxValue)
        {
            stackOffset = default;
            return false;
        }

        stackOffset = new StackOffset(checked((int)byteOffset));
        return true;
    }

    /// <summary>
    /// 将 ARM64 寄存器偏移寻址的移位与扩展精确转换为 ISIL 步长和扩展语义。
    /// </summary>
    internal static bool TryDecodeMemoryIndex(
        Arm64ShiftType shiftType,
        Arm64ExtendType extendType,
        int shiftAmount,
        out int scale,
        out MemoryIndexExtension extension)
    {
        extension = extendType switch
        {
            Arm64ExtendType.NONE => MemoryIndexExtension.None,
            Arm64ExtendType.UXTW => MemoryIndexExtension.ZeroExtend32,
            Arm64ExtendType.SXTW => MemoryIndexExtension.SignExtend32,
            Arm64ExtendType.UXTX => MemoryIndexExtension.ZeroExtend64,
            Arm64ExtendType.SXTX => MemoryIndexExtension.SignExtend64,
            _ => MemoryIndexExtension.None
        };

        if (shiftType is not (Arm64ShiftType.NONE or Arm64ShiftType.LSL)
            || extendType is Arm64ExtendType.UXTB or Arm64ExtendType.UXTH
                or Arm64ExtendType.SXTB or Arm64ExtendType.SXTH
            || shiftAmount is < 0 or > 30)
        {
            scale = 0;
            extension = MemoryIndexExtension.None;
            return false;
        }

        scale = 1 << shiftAmount;
        return true;
    }

    /// <summary>
    /// 构造普通 ARM64 内存操作数；动态索引寄存器、扩展方式和 LSL 步长必须全部保留。
    /// </summary>
    internal static MemoryOperand CreateMemoryOperand(Arm64Instruction instruction)
    {
        IOperand? baseOperand = instruction.MemBase == Arm64Register.INVALID
            ? null
            : new Register(null, Arm64RegisterHelper.CanonicalName(instruction.MemBase));

        if (instruction.MemAddendReg == Arm64Register.INVALID)
            return new MemoryOperand(baseOperand, addend: instruction.MemOffset);

        if (instruction.MemIndexMode != Arm64MemoryIndexMode.Offset
            || !TryDecodeMemoryIndex(
                instruction.MemShiftType,
                instruction.MemExtendType,
                instruction.MemExtendOrShiftAmount,
                out var scale,
                out var extension))
        {
            throw new InvalidOperationException(
                $"ARM64寄存器偏移寻址无法精确转换：{instruction}");
        }

        return new MemoryOperand(
            baseOperand,
            new Register(null, Arm64RegisterHelper.CanonicalName(instruction.MemAddendReg)),
            instruction.MemOffset,
            scale,
            extension);
    }

    internal static bool TryCreateStackAddressOffset(
        Arm64Mnemonic mnemonic,
        Arm64OperandKind destinationKind,
        Arm64Register destinationRegister,
        Arm64OperandKind sourceKind,
        Arm64Register sourceRegister,
        Arm64OperandKind amountKind,
        long amount,
        out StackOffset stackOffset)
    {
        // ADD（立即数）允许把SP作为源寄存器；此形式计算的是栈槽地址，不是读取名为X31的普通值。
        if (mnemonic != Arm64Mnemonic.ADD
            || destinationKind != Arm64OperandKind.Register
            || destinationRegister is < Arm64Register.X0 or > Arm64Register.X30
            || sourceKind != Arm64OperandKind.Register
            || sourceRegister != Arm64Register.X31
            || amountKind != Arm64OperandKind.Immediate
            || amount < 0
            || amount > int.MaxValue)
        {
            stackOffset = default;
            return false;
        }

        stackOffset = new StackOffset(checked((int)amount));
        return true;
    }

    internal static ulong ResolveAdrpPageAddress(ulong instructionAddress, long pageRelativeImmediate)
    {
        var instructionPage = instructionAddress & ~0xFFFUL;
        return unchecked((ulong)(unchecked((long)instructionPage) + pageRelativeImmediate));
    }

    internal static bool TryCreateAdrpMemoryOperand(
        IReadOnlyDictionary<Arm64Register, ulong> pageAddresses,
        Arm64Register baseRegister,
        Arm64Register addendRegister,
        long byteOffset,
        out MemoryOperand memory)
    {
        // ADRP页基址加立即数是绝对地址；带索引的寻址仍保留寄存器表达，避免丢失动态偏移。
        if (addendRegister != Arm64Register.INVALID
            || !pageAddresses.TryGetValue(baseRegister, out var pageAddress))
        {
            memory = default;
            return false;
        }

        memory = new MemoryOperand(addend: unchecked((long)(pageAddress + unchecked((ulong)byteOffset))));
        return true;
    }

    /// <summary>
    /// 通用寄存器被值指令重写后，清除同一物理寄存器上残留的 ADRP 页地址事实。
    /// Wn 写入同样会覆盖 Xn 的低位并清零高位，因此宽度别名必须作为一个寄存器处理。
    /// </summary>
    internal static bool InvalidateAdrpRegisterWrite(
        IDictionary<Arm64Register, ulong> pageAddresses,
        Arm64OperandKind destinationKind,
        Arm64Register destinationRegister)
    {
        if (destinationKind != Arm64OperandKind.Register)
            return false;

        var destinationName = Arm64RegisterHelper.CanonicalName(destinationRegister);
        var staleRegisters = pageAddresses.Keys
            .Where(register => Arm64RegisterHelper.CanonicalName(register) == destinationName)
            .ToArray();
        foreach (var staleRegister in staleRegisters)
            pageAddresses.Remove(staleRegister);

        return staleRegisters.Length != 0;
    }

    internal static bool TryFindObservedIndirectReturnBuffer(
        IReadOnlyList<Instruction> emittedInstructions,
        out StackOffset stackOffset)
    {
        for (var index = emittedInstructions.Count - 1; index >= 0; index--)
        {
            var instruction = emittedInstructions[index];
            if (instruction.IsCall
                || instruction.OpCode is OpCode.Jump or OpCode.ConditionalJump or OpCode.Return)
                break;

            if (instruction.Destination is not Register { Name: "X8" })
                continue;

            if (instruction.OpCode == OpCode.Move
                && instruction.Operands.Count >= 2
                && instruction.Operands[1] is AddressOf { Target: StackOffset observed })
            {
                stackOffset = observed;
                return true;
            }

            // 同一基本块内最近一次X8定义不是栈地址，旧值已失效。
            break;
        }

        stackOffset = default;
        return false;
    }

    internal static bool TryDecodeMoveKeepImmediate(
        uint machineCode,
        out int registerWidth,
        out int halfwordShift,
        out ulong clearMask,
        out ulong shiftedImmediate)
    {
        // MOVK 属于 move-wide immediate 编码族，opc 必须为 3。
        var isMoveWide = (machineCode & 0x1F800000u) == 0x12800000u;
        var operation = (machineCode >> 29) & 0x3u;
        if (!isMoveWide || operation != 0x3u)
        {
            registerWidth = 0;
            halfwordShift = 0;
            clearMask = 0;
            shiftedImmediate = 0;
            return false;
        }

        registerWidth = (machineCode & 0x80000000u) == 0 ? 32 : 64;
        var halfwordIndex = (int)((machineCode >> 21) & 0x3u);
        if (registerWidth == 32 && halfwordIndex > 1)
        {
            halfwordShift = 0;
            clearMask = 0;
            shiftedImmediate = 0;
            return false;
        }

        halfwordShift = halfwordIndex * 16;
        var registerMask = registerWidth == 32 ? uint.MaxValue : ulong.MaxValue;
        var halfwordMask = 0xFFFFul << halfwordShift;
        clearMask = registerMask & ~halfwordMask;
        shiftedImmediate = (ulong)((machineCode >> 5) & 0xFFFFu) << halfwordShift;
        return true;
    }

    /// <summary>
    /// 将 MOVK 精确展开成同一位宽的清半字与写半字操作。位宽必须在原生指令入口写入两条
    /// ISIL 指令；若延迟到退 SSA 后再从 0xFFFF 掩码猜测，W/X 寄存器会产生同形歧义。
    /// </summary>
    internal static bool TryCreateMoveKeepInstructions(
        uint machineCode,
        IOperand destination,
        out Instruction[] instructions)
    {
        if (!TryDecodeMoveKeepImmediate(
                machineCode,
                out var registerWidth,
                out _,
                out var clearMask,
                out var shiftedImmediate))
        {
            instructions = [];
            return false;
        }

        instructions =
        [
            new Instruction(-1, OpCode.And, destination, destination, Imm(clearMask))
            {
                IntegerWidthBits = registerWidth,
            },
            new Instruction(-1, OpCode.Or, destination, destination, Imm(shiftedImmediate))
            {
                IntegerWidthBits = registerWidth,
            },
        ];
        return true;
    }

    internal static bool TryGetFloatingPointPrecisionBits(Arm64Register register, out int bits)
    {
        if (register is >= Arm64Register.S0 and <= Arm64Register.S31)
        {
            bits = 32;
            return true;
        }

        if (register is >= Arm64Register.D0 and <= Arm64Register.D31)
        {
            bits = 64;
            return true;
        }

        bits = 0;
        return false;
    }

    internal static bool TryDecodeScalarFloatingPointImmediate(
        uint machineCode,
        out int precisionBits,
        out double immediate)
    {
        // A64标量FMOV立即数的imm8位于20..13位；Disarm当前会把部分负数解成错误的小数。
        const uint encodingMask = 0xFFE01FE0u;
        const uint singleEncoding = 0x1E201000u;
        const uint doubleEncoding = 0x1E601000u;
        var fixedEncoding = machineCode & encodingMask;
        if (fixedEncoding != singleEncoding && fixedEncoding != doubleEncoding)
        {
            precisionBits = 0;
            immediate = 0;
            return false;
        }

        precisionBits = fixedEncoding == singleEncoding ? 32 : 64;
        var imm8 = (byte)((machineCode >> 13) & 0xFFu);
        var exponentSelector = (imm8 >> 6) & 0x1u;

        // 按ARM VFPExpandImm规则构造IEEE-754双精度位型；四位尾数对单精度也可精确表示。
        var exponent = ((1u - exponentSelector) << 10)
                       | ((exponentSelector == 0 ? 0u : 0xFFu) << 2)
                       | ((uint)imm8 >> 4 & 0x3u);
        var fraction = ((ulong)imm8 & 0xFu) << 48;
        var bits = ((ulong)imm8 >> 7 << 63) | ((ulong)exponent << 52) | fraction;
        immediate = BitConverter.Int64BitsToDouble(unchecked((long)bits));
        return true;
    }

    internal static bool TryGetSignedIntegerWidthBits(Arm64Register register, out int bits)
    {
        if (register is >= Arm64Register.W0 and <= Arm64Register.W30)
        {
            bits = 32;
            return true;
        }

        if (register is >= Arm64Register.X0 and <= Arm64Register.X30)
        {
            bits = 64;
            return true;
        }

        bits = 0;
        return false;
    }

    /// <summary>
    /// 将 ARM64 SMADDL/SMULL 精确展开为两个有符号拓宽、一次64位乘法和一次64位加法。
    /// W 源必须先按 int32 符号扩展，禁止把负数当作 uint32 零扩展后参与乘法。
    /// </summary>
    internal static bool TryCreateSignedMultiplyAddLongInstructions(
        Arm64Instruction instruction,
        ulong address,
        out Instruction[] recovered)
    {
        recovered = [];
        var isMultiplyAlias = instruction.Mnemonic == Arm64Mnemonic.SMULL;
        if (instruction.Mnemonic is not (Arm64Mnemonic.SMADDL or Arm64Mnemonic.SMULL)
            || instruction.Op0Kind != Arm64OperandKind.Register
            || instruction.Op1Kind != Arm64OperandKind.Register
            || instruction.Op2Kind != Arm64OperandKind.Register
            || instruction.Op0Reg is < Arm64Register.X0 or > Arm64Register.X30
            // Disarm 各版本可能把 Wn/Wm 保留为 W，也可能规范化为同槽位 X；
            // 32位有符号源宽度由 SMADDL/SMULL 操作码本身保证。
            || !IsWordSourceRegister(instruction.Op1Reg)
            || !IsWordSourceRegister(instruction.Op2Reg)
            || !isMultiplyAlias
            && (instruction.Op3Kind != Arm64OperandKind.Register
                || instruction.Op3Reg is < Arm64Register.X0 or > Arm64Register.X31))
            return false;

        static bool IsWordSourceRegister(Arm64Register register)
            => register is >= Arm64Register.W0 and <= Arm64Register.W31
                or >= Arm64Register.X0 and <= Arm64Register.X31;

        static IOperand RegisterOrZero(Arm64Register register)
            => Arm64RegisterHelper.IsZeroRegister(register)
                ? Imm(0)
                : new Register(null, Arm64RegisterHelper.CanonicalName(register));

        var destination = new Register(null, Arm64RegisterHelper.CanonicalName(instruction.Op0Reg));
        var left64 = new Register(null, $"SMADDL_LEFT_SIGNED64_{address:X}");
        var right64 = new Register(null, $"SMADDL_RIGHT_SIGNED64_{address:X}");
        var product = new Register(null, $"SMADDL_PRODUCT64_{address:X}");
        var leftSource = RegisterOrZero(instruction.Op1Reg);
        var rightSource = RegisterOrZero(instruction.Op2Reg);
        var accumulator = isMultiplyAlias ? Imm(0) : RegisterOrZero(instruction.Op3Reg);

        recovered =
        [
            new Instruction(0, OpCode.ConvertSignedIntegerWidth, left64, leftSource, Imm(64), Imm(32)),
            new Instruction(1, OpCode.ConvertSignedIntegerWidth, right64, rightSource, Imm(64), Imm(32)),
            new Instruction(2, OpCode.Multiply, product, left64, right64) { IntegerWidthBits = 64 },
            new Instruction(3, OpCode.Add, destination, accumulator, product) { IntegerWidthBits = 64 },
        ];
        return true;
    }

    /// <summary>
    /// 按 ARM DecodeBitMasks 的 UBFM 两段区间语义恢复无符号位域移动。
    /// immr 不越过 imms 时执行逻辑右移并截取低位；越过时先截取源低位再左移到目标位域。
    /// </summary>
    internal static bool TryCreateUnsignedBitfieldMoveInstructions(
        Arm64Instruction instruction,
        out Instruction[] recovered)
    {
        recovered = [];
        if (instruction.Op0Kind != Arm64OperandKind.Register
            || instruction.Op1Kind != Arm64OperandKind.Register
            || !TryGetUnsignedBitfieldRegisterWidthBits(instruction.Op0Reg, out var widthBits)
            || !TryGetUnsignedBitfieldRegisterWidthBits(instruction.Op1Reg, out var sourceWidthBits)
            || sourceWidthBits != widthBits
            || !TryDecodeUnsignedBitfieldImmediates(
                instruction,
                widthBits,
                out var rotateRight,
                out var mostSignificantBit))
            return false;

        if (Arm64RegisterHelper.IsZeroRegister(instruction.Op0Reg))
        {
            recovered = [new Instruction(0, OpCode.Nop)];
            return true;
        }

        var destination = new Register(null, Arm64RegisterHelper.CanonicalName(instruction.Op0Reg));
        IOperand source = Arm64RegisterHelper.IsZeroRegister(instruction.Op1Reg)
            ? Imm(0)
            : new Register(null, Arm64RegisterHelper.CanonicalName(instruction.Op1Reg));
        var instructions = new List<Instruction>(2);

        static long LowMask(int bitCount)
            => bitCount >= 64 ? -1 : (1L << bitCount) - 1;

        void AddSized(OpCode opCode, params IOperand[] operands)
            => instructions.Add(new Instruction(instructions.Count, opCode, operands.ToList())
            {
                IntegerWidthBits = widthBits,
            });

        if (rotateRight <= mostSignificantBit)
        {
            AddSized(OpCode.ShiftRightUnsigned, destination, source, Imm(rotateRight));
            var extractedBits = mostSignificantBit - rotateRight + 1;
            if (extractedBits < widthBits - rotateRight)
                AddSized(OpCode.And, destination, destination, Imm(LowMask(extractedBits)));
        }
        else
        {
            var extractedBits = mostSignificantBit + 1;
            AddSized(OpCode.And, destination, source, Imm(LowMask(extractedBits)));
            AddSized(OpCode.ShiftLeft, destination, destination, Imm(widthBits - rotateRight));
        }

        recovered = instructions.ToArray();
        return true;
    }

    /// <summary>
    /// 将 Disarm 暴露的 UBFM、LSR 与 UBFIZ 别名统一还原为 UBFM 的 immr/imms。
    /// </summary>
    private static bool TryDecodeUnsignedBitfieldImmediates(
        Arm64Instruction instruction,
        int widthBits,
        out int rotateRight,
        out int mostSignificantBit)
    {
        rotateRight = 0;
        mostSignificantBit = 0;

        if (instruction.Op2Kind != Arm64OperandKind.Immediate)
            return false;

        switch (instruction.Mnemonic)
        {
            case Arm64Mnemonic.UBFM:
                if (instruction.Op3Kind != Arm64OperandKind.Immediate
                    || instruction.Op2Imm is < 0 or >= 64
                    || instruction.Op3Imm is < 0 or >= 64)
                    return false;

                rotateRight = (int)instruction.Op2Imm;
                mostSignificantBit = (int)instruction.Op3Imm;
                return rotateRight < widthBits && mostSignificantBit < widthBits;

            case Arm64Mnemonic.LSR:
                if (instruction.Op2Imm < 0 || instruction.Op2Imm >= widthBits)
                    return false;

                rotateRight = (int)instruction.Op2Imm;
                mostSignificantBit = widthBits - 1;
                return true;

            case Arm64Mnemonic.UBFIZ:
                if (instruction.Op3Kind != Arm64OperandKind.Immediate)
                    return false;

                var leastSignificantBit = (int)instruction.Op2Imm;
                var fieldWidth = (int)instruction.Op3Imm;
                if (leastSignificantBit < 0
                    || fieldWidth <= 0
                    || leastSignificantBit + fieldWidth > widthBits)
                    return false;

                rotateRight = (widthBits - leastSignificantBit) % widthBits;
                mostSignificantBit = fieldWidth - 1;
                return true;

            default:
                return false;
        }
    }

    /// <summary>识别 UBFM 可使用的同宽 W/X 通用寄存器，31号槽位按零寄存器处理。</summary>
    private static bool TryGetUnsignedBitfieldRegisterWidthBits(Arm64Register register, out int bits)
    {
        if (register is >= Arm64Register.W0 and <= Arm64Register.W31)
        {
            bits = 32;
            return true;
        }

        if (register is >= Arm64Register.X0 and <= Arm64Register.X31)
        {
            bits = 64;
            return true;
        }

        bits = 0;
        return false;
    }

    /// <summary>
    /// 识别 FMOV 在通用寄存器与标量浮点寄存器之间的同宽位复制。
    /// 该指令不执行数值转换，必须保留 IEEE-754 原始位模式。
    /// </summary>
    internal static bool TryGetFmovBitReinterpretation(
        Arm64OperandKind destinationKind,
        Arm64Register destinationRegister,
        Arm64OperandKind sourceKind,
        Arm64Register sourceRegister,
        out OpCode opCode,
        out int widthBits)
    {
        opCode = OpCode.Invalid;
        widthBits = 0;
        if (destinationKind != Arm64OperandKind.Register || sourceKind != Arm64OperandKind.Register)
            return false;

        if (TryGetFloatingPointPrecisionBits(destinationRegister, out var floatingWidth)
            && TryGetSignedIntegerWidthBits(sourceRegister, out var integerWidth)
            && floatingWidth == integerWidth)
        {
            opCode = OpCode.ReinterpretIntegerBitsAsFloat;
            widthBits = floatingWidth;
            return true;
        }

        if (TryGetSignedIntegerWidthBits(destinationRegister, out integerWidth)
            && TryGetFloatingPointPrecisionBits(sourceRegister, out floatingWidth)
            && integerWidth == floatingWidth)
        {
            opCode = OpCode.ReinterpretFloatBitsAsInteger;
            widthBits = integerWidth;
            return true;
        }

        return false;
    }

    internal static bool TryGetSignedIntegerPayloadWidthBits(Arm64Register register, out int bits)
    {
        if (TryGetSignedIntegerWidthBits(register, out bits))
            return true;

        // FCVTZS/SCVTF 的标量 SIMD 编码会把有符号整数载荷保存在 S/D 物理寄存器中。
        // 这里只恢复转换指令的载荷宽度，避免把所有浮点寄存器全局误判为整数寄存器。
        if (register is >= Arm64Register.S0 and <= Arm64Register.S31)
        {
            bits = 32;
            return true;
        }

        if (register is >= Arm64Register.D0 and <= Arm64Register.D31)
        {
            bits = 64;
            return true;
        }

        bits = 0;
        return false;
    }

    internal static bool TryDecodeReplicatedVectorMoveImmediate32(
        uint machineCode,
        out int vectorWidthBits,
        out int laneCount,
        out uint elementBits)
    {
        // Advanced SIMD MOVI 的 32 位移位立即数变体：
        // Q 决定 64/128 位向量，cmode[2:1] 决定 0/8/16/24 位左移，imm8 被复制到每个 S 通道。
        const uint encodingMask = 0xBFF89C00u;
        const uint encodingValue = 0x0F000400u;
        if ((machineCode & encodingMask) != encodingValue)
        {
            vectorWidthBits = 0;
            laneCount = 0;
            elementBits = 0;
            return false;
        }

        vectorWidthBits = (machineCode & (1u << 30)) == 0 ? 64 : 128;
        laneCount = vectorWidthBits / 32;
        var immediate = (((machineCode >> 16) & 0x7u) << 5) | ((machineCode >> 5) & 0x1Fu);
        var shift = (int)((machineCode >> 13) & 0x3u) * 8;
        elementBits = immediate << shift;
        return true;
    }

    internal static bool TryDecodeReplicatedVectorMoveImmediate16(
        uint machineCode,
        out int vectorWidthBits,
        out int laneCount,
        out ushort elementBits)
    {
        // Advanced SIMD MOVI 的16位立即数变体只允许LSL #0/#8；imm8复制到全部H通道。
        const uint encodingMask = 0xBFF8DC00u;
        const uint encodingValue = 0x0F008400u;
        if ((machineCode & encodingMask) != encodingValue)
        {
            vectorWidthBits = 0;
            laneCount = 0;
            elementBits = 0;
            return false;
        }

        vectorWidthBits = (machineCode & (1u << 30)) == 0 ? 64 : 128;
        laneCount = vectorWidthBits / 16;
        var immediate = (((machineCode >> 16) & 0x7u) << 5) | ((machineCode >> 5) & 0x1Fu);
        var shift = (int)((machineCode >> 13) & 0x1u) * 8;
        elementBits = checked((ushort)(immediate << shift));
        return true;
    }

    internal static bool TryDecodeUnsignedVector128Memory(
        uint machineCode,
        out bool isLoad,
        out int vectorRegister,
        out int baseRegister,
        out int byteOffset)
    {
        // LDR/STR Qt, [Xn|SP, #imm] 的imm12以16字节为单位；Disarm当前只暴露未缩放值。
        var operation = machineCode & 0xFFC00000u;
        if (operation is not (0x3D800000u or 0x3DC00000u))
        {
            isLoad = false;
            vectorRegister = 0;
            baseRegister = 0;
            byteOffset = 0;
            return false;
        }

        isLoad = operation == 0x3DC00000u;
        vectorRegister = (int)(machineCode & 0x1Fu);
        baseRegister = (int)((machineCode >> 5) & 0x1Fu);
        byteOffset = checked((int)((machineCode >> 10) & 0xFFFu) * 16);
        return true;
    }

    internal static bool TryDecodeUnsignedHalfwordMoveToGeneral(
        uint machineCode,
        out int generalRegister,
        out int vectorRegister,
        out int laneIndex)
    {
        // UMOV Wd, Vn.H[index]：imm5最低置位为H宽度标记，其余高位给出0..7通道索引。
        if ((machineCode & 0xFFE0FC00u) != 0x0E003C00u)
        {
            generalRegister = 0;
            vectorRegister = 0;
            laneIndex = 0;
            return false;
        }

        var imm5 = (int)((machineCode >> 16) & 0x1Fu);
        if ((imm5 & 0x3) != 0x2)
        {
            generalRegister = 0;
            vectorRegister = 0;
            laneIndex = 0;
            return false;
        }

        generalRegister = (int)(machineCode & 0x1Fu);
        vectorRegister = (int)((machineCode >> 5) & 0x1Fu);
        laneIndex = imm5 >> 2;
        if (laneIndex >= 4)
        {
            generalRegister = 0;
            vectorRegister = 0;
            laneIndex = 0;
            return false;
        }
        return true;
    }

    internal static bool TryDecodeDisassembledUnsignedHalfwordMoveToGeneral(
        Arm64Mnemonic mnemonic,
        uint machineCode,
        out int generalRegister,
        out int vectorRegister,
        out int laneIndex)
    {
        // Disarm新版本会把UMOV识别为正式助记符；旧版本则可能归入INVALID或UNIMPLEMENTED。
        // 三种入口必须共用同一份机器码解码逻辑，避免分派层与位域语义产生重复实现。
        if (mnemonic is not Arm64Mnemonic.UMOV
            and not Arm64Mnemonic.INVALID
            and not Arm64Mnemonic.UNIMPLEMENTED)
        {
            generalRegister = 0;
            vectorRegister = 0;
            laneIndex = 0;
            return false;
        }

        return TryDecodeUnsignedHalfwordMoveToGeneral(
            machineCode,
            out generalRegister,
            out vectorRegister,
            out laneIndex);
    }

    internal static bool TryDecodePackedHalfwordPredicatePattern(
        ReadOnlySpan<uint> machineCodes,
        out Arm64PackedHalfwordPredicatePattern pattern)
    {
        // 该编译器惯用序列把四个32位比较结果收窄为四个H通道，再与累计低位合并并做UMINV。
        if (machineCodes.Length < 13)
        {
            pattern = default;
            return false;
        }

        var duplicate = machineCodes[0];
        if ((duplicate & 0xFFFFFC00u) != 0x4E040C00u)
        {
            pattern = default;
            return false;
        }

        var duplicatedVector = (int)(duplicate & 0x1Fu);
        var scalarSource = (int)((duplicate >> 5) & 0x1Fu);
        if (!TryDecodeUnsignedVector128Memory(machineCodes[1], out var constantIsLoad,
                out var constantVector, out var constantBase, out var constantOffset)
            || !constantIsLoad)
        {
            pattern = default;
            return false;
        }

        if (!TryDecodeVectorThreeRegister(machineCodes[2], 0x4EA03400u,
                out var firstMask, out var firstLeft, out var firstRight)
            || firstLeft != constantVector || firstRight != duplicatedVector
            || !TryDecodeVectorThreeRegister(machineCodes[3], 0x4EA03400u,
                out var secondMask, out var secondLeft, out var secondRight)
            || secondLeft != duplicatedVector || secondRight != constantVector)
        {
            pattern = default;
            return false;
        }

        if (!TryDecodeVectorTwoRegister(machineCodes[4], 0x0E612800u,
                out var firstNarrowed, out var firstNarrowSource)
            || firstNarrowSource != firstMask
            || !TryDecodeVectorTwoRegister(machineCodes[5], 0x0E612800u,
                out var secondNarrowed, out var secondNarrowSource)
            || secondNarrowSource != secondMask)
        {
            pattern = default;
            return false;
        }

        // MOV Vd.H[1], Vn.H[1]：只把第二通道替换为反向比较。
        var laneMove = machineCodes[6];
        if ((laneMove & 0xFFFFFC00u) != 0x6E061400u
            || (int)(laneMove & 0x1Fu) != firstNarrowed
            || (int)((laneMove >> 5) & 0x1Fu) != secondNarrowed)
        {
            pattern = default;
            return false;
        }

        if (!TryDecodeUnsignedVector128Memory(machineCodes[7], out var accumulatorIsLoad,
                out var accumulatorVector, out var accumulatorBase, out var accumulatorOffset)
            || !accumulatorIsLoad || accumulatorVector != secondNarrowed)
        {
            pattern = default;
            return false;
        }

        if (!TryDecodeVectorThreeRegister(machineCodes[8], 0x0EA01C00u,
                out var orDestination, out var orLeft, out var orRight)
            || orDestination != accumulatorVector || orLeft != accumulatorVector || orRight != firstNarrowed
            || !TryDecodeVectorTwoRegister(machineCodes[9], 0x0F1F5400u,
                out var shifted, out var shiftSource)
            || shifted != accumulatorVector || shiftSource != accumulatorVector
            || !TryDecodeVectorTwoRegister(machineCodes[10], 0x0E60A800u,
                out var compared, out var compareSource)
            || compared != accumulatorVector || compareSource != accumulatorVector
            || !TryDecodeVectorTwoRegister(machineCodes[11], 0x2E71A800u,
                out var minimumDestination, out var minimumSource)
            || minimumDestination != accumulatorVector || minimumSource != accumulatorVector)
        {
            pattern = default;
            return false;
        }

        var scalarMove = machineCodes[12];
        if ((scalarMove & 0xFFFFFC00u) != 0x1E260000u
            || (int)((scalarMove >> 5) & 0x1Fu) != accumulatorVector)
        {
            pattern = default;
            return false;
        }

        pattern = new Arm64PackedHalfwordPredicatePattern(
            scalarSource,
            (int)(scalarMove & 0x1Fu),
            constantBase,
            constantOffset,
            accumulatorBase,
            accumulatorOffset,
            reverseLaneMask: 1 << 1);
        return true;
    }

    private static bool TryDecodeVectorTwoRegister(
        uint machineCode,
        uint encodingValue,
        out int destination,
        out int source)
    {
        if ((machineCode & 0xFFFFFC00u) != encodingValue)
        {
            destination = 0;
            source = 0;
            return false;
        }

        destination = (int)(machineCode & 0x1Fu);
        source = (int)((machineCode >> 5) & 0x1Fu);
        return true;
    }

    private static bool TryDecodeVectorThreeRegister(
        uint machineCode,
        uint encodingValue,
        out int destination,
        out int left,
        out int right)
    {
        if ((machineCode & 0xFFE0FC00u) != encodingValue)
        {
            destination = 0;
            left = 0;
            right = 0;
            return false;
        }

        destination = (int)(machineCode & 0x1Fu);
        left = (int)((machineCode >> 5) & 0x1Fu);
        right = (int)((machineCode >> 16) & 0x1Fu);
        return true;
    }

    internal static bool TryDecodeVectorFloatingMultiply(
        uint machineCode,
        out int vectorWidthBits,
        out int elementWidthBits,
        out int laneCount,
        out int destinationRegister,
        out int leftRegister,
        out int rightRegister)
    {
        // Advanced SIMD 三同型 FMUL 仅放开 Q、size 与三个寄存器字段。
        const uint encodingMask = 0xBFA0FC00u;
        const uint encodingValue = 0x2E20DC00u;
        if ((machineCode & encodingMask) != encodingValue)
        {
            vectorWidthBits = 0;
            elementWidthBits = 0;
            laneCount = 0;
            destinationRegister = 0;
            leftRegister = 0;
            rightRegister = 0;
            return false;
        }

        vectorWidthBits = (machineCode & (1u << 30)) == 0 ? 64 : 128;
        elementWidthBits = (machineCode & (1u << 22)) == 0 ? 32 : 64;
        if (vectorWidthBits == 64 && elementWidthBits == 64)
        {
            laneCount = 0;
            destinationRegister = 0;
            leftRegister = 0;
            rightRegister = 0;
            return false;
        }

        laneCount = vectorWidthBits / elementWidthBits;
        destinationRegister = (int)(machineCode & 0x1Fu);
        leftRegister = (int)((machineCode >> 5) & 0x1Fu);
        rightRegister = (int)((machineCode >> 16) & 0x1Fu);
        return true;
    }

    internal static bool TryDecodeRecoveredVectorInstruction(
        uint machineCode,
        out Arm64RecoveredVectorInstruction instruction)
    {
        var destinationRegister = (int)(machineCode & 0x1Fu);
        var firstSourceRegister = (int)((machineCode >> 5) & 0x1Fu);
        var secondSourceRegister = (int)((machineCode >> 16) & 0x1Fu);

        // DUP Vd.4H, Wn：将一个32位通用寄存器的低16位复制到四个半字通道。
        if ((machineCode & 0xFFFFFC00u) == 0x0E020C00u)
        {
            instruction = new Arm64RecoveredVectorInstruction(
                Arm64RecoveredVectorOperation.DuplicateInt16,
                destinationRegister,
                firstSourceRegister,
                laneCount: 4,
                elementWidthBits: 16);
            return true;
        }

        // USHLL Vd.4S, Vn.4H, #0：无符号拓宽四个半字通道，不改变数值。
        if ((machineCode & 0xFFFFFC00u) == 0x2F10A400u)
        {
            instruction = new Arm64RecoveredVectorInstruction(
                Arm64RecoveredVectorOperation.WidenUnsignedInt16ToInt32,
                destinationRegister,
                firstSourceRegister,
                laneCount: 4,
                elementWidthBits: 32);
            return true;
        }

        // SHL Vd.4S, Vn.4S, #31：把每个32位通道的低位推进到符号位。
        if ((machineCode & 0xFFFFFC00u) == 0x4F3F5400u)
        {
            instruction = new Arm64RecoveredVectorInstruction(
                Arm64RecoveredVectorOperation.ShiftLeftInt32,
                destinationRegister,
                firstSourceRegister,
                laneCount: 4,
                elementWidthBits: 32,
                immediate: 31);
            return true;
        }

        // CMLT Vd.4S, Vn.4S, #0：逐通道生成全一或全零选择掩码。
        if ((machineCode & 0xFFFFFC00u) == 0x4EA0A800u)
        {
            instruction = new Arm64RecoveredVectorInstruction(
                Arm64RecoveredVectorOperation.CompareLessThanZeroInt32,
                destinationRegister,
                firstSourceRegister,
                laneCount: 4,
                elementWidthBits: 32);
            return true;
        }

        // BSL Vd.16B, Vn.16B, Vm.16B：Vd既是输入掩码也是输出。
        if ((machineCode & 0xFFE0FC00u) == 0x6E601C00u)
        {
            instruction = new Arm64RecoveredVectorInstruction(
                Arm64RecoveredVectorOperation.BitwiseSelect128,
                destinationRegister,
                firstSourceRegister,
                secondSourceRegister,
                laneCount: 16,
                elementWidthBits: 8);
            return true;
        }

        // FMUL Vd.4S, Vn.4S, Vm.S[0]：逐通道乘以指定寄存器的首个单精度元素。
        if ((machineCode & 0xFFE0FC00u) == 0x4F809000u)
        {
            instruction = new Arm64RecoveredVectorInstruction(
                Arm64RecoveredVectorOperation.MultiplyFloat32ByElement,
                destinationRegister,
                firstSourceRegister,
                secondSourceRegister,
                laneCount: 4,
                elementWidthBits: 32,
                immediate: 0);
            return true;
        }

        instruction = default;
        return false;
    }

    internal static bool TryFormatRecoveredVectorInstruction(
        uint machineCode,
        ulong address,
        out string formatted)
    {
        if (!TryDecodeRecoveredVectorInstruction(machineCode, out var instruction))
        {
            formatted = string.Empty;
            return false;
        }

        formatted = instruction.Operation switch
        {
            Arm64RecoveredVectorOperation.DuplicateInt16 =>
                $"0x{address:X8} DUP V{instruction.DestinationRegister}.4H, W{instruction.FirstSourceRegister}",
            Arm64RecoveredVectorOperation.WidenUnsignedInt16ToInt32 =>
                $"0x{address:X8} USHLL V{instruction.DestinationRegister}.4S, V{instruction.FirstSourceRegister}.4H, #0",
            Arm64RecoveredVectorOperation.ShiftLeftInt32 =>
                $"0x{address:X8} SHL V{instruction.DestinationRegister}.4S, V{instruction.FirstSourceRegister}.4S, #{instruction.Immediate}",
            Arm64RecoveredVectorOperation.CompareLessThanZeroInt32 =>
                $"0x{address:X8} CMLT V{instruction.DestinationRegister}.4S, V{instruction.FirstSourceRegister}.4S, #0",
            Arm64RecoveredVectorOperation.BitwiseSelect128 =>
                $"0x{address:X8} BSL V{instruction.DestinationRegister}.16B, V{instruction.FirstSourceRegister}.16B, V{instruction.SecondSourceRegister}.16B",
            Arm64RecoveredVectorOperation.MultiplyFloat32ByElement =>
                $"0x{address:X8} FMUL V{instruction.DestinationRegister}.4S, V{instruction.FirstSourceRegister}.4S, V{instruction.SecondSourceRegister}.S[{instruction.Immediate}]",
            _ => throw new ArgumentOutOfRangeException(nameof(instruction.Operation))
        };
        return true;
    }

    internal static ulong ResolveInstructionAddress(
        ulong methodStart,
        int instructionIndex,
        Arm64Mnemonic mnemonic,
        ulong reportedAddress)
    {
        if (reportedAddress != 0 || mnemonic != Arm64Mnemonic.INVALID)
            return reportedAddress;

        if (instructionIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(instructionIndex));

        return checked(methodStart + checked((ulong)instructionIndex * sizeof(uint)));
    }

    internal static bool IsBranchOutsideMethod(ulong target, ulong methodStart, int methodLength)
    {
        if (methodLength < 0)
            throw new ArgumentOutOfRangeException(nameof(methodLength));

        var methodEndExclusive = checked(methodStart + (ulong)methodLength);
        return target < methodStart || target >= methodEndExclusive;
    }

    /// <summary>
    /// 从方法体内部跳回自身首地址会重新执行原生序言，只可能是已完成当前栈帧恢复后的自尾调用。
    /// 普通循环必须跳到序言之后的局部标签；分支指令自身位于首地址时不构成递归证据。
    /// </summary>
    internal static bool IsSelfTailBranch(ulong target, ulong methodStart, ulong branchAddress)
        => target == methodStart && branchAddress > methodStart;

    internal static bool? ShouldInvertZeroFlag(Arm64ConditionCode conditionCode)
    {
        return conditionCode switch
        {
            Arm64ConditionCode.EQ => false,
            Arm64ConditionCode.NE => true,
            _ => null
        };
    }

    internal static bool? ShouldInvertSignFlag(Arm64ConditionCode conditionCode)
    {
        return conditionCode switch
        {
            Arm64ConditionCode.MI => false,
            Arm64ConditionCode.PL => true,
            _ => null
        };
    }

    internal static bool IsScalarLoadMnemonic(Arm64Mnemonic mnemonic)
    {
        return mnemonic is Arm64Mnemonic.LDR
            or Arm64Mnemonic.LDRB
            or Arm64Mnemonic.LDRH
            or Arm64Mnemonic.LDRSW
            or Arm64Mnemonic.LDUR
            or Arm64Mnemonic.LDURH;
    }

    internal static bool IsScalarStoreMnemonic(Arm64Mnemonic mnemonic)
    {
        return mnemonic is Arm64Mnemonic.STR
            or Arm64Mnemonic.STRB
            or Arm64Mnemonic.STRH
            or Arm64Mnemonic.STUR
            or Arm64Mnemonic.STURH;
    }

    /// <summary>
    /// 返回标量存储真实覆盖的位数；字节与半字由操作码决定，其余由源寄存器宽度决定。
    /// </summary>
    internal static int GetScalarStoreWidthBits(
        Arm64Mnemonic mnemonic,
        Arm64Register sourceRegister)
    {
        return mnemonic switch
        {
            Arm64Mnemonic.STRB => 8,
            Arm64Mnemonic.STRH or Arm64Mnemonic.STURH => 16,
            Arm64Mnemonic.STR or Arm64Mnemonic.STUR =>
                Arm64RegisterHelper.SizeBytes(sourceRegister) * 8,
            _ => 0
        };
    }

    internal static bool IsExactlyRepresentableMovi(Arm64OperandKind immediateKind, long immediate)
    {
        return immediateKind == Arm64OperandKind.Immediate && immediate == 0;
    }

    internal static bool TryDecodeCmnSignedThreshold(
        Arm64Register destinationRegister,
        Arm64OperandKind rightOperandKind,
        long rightImmediate,
        out int registerWidthBits,
        out long signedThreshold)
    {
        // ADDS WZR/XZR, Rn, #imm 是 CMN 别名；其后继有符号条件等价于 Rn 与 -imm 的比较。
        // 只接入架构允许的非负立即数，避免把普通寄存器加法或畸形反汇编误写成比较。
        if (!Arm64RegisterHelper.IsZeroRegister(destinationRegister)
            || rightOperandKind != Arm64OperandKind.Immediate
            || rightImmediate < 0)
        {
            registerWidthBits = 0;
            signedThreshold = 0;
            return false;
        }

        registerWidthBits = destinationRegister == Arm64Register.W31 ? 32 : 64;
        signedThreshold = checked(-rightImmediate);
        return true;
    }

    internal static bool CanEmitScalarFloatingNegate(
        Arm64OperandKind destinationKind,
        Arm64Register destinationRegister,
        Arm64OperandKind sourceKind,
        Arm64Register sourceRegister)
    {
        if (destinationKind != Arm64OperandKind.Register
            || sourceKind != Arm64OperandKind.Register
            || !TryGetFloatingPointPrecisionBits(destinationRegister, out var destinationBits)
            || !TryGetFloatingPointPrecisionBits(sourceRegister, out var sourceBits))
            return false;

        return destinationBits == sourceBits;
    }

    /// <summary>
    /// 验证一组 ARM64 标量浮点操作数均为寄存器且精度完全一致。FABS、FABD 与 FMAXNM
    /// 共用该门，避免每条指令分别实现并逐渐产生不同的宽度判定规则。
    /// </summary>
    internal static bool TryGetMatchingScalarFloatingWidth(
        IReadOnlyList<(Arm64OperandKind Kind, Arm64Register Register)> operands,
        out int widthBits)
    {
        widthBits = 0;
        if (operands.Count == 0)
            return false;

        foreach (var (kind, register) in operands)
        {
            if (kind != Arm64OperandKind.Register
                || !TryGetFloatingPointPrecisionBits(register, out var operandWidth))
                return false;

            if (widthBits == 0)
                widthBits = operandWidth;
            else if (widthBits != operandWidth)
                return false;
        }

        return true;
    }

    internal static bool IsUnconditionalBranchCode(Arm64ConditionCode conditionCode)
    {
        return conditionCode is Arm64ConditionCode.NONE
            or Arm64ConditionCode.AL
            or Arm64ConditionCode.NV;
    }

    internal static OpCode? GetIndirectBranchOpCode(Arm64Mnemonic mnemonic)
    {
        return mnemonic switch
        {
            Arm64Mnemonic.BLR => OpCode.IndirectCall,
            Arm64Mnemonic.BR => OpCode.IndirectJump,
            _ => null
        };
    }

    internal static OpCode? GetRelationalBranchOpCode(Arm64ConditionCode conditionCode)
    {
        return conditionCode switch
        {
            Arm64ConditionCode.GT => OpCode.CheckGreater,
            Arm64ConditionCode.LT => OpCode.CheckLess,
            Arm64ConditionCode.GE => OpCode.CheckGreaterOrEqual,
            Arm64ConditionCode.LE => OpCode.CheckLessOrEqual,
            Arm64ConditionCode.HI => OpCode.CheckGreaterUnsigned,
            Arm64ConditionCode.CC => OpCode.CheckLessUnsigned,
            Arm64ConditionCode.CS => OpCode.CheckGreaterOrEqualUnsigned,
            Arm64ConditionCode.LS => OpCode.CheckLessOrEqualUnsigned,
            _ => null
        };
    }

    internal static OpCode? GetConditionalSetRelationalOpCode(
        Arm64ConditionCode conditionCode,
        Arm64FlagState flagState)
    {
        return flagState is Arm64FlagState.Comparison or Arm64FlagState.FloatingComparison
            ? GetRelationalBranchOpCode(conditionCode)
            : null;
    }

    internal static OpCode? GetConditionalComparisonOpCode(Arm64ConditionCode conditionCode)
    {
        return conditionCode switch
        {
            Arm64ConditionCode.EQ => OpCode.CheckEqual,
            Arm64ConditionCode.NE => OpCode.CheckNotEqual,
            _ => GetRelationalBranchOpCode(conditionCode)
        };
    }

    internal static bool TryEvaluateConditionFromNzcv(
        Arm64ConditionCode conditionCode,
        long nzcv,
        out bool result)
    {
        var negative = (nzcv & 0b1000) != 0;
        var zero = (nzcv & 0b0100) != 0;
        var carry = (nzcv & 0b0010) != 0;
        var overflow = (nzcv & 0b0001) != 0;
        result = conditionCode switch
        {
            Arm64ConditionCode.EQ => zero,
            Arm64ConditionCode.NE => !zero,
            Arm64ConditionCode.CS => carry,
            Arm64ConditionCode.CC => !carry,
            Arm64ConditionCode.MI => negative,
            Arm64ConditionCode.PL => !negative,
            Arm64ConditionCode.VS => overflow,
            Arm64ConditionCode.VC => !overflow,
            Arm64ConditionCode.HI => carry && !zero,
            Arm64ConditionCode.LS => !carry || zero,
            Arm64ConditionCode.GE => negative == overflow,
            Arm64ConditionCode.LT => negative != overflow,
            Arm64ConditionCode.GT => !zero && negative == overflow,
            Arm64ConditionCode.LE => zero || negative != overflow,
            Arm64ConditionCode.AL or Arm64ConditionCode.NV => true,
            _ => false
        };
        return conditionCode != Arm64ConditionCode.NONE;
    }

    internal static ulong ResolveAdrAddress(ulong instructionAddress, long pcRelativeImmediate)
        => unchecked((ulong)(unchecked((long)instructionAddress) + pcRelativeImmediate));

    internal static OpCode? GetConditionalFalseTransformOpCode(Arm64Mnemonic mnemonic)
    {
        return mnemonic switch
        {
            Arm64Mnemonic.CSNEG => OpCode.Negate,
            Arm64Mnemonic.CSINV => OpCode.Not,
            _ => null
        };
    }

    /// <summary>
    /// 由显式 CMP 上界和条件分支创建跳转表边界。case 数量只取自控制流，默认目标只取自边界分支。
    /// </summary>
    internal static bool TryCreateJumpTableBounds(
        long lowerBound,
        long maximumAdjustedIndex,
        ulong defaultTarget,
        ulong methodStart,
        ulong methodEnd,
        out Arm64JumpTableBounds bounds)
    {
        bounds = default;
        if (methodStart >= methodEnd
            || maximumAdjustedIndex < 0
            || maximumAdjustedIndex >= MaximumJumpTableEntryCount
            || defaultTarget < methodStart
            || defaultTarget >= methodEnd
            || (defaultTarget - methodStart) % sizeof(uint) != 0)
            return false;

        try
        {
            // 同时验证 case 标签上界，防止 lower + count 在命名或后续 CIL 恢复时溢出。
            _ = checked(lowerBound + maximumAdjustedIndex);
            bounds = new Arm64JumpTableBounds(
                lowerBound,
                checked((int)maximumAdjustedIndex + 1),
                defaultTarget);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>
    /// 按一字节或小端两字节表项解析分支目标。读取长度、目标范围与四字节对齐任一不闭合时整体失败。
    /// </summary>
    internal static bool TryResolveJumpTableTargets(
        ReadOnlySpan<byte> table,
        int entryWidthBytes,
        int entryCount,
        ulong branchBaseAddress,
        ulong methodStart,
        ulong methodEnd,
        out ulong[] targets)
    {
        targets = [];
        if (entryWidthBytes is not (sizeof(byte) or sizeof(ushort))
            || entryCount <= 0
            || entryCount > MaximumJumpTableEntryCount
            || methodStart >= methodEnd)
            return false;

        int requiredByteCount;
        try
        {
            requiredByteCount = checked(entryWidthBytes * entryCount);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (table.Length < requiredByteCount)
            return false;

        var resolved = new ulong[entryCount];
        for (var index = 0; index < entryCount; index++)
        {
            ulong encodedOffset = entryWidthBytes == sizeof(byte)
                ? table[index]
                : BinaryPrimitives.ReadUInt16LittleEndian(
                    table.Slice(index * sizeof(ushort), sizeof(ushort)));

            ulong target;
            try
            {
                target = checked(branchBaseAddress + checked(encodedOffset * sizeof(uint)));
            }
            catch (OverflowException)
            {
                return false;
            }

            if (target < methodStart
                || target >= methodEnd
                || (target - methodStart) % sizeof(uint) != 0)
                return false;

            resolved[index] = target;
        }

        targets = resolved;
        return true;
    }

    /// <summary>
    /// 保留既有字节表测试入口；真实恢复统一走带显式宽度和显式数量的解析器。
    /// </summary>
    internal static bool TryResolveByteJumpTableTargets(
        ReadOnlySpan<byte> table,
        ulong branchBaseAddress,
        ulong methodStart,
        ulong methodEnd,
        out ulong[] targets)
        => TryResolveJumpTableTargets(
            table,
            sizeof(byte),
            table.Length,
            branchBaseAddress,
            methodStart,
            methodEnd,
            out targets);

    internal static bool CanEmitCarryZeroCondition(
        Arm64ConditionCode conditionCode,
        Arm64FlagState flagState)
    {
        // CCMP 的立即 NZCV 分支与真实比较分支都能精确物化 C、Z；仅放行只依赖这两位的无符号条件。
        return flagState == Arm64FlagState.CarryAndZero
            && conditionCode is Arm64ConditionCode.HI
                or Arm64ConditionCode.LS
                or Arm64ConditionCode.CS
                or Arm64ConditionCode.CC;
    }

    internal static bool CanEmitConditionalBranch(
        Arm64ConditionCode conditionCode,
        Arm64FlagState flagState)
    {
        if (flagState == Arm64FlagState.None)
            return false;

        if (ShouldInvertZeroFlag(conditionCode) is not null)
            return true;

        // 整数 CMP/SUBS 已显式物化减法结果的 N 标志，因此 MI/PL 可以无损读取或反转该标志。
        if (flagState == Arm64FlagState.Comparison &&
            ShouldInvertSignFlag(conditionCode) is not null)
            return true;

        // FCMP 的 MI/PL 分别精确表示 N == 1/0；在 ISIL 中等价于“小于”与“不是小于”，并保留NaN语义。
        if (flagState == Arm64FlagState.FloatingComparison &&
            ShouldInvertSignFlag(conditionCode) is not null)
            return true;

        return (flagState is Arm64FlagState.Comparison or Arm64FlagState.FloatingComparison) &&
               GetRelationalBranchOpCode(conditionCode) is not null;
    }

    public override BinarySlice GetRawBytesForMethod(MethodAnalysisContext context, bool isAttributeGenerator)
    {
        var binary = context.AppContext.Binary;

        // 普通托管方法使用相邻虚拟地址确定动态边界，再由二进制格式映射器得到文件区间。
        if (context is not ConcreteGenericMethodAnalysisContext &&
            Arm64MethodBodyReader.TryReadManagedMethodBody(context.AppContext, context.UnderlyingPointer, out var body))
            return body;

        var result = NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(context.AppContext, context.UnderlyingPointer);
        var lastInsn = result.LastValid();

        var start = (int)binary.MapVirtualAddressToRaw(context.UnderlyingPointer);
        // Map the last instruction (always within segment) and add 4 (ARM64 instruction size).
        // This avoids mapping endVa which may land exactly at a segment boundary gap.
        var end = (int)binary.MapVirtualAddressToRaw(lastInsn.Address) + 4;

        //Sanity check
        if (start < 0 || end < 0 || start >= binary.RawLength || end >= binary.RawLength)
            throw new Exception($"Failed to map virtual address 0x{context.UnderlyingPointer:X} to raw address for method {context!.DeclaringType?.FullName}/{context.Name} - start: 0x{start:X}, end: 0x{end:X} are out of bounds for length {binary.RawLength}.");

        return new BinarySlice(binary, start, end - start);
    }

    public override List<IOperand> GetParameterOperandsFromMethod(MethodAnalysisContext context)
    {
        // Is this correct (?)
        return GetArgumentOperandsForCall(context);
    }

    public override List<Instruction> GetIsilFromMethod(MethodAnalysisContext context)
    {
        var insns = NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(context.AppContext, context.UnderlyingPointer);

        if (adrpOffsets == null!) // initializers for ThreadStatic fields only run on the first thread
            adrpOffsets = new();
        else
            adrpOffsets.Clear();

        var instructions = new List<Instruction>();
        var addresses = new List<ulong>();
        var flagState = Arm64FlagState.None;
        var conditionalComparisonFallbackNzcv = 0L;
        var jumpTableDispatches = RecoverJumpTableDispatches(insns, context);
        var jumpTableTargets = new HashSet<ulong>(jumpTableDispatches.Values
            .SelectMany(dispatch => dispatch.CaseTargets.Append(dispatch.Bounds.DefaultTarget)));

        for (var index = 0; index < insns.Count; index++)
        {
            var instruction = insns[index];
            var address = ResolveInstructionAddress(
                context.UnderlyingPointer,
                index,
                instruction.Mnemonic,
                instruction.Address);

            // 跳转表目标可能落在普通转换不产出 ISIL 的原生指令上；显式锚点保证地址修复不漂移。
            if (jumpTableTargets.Contains(address))
            {
                instructions.Add(new Instruction(instructions.Count, OpCode.Nop));
                addresses.Add(address);
            }

            if (jumpTableDispatches.TryGetValue(index, out var jumpTableDispatch))
            {
                AppendJumpTableDispatch(
                    jumpTableDispatch,
                    instructions,
                    addresses);
                // 只替换最终 BR；ADR/LDR/ADD 继续走原转换器，保留 scratch 寄存器副作用。
                continue;
            }

            // 若向量组合窗口内部存在分支目标，则保留逐指令地址，禁止一次合并吞掉目标锚点。
            var packedPatternContainsJumpTarget = jumpTableTargets.Count > 0
                && Enumerable.Range(index, Math.Min(13, insns.Count - index))
                    .Select(candidateIndex => ResolveInstructionAddress(
                        context.UnderlyingPointer,
                        candidateIndex,
                        insns[candidateIndex].Mnemonic,
                        insns[candidateIndex].Address))
                    .Any(jumpTableTargets.Contains);
            if (!packedPatternContainsJumpTarget
                && TryEmitPackedHalfwordPredicatePattern(
                    insns,
                    index,
                    context,
                    instructions,
                    addresses,
                    out var consumedInstructionCount))
            {
                index += consumedInstructionCount - 1;
                continue;
            }

            ConvertInstructionStatement(
                instruction,
                address,
                instructions,
                addresses,
                context,
                ref flagState,
                ref conditionalComparisonFallbackNzcv);
        }

        PruneUnconsumedReturnProjections(instructions, context);

        // fix branches
        for (var i = 0; i < instructions.Count; i++)
        {
            var instruction = instructions[i];

            if (instruction.OpCode != OpCode.Jump && instruction.OpCode != OpCode.ConditionalJump)
                continue;

            var targetAddress = ((Immediate)instruction.Operands[0]).UnsignedValue;
            var targetIndex = addresses.FindIndex(addr => addr == targetAddress);

            if (targetIndex == -1)
            {
                instruction.OpCode = OpCode.Invalid;
                instruction.SetOperands(new StringLiteral($"Jump target not found in method: 0x{targetAddress:X4}"));
                continue;
            }

            var targetInstruction = instructions[targetIndex];

            instruction.SetOperand(0, targetInstruction);
        }

        adrpOffsets.Clear();
        return instructions;
    }

    /// <summary>
    /// 对所有调用后聚合体投影执行统一的消费者硬门。投影只是把原生 ABI 分槽重新映射到
    /// 托管字段的候选；缺少确定后继消费者时必须恢复完整值类型语义。
    /// </summary>
    private static void PruneUnconsumedReturnProjections(
        IReadOnlyList<Instruction> instructions,
        MethodAnalysisContext context)
    {
        PruneUnconsumedHomogeneousFloatingReturnProjections(instructions, context);
        PruneUnconsumedReferenceRegisterReturnProjections(instructions, context);
    }

    /// <summary>
    /// HFA返回后的V0既代表完整值类型，也可被后续标量指令当作第一个字段读取。
    /// 只有观察到字段分量消费者时才保留调用后投影；若调用结果直接通过128位存储写入托管字段，
    /// 则保留完整聚合体，避免把Color等值类型错误降级为第一个Single字段。
    /// </summary>
    private static void PruneUnconsumedHomogeneousFloatingReturnProjections(
        IReadOnlyList<Instruction> instructions,
        MethodAnalysisContext context)
    {
        for (var callIndex = 0; callIndex < instructions.Count; callIndex++)
        {
            var call = instructions[callIndex];
            if (call.OpCode != OpCode.Call
                || call.Operands.Count < 2
                || call.Operands[0] is not Immediate target
                || !context.AppContext.MethodsByAddress.TryGetValue(target.UnsignedValue, out var methods)
                || methods.Count != 1)
                continue;

            var projections = Arm64CallingConventionResolver.ReturnProjections(methods[0]);
            if (projections.Count == 0
                || !MatchesReturnProjectionSequence(instructions, callIndex + 1, projections))
                continue;

            var consumerStart = callIndex + 1 + projections.Count;
            if (HasHomogeneousFloatingComponentConsumer(instructions, consumerStart, projections))
            {
                callIndex += projections.Count;
                continue;
            }

            for (var projectionIndex = callIndex + 1; projectionIndex < consumerStart; projectionIndex++)
            {
                instructions[projectionIndex].OpCode = OpCode.Nop;
                instructions[projectionIndex].SetOperands();
            }
            callIndex += projections.Count;
        }
    }

    private static bool MatchesReturnProjectionSequence(
        IReadOnlyList<Instruction> instructions,
        int startIndex,
        IReadOnlyList<(Register Destination, MemoryOperand Source)> projections)
    {
        if (startIndex < 0 || startIndex + projections.Count > instructions.Count)
            return false;

        for (var projectionIndex = 0; projectionIndex < projections.Count; projectionIndex++)
        {
            var expected = projections[projectionIndex];
            var actual = instructions[startIndex + projectionIndex];
            if (actual is not
                {
                    OpCode: OpCode.Move,
                    Operands.Count: 2
                }
                || actual.Operands[0] is not Register destination
                || actual.Operands[1] is not MemoryOperand source
                || destination.Number != expected.Destination.Number
                || source.Base is not Register sourceBase
                || expected.Source.Base is not Register expectedBase
                || sourceBase.Number != expectedBase.Number
                || source.Index != null
                || source.Addend != expected.Source.Addend)
                return false;
        }

        return true;
    }

    /// <summary>
    /// 引用聚合体返回既可能被原生代码按 X0/X1 字段槽消费，也可能继续作为完整值类型使用。
    /// 仅在后继指令给出确定字段证据时保留投影：高位返回槽被读取，或已解析托管调用以同一
    /// 物理寄存器接收精确字段类型。原始调用的宽寄存器快照、隐藏 MethodInfo 和完整聚合体
    /// 接收者都不构成字段证据。
    /// </summary>
    private static void PruneUnconsumedReferenceRegisterReturnProjections(
        IReadOnlyList<Instruction> instructions,
        MethodAnalysisContext context)
    {
        for (var callIndex = 0; callIndex < instructions.Count; callIndex++)
        {
            var call = instructions[callIndex];
            if (call.OpCode != OpCode.Call
                || call.Operands.Count < 2
                || call.Operands[0] is not Immediate target
                || !context.AppContext.MethodsByAddress.TryGetValue(target.UnsignedValue, out var methods))
                continue;

            var calledMethod = methods.Count == 1
                ? methods[0]
                : Arm64CallingConventionResolver.TryGetReferenceRegisterAggregateReturnPrototype(
                    methods,
                    out var sharedPrototype)
                    ? sharedPrototype
                    : null;
            if (calledMethod == null)
                continue;

            var projections = Arm64CallingConventionResolver
                .ReferenceRegisterReturnProjections(calledMethod);
            if (projections.Count == 0
                || !MatchesReturnProjectionSequence(instructions, callIndex + 1, projections))
                continue;

            var consumerStart = callIndex + 1 + projections.Count;
            if (HasReferenceRegisterAggregateFieldConsumer(
                    instructions,
                    consumerStart,
                    calledMethod,
                    context.AppContext.MethodsByAddress))
            {
                callIndex += projections.Count;
                continue;
            }

            for (var projectionIndex = callIndex + 1; projectionIndex < consumerStart; projectionIndex++)
            {
                instructions[projectionIndex].OpCode = OpCode.Nop;
                instructions[projectionIndex].SetOperands();
            }
            callIndex += projections.Count;
        }
    }

    /// <summary>
    /// 从投影序列之后扫描引用字段的确定消费者。两槽聚合体的 X1 没有完整值载体含义，
    /// 因此普通指令读取 X1 即为字段证据；X0 只有在唯一托管调用声明了精确字段类型时才成立。
    /// 所有调用均会改写易失 X 寄存器，未匹配的调用会终止当前候选的数据流。
    /// </summary>
    internal static bool HasReferenceRegisterAggregateFieldConsumer(
        IReadOnlyList<Instruction> instructions,
        int startIndex,
        MethodAnalysisContext returnMethod,
        IReadOnlyDictionary<ulong, List<MethodAnalysisContext>> methodsByAddress)
    {
        if (!Arm64CallingConventionResolver.TryGetReferenceRegisterAggregateFields(
                returnMethod.ReturnType,
                out var fields))
            return false;

        var activeFields = fields.ToDictionary(
            field => $"X{field.Offset / sizeof(long)}",
            field => field,
            StringComparer.Ordinal);

        for (var instructionIndex = startIndex;
             instructionIndex < instructions.Count && activeFields.Count > 0;
             instructionIndex++)
        {
            var instruction = instructions[instructionIndex];
            if (instruction.OpCode is OpCode.Call or OpCode.CallVoid)
            {
                if (instruction.Operands[0] is Immediate target
                    && methodsByAddress.TryGetValue(target.UnsignedValue, out var callees)
                    && callees.Count == 1
                    && activeFields.Any(active =>
                        EnumerateSourceRegisters(instruction)
                            .Any(register => register.Name == active.Key)
                        && Arm64CallingConventionResolver.UsesGeneralRegisterForManagedArgumentOfType(
                            callees[0],
                            active.Key,
                            active.Value.Field.FieldType)))
                    return true;

                activeFields.Clear();
                continue;
            }

            if (instruction.OpCode == OpCode.IndirectCall)
            {
                activeFields.Clear();
                continue;
            }

            var readRegisterNames = new HashSet<string>(
                EnumerateSourceRegisters(instruction).Select(register => register.Name),
                StringComparer.Ordinal);
            if (activeFields.Keys.Any(registerName =>
                    registerName != "X0" && readRegisterNames.Contains(registerName)))
                return true;

            if (TryGetDirectDestinationRegister(instruction, out var destination))
                activeFields.Remove(destination.Name);

            if (instruction.OpCode is OpCode.Return or OpCode.Throw or OpCode.IndirectJump)
                activeFields.Clear();
        }

        return false;
    }

    /// <summary>
    /// 判断投影寄存器在下一次定义前是否以字段分量身份被读取。
    /// V1及更高寄存器天然只承载后续字段；V0则根据HFA实参、标量运算、标量存储或直接标量调用区分。
    /// </summary>
    internal static bool HasHomogeneousFloatingComponentConsumer(
        IReadOnlyList<Instruction> instructions,
        int startIndex,
        IReadOnlyList<(Register Destination, MemoryOperand Source)> projections)
    {
        if (IsCompleteHomogeneousFloatingAggregateStore(instructions, startIndex, projections))
            return false;

        var activeRegisters = new HashSet<int>(projections
            .Select(projection => projection.Destination.Number));
        var aggregateCarrier = ((Register)projections[^1].Source.Base!).Number;

        for (var instructionIndex = startIndex;
             instructionIndex < instructions.Count && activeRegisters.Count > 0;
             instructionIndex++)
        {
            var instruction = instructions[instructionIndex];
            var readRegisters = new HashSet<int>(EnumerateSourceRegisters(instruction)
                .Select(register => register.Number));

            if (readRegisters.Any(register =>
                    register != aggregateCarrier && activeRegisters.Contains(register))
                || activeRegisters.Contains(aggregateCarrier)
                && ReadsAggregateCarrierAsScalar(instruction, aggregateCarrier))
                return true;

            if (TryGetDirectDestinationRegister(instruction, out var destination))
                activeRegisters.Remove(destination.Number);

            // 分支前尚未得到确定消费者时保守保留投影，跨边数据流交给后续CFG/SSA处理。
            if (instruction.OpCode is OpCode.Jump or OpCode.ConditionalJump)
                return true;
        }

        return false;
    }

    /// <summary>
    /// 识别编译器把一个HFA返回值按字段拆成连续标量存储的完整聚合体赋值。
    /// 所有V分量必须各出现一次、写入同一基址且“目标偏移减源字段偏移”完全一致；
    /// 缺字段、重复字段、额外消费者和基址漂移均保持标量投影。
    /// </summary>
    private static bool IsCompleteHomogeneousFloatingAggregateStore(
        IReadOnlyList<Instruction> instructions,
        int startIndex,
        IReadOnlyList<(Register Destination, MemoryOperand Source)> projections)
    {
        var fieldOffsets = projections.ToDictionary(
            projection => projection.Destination.Number,
            projection => projection.Source.Addend);
        var remaining = new HashSet<int>(fieldOffsets.Keys);
        int? destinationBase = null;
        long? aggregateOffset = null;

        for (var instructionIndex = startIndex;
             instructionIndex < instructions.Count && remaining.Count > 0;
             instructionIndex++)
        {
            var instruction = instructions[instructionIndex];
            var componentReads = EnumerateSourceRegisters(instruction)
                .Select(register => register.Number)
                .Where(remaining.Contains)
                .Distinct()
                .ToArray();

            if (componentReads.Length == 0)
            {
                if (TryGetDirectDestinationRegister(instruction, out var destination)
                    && remaining.Contains(destination.Number))
                    return false;
                if (instruction.OpCode is OpCode.Call or OpCode.CallVoid or OpCode.IndirectCall
                    or OpCode.Jump or OpCode.ConditionalJump or OpCode.Return or OpCode.Throw)
                    return false;
                continue;
            }

            if (componentReads.Length != 1
                || instruction is not
                {
                    OpCode: OpCode.Move,
                    Operands.Count: 2
                }
                || instruction.Operands[0] is not MemoryOperand
                {
                    Base: Register memoryBase,
                    Index: null,
                    Scale: 0
                } memory
                || instruction.Operands[1] is not Register source
                || source.Number != componentReads[0]
                || instruction.MemoryAccessWidthBits is not (32 or 64))
                return false;

            long candidateAggregateOffset;
            try
            {
                candidateAggregateOffset = checked(memory.Addend - fieldOffsets[source.Number]);
            }
            catch (OverflowException)
            {
                return false;
            }
            destinationBase ??= memoryBase.Number;
            aggregateOffset ??= candidateAggregateOffset;
            if (destinationBase.Value != memoryBase.Number
                || aggregateOffset.Value != candidateAggregateOffset)
                return false;

            remaining.Remove(source.Number);
        }

        return remaining.Count == 0;
    }

    private static bool ReadsAggregateCarrierAsScalar(Instruction instruction, int aggregateCarrier)
    {
        foreach (var aggregate in instruction.Operands.OfType<HomogeneousFloatingAggregateArgument>())
            if (aggregate.Components
                .SelectMany(EnumerateOperandRegisters)
                .Any(register => register.Number == aggregateCarrier))
                return true;

        if (instruction.OpCode is OpCode.Call or OpCode.CallVoid or OpCode.IndirectCall)
        {
            var argumentBase = instruction.OpCode == OpCode.CallVoid ? 1 : 2;
            if (instruction.Operands
                .Skip(argumentBase)
                .OfType<Register>()
                .Any(register => register.Number == aggregateCarrier))
                return true;
        }

        if (instruction.OpCode == OpCode.Move
            && instruction.Operands.Count > 1
            && instruction.Operands[1] is Register moveSource
            && moveSource.Number == aggregateCarrier)
        {
            if (instruction.MemoryAccessWidthBits is 32 or 64)
                return true;
            if (instruction.MemoryAccessWidthBits >= 128)
                return false;
            return instruction.Operands[0] is Register;
        }

        return instruction.OpCode is
            OpCode.ConditionalSelect or OpCode.Add or OpCode.Subtract or OpCode.Multiply
            or OpCode.Divide or OpCode.ShiftLeft or OpCode.ShiftRight or OpCode.ShiftRightUnsigned
            or OpCode.And or OpCode.Or or OpCode.Xor or OpCode.Not or OpCode.Negate
            or OpCode.AbsoluteNumber or OpCode.AbsoluteDifference or OpCode.MaximumNumber
            or OpCode.ConvertFloatingPointPrecision or OpCode.ConvertFloatToSignedInteger
            or OpCode.ConvertSignedIntegerToFloat or OpCode.ConvertSignedIntegerWidth
            or OpCode.ReinterpretIntegerBitsAsFloat
            or OpCode.ReinterpretFloatBitsAsInteger or OpCode.RoundFloatTowardPositiveInfinity
            or OpCode.RoundFloatTowardNegativeInfinity
            or >= OpCode.CheckEqual and <= OpCode.CheckLessOrEqualUnsigned;
    }

    /// <summary>
    /// 在CFG构建前从原始操作数安全枚举读取寄存器。部分尚未恢复的原生指令会以缺操作数的
    /// Move或比较形态存在，此阶段不得调用要求完整形态的Instruction.Sources。
    /// </summary>
    private static IEnumerable<Register> EnumerateSourceRegisters(Instruction instruction)
    {
        var destinationIndex = TryGetDestinationOperandIndex(instruction, out var index)
            ? index
            : -1;
        for (var operandIndex = 0; operandIndex < instruction.Operands.Count; operandIndex++)
        {
            var operand = instruction.Operands[operandIndex];
            // 直接寄存器目标只写不读；内存目标仍需枚举其基址和索引。
            if (operandIndex == destinationIndex && operand is Register)
                continue;

            foreach (var register in EnumerateOperandRegisters(operand))
                yield return register;
        }
    }

    private static bool TryGetDirectDestinationRegister(
        Instruction instruction,
        out Register destination)
    {
        if (TryGetDestinationOperandIndex(instruction, out var destinationIndex)
            && instruction.Operands[destinationIndex] is Register register)
        {
            destination = register;
            return true;
        }

        destination = default;
        return false;
    }

    /// <summary>
    /// 安全取得目标操作数位置；缺少目标槽的畸形指令返回false并保持保守读取语义。
    /// </summary>
    private static bool TryGetDestinationOperandIndex(
        Instruction instruction,
        out int destinationIndex)
    {
        destinationIndex = instruction.OpCode switch
        {
            OpCode.Call or OpCode.IndirectCall => 1,
            OpCode.Move or OpCode.Phi or OpCode.ConditionalSelect
                or OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide
                or OpCode.ShiftLeft or OpCode.ShiftRight or OpCode.ShiftRightUnsigned or OpCode.And or OpCode.Or
                or OpCode.Xor or OpCode.Not or OpCode.Negate or OpCode.AbsoluteNumber
                or OpCode.AbsoluteDifference or OpCode.MaximumNumber
                or OpCode.ConvertFloatingPointPrecision or OpCode.ConvertFloatToSignedInteger
                or OpCode.ConvertSignedIntegerToFloat or OpCode.ConvertSignedIntegerWidth
                or OpCode.ReinterpretIntegerBitsAsFloat
                or OpCode.ReinterpretFloatBitsAsInteger or OpCode.VectorDuplicate
                or OpCode.VectorWidenUnsignedInt16ToInt32 or OpCode.VectorShiftLeft
                or OpCode.VectorCompareLessThanZero or OpCode.VectorBitwiseSelect
                or OpCode.VectorMultiplyByElement or OpCode.VectorAllLanesPredicate
                or OpCode.VectorExtractUnsignedInt16 or OpCode.RoundFloatTowardPositiveInfinity
                or OpCode.RoundFloatTowardNegativeInfinity
                or OpCode.CheckEqual or OpCode.CheckGreater or OpCode.CheckLess
                or OpCode.CheckNotEqual or OpCode.CheckGreaterOrEqual or OpCode.CheckLessOrEqual
                or OpCode.CheckGreaterUnsigned or OpCode.CheckLessUnsigned
                or OpCode.CheckGreaterOrEqualUnsigned or OpCode.CheckLessOrEqualUnsigned
                or OpCode.Newobj or OpCode.Box or OpCode.Unbox or OpCode.CastClass or OpCode.IsInst => 0,
            _ => -1
        };
        return destinationIndex >= 0 && instruction.Operands.Count > destinationIndex;
    }

    private static IEnumerable<Register> EnumerateOperandRegisters(IOperand operand)
    {
        switch (operand)
        {
            case Register register:
                yield return register;
                break;
            case MemoryOperand { Base: Register baseRegister, Index: Register indexRegister }:
                yield return baseRegister;
                yield return indexRegister;
                break;
            case MemoryOperand { Base: Register baseRegister }:
                yield return baseRegister;
                break;
            case MemoryOperand { Index: Register indexRegister }:
                yield return indexRegister;
                break;
            case AddressOf { Target: Register addressed }:
                yield return addressed;
                break;
            case HomogeneousFloatingAggregateArgument aggregate:
                foreach (var component in aggregate.Components)
                foreach (var componentRegister in EnumerateOperandRegisters(component))
                    yield return componentRegister;
                break;
        }
    }

    /// <summary>
    /// 单次扫描全部 BR；一个候选失败不会阻断后续站点。
    /// </summary>
    internal static IReadOnlyList<Arm64JumpTableMatch> FindJumpTableMatches(
        IReadOnlyList<Arm64Instruction> nativeInstructions,
        ulong methodStart,
        ulong methodEnd)
    {
        var result = new List<Arm64JumpTableMatch>();
        if (methodStart >= methodEnd)
            return result;

        for (var branchIndex = 3; branchIndex < nativeInstructions.Count; branchIndex++)
        {
            if (TryMatchJumpTableDispatch(
                    nativeInstructions,
                    branchIndex,
                    methodStart,
                    methodEnd,
                    out var match))
                result.Add(match);
        }

        return result;
    }

    /// <summary>
    /// 只匹配由 SUB/ADD、CMP、B.HI/B.CS 支配的 ADR、LDRB/LDRH、ADD、BR 形状。
    /// </summary>
    private static bool TryMatchJumpTableDispatch(
        IReadOnlyList<Arm64Instruction> nativeInstructions,
        int branchIndex,
        ulong methodStart,
        ulong methodEnd,
        out Arm64JumpTableMatch match)
    {
        match = null!;
        var branch = nativeInstructions[branchIndex];
        if (branch.Mnemonic != Arm64Mnemonic.BR
            || branch.Op0Kind != Arm64OperandKind.Register)
            return false;

        // 中文注释：Clang 允许在目标 ADD 与最终 BR 之间保存与分派无关的被调用方保存寄存器。
        // 只跨过寄存器到寄存器的 MOV；一旦遇到控制流、内存或算术就停止，避免向前猜测模式。
        var targetAddIndex = branchIndex - 1;
        while (targetAddIndex >= 3
               && IsIndependentRegisterPreparationBeforeJump(
                   nativeInstructions[targetAddIndex],
                   branch.Op0Reg))
        {
            targetAddIndex--;
        }

        if (targetAddIndex < 3)
            return false;
        var targetAdd = nativeInstructions[targetAddIndex];
        var tableLoadIndex = targetAddIndex - 1;
        var tableLoad = nativeInstructions[tableLoadIndex];
        var branchBaseLoad = nativeInstructions[targetAddIndex - 2];
        if (targetAdd.Mnemonic != Arm64Mnemonic.ADD
            || tableLoad.Mnemonic is not (Arm64Mnemonic.LDRB or Arm64Mnemonic.LDRH)
            || branchBaseLoad.Mnemonic != Arm64Mnemonic.ADR
            || tableLoad.Op0Kind != Arm64OperandKind.Register
            || tableLoad.Op1Kind != Arm64OperandKind.Memory
            || tableLoad.MemAddendReg == Arm64Register.INVALID
            || tableLoad.MemOffset != 0
            || tableLoad.MemIndexMode != Arm64MemoryIndexMode.Offset
            || branchBaseLoad.Op0Kind != Arm64OperandKind.Register
            || branchBaseLoad.Op1Kind is not (
                Arm64OperandKind.Immediate or Arm64OperandKind.ImmediatePcRelative)
            || !IsScaledJumpTargetAdd(targetAdd, branchBaseLoad, tableLoad, branch))
            return false;

        var entryWidthBytes = tableLoad.Mnemonic == Arm64Mnemonic.LDRB
            ? sizeof(byte)
            : sizeof(ushort);
        if (!TryDecodeMemoryIndex(
                tableLoad.MemShiftType,
                tableLoad.MemExtendType,
                tableLoad.MemExtendOrShiftAmount,
                out var tableIndexScale,
                out var tableIndexExtension)
            || tableIndexScale != entryWidthBytes
            || tableIndexExtension is MemoryIndexExtension.SignExtend32
                or MemoryIndexExtension.SignExtend64)
            return false;

        var protectedGapRegisters = new HashSet<string>(StringComparer.Ordinal)
        {
            Arm64RegisterHelper.CanonicalName(branch.Op0Reg),
        };
        for (var index = targetAddIndex + 1; index < branchIndex; index++)
        {
            var snapshot = nativeInstructions[index];
            if (!IsIndependentRegisterPreparationBeforeJump(snapshot, branch.Op0Reg)
                || protectedGapRegisters.Contains(
                    Arm64RegisterHelper.CanonicalName(snapshot.Op0Reg)))
                return false;
        }

        var dispatchStartIndex = targetAddIndex - 2;
        var adjustedIndexName = Arm64RegisterHelper.CanonicalName(tableLoad.MemAddendReg);
        var boundaryIndex = -1;
        for (var index = dispatchStartIndex - 1; index >= 1; index--)
        {
            var candidate = nativeInstructions[index];
            if (candidate.Mnemonic == Arm64Mnemonic.B
                && candidate.MnemonicConditionCode is Arm64ConditionCode.HI or Arm64ConditionCode.CS)
                boundaryIndex = index;
            if (boundaryIndex >= 0 || IsNativeControlTransfer(candidate.Mnemonic))
                break;
        }

        Arm64JumpTableBounds bounds;
        var usesClampedIndex = boundaryIndex < 1;
        if (usesClampedIndex)
        {
            if (!TryMatchClampedJumpTableBounds(
                    nativeInstructions,
                    dispatchStartIndex,
                    tableLoadIndex,
                    tableLoad.MemAddendReg,
                    methodStart,
                    methodEnd,
                    out boundaryIndex,
                    out bounds))
                return false;
        }
        else
        {
            // 中文注释：CMP 的 NZCV 标志可跨过不改标志的独立载荷供 B.HI/B.CS 消费。
            // 只向前跨过明确的 LDR 家族，且随后验证其目标没有覆盖被比较索引；算术、调用、
            // 存储和未知指令均关闭候选，避免把另一段控制流的旧标志误当边界证明。
            var comparisonIndex = boundaryIndex - 1;
            while (comparisonIndex >= 1
                   && IsIndependentFlagPreservingMemoryBeforeBoundary(
                       nativeInstructions[comparisonIndex]))
            {
                comparisonIndex--;
            }

            if (comparisonIndex < 0)
                return false;
            var comparison = nativeInstructions[comparisonIndex];
            if (comparison.Mnemonic != Arm64Mnemonic.CMP
                || comparison.Op0Kind != Arm64OperandKind.Register
                || comparison.Op1Kind != Arm64OperandKind.Immediate
                || comparison.Op1Imm < 0)
                return false;
            var comparisonIndexName = Arm64RegisterHelper.CanonicalName(comparison.Op0Reg);
            for (var index = comparisonIndex + 1; index < boundaryIndex; index++)
            {
                var gap = nativeInstructions[index];
                if (!IsIndependentFlagPreservingMemoryBeforeBoundary(gap)
                    || gap.Mnemonic == Arm64Mnemonic.LDR
                    && Arm64RegisterHelper.CanonicalName(gap.Op0Reg) == comparisonIndexName)
                    return false;
            }

            var lowerBound = 0L;
            if (comparisonIndex >= 1)
            {
                var normalization = nativeInstructions[comparisonIndex - 1];
                if (normalization.Op0Kind == Arm64OperandKind.Register
                    && Arm64RegisterHelper.CanonicalName(normalization.Op0Reg) == comparisonIndexName
                    && normalization.Mnemonic is Arm64Mnemonic.SUB or Arm64Mnemonic.ADD)
                {
                    if (normalization.Op1Kind != Arm64OperandKind.Register
                        || normalization.Op2Kind != Arm64OperandKind.Immediate
                        || normalization.Op2Imm < 0)
                        return false;
                    lowerBound = normalization.Mnemonic == Arm64Mnemonic.SUB
                        ? normalization.Op2Imm
                        : -normalization.Op2Imm;
                }
            }

            // 中文注释：Clang 会在边界检查后用 MOV 把已经验证的 Wn 索引复制到表寻址寄存器。
            // 两个寄存器不同时只接受一次精确寄存器复制；复制前不得重写比较寄存器，复制后不得
            // 再写表索引。这样既保留同寄存器旧形状，也不把任意数据搬运误认成已验证索引。
            var awaitsIndexCopy = comparisonIndexName != adjustedIndexName;
            for (var index = boundaryIndex + 1; index < tableLoadIndex; index++)
            {
                var candidate = nativeInstructions[index];
                if (candidate.Op0Kind != Arm64OperandKind.Register)
                    continue;

                var destinationName = Arm64RegisterHelper.CanonicalName(candidate.Op0Reg);
                if (awaitsIndexCopy && destinationName == comparisonIndexName)
                    return false;
                if (destinationName != adjustedIndexName)
                    continue;
                if (!awaitsIndexCopy
                    || candidate.Mnemonic != Arm64Mnemonic.MOV
                    || candidate.Op1Kind != Arm64OperandKind.Register
                    || Arm64RegisterHelper.CanonicalName(candidate.Op1Reg) != comparisonIndexName)
                    return false;
                awaitsIndexCopy = false;
            }

            if (awaitsIndexCopy)
                return false;

            var boundary = nativeInstructions[boundaryIndex];
            var maximumAdjustedIndex = boundary.MnemonicConditionCode == Arm64ConditionCode.HI
                ? comparison.Op1Imm
                : comparison.Op1Imm - 1;
            if (!TryCreateJumpTableBounds(
                    lowerBound,
                    maximumAdjustedIndex,
                    boundary.BranchTarget,
                    methodStart,
                    methodEnd,
                    out bounds))
                return false;
        }

        var tableBaseName = Arm64RegisterHelper.CanonicalName(tableLoad.MemBase);
        var tableAddIndex = -1;
        var tablePageIndex = -1;
        var tableDefinitionSearchFloor = IsCalleeSavedGeneralPurposeRegister(tableLoad.MemBase)
            ? Math.Max(0, boundaryIndex - 128)
            : boundaryIndex + 1;
        for (var index = tableLoadIndex - 1; index >= tableDefinitionSearchFloor; index--)
        {
            var candidate = nativeInstructions[index];
            if (candidate.Op0Kind != Arm64OperandKind.Register
                || Arm64RegisterHelper.CanonicalName(candidate.Op0Reg) != tableBaseName)
                continue;

            if (tableAddIndex < 0)
            {
                if (candidate.Mnemonic != Arm64Mnemonic.ADD
                    || candidate.Op1Kind != Arm64OperandKind.Register
                    || Arm64RegisterHelper.CanonicalName(candidate.Op1Reg) != tableBaseName
                    || candidate.Op2Kind != Arm64OperandKind.Immediate
                    || candidate.Op2Imm < 0)
                    return false;
                tableAddIndex = index;
                continue;
            }

            if (candidate.Mnemonic != Arm64Mnemonic.ADRP
                || candidate.Op1Kind is not (
                    Arm64OperandKind.Immediate or Arm64OperandKind.ImmediatePcRelative))
                return false;
            tablePageIndex = index;
            break;
        }

        if (tablePageIndex < 0 || tableAddIndex < 0)
            return false;

        // 中文注释：只使用到表读取点的最后两次同寄存器定义。更早的 ADRP/LDR 生命期可复用
        // 同一物理寄存器加载浮点常量；它已经被最终 ADRP 覆盖，不参与表地址证明。
        for (var index = tablePageIndex + 1; index < tableLoadIndex; index++)
        {
            if (index == tableAddIndex)
                continue;
            var candidate = nativeInstructions[index];
            if (candidate.Op0Kind == Arm64OperandKind.Register
                && Arm64RegisterHelper.CanonicalName(candidate.Op0Reg) == tableBaseName)
                return false;
        }

        var tablePageLoad = nativeInstructions[tablePageIndex];
        var tableAddressAdd = nativeInstructions[tableAddIndex];
        var tablePageInstructionAddress = ResolveInstructionAddress(
            methodStart,
            tablePageIndex,
            tablePageLoad.Mnemonic,
            tablePageLoad.Address);
        ulong tableAddress;
        try
        {
            tableAddress = checked(
                ResolveAdrpPageAddress(tablePageInstructionAddress, tablePageLoad.Op1Imm)
                + (ulong)tableAddressAdd.Op2Imm);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (tableAddress % (ulong)entryWidthBytes != 0)
            return false;

        var boundaryAddress = ResolveInstructionAddress(
            methodStart,
            boundaryIndex,
            nativeInstructions[boundaryIndex].Mnemonic,
            nativeInstructions[boundaryIndex].Address);
        var dispatchAddress = ResolveInstructionAddress(
            methodStart,
            branchIndex,
            branch.Mnemonic,
            branch.Address);
        if (dispatchAddress < methodStart
            || dispatchAddress >= methodEnd
            || dispatchAddress % sizeof(uint) != 0
            || (!usesClampedIndex
                && bounds.DefaultTarget >= boundaryAddress
                && bounds.DefaultTarget <= dispatchAddress))
            return false;

        var branchBaseAddress = ResolveAdrAddress(
            ResolveInstructionAddress(
                methodStart,
                dispatchStartIndex,
                branchBaseLoad.Mnemonic,
                branchBaseLoad.Address),
            branchBaseLoad.Op1Imm);
        match = new Arm64JumpTableMatch(
            branchIndex,
            tableLoad.MemAddendReg,
            entryWidthBytes,
            boundaryAddress,
            dispatchAddress,
            tableAddress,
            branchBaseAddress,
            bounds,
            usesClampedIndex);
        return true;
    }

    /// <summary>
    /// 识别由两次 CSEL 把有符号索引夹紧到闭区间、再用 ADD 平移到零基表索引的形状。
    /// 该形状没有显式 default 分支，范围必须同时由 CMP/CSEL 上界、CMN/CSEL 下界和最终
    /// 平移立即数三份一致证据闭合；任一寄存器被重写或条件方向不符时整体拒绝。
    /// </summary>
    private static bool TryMatchClampedJumpTableBounds(
        IReadOnlyList<Arm64Instruction> nativeInstructions,
        int dispatchStartIndex,
        int tableLoadIndex,
        Arm64Register adjustedIndexRegister,
        ulong methodStart,
        ulong methodEnd,
        out int evidenceStartIndex,
        out Arm64JumpTableBounds bounds)
    {
        evidenceStartIndex = -1;
        bounds = default;
        if (methodStart >= methodEnd || dispatchStartIndex < 1)
            return false;

        var adjustedName = Arm64RegisterHelper.CanonicalName(adjustedIndexRegister);
        static bool Writes(Arm64Instruction instruction, string registerName)
            => instruction.Op0Kind == Arm64OperandKind.Register
               && Arm64RegisterHelper.CanonicalName(instruction.Op0Reg) == registerName;

        var normalizationIndex = -1;
        for (var index = dispatchStartIndex - 1; index >= 0; index--)
        {
            var candidate = nativeInstructions[index];
            if (Writes(candidate, adjustedName))
            {
                normalizationIndex = index;
                break;
            }
            if (IsNativeControlTransfer(candidate.Mnemonic))
                return false;
        }

        if (normalizationIndex < 0)
            return false;
        var normalization = nativeInstructions[normalizationIndex];
        if (normalization.Mnemonic != Arm64Mnemonic.ADD
            || normalization.Op1Kind != Arm64OperandKind.Register
            || Arm64RegisterHelper.CanonicalName(normalization.Op1Reg) != adjustedName
            || normalization.Op2Kind != Arm64OperandKind.Immediate
            || normalization.Op2Imm <= 0)
            return false;
        var bias = normalization.Op2Imm;

        var lowerSelectIndex = -1;
        for (var index = normalizationIndex - 1; index >= 1; index--)
        {
            var candidate = nativeInstructions[index];
            if (Writes(candidate, adjustedName))
            {
                lowerSelectIndex = index;
                break;
            }
            if (IsNativeControlTransfer(candidate.Mnemonic))
                return false;
        }

        if (lowerSelectIndex < 1)
            return false;
        var lowerSelect = nativeInstructions[lowerSelectIndex];
        if (lowerSelect.Mnemonic != Arm64Mnemonic.CSEL
            || lowerSelect.FinalOpConditionCode != Arm64ConditionCode.GT
            || lowerSelect.Op1Kind != Arm64OperandKind.Register
            || Arm64RegisterHelper.CanonicalName(lowerSelect.Op1Reg) != adjustedName
            || lowerSelect.Op2Kind != Arm64OperandKind.Register)
            return false;
        var lowerConstantName = Arm64RegisterHelper.CanonicalName(lowerSelect.Op2Reg);

        var lowerComparison = nativeInstructions[lowerSelectIndex - 1];
        var isCmnAlias = lowerComparison.Mnemonic == Arm64Mnemonic.ADDS
                         && lowerComparison.Op0Kind == Arm64OperandKind.Register
                         && Arm64RegisterHelper.IsZeroRegister(lowerComparison.Op0Reg)
                         && lowerComparison.Op1Kind == Arm64OperandKind.Register
                         && Arm64RegisterHelper.CanonicalName(lowerComparison.Op1Reg) == adjustedName
                         && lowerComparison.Op2Kind == Arm64OperandKind.Immediate
                         && lowerComparison.Op2Imm == bias;
        var isDecodedCmn = lowerComparison.Mnemonic == Arm64Mnemonic.CMN
                           && lowerComparison.Op0Kind == Arm64OperandKind.Register
                           && Arm64RegisterHelper.CanonicalName(lowerComparison.Op0Reg) == adjustedName
                           && lowerComparison.Op1Kind == Arm64OperandKind.Immediate
                           && lowerComparison.Op1Imm == bias;
        if (!isCmnAlias && !isDecodedCmn)
            return false;

        var lowerConstantIndex = -1;
        for (var index = lowerSelectIndex - 2; index >= 0; index--)
        {
            var candidate = nativeInstructions[index];
            if (Writes(candidate, lowerConstantName))
            {
                lowerConstantIndex = index;
                break;
            }
            if (IsNativeControlTransfer(candidate.Mnemonic))
                return false;
        }
        if (lowerConstantIndex < 0
            || nativeInstructions[lowerConstantIndex] is not
            {
                Mnemonic: Arm64Mnemonic.MOV,
                Op1Kind: Arm64OperandKind.Immediate
            } lowerConstant
            || GetSignedRegisterImmediate(lowerConstant) != -bias)
            return false;

        var upperSelectIndex = -1;
        for (var index = lowerSelectIndex - 2; index >= 0; index--)
        {
            var candidate = nativeInstructions[index];
            if (Writes(candidate, adjustedName))
            {
                upperSelectIndex = index;
                break;
            }
            if (IsNativeControlTransfer(candidate.Mnemonic))
                return false;
        }
        if (upperSelectIndex < 0)
            return false;
        var upperSelect = nativeInstructions[upperSelectIndex];
        if (upperSelect.Mnemonic != Arm64Mnemonic.CSEL
            || upperSelect.FinalOpConditionCode != Arm64ConditionCode.LT
            || upperSelect.Op1Kind != Arm64OperandKind.Register
            || upperSelect.Op2Kind != Arm64OperandKind.Register
            || Arm64RegisterHelper.CanonicalName(upperSelect.Op2Reg) != adjustedName)
            return false;
        var sourceName = Arm64RegisterHelper.CanonicalName(upperSelect.Op1Reg);

        var upperConstantIndex = -1;
        for (var index = upperSelectIndex - 1; index >= 0; index--)
        {
            var candidate = nativeInstructions[index];
            if (Writes(candidate, adjustedName))
            {
                upperConstantIndex = index;
                break;
            }
            if (IsNativeControlTransfer(candidate.Mnemonic))
                return false;
        }
        if (upperConstantIndex < 0
            || nativeInstructions[upperConstantIndex] is not
            {
                Mnemonic: Arm64Mnemonic.MOV,
                Op1Kind: Arm64OperandKind.Immediate
            } upperConstant)
            return false;

        var upperComparisonIndex = -1;
        for (var index = upperSelectIndex - 1; index >= 0; index--)
        {
            var candidate = nativeInstructions[index];
            if (candidate.Mnemonic == Arm64Mnemonic.CMP)
            {
                upperComparisonIndex = index;
                break;
            }
            if (candidate.Mnemonic != Arm64Mnemonic.MOV)
                return false;
        }
        if (upperComparisonIndex < 0)
            return false;
        var upperComparison = nativeInstructions[upperComparisonIndex];
        if (upperComparison.Op0Kind != Arm64OperandKind.Register
            || Arm64RegisterHelper.CanonicalName(upperComparison.Op0Reg) != sourceName
            || upperComparison.Op1Kind != Arm64OperandKind.Immediate
            || upperComparison.Op1Imm != upperConstant.Op1Imm)
            return false;

        for (var index = upperSelectIndex + 1; index < lowerSelectIndex - 1; index++)
        {
            if (Writes(nativeInstructions[index], adjustedName))
                return false;
        }
        for (var index = lowerSelectIndex + 1; index < normalizationIndex; index++)
        {
            if (Writes(nativeInstructions[index], adjustedName))
                return false;
        }
        for (var index = normalizationIndex + 1; index < tableLoadIndex; index++)
        {
            if (Writes(nativeInstructions[index], adjustedName))
                return false;
        }

        var lowerBound = -bias;
        var upperBound = GetSignedRegisterImmediate(upperConstant);
        long entryCount;
        try
        {
            entryCount = checked(upperBound - lowerBound + 1);
        }
        catch (OverflowException)
        {
            return false;
        }
        if (entryCount <= 0 || entryCount > MaximumJumpTableEntryCount)
            return false;

        evidenceStartIndex = upperComparisonIndex;
        // 中文注释：夹紧形状没有显式 default 边；先保存方法入口作为有效占位，读取并验证
        // 全部表项后再用首个 case 目标补齐理论上不可达的 CIL 总分支。
        bounds = new Arm64JumpTableBounds(lowerBound, checked((int)entryCount), methodStart);
        return true;
    }

    /// <summary>按目标寄存器宽度还原 MOV 立即数；Disarm 会把 W 寄存器负数保存在 uint 正值中。</summary>
    private static long GetSignedRegisterImmediate(Arm64Instruction instruction)
    {
        if (instruction.Op0Kind == Arm64OperandKind.Register
            && TryGetSignedIntegerWidthBits(instruction.Op0Reg, out var widthBits)
            && widthBits == 32)
            return unchecked((int)(uint)instruction.Op1Imm);

        return instruction.Op1Imm;
    }

    private Dictionary<int, Arm64JumpTableDispatch> RecoverJumpTableDispatches(
        IReadOnlyList<Arm64Instruction> nativeInstructions,
        MethodAnalysisContext context)
    {
        var result = new Dictionary<int, Arm64JumpTableDispatch>();
        var methodStart = context.UnderlyingPointer;
        ulong methodEnd;
        try
        {
            methodEnd = checked(methodStart + (ulong)context.RawBytes.Length);
        }
        catch (OverflowException)
        {
            return result;
        }

        foreach (var match in FindJumpTableMatches(nativeInstructions, methodStart, methodEnd))
        {
            byte[] table;
            try
            {
                var tableByteCount = checked(match.Bounds.EntryCount * match.EntryWidthBytes);
                var rawTableAddress = context.AppContext.Binary.MapVirtualAddressToRaw(match.TableAddress);
                table = context.AppContext.Binary.Reader.ReadByteArrayAtRawAddress(
                    rawTableAddress,
                    tableByteCount);
                if (table.Length != tableByteCount)
                    continue;
            }
            catch
            {
                continue;
            }

            if (!TryResolveJumpTableTargets(
                    table,
                    match.EntryWidthBytes,
                    match.Bounds.EntryCount,
                    match.BranchBaseAddress,
                    methodStart,
                    methodEnd,
                    out var caseTargets)
                || caseTargets.Any(target =>
                    target >= match.BoundaryAddress
                    && target <= match.DispatchAddress))
                continue;

            var effectiveBounds = match.UsesClampedIndex
                ? match.Bounds with { DefaultTarget = caseTargets[0] }
                : match.Bounds;
            result[match.BranchIndex] = new Arm64JumpTableDispatch(
                match.AdjustedIndexRegister,
                match.DispatchAddress,
                effectiveBounds,
                caseTargets);
        }

        foreach (var invalidBranchIndex in FindUnsafeJumpTableDispatches(result, nativeInstructions))
            result.Remove(invalidBranchIndex);

        return result;
    }

    /// <summary>
    /// 一次建立候选地址索引并关闭所有跨候选边的两端；普通直接分支命中的候选也保持原始 BR。
    /// </summary>
    internal static HashSet<int> FindUnsafeJumpTableDispatches(
        IReadOnlyDictionary<int, Arm64JumpTableDispatch> dispatches,
        IReadOnlyList<Arm64Instruction> nativeInstructions)
    {
        var dispatchIndicesByAddress = new Dictionary<ulong, List<int>>();
        foreach (var pair in dispatches)
        {
            if (!dispatchIndicesByAddress.TryGetValue(pair.Value.DispatchAddress, out var indices))
                dispatchIndicesByAddress[pair.Value.DispatchAddress] = indices = [];
            indices.Add(pair.Key);
        }

        var unsafeIndices = new HashSet<int>();
        void MarkTarget(ulong target, int? sourceIndex)
        {
            if (!dispatchIndicesByAddress.TryGetValue(target, out var destinationIndices))
                return;
            if (sourceIndex.HasValue)
                unsafeIndices.Add(sourceIndex.Value);
            foreach (var destinationIndex in destinationIndices)
                unsafeIndices.Add(destinationIndex);
        }

        // 每条候选边只访问一次；A→B 会同时关闭 A、B，链式关系自然覆盖整个相关连通分量。
        foreach (var pair in dispatches)
        foreach (var target in pair.Value.CaseTargets.Append(pair.Value.Bounds.DefaultTarget))
            MarkTarget(target, pair.Key);

        foreach (var instruction in nativeInstructions)
        {
            if (TryGetNonCallDirectBranchTarget(instruction, out var target))
                MarkTarget(target, null);
        }

        return unsafeIndices;
    }

    /// <summary>
    /// 按指令族读取非调用直接分支目标。Disarm 的 BranchTarget 只适用于 B/BL，
    /// 比较分支和位测试分支必须使用各自的 PC 相对立即数字段。
    /// </summary>
    internal static bool TryGetNonCallDirectBranchTarget(
        Arm64Instruction instruction,
        out ulong target)
    {
        switch (instruction.Mnemonic)
        {
            case Arm64Mnemonic.B:
                target = instruction.BranchTarget;
                return true;
            case Arm64Mnemonic.CBZ:
            case Arm64Mnemonic.CBNZ:
                return TryAddSignedOffset(instruction.Address, instruction.Op1Imm, out target);
            case Arm64Mnemonic.TBZ:
            case Arm64Mnemonic.TBNZ:
                return TryAddSignedOffset(instruction.Address, instruction.Op2Imm, out target);
            default:
                target = 0;
                return false;
        }
    }

    /// <summary>在不发生地址环绕的前提下解析 PC 相对目标。</summary>
    private static bool TryAddSignedOffset(ulong address, long offset, out ulong target)
    {
        if (offset >= 0)
        {
            var positiveOffset = (ulong)offset;
            if (positiveOffset > ulong.MaxValue - address)
            {
                target = 0;
                return false;
            }

            target = address + positiveOffset;
            return true;
        }

        // 避免对 long.MinValue 直接取反；先平移一位再恢复其绝对值。
        var negativeMagnitude = (ulong)(-(offset + 1)) + 1;
        if (negativeMagnitude > address)
        {
            target = 0;
            return false;
        }

        target = address - negativeMagnitude;
        return true;
    }

    private static bool IsScaledJumpTargetAdd(
        Arm64Instruction targetAdd,
        Arm64Instruction branchBaseLoad,
        Arm64Instruction tableLoad,
        Arm64Instruction branch)
    {
        if (targetAdd.Op0Kind != Arm64OperandKind.Register
            || targetAdd.Op1Kind != Arm64OperandKind.Register
            || targetAdd.Op2Kind != Arm64OperandKind.Register
            || targetAdd.Op3Kind != Arm64OperandKind.Immediate
            || targetAdd.Op3Imm != 2
            || Arm64RegisterHelper.CanonicalName(branch.Op0Reg)
                != Arm64RegisterHelper.CanonicalName(targetAdd.Op0Reg)
            || Arm64RegisterHelper.CanonicalName(targetAdd.Op1Reg)
                != Arm64RegisterHelper.CanonicalName(branchBaseLoad.Op0Reg)
            || Arm64RegisterHelper.CanonicalName(targetAdd.Op2Reg)
                != Arm64RegisterHelper.CanonicalName(tableLoad.Op0Reg))
            return false;

        var isShiftedRegister = targetAdd.FinalOpExtendType == Arm64ExtendType.NONE
            && targetAdd.FinalOpShiftType == Arm64ShiftType.LSL;
        var isUnsignedExtendedRegister = targetAdd.FinalOpShiftType == Arm64ShiftType.NONE
            && targetAdd.FinalOpExtendType is Arm64ExtendType.UXTW or Arm64ExtendType.UXTX;
        return isShiftedRegister || isUnsignedExtendedRegister;
    }

    private static bool IsIndependentRegisterPreparationBeforeJump(
        Arm64Instruction instruction,
        Arm64Register jumpTargetRegister)
        => instruction.Mnemonic is Arm64Mnemonic.MOV
               or Arm64Mnemonic.MOVK
               or Arm64Mnemonic.FMOV
               or Arm64Mnemonic.MOVI
           && instruction.Op0Kind == Arm64OperandKind.Register
           && Arm64RegisterHelper.CanonicalName(instruction.Op0Reg)
               != Arm64RegisterHelper.CanonicalName(jumpTargetRegister);

    private static bool IsIndependentFlagPreservingMemoryBeforeBoundary(
        Arm64Instruction instruction)
        => instruction.Mnemonic is Arm64Mnemonic.LDR or Arm64Mnemonic.STR
           && instruction.Op0Kind == Arm64OperandKind.Register
           && instruction.Op1Kind == Arm64OperandKind.Memory;

    private static bool IsCalleeSavedGeneralPurposeRegister(Arm64Register register)
    {
        var canonicalName = Arm64RegisterHelper.CanonicalName(register);
        return canonicalName.Length >= 3
               && canonicalName[0] == 'X'
               && int.TryParse(canonicalName[1..], out var number)
               && number is >= 19 and <= 28;
    }

    private static bool IsNativeControlTransfer(Arm64Mnemonic mnemonic)
        => mnemonic is Arm64Mnemonic.B
            or Arm64Mnemonic.BL
            or Arm64Mnemonic.BR
            or Arm64Mnemonic.BLR
            or Arm64Mnemonic.RET
            or Arm64Mnemonic.CBZ
            or Arm64Mnemonic.CBNZ
            or Arm64Mnemonic.TBZ
            or Arm64Mnemonic.TBNZ;

    private static void AppendJumpTableDispatch(
        Arm64JumpTableDispatch dispatch,
        List<Instruction> instructions,
        List<ulong> addresses)
    {
        // 保留调整后的索引做逐项比较；边界分支已保证它只会落入合法表项范围。
        var indexOperand = new Register(
            null,
            Arm64RegisterHelper.CanonicalName(dispatch.AdjustedIndexRegister));
        for (var tableIndex = 0; tableIndex < dispatch.CaseTargets.Length; tableIndex++)
        {
            var caseValue = checked(dispatch.Bounds.LowerBound + tableIndex);
            var condition = new Register(
                null,
                $"JUMP_TABLE_{dispatch.DispatchAddress:X}_CASE_{caseValue}");
            instructions.Add(new Instruction(
                instructions.Count,
                OpCode.CheckEqual,
                condition,
                indexOperand,
                Imm(tableIndex)));
            addresses.Add(dispatch.DispatchAddress);
            instructions.Add(new Instruction(
                instructions.Count,
                OpCode.ConditionalJump,
                Imm(dispatch.CaseTargets[tableIndex]),
                condition));
            addresses.Add(dispatch.DispatchAddress);
        }

        // default 是边界分支自己的目标，不能把最后一个 case 偷换成 default。
        instructions.Add(new Instruction(
            instructions.Count,
            OpCode.Jump,
            Imm(dispatch.Bounds.DefaultTarget)));
        addresses.Add(dispatch.DispatchAddress);
    }

    private bool TryEmitPackedHalfwordPredicatePattern(
        IReadOnlyList<Arm64Instruction> nativeInstructions,
        int startIndex,
        MethodAnalysisContext context,
        List<Instruction> instructions,
        List<ulong> addresses,
        out int consumedInstructionCount)
    {
        const int patternLength = 13;
        if (startIndex < 0 || startIndex + patternLength > nativeInstructions.Count)
        {
            consumedInstructionCount = 0;
            return false;
        }

        Span<uint> machineCodes = stackalloc uint[patternLength];
        Span<ulong> nativeAddresses = stackalloc ulong[patternLength];
        for (var offset = 0; offset < patternLength; offset++)
        {
            var nativeInstruction = nativeInstructions[startIndex + offset];
            var nativeAddress = ResolveInstructionAddress(
                context.UnderlyingPointer,
                startIndex + offset,
                nativeInstruction.Mnemonic,
                nativeInstruction.Address);
            nativeAddresses[offset] = nativeAddress;
            machineCodes[offset] = ReadMachineCodeAtAddress(context, nativeAddress);
        }

        if (!TryDecodePackedHalfwordPredicatePattern(machineCodes, out var pattern))
        {
            consumedInstructionCount = 0;
            return false;
        }

        var constantLoad = nativeInstructions[startIndex + 1];
        if (!adrpOffsets.TryGetValue(constantLoad.MemBase, out var constantPage))
        {
            consumedInstructionCount = 0;
            return false;
        }

        var constantAddress = checked(constantPage + (ulong)pattern.ConstantByteOffset);
        var constantRawAddress = context.AppContext.Binary.MapVirtualAddressToRaw(constantAddress);
        var constantBytes = context.AppContext.Binary.Reader.ReadByteArrayAtRawAddress(
            constantRawAddress,
            sizeof(int) * 4);
        Span<int> constants = stackalloc int[4];
        for (var lane = 0; lane < constants.Length; lane++)
            constants[lane] = BinaryPrimitives.ReadInt32LittleEndian(
                constantBytes.AsSpan(lane * sizeof(int), sizeof(int)));

        // 为每个被折叠的原生地址保留可跳转锚点；真实谓词只在最终FMOV地址计算一次。
        for (var offset = 0; offset < patternLength - 1; offset++)
        {
            addresses.Add(nativeAddresses[offset]);
            instructions.Add(new Instruction(instructions.Count, OpCode.Nop));
        }

        var accumulatorLoad = nativeInstructions[startIndex + 7];
        var accumulator = new MemoryOperand(
            new Register(null, Arm64RegisterHelper.CanonicalName(accumulatorLoad.MemBase)),
            addend: pattern.AccumulatorByteOffset);
        var predicate = new Instruction(
            instructions.Count,
            OpCode.VectorAllLanesPredicate,
            ConvertOperand(nativeInstructions[startIndex + 12], 0),
            accumulator,
            new Register(null, $"X{pattern.ScalarSourceRegister}"),
            Imm(constants[0]),
            Imm(constants[1]),
            Imm(constants[2]),
            Imm(constants[3]),
            Imm(pattern.ReverseLaneMask));
        addresses.Add(nativeAddresses[patternLength - 1]);
        instructions.Add(predicate);
        consumedInstructionCount = patternLength;
        return true;
    }

    private void ConvertInstructionStatement(
        Arm64Instruction instruction,
        ulong address,
        List<Instruction> instructions,
        List<ulong> addresses,
        MethodAnalysisContext context,
        ref Arm64FlagState flagState,
        ref long conditionalComparisonFallbackNzcv)
    {
        var inputFlagState = flagState;
        var inputConditionalComparisonFallbackNzcv = conditionalComparisonFallbackNzcv;

        Instruction Add(ulong address, OpCode opCode, params List<IOperand> operands)
        {
            addresses.Add(address);
            var newInstruction = new Instruction(instructions.Count, opCode, operands);
            instructions.Add(newInstruction);
            return newInstruction;
        }

        Instruction AddInteger(ulong address, OpCode opCode, params List<IOperand> operands)
        {
            var emitted = Add(address, opCode, operands);
            if (TryGetSignedIntegerWidthBits(instruction.Op0Reg, out var widthBits))
                emitted.IntegerWidthBits = widthBits;
            return emitted;
        }

        Instruction AddMemory(
            ulong address,
            int widthBits,
            OpCode opCode,
            params List<IOperand> operands)
        {
            var emitted = Add(address, opCode, operands);
            emitted.MemoryAccessWidthBits = widthBits;
            return emitted;
        }

        void AddCall(MethodAnalysisContext context, ulong address, ulong target)
        {
            var hasObservedIndirectReturnBuffer =
                TryFindObservedIndirectReturnBuffer(instructions, out _);

            if (!context.AppContext.MethodsByAddress.TryGetValue(target, out var methodsAtAddress))
            {
                // 原生目标的签名尚未解析时保留 X0 返回值与全部 AAPCS64 参数，供后续 key function、
                // 接口分派和委托恢复器统一裁决；未使用的返回值会由死代码消除器删除。
                var unknownCall = Add(
                    address,
                    OpCode.Call,
                    Imm(target),
                    new Register(null, nameof(Arm64Register.X0)));
                unknownCall.AddOperands(Arm64CallingConventionResolver.ResolveForUnmanaged());
                if (hasObservedIndirectReturnBuffer)
                {
                    // 共享泛型体可能尚未进入地址索引；仍按调用点事实保留X8，供MethodInfo解析后绑定。
                    unknownCall.AddOperands([new Register(null, nameof(Arm64Register.X8))]);
                }
                return;
            }

            // 共享地址原有的保守规则以当前调用者返回 ABI 维持调用后 X0/V0 的生命期；
            // void 调用者中的 Enum.TryParse 是唯一需要由候选族补回 Boolean X0 的例外。
            var calledMethod = methodsAtAddress.Count == 1 ? methodsAtAddress[0] : context;
            // 某些 Unity 6 构建会让接口慢查表助手与 List<T>.AddWithResize 共用原生地址。
            // 前者真实返回 VirtualInvokeData*，后者为 void；在 CFG/SSA 消费关系建立前不能丢掉 X0。
            // 仅对该已知共享身份保留原始返回槽，MetadataResolver 会删除普通 void 的伪返回值，
            // InterfaceDispatchRecovery 则以 Phi、零偏移 methodPtr 和 vtable 链完成最终裁决。
            var preservePotentialInterfaceLookupResult = calledMethod.IsVoid
                && methodsAtAddress.Any(InterfaceDispatchRecovery.IsSharedAddWithResizeCandidate)
                && HasRecentSmallImmediateArgument(instructions, "X2", ushort.MaxValue);
            Register? returnRegister = !calledMethod.IsVoid
                ? Arm64CallingConventionResolver.ReturnRegister(calledMethod)
                : methodsAtAddress.Count > 1
                  && Arm64CallingConventionResolver.TryGetSharedEnumTryParseReturnRegister(
                      methodsAtAddress,
                      out var sharedReturnRegister)
                    ? sharedReturnRegister
                    : preservePotentialInterfaceLookupResult
                        ? new Register(null, nameof(Arm64Register.X0))
                        : null;
            var call = returnRegister == null
                ? Add(address, OpCode.CallVoid, Imm(target))
                : Add(address, OpCode.Call, Imm(target), returnRegister);

            call.AddOperands(GetArgumentOperandsForCall(methodsAtAddress.First()));
            if (returnRegister != null
                && hasObservedIndirectReturnBuffer)
            {
                // 调用点已观察到X8=&stack且目标返回值类型；先保留候选，待SSA后绑定到唯一栈局部。
                call.AddOperands([new Register(null, nameof(Arm64Register.X8))]);
            }

            if (methodsAtAddress.Count == 1)
            {
                // HFA调用在托管CIL中返回一个完整值类型，在AAPCS64中则会改写连续V寄存器。
                // 逆序字段投影让SSA同时看到V0..Vn的新定义，并保证所有字段都从尚未覆盖的V0
                // 聚合体载体读取；后续字段偏移解析会把这些内存形态精确绑定到值类型字段。
                foreach (var projection in Arm64CallingConventionResolver.ReturnProjections(calledMethod))
                    Add(address, OpCode.Move, projection.Destination, projection.Source);

            }

            // 普通小结构体会把一至两个完整托管引用槽分别返回在X0/X1。唯一目标直接使用
            // 其签名；共享泛型体只在全部封闭候选具有同一引用槽布局时使用该布局原型。
            var referenceReturnPrototype = methodsAtAddress.Count == 1
                ? calledMethod
                : Arm64CallingConventionResolver.TryGetReferenceRegisterAggregateReturnPrototype(
                    methodsAtAddress,
                    out var sharedPrototype)
                    ? sharedPrototype
                    : null;
            if (referenceReturnPrototype != null)
            {
                foreach (var projection in Arm64CallingConventionResolver
                             .ReferenceRegisterReturnProjections(referenceReturnPrototype))
                    Add(address, OpCode.Move, projection.Destination, projection.Source);
            }
        }

        void AddIndirectTransfer(Arm64Mnemonic mnemonic, ulong address, IOperand target)
        {
            var opCode = GetIndirectBranchOpCode(mnemonic)
                         ?? throw new ArgumentOutOfRangeException(nameof(mnemonic));
            var transfer = Add(
                address,
                opCode,
                target,
                new Register(null, nameof(Arm64Register.X0)));
            transfer.AddOperands(Arm64CallingConventionResolver.ResolveForUnmanaged());
        }

        bool TryEmitCondition(
            Arm64ConditionCode conditionCode,
            string registerPrefix,
            out IOperand condition)
        {
            if (inputFlagState == Arm64FlagState.ConditionalComparison
                && GetConditionalComparisonOpCode(conditionCode) is { } comparisonOpCode
                && TryEvaluateConditionFromNzcv(
                    conditionCode,
                    inputConditionalComparisonFallbackNzcv,
                    out var fallbackResult))
            {
                var comparedCondition = new Register(null, registerPrefix + "_CCMP_COMPARED");
                var selectedCondition = new Register(null, registerPrefix + "_CCMP_SELECTED");
                Add(
                    address,
                    comparisonOpCode,
                    comparedCondition,
                    new Register(null, "CCMP_COMPARE_LEFT"),
                    new Register(null, "CCMP_COMPARE_RIGHT"));
                Add(
                    address,
                    OpCode.ConditionalSelect,
                    selectedCondition,
                    new Register(null, "CCMP_GATE"),
                    comparedCondition,
                    Imm(fallbackResult ? 1 : 0));
                condition = selectedCondition;
                return true;
            }

            var invertZeroFlag = ShouldInvertZeroFlag(conditionCode);
            if (invertZeroFlag is not null)
            {
                condition = new Register(null, "Z");
                if (invertZeroFlag.Value)
                {
                    var inverted = new Register(null, registerPrefix + "_NOT_ZERO");
                    Add(address, OpCode.Not, inverted, condition);
                    condition = inverted;
                }

                return inputFlagState != Arm64FlagState.None;
            }

            var invertSignFlag = ShouldInvertSignFlag(conditionCode);
            if (inputFlagState == Arm64FlagState.Comparison &&
                invertSignFlag is not null)
            {
                condition = new Register(null, "N");
                if (invertSignFlag.Value)
                {
                    var inverted = new Register(null, registerPrefix + "_NOT_NEGATIVE");
                    Add(address, OpCode.Not, inverted, condition);
                    condition = inverted;
                }

                return true;
            }

            if (inputFlagState == Arm64FlagState.FloatingComparison &&
                invertSignFlag is not null)
            {
                var less = new Register(null, registerPrefix + "_LESS");
                Add(
                    address,
                    OpCode.CheckLess,
                    less,
                    new Register(null, "FLAG_COMPARE_LEFT"),
                    new Register(null, "FLAG_COMPARE_RIGHT"));
                if (invertSignFlag.Value)
                {
                    var notLess = new Register(null, registerPrefix + "_NOT_LESS");
                    Add(address, OpCode.Not, notLess, less);
                    condition = notLess;
                }
                else
                {
                    condition = less;
                }
                return true;
            }

            if (CanEmitCarryZeroCondition(conditionCode, inputFlagState))
            {
                var carry = new Register(null, "C");
                var zero = new Register(null, "Z");
                switch (conditionCode)
                {
                    case Arm64ConditionCode.CS:
                        condition = carry;
                        return true;
                    case Arm64ConditionCode.CC:
                    {
                        var notCarry = new Register(null, registerPrefix + "_NOT_CARRY");
                        Add(address, OpCode.Not, notCarry, carry);
                        condition = notCarry;
                        return true;
                    }
                    case Arm64ConditionCode.HI:
                    {
                        var notZero = new Register(null, registerPrefix + "_NOT_ZERO");
                        var higher = new Register(null, registerPrefix + "_HIGHER");
                        Add(address, OpCode.Not, notZero, zero);
                        Add(address, OpCode.And, higher, carry, notZero);
                        condition = higher;
                        return true;
                    }
                    case Arm64ConditionCode.LS:
                    {
                        var notCarry = new Register(null, registerPrefix + "_NOT_CARRY");
                        var lowerOrSame = new Register(null, registerPrefix + "_LOWER_OR_SAME");
                        Add(address, OpCode.Not, notCarry, carry);
                        Add(address, OpCode.Or, lowerOrSame, notCarry, zero);
                        condition = lowerOrSame;
                        return true;
                    }
                }
            }

            var relationalOpCode = GetConditionalSetRelationalOpCode(
                conditionCode,
                inputFlagState);
            if (relationalOpCode is not null)
            {
                var relational = new Register(null, registerPrefix + "_RELATIONAL");
                Add(
                    address,
                    relationalOpCode.Value,
                    relational,
                    new Register(null, "FLAG_COMPARE_LEFT"),
                    new Register(null, "FLAG_COMPARE_RIGHT"));
                condition = relational;
                return true;
            }

            condition = null!;
            return false;
        }

        bool TryEmitConditionalSelect(string registerPrefix)
        {
            // CSEL/FCSEL 的目标是新值，旧 ADRP 页基址到这里已经死亡。必须在条件解析前失效，
            // 这样即使条件暂未建模，后续 LDR 也不会把动态选择结果错误折叠成陈旧绝对地址。
            InvalidateAdrpRegisterWrite(adrpOffsets, instruction.Op0Kind, instruction.Op0Reg);

            if (!TryEmitCondition(
                    instruction.FinalOpConditionCode,
                    registerPrefix + "_CONDITION",
                    out var condition))
                return false;

            var destination = ConvertOperand(instruction, 0);
            var preservedTrue = new Register(null, registerPrefix + "_TRUE");
            var preservedFalse = new Register(null, registerPrefix + "_FALSE");
            Add(
                address,
                OpCode.Move,
                preservedTrue,
                NormalizeZeroRegisterValueOperand(
                    instruction.Op1Kind,
                    instruction.Op1Reg,
                    ConvertOperand(instruction, 1)));
            Add(
                address,
                OpCode.Move,
                preservedFalse,
                NormalizeZeroRegisterValueOperand(
                    instruction.Op2Kind,
                    instruction.Op2Reg,
                    ConvertOperand(instruction, 2)));
            // 条件选择是单条值指令，不是原生控制流。把它提升成跨地址跳转会制造伪基本块，
            // 特别是在同一地址连续生成多条 ISIL 时形成自环；保留为原子值选择供 CIL 内部展开。
            Add(address, OpCode.ConditionalSelect, destination, condition, preservedTrue, preservedFalse);
            return true;
        }

        bool TryEmitRecoveredVectorInstruction(uint machineCode)
        {
            if (!TryDecodeRecoveredVectorInstruction(machineCode, out var decoded))
                return false;

            var destination = new Register(null, $"V{decoded.DestinationRegister}");
            var firstVectorSource = new Register(null, $"V{decoded.FirstSourceRegister}");
            switch (decoded.Operation)
            {
                case Arm64RecoveredVectorOperation.DuplicateInt16:
                    Add(
                        address,
                        OpCode.VectorDuplicate,
                        destination,
                        new Register(null, $"W{decoded.FirstSourceRegister}"),
                        Imm(decoded.LaneCount),
                        Imm(decoded.ElementWidthBits));
                    return true;

                case Arm64RecoveredVectorOperation.WidenUnsignedInt16ToInt32:
                    Add(
                        address,
                        OpCode.VectorWidenUnsignedInt16ToInt32,
                        destination,
                        firstVectorSource,
                        Imm(decoded.LaneCount));
                    return true;

                case Arm64RecoveredVectorOperation.ShiftLeftInt32:
                    Add(
                        address,
                        OpCode.VectorShiftLeft,
                        destination,
                        firstVectorSource,
                        Imm(decoded.Immediate),
                        Imm(decoded.LaneCount),
                        Imm(decoded.ElementWidthBits));
                    return true;

                case Arm64RecoveredVectorOperation.CompareLessThanZeroInt32:
                    Add(
                        address,
                        OpCode.VectorCompareLessThanZero,
                        destination,
                        firstVectorSource,
                        Imm(decoded.LaneCount),
                        Imm(decoded.ElementWidthBits));
                    return true;

                case Arm64RecoveredVectorOperation.BitwiseSelect128:
                    Add(
                        address,
                        OpCode.VectorBitwiseSelect,
                        destination,
                        destination,
                        firstVectorSource,
                        new Register(null, $"V{decoded.SecondSourceRegister}"),
                        Imm(decoded.LaneCount * decoded.ElementWidthBits));
                    return true;

                case Arm64RecoveredVectorOperation.MultiplyFloat32ByElement:
                    Add(
                        address,
                        OpCode.VectorMultiplyByElement,
                        destination,
                        firstVectorSource,
                        new Register(
                            null,
                            $"V{decoded.SecondSourceRegister}.S[{decoded.Immediate}]"),
                        Imm(decoded.Immediate),
                        Imm(decoded.LaneCount),
                        Imm(decoded.ElementWidthBits));
                    return true;

                default:
                    throw new ArgumentOutOfRangeException(nameof(decoded.Operation));
            }
        }

        switch (instruction.Mnemonic)
        {
            case Arm64Mnemonic.MOV:
            case Arm64Mnemonic.MOVZ:
            case Arm64Mnemonic.FMOV:
            case Arm64Mnemonic.SXTW: // move and sign extend Wn to Xd
            case var scalarLoad when IsScalarLoadMnemonic(scalarLoad):
                //Load and move are (dest, src)

                if (instruction.Op1Kind == Arm64OperandKind.Memory)
                {
                    var vectorMemoryCode = ReadMachineCodeAtAddress(context, address);
                    if (TryDecodeUnsignedVector128Memory(
                            vectorMemoryCode,
                            out var isVectorLoad,
                            out _,
                            out _,
                            out var vectorByteOffset)
                        && isVectorLoad)
                    {
                        IOperand vectorSource;
                        if (TryCreateStackOffset(
                                instruction.MemBase,
                                instruction.MemAddendReg,
                                vectorByteOffset,
                                out var vectorStackOffset))
                        {
                            // Q寄存器栈加载必须与标量栈加载使用相同槽位身份；否则16字节聚合复制会退化为普通指针内存。
                            vectorSource = vectorStackOffset;
                        }
                        else if (adrpOffsets.TryGetValue(instruction.MemBase, out var vectorPage)
                            && instruction.MemAddendReg == Arm64Register.INVALID)
                        {
                            vectorSource = new MemoryOperand(
                                addend: checked((long)vectorPage + vectorByteOffset));
                        }
                        else
                        {
                            vectorSource = new MemoryOperand(
                                new Register(null, Arm64RegisterHelper.CanonicalName(instruction.MemBase)),
                                addend: vectorByteOffset);
                        }

                        if (instruction.Op0Kind == Arm64OperandKind.Register)
                            adrpOffsets.Remove(instruction.Op0Reg);
                        Add(address, OpCode.Move, ConvertOperand(instruction, 0), vectorSource);
                        break;
                    }
                }

                if (instruction.MemIsPreIndexed) //  such as  X8, [X19,#0x30]!
                {
                    //Regardless of anything else, we're trashing any possible ADRP offsets in the dest here, so let's clear that
                    if (instruction.Op0Kind == Arm64OperandKind.Register)
                        adrpOffsets.Remove(instruction.Op0Reg);

                    var operate = ConvertOperand(instruction, 1);
                    if (operate is MemoryOperand operand)
                    {
                        var register = (Register)operand.Base!;
                        // X19= X19, #0x30
                        Add(address, OpCode.Add, register, register, Imm(operand.Addend));
                        //X8 = [X19]
                        Add(address, OpCode.Move, ConvertOperand(instruction, 0), new MemoryOperand(new Register(null, register.ToString()!.ToUpperInvariant())));
                        break;
                    }
                }

                if (instruction.Op1Kind == Arm64OperandKind.Memory
                    && TryCreateAdrpMemoryOperand(
                        adrpOffsets,
                        instruction.MemBase,
                        instruction.MemAddendReg,
                        instruction.MemOffset,
                        out var absoluteLoadSource))
                {
                    // ADRP后的标量LDR无论页内偏移是否为零都属于绝对地址加载；与存储路径
                    // 共用同一解析函数，避免零偏移槽退化为“页地址再解引用”的动态内存链。
                    if (instruction.Op0Kind == Arm64OperandKind.Register)
                        adrpOffsets.Remove(instruction.Op0Reg);

                    Add(address, OpCode.Move, ConvertOperand(instruction, 0), absoluteLoadSource);
                    break;
                }

                //And again here we're trashing any possible ADRP offsets in the dest here, so let's clear that
                if (instruction.Op0Kind == Arm64OperandKind.Register)
                    adrpOffsets.Remove(instruction.Op0Reg);

                if (instruction.Mnemonic == Arm64Mnemonic.FMOV
                    && TryGetFmovBitReinterpretation(
                        instruction.Op0Kind,
                        instruction.Op0Reg,
                        instruction.Op1Kind,
                        instruction.Op1Reg,
                        out var reinterpretOpCode,
                        out var reinterpretWidthBits))
                {
                    Add(
                        address,
                        reinterpretOpCode,
                        ConvertOperand(instruction, 0),
                        ConvertOperand(instruction, 1),
                        Imm(reinterpretWidthBits));
                    break;
                }

                IOperand moveSource;
                if (instruction.Mnemonic == Arm64Mnemonic.FMOV
                    && instruction.Op1Kind == Arm64OperandKind.FloatingPointImmediate)
                {
                    var machineCode = ReadMachineCodeAtAddress(context, address);
                    if (!TryDecodeScalarFloatingPointImmediate(
                            machineCode,
                            out var precisionBits,
                            out var floatingImmediate))
                    {
                        throw new InvalidOperationException(
                            $"FMOV浮点立即数原始编码无效：0x{machineCode:X8} @ 0x{address:X}");
                    }

                    // FMOV S 与 FMOV D 的立即数编码都由解码器返回 double 承载，但 ISIL
                    // 字面量必须保持目标原生精度，避免 S 寄存器先被错误定型为 System.Double。
                    moveSource = precisionBits == 32
                        ? new FloatLiteral((float)floatingImmediate)
                        : new DoubleLiteral(floatingImmediate);
                }
                else
                {
                    moveSource = ConvertMoveSourceOperand(instruction);
                }

                Add(address, OpCode.Move, ConvertOperand(instruction, 0), moveSource);
                // MOV、FMOV、SXTW 与 LDR 家族均不写 NZCV。保留此前 CMP/TST 等标志生产者，
                // 使跨加载的 CSEL/条件分支继续读取原生指令流中的真实条件。
                break;
            case Arm64Mnemonic.MOVK:
                {
                    // MOVK 保留目标寄存器其他半字；必须读原始编码的 hw 字段，不能从已移位的显示立即数反推。
                    var machineCode = ReadMachineCodeAtAddress(context, address);
                    var destination = ConvertOperand(instruction, 0);
                    if (!TryCreateMoveKeepInstructions(machineCode, destination, out var recovered))
                    {
                        throw new InvalidOperationException(
                            $"MOVK原始编码无效：0x{machineCode:X8} @ 0x{address:X}");
                    }

                    if (instruction.Op0Kind == Arm64OperandKind.Register)
                        adrpOffsets.Remove(instruction.Op0Reg);

                    foreach (var recoveredInstruction in recovered)
                    {
                        var emitted = Add(
                            address,
                            recoveredInstruction.OpCode,
                            recoveredInstruction.Operands.ToList());
                        emitted.IntegerWidthBits = recoveredInstruction.IntegerWidthBits;
                    }
                    break;
                }
            case Arm64Mnemonic.MOVN:
                {
                    // dest = ~src

                    //See above re: ADRP offsets
                    if (instruction.Op0Kind == Arm64OperandKind.Register)
                        adrpOffsets.Remove(instruction.Op0Reg);

                    var temp2 = new Register(null, "TEMP");
                    Add(address, OpCode.Move, temp2, ConvertOperand(instruction, 1));
                    Add(address, OpCode.Not, temp2, temp2);
                    Add(address, OpCode.Move, ConvertOperand(instruction, 0), temp2);
                    break;
                }
            case Arm64Mnemonic.MOVI:
                {
                    var machineCode = ReadMachineCodeAtAddress(context, address);
                    if (TryDecodeReplicatedVectorMoveImmediate16(
                            machineCode,
                            out var vectorWidthBits16,
                            out var laneCount16,
                            out var elementBits16))
                    {
                        if (vectorWidthBits16 != 64 || laneCount16 != 4)
                        {
                            Add(address, OpCode.NotImplemented, new StringLiteral(
                                $"Instruction MOVI {laneCount16}H requires 128-bit storage."));
                            break;
                        }

                        ulong packed = 0;
                        for (var lane = 0; lane < laneCount16; lane++)
                            packed |= (ulong)elementBits16 << (lane * 16);
                        Add(address, OpCode.Move, ConvertOperand(instruction, 0), Imm(packed));
                        break;
                    }

                    if (TryDecodeReplicatedVectorMoveImmediate32(
                            machineCode,
                            out _,
                            out _,
                            out var elementBits))
                    {
                        // 同一位型被复制到全部 S 通道；FloatLiteral 保留后续标量读取的精确 IEEE-754 语义。
                        var elementValue = BitConverter.ToSingle(BitConverter.GetBytes(elementBits), 0);
                        Add(address, OpCode.Move, ConvertOperand(instruction, 0), new FloatLiteral(elementValue));
                        break;
                    }

                    // 其他已确认的零立即数仍可由托管零精确表达。
                    if (!IsExactlyRepresentableMovi(instruction.Op1Kind, instruction.Op1Imm))
                    {
                        Add(address, OpCode.NotImplemented, new StringLiteral($"Instruction MOVI immediate {instruction.Op1Imm} not yet implemented."));
                        break;
                    }

                    Add(address, OpCode.Move, ConvertOperand(instruction, 0), Imm(0));
                    break;
                }
            case var scalarStore when IsScalarStoreMnemonic(scalarStore):
                //Store is (src, dest)
                {
                    var storeWidthBits = GetScalarStoreWidthBits(
                        instruction.Mnemonic,
                        instruction.Op0Reg);
                    var vectorMemoryCode = ReadMachineCodeAtAddress(context, address);
                    if (TryDecodeUnsignedVector128Memory(
                            vectorMemoryCode,
                            out var isVectorLoad,
                            out _,
                            out _,
                            out var vectorByteOffset)
                        && !isVectorLoad)
                    {
                        IOperand vectorDestination = TryCreateStackOffset(
                            instruction.MemBase,
                            instruction.MemAddendReg,
                            vectorByteOffset,
                            out var vectorStackOffset)
                            ? vectorStackOffset
                            : new MemoryOperand(
                                new Register(null, Arm64RegisterHelper.CanonicalName(instruction.MemBase)),
                                addend: vectorByteOffset);
                        AddMemory(
                            address,
                            128,
                            OpCode.Move,
                            vectorDestination,
                            ConvertStoreSourceOperand(instruction));
                        break;
                    }

                    if (!instruction.MemIsPreIndexed
                        && TryCreateAdrpMemoryOperand(
                            adrpOffsets,
                            instruction.MemBase,
                            instruction.MemAddendReg,
                            instruction.MemOffset,
                            out var absoluteStoreDestination))
                    {
                        AddMemory(
                            address,
                            storeWidthBits,
                            OpCode.Move,
                            absoluteStoreDestination,
                            ConvertStoreSourceOperand(instruction));
                        break;
                    }

                    if (instruction.MemIsPreIndexed
                        && TryCreateStackOffset(
                            instruction.MemBase,
                            instruction.MemAddendReg,
                            instruction.MemOffset,
                            out var preIndexedStoreOffset))
                    {
                        // 预索引先调整SP，再把值写入新SP的零偏移槽位。
                        Add(address, OpCode.ShiftStack, Imm(preIndexedStoreOffset.Offset));
                        AddMemory(
                            address,
                            storeWidthBits,
                            OpCode.Move,
                            new StackOffset(0),
                            ConvertStoreSourceOperand(instruction));
                        break;
                    }

                    if (TryDecodePreIndexedRegisterWriteback(
                            instruction.MemIndexMode,
                            instruction.MemBase,
                            instruction.MemAddendReg,
                            out var preIndexedWritebackRegister))
                    {
                        // 普通寄存器前索引必须显式产生基址写回；否则后续 [Xn] 会继续读取旧对象。
                        // 先更新基址再访问零偏移内存，精确对应 ARM64 的 pre-index 语义。
                        Add(
                            address,
                            OpCode.Add,
                            preIndexedWritebackRegister,
                            preIndexedWritebackRegister,
                            Imm(instruction.MemOffset));
                        AddMemory(
                            address,
                            storeWidthBits,
                            OpCode.Move,
                            new MemoryOperand(new Register(null, preIndexedWritebackRegister.Name)),
                            ConvertStoreSourceOperand(instruction));

                        if (adrpOffsets.TryGetValue(instruction.MemBase, out var knownBaseAddress))
                            adrpOffsets[instruction.MemBase] = unchecked(knownBaseAddress + (ulong)instruction.MemOffset);
                        break;
                    }

                    AddMemory(
                        address,
                        storeWidthBits,
                        OpCode.Move,
                        ConvertOperand(instruction, 1),
                        ConvertStoreSourceOperand(instruction));
                    break;
                }
            case Arm64Mnemonic.STP:
                // store pair of registers (reg1, reg2, dest)
                {
                    var dest3 = ConvertOperand(instruction, 2);
                    if (dest3 is StackOffset stackOffset)
                    {
                        if (instruction.MemIsPreIndexed)
                        {
                            Add(address, OpCode.ShiftStack, Imm(stackOffset.Offset));
                            stackOffset = new StackOffset(0);
                        }

                        var size = Arm64RegisterHelper.SizeBytes(instruction.Op0Reg);
                        AddMemory(
                            address,
                            size * 8,
                            OpCode.Move,
                            stackOffset,
                            ConvertStorePairSourceOperand(instruction, 0));
                        AddMemory(
                            address,
                            size * 8,
                            OpCode.Move,
                            new StackOffset(stackOffset.Offset + size),
                            ConvertStorePairSourceOperand(instruction, 1));
                    }
                    else if (dest3 is MemoryOperand memory)
                    {
                        var firstRegister = ConvertOperand(instruction, 0);
                        var size = Arm64RegisterHelper.SizeBytes(instruction.Op0Reg);
                        AddMemory(address, size * 8, OpCode.Move, dest3, firstRegister); // [REG + offset] = REG1
                        memory = new MemoryOperand((Register)memory.Base!, addend: memory.Addend + size);
                        dest3 = memory;
                        AddMemory(address, size * 8, OpCode.Move, dest3, ConvertOperand(instruction, 1)); // [REG + offset + size] = REG2
                    }
                    else // reg pointer
                    {
                        var firstRegister = ConvertOperand(instruction, 0);
                        var size = Arm64RegisterHelper.SizeBytes(instruction.Op0Reg);
                        AddMemory(address, size * 8, OpCode.Move, dest3, firstRegister);
                        Add(address, OpCode.Add, dest3, dest3, Imm(size));
                        AddMemory(address, size * 8, OpCode.Move, dest3, ConvertOperand(instruction, 1));
                    }
                }
                break;
            case Arm64Mnemonic.ADRP:
                // ADRP立即数相对当前指令页；ISIL局部量与后续内存折叠统一保存绝对页地址。
                var absolutePageAddress = ResolveAdrpPageAddress(address, instruction.Op1Imm);
                Add(address, OpCode.Move, ConvertOperand(instruction, 0), Imm(absolutePageAddress));
                adrpOffsets[instruction.Op0Reg] = absolutePageAddress;
                break;
            case Arm64Mnemonic.LDP when instruction.Op2Kind == Arm64OperandKind.Memory:
                //LDP (dest1, dest2, [mem]) - basically just treat as two loads, with the second offset by the length of the first
                var destRegSize = instruction.Op0Reg switch
                {
                    //vector (128 bit)
                    >= Arm64Register.V0 and <= Arm64Register.V31 => 16, //TODO check if this is accurate
                    //double
                    >= Arm64Register.D0 and <= Arm64Register.D31 => 8,
                    //single
                    >= Arm64Register.S0 and <= Arm64Register.S31 => 4,
                    //half
                    >= Arm64Register.H0 and <= Arm64Register.H31 => 2,
                    //word
                    >= Arm64Register.W0 and <= Arm64Register.W31 => 4,
                    //x
                    >= Arm64Register.X0 and <= Arm64Register.X31 => 8,
                    _ => throw new($"Unknown register size for LDP: {instruction.Op0Reg}")
                };

                var dest1 = ConvertOperand(instruction, 0);
                var dest2 = ConvertOperand(instruction, 1);
                var mem = ConvertOperand(instruction, 2);

                IOperand mem2;
                if (mem is StackOffset stackOffset2)
                {
                    if (instruction.MemIsPreIndexed)
                    {
                        Add(address, OpCode.ShiftStack, Imm(stackOffset2.Offset));
                        stackOffset2 = new StackOffset(0);
                        mem = stackOffset2;
                    }

                    mem2 = new StackOffset(stackOffset2.Offset + destRegSize);
                }
                else
                {
                    // 非栈内存继续保持基址和第二寄存器宽度的精确偏移。
                    var memInternal = (MemoryOperand)mem;
                    mem2 = new MemoryOperand((Register)memInternal.Base!, addend: memInternal.Addend + destRegSize);
                }

                Add(address, OpCode.Move, dest1, mem);
                Add(address, OpCode.Move, dest2, mem2);
                if (TryDecodePostIndexedStackAdjustment(
                        instruction.MemIndexMode,
                        instruction.MemBase,
                        instruction.MemOffset,
                        out var postIndexedStackDelta))
                    Add(address, OpCode.ShiftStack, Imm(postIndexedStackDelta));
                break;
            case Arm64Mnemonic.BL:
                AddCall(context, address, instruction.BranchTarget);
                break;
            case Arm64Mnemonic.RET:
                Add(address, OpCode.Return, GetReturnOperandsForContext(context));
                break;
            case Arm64Mnemonic.B:
                var target = instruction.BranchTarget;
                var branchConditionCode = instruction.MnemonicConditionCode;

                if (!IsUnconditionalBranchCode(branchConditionCode))
                {
                    if (IsBranchOutsideMethod(target, context.UnderlyingPointer, context.RawBytes.Length))
                    {
                        Add(address, OpCode.NotImplemented, new StringLiteral($"Conditional branch {branchConditionCode} leaves the current method."));
                        break;
                    }

                    if (!CanEmitConditionalBranch(branchConditionCode, inputFlagState))
                    {
                        Add(address, OpCode.NotImplemented, new StringLiteral($"Conditional branch {branchConditionCode} has no exactly modeled flag producer."));
                        break;
                    }

                    if (!TryEmitCondition(
                            branchConditionCode,
                            "BRANCH_CONDITION",
                            out var branchCondition))
                        throw new InvalidOperationException("条件分支的可发射判定与条件构造结果不一致。");

                    Add(address, OpCode.ConditionalJump, Imm(target), branchCondition);
                    break;
                }

                if (IsSelfTailBranch(target, context.UnderlyingPointer, address)
                    || IsBranchOutsideMethod(target, context.UnderlyingPointer, context.RawBytes.Length))
                {
                    // 跳到相邻方法首地址或自身原生序言均属于尾调用，随后返回当前托管方法。
                    var returnOperands = GetReturnOperandsForContext(context);
                    AddCall(context, address, target);
                    Add(address, OpCode.Return, returnOperands);
                }
                else
                {
                    Add(address, OpCode.Jump, Imm(instruction.BranchTarget));
                }

                break;
            case Arm64Mnemonic.BLR:
            case Arm64Mnemonic.BR:
                // BLR 是带返回地址的间接调用；BR 是不返回当前点的间接尾跳转。
                AddIndirectTransfer(instruction.Mnemonic, address, ConvertOperand(instruction, 0));
                break;
            case Arm64Mnemonic.CBNZ:
            case Arm64Mnemonic.CBZ:
                {
                    // CBZ/CBNZ 不写 NZCV，因此使用独立条件寄存器，保留此前 CMP/SUBS 的标志状态。
                    var targetAddr = (ulong)((long)instruction.Address + instruction.Op1Imm);
                    var conditionRegister = new Register(null, "COMPARE_AND_BRANCH_CONDITION");
                    var comparisonOpCode = instruction.Mnemonic == Arm64Mnemonic.CBZ
                        ? OpCode.CheckEqual
                        : OpCode.CheckNotEqual;
                    Add(address, comparisonOpCode, conditionRegister, ConvertOperand(instruction, 0), Imm(0));
                    Add(address, OpCode.ConditionalJump, Imm(targetAddr), conditionRegister);
                }
                break;

            case Arm64Mnemonic.CMP:
            case Arm64Mnemonic.FCMP:
                var compareLeft = new Register(null, "FLAG_COMPARE_LEFT");
                var compareRight = new Register(null, "FLAG_COMPARE_RIGHT");
                Add(address, OpCode.Move, compareLeft, ConvertOperand(instruction, 0));
                Add(address, OpCode.Move, compareRight, ConvertOperand(instruction, 1));
                Add(address, OpCode.CheckEqual, new Register(null, "Z"), compareLeft, compareRight);
                if (instruction.Mnemonic == Arm64Mnemonic.CMP)
                {
                    // CMP 是丢弃结果的 SUBS；保留固定宽度减法结果可精确重建 N 标志，不能把 MI 简化为 LT。
                    var comparisonDifference = new Register(null, "FLAG_COMPARE_DIFFERENCE");
                    Add(address, OpCode.Subtract, comparisonDifference, compareLeft, compareRight);
                    Add(address, OpCode.CheckLess, new Register(null, "N"), comparisonDifference, Imm(0));
                }
                flagState = instruction.Mnemonic == Arm64Mnemonic.FCMP
                    ? Arm64FlagState.FloatingComparison
                    : Arm64FlagState.Comparison;
                break;

            case Arm64Mnemonic.CCMP:
                {
                    if (!TryEmitCondition(
                            instruction.FinalOpConditionCode,
                            "CCMP_CONDITION",
                            out var ccmpCondition))
                    {
                        Add(address, OpCode.NotImplemented, new StringLiteral($"Instruction CCMP condition {instruction.FinalOpConditionCode} has no exactly modeled flag producer."));
                        break;
                    }

                    // CCMP 必须把门条件、比较两端与回退 NZCV 一并冻结；后继分支或 CSEL
                    // 按自身条件码只计算一次比较，再原子选择真实比较结果或回退标志结果。
                    Add(
                        address,
                        OpCode.Move,
                        new Register(null, "CCMP_GATE"),
                        ccmpCondition);
                    Add(
                        address,
                        OpCode.Move,
                        new Register(null, "CCMP_COMPARE_LEFT"),
                        ConvertOperand(instruction, 0));
                    Add(
                        address,
                        OpCode.Move,
                        new Register(null, "CCMP_COMPARE_RIGHT"),
                        ConvertOperand(instruction, 1));
                    conditionalComparisonFallbackNzcv = instruction.Op2Imm & 0xF;
                    flagState = Arm64FlagState.ConditionalComparison;
                    break;
                }

            case Arm64Mnemonic.CSET:
                {
                    var destination = ConvertOperand(instruction, 0);
                    if (!TryEmitCondition(
                            instruction.FinalOpConditionCode,
                            "CSET_CONDITION",
                            out var condition))
                    {
                        Add(address, OpCode.NotImplemented, new StringLiteral($"Instruction CSET condition {instruction.FinalOpConditionCode} not yet implemented."));
                        break;
                    }

                    Add(address, OpCode.Move, destination, condition);
                    break;
                }

            case Arm64Mnemonic.CSEL:
                {
                    if (!TryEmitConditionalSelect("CSEL"))
                    {
                        Add(address, OpCode.NotImplemented, new StringLiteral($"Instruction CSEL condition {instruction.FinalOpConditionCode} not yet implemented."));
                    }
                    break;
                }

            case Arm64Mnemonic.FCSEL:
                {
                    if (!TryEmitConditionalSelect("FCSEL"))
                        Add(address, OpCode.NotImplemented, new StringLiteral($"Instruction FCSEL condition {instruction.FinalOpConditionCode} not yet implemented."));
                    break;
                }

            case Arm64Mnemonic.FCVT:
                {
                    if (!TryGetFloatingPointPrecisionBits(instruction.Op0Reg, out var destinationBits) ||
                        !TryGetFloatingPointPrecisionBits(instruction.Op1Reg, out var sourceBits))
                    {
                        Add(address, OpCode.NotImplemented, new StringLiteral("Instruction FCVT register widths not yet implemented."));
                        break;
                    }

                    Add(
                        address,
                        OpCode.ConvertFloatingPointPrecision,
                        ConvertOperand(instruction, 0),
                        ConvertOperand(instruction, 1),
                        Imm(destinationBits),
                        Imm(sourceBits));
                    break;
                }

            case Arm64Mnemonic.FCVTZS:
                {
                    if (!TryGetSignedIntegerPayloadWidthBits(instruction.Op0Reg, out var destinationBits) ||
                        !TryGetFloatingPointPrecisionBits(instruction.Op1Reg, out var sourceBits))
                    {
                        Add(address, OpCode.NotImplemented, new StringLiteral("Instruction FCVTZS register widths not yet implemented."));
                        break;
                    }

                    Add(
                        address,
                        OpCode.ConvertFloatToSignedInteger,
                        ConvertOperand(instruction, 0),
                        ConvertOperand(instruction, 1),
                        Imm(destinationBits),
                        Imm(sourceBits));
                    break;
                }

            case Arm64Mnemonic.SCVTF:
                {
                    if (!TryGetFloatingPointPrecisionBits(instruction.Op0Reg, out var destinationBits) ||
                        !TryGetSignedIntegerPayloadWidthBits(instruction.Op1Reg, out var sourceBits))
                    {
                        Add(address, OpCode.NotImplemented, new StringLiteral("Instruction SCVTF register widths not yet implemented."));
                        break;
                    }

                    Add(
                        address,
                        OpCode.ConvertSignedIntegerToFloat,
                        ConvertOperand(instruction, 0),
                        ConvertOperand(instruction, 1),
                        Imm(destinationBits),
                        Imm(sourceBits));
                    break;
                }

            case Arm64Mnemonic.FRINTP:
            case Arm64Mnemonic.FRINTM:
                {
                    if (!TryGetFloatingPointPrecisionBits(instruction.Op0Reg, out var destinationBits) ||
                        !TryGetFloatingPointPrecisionBits(instruction.Op1Reg, out var sourceBits) ||
                        destinationBits != sourceBits)
                    {
                        Add(address, OpCode.NotImplemented, new StringLiteral($"Instruction {instruction.Mnemonic} register widths not yet implemented."));
                        break;
                    }

                    var opCode = instruction.Mnemonic == Arm64Mnemonic.FRINTP
                        ? OpCode.RoundFloatTowardPositiveInfinity
                        : OpCode.RoundFloatTowardNegativeInfinity;
                    Add(
                        address,
                        opCode,
                        ConvertOperand(instruction, 0),
                        ConvertOperand(instruction, 1),
                        Imm(destinationBits));
                    break;
                }

            case Arm64Mnemonic.CSINC:
                {
                    if (!TryEmitCondition(
                            instruction.FinalOpConditionCode,
                            "CSINC_CONDITION",
                            out var condition))
                    {
                        Add(address, OpCode.NotImplemented, new StringLiteral($"Instruction CSINC condition {instruction.FinalOpConditionCode} not yet implemented."));
                        break;
                    }

                    var destination = ConvertOperand(instruction, 0);
                    var preservedTrue = new Register(null, "CSINC_TRUE");
                    var preservedFalse = new Register(null, "CSINC_FALSE");
                    var incrementedFalse = new Register(null, "CSINC_FALSE_INCREMENTED");
                    Add(
                        address,
                        OpCode.Move,
                        preservedTrue,
                        NormalizeZeroRegisterValueOperand(
                            instruction.Op1Kind,
                            instruction.Op1Reg,
                            ConvertOperand(instruction, 1)));
                    Add(
                        address,
                        OpCode.Move,
                        preservedFalse,
                        NormalizeZeroRegisterValueOperand(
                            instruction.Op2Kind,
                            instruction.Op2Reg,
                            ConvertOperand(instruction, 2)));
                    AddInteger(address, OpCode.Add, incrementedFalse, preservedFalse, Imm(1));
                    // CSINC 是单条值指令：条件为真选 Rn，否则选 Rm+1。
                    // 伪跳转展开会制造多余基本块并切断目标标量类型，因此保留原子条件选择。
                    Add(
                        address,
                        OpCode.ConditionalSelect,
                        destination,
                        condition,
                        preservedTrue,
                        incrementedFalse);
                    break;
                }

            case Arm64Mnemonic.CSNEG:
            case Arm64Mnemonic.CSINV:
                {
                    if (!TryEmitCondition(
                            instruction.FinalOpConditionCode,
                            instruction.Mnemonic + "_CONDITION",
                            out var condition))
                    {
                        Add(address, OpCode.NotImplemented, new StringLiteral($"Instruction {instruction.Mnemonic} condition {instruction.FinalOpConditionCode} not yet implemented."));
                        break;
                    }

                    var destination = ConvertOperand(instruction, 0);
                    var preservedTrue = new Register(null, instruction.Mnemonic + "_TRUE");
                    var preservedFalse = new Register(null, instruction.Mnemonic + "_FALSE");
                    var transformedFalse = new Register(null, instruction.Mnemonic + "_FALSE_TRANSFORMED");
                    Add(
                        address,
                        OpCode.Move,
                        preservedTrue,
                        NormalizeZeroRegisterValueOperand(
                            instruction.Op1Kind,
                            instruction.Op1Reg,
                            ConvertOperand(instruction, 1)));
                    Add(
                        address,
                        OpCode.Move,
                        preservedFalse,
                        NormalizeZeroRegisterValueOperand(
                            instruction.Op2Kind,
                            instruction.Op2Reg,
                            ConvertOperand(instruction, 2)));
                    var falseTransform = GetConditionalFalseTransformOpCode(instruction.Mnemonic)
                        ?? throw new InvalidOperationException($"条件变换指令未映射：{instruction.Mnemonic}");
                    Add(
                        address,
                        falseTransform,
                        transformedFalse,
                        preservedFalse);
                    Add(address, OpCode.ConditionalSelect, destination, condition, preservedTrue, transformedFalse);
                    break;
                }

            case Arm64Mnemonic.CINC:
                {
                    if (!TryEmitCondition(
                            instruction.FinalOpConditionCode,
                            "CINC_CONDITION",
                            out var condition))
                    {
                        Add(address, OpCode.NotImplemented, new StringLiteral($"Instruction CINC condition {instruction.FinalOpConditionCode} not yet implemented."));
                        break;
                    }

                    var destination = ConvertOperand(instruction, 0);
                    var preservedSource = new Register(null, "CINC_SOURCE");
                    var incrementedSource = new Register(null, "CINC_INCREMENTED");
                    Add(
                        address,
                        OpCode.Move,
                        preservedSource,
                        NormalizeZeroRegisterValueOperand(
                            instruction.Op1Kind,
                            instruction.Op1Reg,
                            ConvertOperand(instruction, 1)));
                    AddInteger(address, OpCode.Add, incrementedSource, preservedSource, Imm(1));
                    // CINC 条件为真时选 Rn+1，否则保留 Rn；与 CSEL 使用同一原子值语义。
                    Add(
                        address,
                        OpCode.ConditionalSelect,
                        destination,
                        condition,
                        incrementedSource,
                        preservedSource);
                    break;
                }

            case Arm64Mnemonic.TBNZ:
            // TBNZ R<t>, #imm, label
            // test bit and branch if NonZero
            case Arm64Mnemonic.TBZ:
                // TBZ R<t>, #imm, label
                // test bit and branch if Zero
                {
                    var targetAddr = (ulong)((long)instruction.Address + instruction.Op2Imm);
                    var bit = 1L << (int)instruction.Op1Imm;
                    var maskedBit = new Register(null, "TEST_BIT_VALUE");
                    var conditionRegister = new Register(null, "TEST_BIT_CONDITION");
                    var src = ConvertOperand(instruction, 0);
                    Add(address, OpCode.And, maskedBit, src, Imm(bit));
                    var comparisonOpCode = instruction.Mnemonic == Arm64Mnemonic.TBZ
                        ? OpCode.CheckEqual
                        : OpCode.CheckNotEqual;
                    Add(address, comparisonOpCode, conditionRegister, maskedBit, Imm(0));
                    Add(address, OpCode.ConditionalJump, Imm(targetAddr), conditionRegister);
                }
                break;
            case Arm64Mnemonic.UBFM:
            case Arm64Mnemonic.LSR:
            case Arm64Mnemonic.UBFIZ:
                {
                    if (!TryCreateUnsignedBitfieldMoveInstructions(instruction, out var recovered))
                    {
                        Add(address, OpCode.NotImplemented, new StringLiteral("Instruction UBFM operand widths are not exactly modeled."));
                        break;
                    }

                    foreach (var recoveredInstruction in recovered)
                    {
                        var emitted = Add(address, recoveredInstruction.OpCode, recoveredInstruction.Operands.ToList());
                        emitted.IntegerWidthBits = recoveredInstruction.IntegerWidthBits;
                    }
                }
                break;

            case Arm64Mnemonic.MUL:
                // 整数乘法保留目标寄存器位宽，供退SSA后的标量载体定型。
                AddInteger(address, OpCode.Multiply, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                break;
            case Arm64Mnemonic.SMADDL:
            case Arm64Mnemonic.SMULL:
                {
                    if (!TryCreateSignedMultiplyAddLongInstructions(instruction, address, out var recovered))
                    {
                        Add(address, OpCode.NotImplemented, new StringLiteral("Instruction SMADDL operand widths are not exactly modeled."));
                        break;
                    }

                    foreach (var recoveredInstruction in recovered)
                    {
                        var emitted = Add(address, recoveredInstruction.OpCode, recoveredInstruction.Operands.ToList());
                        emitted.IntegerWidthBits = recoveredInstruction.IntegerWidthBits;
                    }
                    break;
                }
            case Arm64Mnemonic.FMUL:
                Add(address, OpCode.Multiply, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                break;

            case Arm64Mnemonic.FDIV:
                Add(address, OpCode.Divide, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                break;

            case Arm64Mnemonic.FABS:
                {
                    if (!TryGetMatchingScalarFloatingWidth(
                            [
                                (instruction.Op0Kind, instruction.Op0Reg),
                                (instruction.Op1Kind, instruction.Op1Reg),
                            ],
                            out var widthBits))
                    {
                        Add(address, OpCode.NotImplemented, new StringLiteral("Instruction FABS register widths not yet implemented."));
                        break;
                    }

                    Add(
                        address,
                        OpCode.AbsoluteNumber,
                        ConvertOperand(instruction, 0),
                        ConvertOperand(instruction, 1),
                        Imm(widthBits));
                    break;
                }

            case Arm64Mnemonic.FABD:
                {
                    if (!TryGetMatchingScalarFloatingWidth(
                            [
                                (instruction.Op0Kind, instruction.Op0Reg),
                                (instruction.Op1Kind, instruction.Op1Reg),
                                (instruction.Op2Kind, instruction.Op2Reg),
                            ],
                            out var widthBits))
                    {
                        Add(address, OpCode.NotImplemented, new StringLiteral("Instruction FABD register widths not yet implemented."));
                        break;
                    }

                    Add(
                        address,
                        OpCode.AbsoluteDifference,
                        ConvertOperand(instruction, 0),
                        ConvertOperand(instruction, 1),
                        ConvertOperand(instruction, 2),
                        Imm(widthBits));
                    break;
                }

            case Arm64Mnemonic.FMAXNM:
                {
                    if (!TryGetMatchingScalarFloatingWidth(
                            [
                                (instruction.Op0Kind, instruction.Op0Reg),
                                (instruction.Op1Kind, instruction.Op1Reg),
                                (instruction.Op2Kind, instruction.Op2Reg),
                            ],
                            out var widthBits))
                    {
                        Add(address, OpCode.NotImplemented, new StringLiteral("Instruction FMAXNM register widths not yet implemented."));
                        break;
                    }

                    Add(
                        address,
                        OpCode.MaximumNumber,
                        ConvertOperand(instruction, 0),
                        ConvertOperand(instruction, 1),
                        ConvertOperand(instruction, 2),
                        Imm(widthBits));
                    break;
                }

            case Arm64Mnemonic.FNEG:
                {
                    if (!CanEmitScalarFloatingNegate(
                            instruction.Op0Kind,
                            instruction.Op0Reg,
                            instruction.Op1Kind,
                            instruction.Op1Reg))
                    {
                        Add(address, OpCode.NotImplemented, new StringLiteral("Instruction FNEG register widths not yet implemented."));
                        break;
                    }

                    Add(address, OpCode.Negate, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1));
                    break;
                }

            case Arm64Mnemonic.FNMUL:
                {
                    var product = new Register(null, $"FNMUL_PRODUCT_{address:X}");
                    Add(address, OpCode.Multiply, product, ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                    Add(address, OpCode.Negate, ConvertOperand(instruction, 0), product);
                    break;
                }

            case Arm64Mnemonic.ADD:
                if (TryDecodeStackPointerAdjustment(
                        instruction.Mnemonic,
                        instruction.Op0Kind,
                        instruction.Op0Reg,
                        instruction.Op1Kind,
                        instruction.Op1Reg,
                        instruction.Op2Kind,
                        instruction.Op2Imm,
                        out var addStackDelta))
                {
                    Add(address, OpCode.ShiftStack, Imm(addStackDelta));
                    break;
                }

                if (TryCreateStackAddressOffset(
                        instruction.Mnemonic,
                        instruction.Op0Kind,
                        instruction.Op0Reg,
                        instruction.Op1Kind,
                        instruction.Op1Reg,
                        instruction.Op2Kind,
                        instruction.Op2Imm,
                        out var addressedStackOffset))
                {
                    Add(
                        address,
                        OpCode.Move,
                        ConvertOperand(instruction, 0),
                        new AddressOf(addressedStackOffset));
                    break;
                }

                // ADD 的扩展寄存器和移位寄存器格式必须先恢复第三操作数语义。
                if (!Arm64AddOperandHelper.TryEmit(
                        instruction,
                        ConvertOperand(instruction, 2),
                        (opCode, operands) => Add(address, opCode, operands),
                        out var addRight))
                {
                    Add(address, OpCode.NotImplemented, new StringLiteral($"Instruction ADD modifier {instruction.FinalOpExtendType}/{instruction.FinalOpShiftType} is not exactly modeled."));
                    break;
                }

                AddInteger(address, OpCode.Add, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), addRight);
                break;
            case Arm64Mnemonic.FADD:
                // 浮点加法没有整数扩展寄存器格式。
                Add(address, OpCode.Add, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                break;

            case Arm64Mnemonic.SUB:
                if (TryDecodeStackPointerAdjustment(
                        instruction.Mnemonic,
                        instruction.Op0Kind,
                        instruction.Op0Reg,
                        instruction.Op1Kind,
                        instruction.Op1Reg,
                        instruction.Op2Kind,
                        instruction.Op2Imm,
                        out var subtractStackDelta))
                {
                    Add(address, OpCode.ShiftStack, Imm(subtractStackDelta));
                    break;
                }

                AddInteger(address, OpCode.Subtract, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                break;
            case Arm64Mnemonic.FSUB:
                //Sub is (dest, src1, src2)
                Add(address, OpCode.Subtract, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                break;

            case Arm64Mnemonic.AND:
                //And is (dest, src1, src2)
                AddInteger(address, OpCode.And, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                break;

            case Arm64Mnemonic.ADDS:
            case Arm64Mnemonic.SUBS:
            case Arm64Mnemonic.ANDS:
                {
                    var writesZeroRegister = instruction.Op0Kind == Arm64OperandKind.Register
                                             && Arm64RegisterHelper.IsZeroRegister(instruction.Op0Reg);
                    // TST/ANDS WZR|XZR 只写 NZCV，不得定义后续指令可读的 W31/X31 值。
                    // 使用独立旗标结果既保留零比较，又阻断零寄存器污染跨指令传播。
                    var dest = writesZeroRegister
                        ? new Register(null, "FLAG_LOGICAL_RESULT")
                        : ConvertOperand(instruction, 0);
                    var src1 = NormalizeZeroRegisterValueOperand(
                        instruction.Op1Kind,
                        instruction.Op1Reg,
                        ConvertOperand(instruction, 1));
                    var src2 = NormalizeZeroRegisterValueOperand(
                        instruction.Op2Kind,
                        instruction.Op2Reg,
                        ConvertOperand(instruction, 2));

                    if (instruction.Mnemonic == Arm64Mnemonic.ADDS
                        && TryDecodeCmnSignedThreshold(
                            instruction.Op0Reg,
                            instruction.Op2Kind,
                            instruction.Op2Imm,
                            out _,
                            out var signedThreshold))
                    {
                        // CMN 不写通用寄存器；直接冻结比较两端，同时恢复 Z/N 供 EQ/NE/MI/PL 使用。
                        // GT/LE/GE/LT 由同一对 FLAG_COMPARE 操作数恢复，完整保留加法溢出的有符号语义。
                        var cmnCompareLeft = new Register(null, "FLAG_COMPARE_LEFT");
                        var cmnCompareRight = new Register(null, "FLAG_COMPARE_RIGHT");
                        var cmnDifference = new Register(null, "FLAG_COMPARE_DIFFERENCE");
                        Add(address, OpCode.Move, cmnCompareLeft, src1);
                        Add(address, OpCode.Move, cmnCompareRight, Imm(signedThreshold));
                        Add(address, OpCode.Subtract, cmnDifference, cmnCompareLeft, cmnCompareRight);
                        Add(address, OpCode.CheckEqual, new Register(null, "Z"), cmnCompareLeft, cmnCompareRight);
                        Add(address, OpCode.CheckLess, new Register(null, "N"), cmnDifference, Imm(0));
                        flagState = Arm64FlagState.Comparison;
                        break;
                    }

                    var opCode = instruction.Mnemonic switch
                    {
                        Arm64Mnemonic.ADDS => OpCode.Add,
                        Arm64Mnemonic.SUBS => OpCode.Subtract,
                        Arm64Mnemonic.ANDS => OpCode.And,
                        _ => OpCode.Invalid
                    };

                    if (instruction.Mnemonic == Arm64Mnemonic.SUBS)
                    {
                        // SUBS 的目标寄存器可能覆盖源寄存器，必须在运算前保存比较操作数。
                        var subsCompareLeft = new Register(null, "FLAG_COMPARE_LEFT");
                        var subsCompareRight = new Register(null, "FLAG_COMPARE_RIGHT");
                        Add(address, OpCode.Move, subsCompareLeft, src1);
                        Add(address, OpCode.Move, subsCompareRight, src2);
                        AddInteger(address, opCode, dest, subsCompareLeft, subsCompareRight);
                        Add(address, OpCode.CheckLess, new Register(null, "N"), dest, Imm(0));
                        flagState = Arm64FlagState.Comparison;
                    }
                    else
                    {
                        var logicalResult = AddInteger(address, opCode, dest, src1, src2);
                        if (writesZeroRegister)
                            logicalResult.IntegerWidthBits = instruction.Op0Reg == Arm64Register.W31 ? 32 : 64;
                        flagState = Arm64FlagState.ZeroOnly;
                    }

                    Add(address, OpCode.CheckEqual, new Register(null, "Z"), dest, Imm(0));
                    break;
                }

            case Arm64Mnemonic.ORR:
                //Orr is (dest, src1, src2)
                AddInteger(address, OpCode.Or, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                break;

            case Arm64Mnemonic.EOR:
                //Eor (aka xor) is (dest, src1, src2)
                AddInteger(address, OpCode.Xor, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                break;

            case Arm64Mnemonic.UMOV:
            case Arm64Mnemonic.INVALID:
            case Arm64Mnemonic.UNIMPLEMENTED:
                {
                    var machineCode = ReadMachineCodeAtAddress(context, address);
                    if (TryDecodeDisassembledUnsignedHalfwordMoveToGeneral(
                            instruction.Mnemonic,
                            machineCode,
                            out var generalRegister,
                            out var vectorRegister,
                            out var laneIndex))
                    {
                        Add(
                            address,
                            OpCode.VectorExtractUnsignedInt16,
                            new Register(null, $"X{generalRegister}"),
                            new Register(null, $"V{vectorRegister}"),
                            Imm(laneIndex));
                        break;
                    }

                    if (TryEmitRecoveredVectorInstruction(machineCode))
                        break;

                    if (!TryDecodeVectorFloatingMultiply(
                            machineCode,
                            out _,
                            out _,
                            out _,
                            out var destinationRegister,
                            out var leftRegister,
                            out var rightRegister))
                    {
                        Add(address, OpCode.NotImplemented, new StringLiteral($"Instruction {instruction.Mnemonic} 0x{machineCode:X8} not yet implemented."));
                        break;
                    }

                    Add(
                        address,
                        OpCode.Multiply,
                        new Register(null, $"V{destinationRegister}"),
                        new Register(null, $"V{leftRegister}"),
                        new Register(null, $"V{rightRegister}"));
                    break;
                }

            default:
                Add(address, OpCode.NotImplemented, new StringLiteral($"Instruction {instruction.Mnemonic} not yet implemented."));
                break;
        }
    }

    /// <summary>
    /// 在当前基本块内读取最近一次整数参数寄存器赋值。
    /// 接口慢查表第三实参是较小的虚表槽号；真实 AddWithResize 的 X2 则承载 MethodInfo 指针。
    /// 一旦跨越调用或控制流边界，AAPCS64 参数寄存器内容便不再属于当前调用点，因此立即停止追踪。
    /// </summary>
    internal static bool HasRecentSmallImmediateArgument(
        IReadOnlyList<Instruction> instructions,
        string registerName,
        long maximumValue)
    {
        if (string.IsNullOrWhiteSpace(registerName) || maximumValue < 0)
            return false;

        for (var index = instructions.Count - 1; index >= 0; index--)
        {
            var instruction = instructions[index];
            if (instruction.IsCall || !instruction.IsFallThrough)
                return false;

            if (instruction.Destination is not Register destination
                || !string.Equals(destination.Name, registerName, StringComparison.Ordinal))
            {
                continue;
            }

            return instruction.OpCode == OpCode.Move
                   && instruction.Operands.Count >= 2
                   && instruction.Operands[1] is Immediate { Value: >= 0 } immediate
                   && immediate.Value <= maximumValue;
        }

        return false;
    }

    private IOperand ConvertOperand(Arm64Instruction instruction, int operand)
    {
        var kind = operand switch
        {
            0 => instruction.Op0Kind,
            1 => instruction.Op1Kind,
            2 => instruction.Op2Kind,
            3 => instruction.Op3Kind,
            _ => throw new ArgumentOutOfRangeException(nameof(operand), $"Operand must be between 0 and 3, inclusive. Got {operand}")
        };

        if (kind is Arm64OperandKind.Immediate or Arm64OperandKind.ImmediatePcRelative)
        {
            var imm = operand switch
            {
                0 => instruction.Op0Imm,
                1 => instruction.Op1Imm,
                2 => instruction.Op2Imm,
                3 => instruction.Op3Imm,
                _ => throw new ArgumentOutOfRangeException(nameof(operand), $"Operand must be between 0 and 3, inclusive. Got {operand}")
            };

            if (kind == Arm64OperandKind.ImmediatePcRelative)
                imm += (long)instruction.Address + 4; //Add 4 to the address to get the address of the next instruction (PC-relative addressing is relative to the address of the next instruction, not the current one

            return new Immediate(imm);
        }

        if (kind == Arm64OperandKind.FloatingPointImmediate)
        {
            var imm = operand switch
            {
                0 => instruction.Op0FpImm,
                1 => instruction.Op1FpImm,
                2 => instruction.Op2FpImm,
                3 => instruction.Op3FpImm,
                _ => throw new ArgumentOutOfRangeException(nameof(operand), $"Operand must be between 0 and 3, inclusive. Got {operand}")
            };

            return new DoubleLiteral(imm);
        }

        if (kind == Arm64OperandKind.Register)
        {
            var reg = operand switch
            {
                0 => instruction.Op0Reg,
                1 => instruction.Op1Reg,
                2 => instruction.Op2Reg,
                3 => instruction.Op3Reg,
                _ => throw new ArgumentOutOfRangeException(nameof(operand), $"Operand must be between 0 and 3, inclusive. Got {operand}")
            };

            return new Register(null, Arm64RegisterHelper.CanonicalName(reg));
        }

        if (kind == Arm64OperandKind.Memory)
        {
            var reg = instruction.MemBase;
            var offset = instruction.MemOffset;

            if (reg == Arm64Register.INVALID)
                //Offset only
                return new MemoryOperand(addend: offset);

            if (TryCreateStackOffset(
                    reg,
                    instruction.MemAddendReg,
                    offset,
                    out var stackOffset))
                return stackOffset;

            return CreateMemoryOperand(instruction);
        }

        if (kind == Arm64OperandKind.VectorRegisterElement)
        {
            var reg = operand switch
            {
                0 => instruction.Op0Reg,
                1 => instruction.Op1Reg,
                2 => instruction.Op2Reg,
                3 => instruction.Op3Reg,
                _ => throw new ArgumentOutOfRangeException(nameof(operand), $"Operand must be between 0 and 3, inclusive. Got {operand}")
            };

            var vectorElement = operand switch
            {
                0 => instruction.Op0VectorElement,
                1 => instruction.Op1VectorElement,
                2 => instruction.Op2VectorElement,
                3 => instruction.Op3VectorElement,
                _ => throw new ArgumentOutOfRangeException(nameof(operand), $"Operand must be between 0 and 3, inclusive. Got {operand}")
            };

            var width = vectorElement.Width switch
            {
                Arm64VectorElementWidth.B => "B",
                Arm64VectorElementWidth.H => "H",
                Arm64VectorElementWidth.S => "S",
                Arm64VectorElementWidth.D => "D",
                _ => throw new ArgumentOutOfRangeException(nameof(vectorElement.Width), $"Unknown vector element width {vectorElement.Width}")
            };

            var name = $"{reg.ToString().ToUpperInvariant()}.{width}{vectorElement.Index}";
            return new Register(null, name);
        }

        return new StringLiteral($"<UNIMPLEMENTED OPERAND TYPE {kind}>");
    }

    private IOperand ConvertMoveSourceOperand(Arm64Instruction instruction)
    {
        if (instruction.Op1Kind == Arm64OperandKind.Register
            && Arm64RegisterHelper.IsZeroRegister(instruction.Op1Reg))
            return Imm(0);

        return ConvertOperand(instruction, 1);
    }

    private IOperand ConvertStoreSourceOperand(Arm64Instruction instruction)
    {
        if (instruction.Op0Kind == Arm64OperandKind.Register
            && Arm64RegisterHelper.IsZeroRegister(instruction.Op0Reg))
            return Imm(0);

        return ConvertOperand(instruction, 0);
    }

    private IOperand ConvertStorePairSourceOperand(Arm64Instruction instruction, int operand)
    {
        var kind = operand switch
        {
            0 => instruction.Op0Kind,
            1 => instruction.Op1Kind,
            _ => throw new ArgumentOutOfRangeException(nameof(operand), operand, "STP源操作数只能是0或1。")
        };
        var register = operand switch
        {
            0 => instruction.Op0Reg,
            1 => instruction.Op1Reg,
            _ => Arm64Register.INVALID
        };

        // STP的源位置把31号通用寄存器解释为XZR/WZR，必须写入常量零。
        if (kind == Arm64OperandKind.Register && Arm64RegisterHelper.IsZeroRegister(register))
            return Imm(0);

        return ConvertOperand(instruction, operand);
    }

    public override BaseKeyFunctionAddresses CreateKeyFunctionAddressesInstance() => new NewArm64KeyFunctionAddresses();

    public override string PrintAssembly(MethodAnalysisContext context)
    {
        if (context.RawBytes.Length <= 0)
            return "";

        var raw = context.RawBytes.AsSpan();
        var disassembled = Disassembler.Disassemble(
            raw,
            context.UnderlyingPointer,
            new Disassembler.Options(true, true, false)).ToList();
        var lines = new List<string>(disassembled.Count);
        for (var index = 0; index < disassembled.Count && index * sizeof(uint) + sizeof(uint) <= raw.Length; index++)
        {
            var decodedInstruction = disassembled[index];
            var machineCode = BinaryPrimitives.ReadUInt32LittleEndian(
                raw.Slice(index * sizeof(uint), sizeof(uint)));
            var address = checked(context.UnderlyingPointer + (ulong)index * sizeof(uint));
            if (decodedInstruction.Mnemonic is not (
                    Arm64Mnemonic.INVALID or Arm64Mnemonic.UNIMPLEMENTED))
            {
                if (decodedInstruction.Mnemonic == Arm64Mnemonic.FMOV
                    && decodedInstruction.Op1Kind == Arm64OperandKind.FloatingPointImmediate
                    && TryDecodeScalarFloatingPointImmediate(
                        machineCode,
                        out _,
                        out var floatingImmediate))
                {
                    lines.Add(
                        $"0x{address:X8} FMOV "
                        + $"{decodedInstruction.Op0Reg.ToString().ToUpperInvariant()}, "
                        + floatingImmediate.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    continue;
                }

                lines.Add(decodedInstruction.ToString());
                continue;
            }

            if (TryFormatRecoveredVectorInstruction(
                    machineCode,
                    address,
                    out var recoveredVectorInstruction))
            {
                lines.Add(recoveredVectorInstruction);
                continue;
            }

            if (!TryDecodeVectorFloatingMultiply(
                    machineCode,
                    out _,
                    out var elementWidthBits,
                    out var laneCount,
                    out var destinationRegister,
                    out var leftRegister,
                    out var rightRegister))
            {
                lines.Add(decodedInstruction.ToString());
                continue;
            }

            var elementSuffix = elementWidthBits == 32 ? "S" : "D";
            lines.Add($"0x{address:X8} FMUL V{destinationRegister}.{laneCount}{elementSuffix}, V{leftRegister}.{laneCount}{elementSuffix}, V{rightRegister}.{laneCount}{elementSuffix}");
        }

        return string.Join("\n", lines);
    }

    private static uint ReadMachineCodeAtAddress(MethodAnalysisContext context, ulong address)
    {
        var binary = context.AppContext.Binary;
        var rawAddress = binary.MapVirtualAddressToRaw(address);
        var machineCodeBytes = binary.Reader.ReadByteArrayAtRawAddress(rawAddress, sizeof(uint));
        if (BitConverter.IsLittleEndian != binary.Reader.IsLittleEndian)
            Array.Reverse(machineCodeBytes);
        return BitConverter.ToUInt32(machineCodeBytes, 0);
    }

    private static List<IOperand> GetReturnOperandsForContext(MethodAnalysisContext context)
        => Arm64CallingConventionResolver.ReturnOperands(context).ToList();

    private List<IOperand> GetArgumentOperandsForCall(MethodAnalysisContext contextBeingCalled)
        => Arm64CallingConventionResolver.ArgumentOperands(contextBeingCalled).ToList();

    private List<IOperand> GetArgumentOperandsForCall(MethodAnalysisContext contextBeingAnalyzed, ulong callAddr)
    {
        if (!contextBeingAnalyzed.AppContext.MethodsByAddress.TryGetValue(callAddr, out var methodsAtAddress))
            //TODO
            return [];

        return GetArgumentOperandsForCall(methodsAtAddress.First());
    }
}
