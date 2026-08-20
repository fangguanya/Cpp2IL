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

public class NewArmV8InstructionSet : Cpp2IlInstructionSet
{
    private static readonly Arm64CallingConventionAdapter CallingConventions = new();

    public override BaseCallingConventionResolver CallingConventionResolver => CallingConventions;

    [ThreadStatic]
    private static Dictionary<Arm64Register, ulong> adrpOffsets = new();

    private static Immediate Imm(long value) => new(value);
    private static Immediate Imm(ulong value) => new(unchecked((long)value));

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

    internal static bool TryResolveByteJumpTableTargets(
        ReadOnlySpan<byte> table,
        ulong branchBaseAddress,
        ulong methodStart,
        ulong methodEnd,
        out ulong[] targets)
    {
        targets = new ulong[table.Length];
        for (var index = 0; index < table.Length; index++)
        {
            var target = checked(branchBaseAddress + (ulong)table[index] * sizeof(uint));
            if (target < methodStart || target >= methodEnd || (target - methodStart) % sizeof(uint) != 0)
            {
                targets = [];
                return false;
            }

            targets[index] = target;
        }

        return targets.Length > 0;
    }

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
            Arm64MethodBodyReader.TryReadManagedMethodBody(binary, context.UnderlyingPointer, out var body))
            return body;
