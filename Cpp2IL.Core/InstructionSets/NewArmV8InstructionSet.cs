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

        var result = NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(binary, context.UnderlyingPointer);
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
        var insns = NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(context.AppContext.Binary, context.UnderlyingPointer);

        if (TryRecoverByteJumpTableMethod(insns, context, out var jumpTableInstructions))
            return jumpTableInstructions;

        if (adrpOffsets == null!) // initializers for ThreadStatic fields only run on the first thread
            adrpOffsets = new();
        else
            adrpOffsets.Clear();

        var instructions = new List<Instruction>();
        var addresses = new List<ulong>();
        var flagState = Arm64FlagState.None;
        var conditionalComparisonFallbackNzcv = 0L;

        for (var index = 0; index < insns.Count; index++)
        {
            if (TryEmitPackedHalfwordPredicatePattern(
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

            var instruction = insns[index];
            var address = ResolveInstructionAddress(
                context.UnderlyingPointer,
                index,
                instruction.Mnemonic,
                instruction.Address);
            ConvertInstructionStatement(
                instruction,
                address,
                instructions,
                addresses,
                context,
                ref flagState,
                ref conditionalComparisonFallbackNzcv);
        }

        PruneUnconsumedHomogeneousFloatingReturnProjections(instructions, context);

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

        var activeRegisters = projections
            .Select(projection => projection.Destination.Number)
            .ToHashSet();
        var aggregateCarrier = ((Register)projections[^1].Source.Base!).Number;

        for (var instructionIndex = startIndex;
             instructionIndex < instructions.Count && activeRegisters.Count > 0;
             instructionIndex++)
        {
            var instruction = instructions[instructionIndex];
            var readRegisters = instruction.Sources
                .SelectMany(EnumerateOperandRegisters)
                .Select(register => register.Number)
                .ToHashSet();

            if (readRegisters.Any(register =>
                    register != aggregateCarrier && activeRegisters.Contains(register))
                || activeRegisters.Contains(aggregateCarrier)
                && ReadsAggregateCarrierAsScalar(instruction, aggregateCarrier))
                return true;

            if (instruction.Destination is Register destination)
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
        var remaining = fieldOffsets.Keys.ToHashSet();
        int? destinationBase = null;
        long? aggregateOffset = null;

        for (var instructionIndex = startIndex;
             instructionIndex < instructions.Count && remaining.Count > 0;
             instructionIndex++)
        {
            var instruction = instructions[instructionIndex];
            var componentReads = instruction.Sources
                .SelectMany(EnumerateOperandRegisters)
                .Select(register => register.Number)
                .Where(remaining.Contains)
                .Distinct()
                .ToArray();

            if (componentReads.Length == 0)
            {
                if (instruction.Destination is Register destination
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
            or OpCode.Divide or OpCode.ShiftLeft or OpCode.ShiftRight
            or OpCode.And or OpCode.Or or OpCode.Xor or OpCode.Not or OpCode.Negate
            or OpCode.AbsoluteNumber or OpCode.AbsoluteDifference or OpCode.MaximumNumber
            or OpCode.ConvertFloatingPointPrecision or OpCode.ConvertFloatToSignedInteger
            or OpCode.ConvertSignedIntegerToFloat or OpCode.ReinterpretIntegerBitsAsFloat
            or OpCode.ReinterpretFloatBitsAsInteger or OpCode.RoundFloatTowardPositiveInfinity
            or OpCode.RoundFloatTowardNegativeInfinity
            or >= OpCode.CheckEqual and <= OpCode.CheckLessOrEqualUnsigned;
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

    private bool TryRecoverByteJumpTableMethod(
        IReadOnlyList<Arm64Instruction> nativeInstructions,
        MethodAnalysisContext context,
        out List<Instruction> instructions)
    {
        instructions = [];
        for (var branchIndex = 4; branchIndex < nativeInstructions.Count; branchIndex++)
        {
            var branch = nativeInstructions[branchIndex];
            if (branch.Mnemonic != Arm64Mnemonic.BR
                || nativeInstructions[branchIndex - 1].Mnemonic != Arm64Mnemonic.ADD
                || nativeInstructions[branchIndex - 2].Mnemonic != Arm64Mnemonic.LDRB
                || nativeInstructions[branchIndex - 3].Mnemonic != Arm64Mnemonic.ADR)
                continue;

            var tableLoad = nativeInstructions[branchIndex - 2];
            var branchBaseLoad = nativeInstructions[branchIndex - 3];
            if (branch.Op0Reg != nativeInstructions[branchIndex - 1].Op0Reg
                || nativeInstructions[branchIndex - 1].Op1Reg != branchBaseLoad.Op0Reg
                || tableLoad.Op0Kind != Arm64OperandKind.Register
                || tableLoad.Op1Kind != Arm64OperandKind.Memory
                || tableLoad.MemAddendReg == Arm64Register.INVALID
                || branchBaseLoad.Op1Kind is not (
                    Arm64OperandKind.Immediate or Arm64OperandKind.ImmediatePcRelative))
                continue;

            var tableBaseAdd = nativeInstructions
                .Take(branchIndex - 2)
                .LastOrDefault(candidate =>
                    candidate.Mnemonic == Arm64Mnemonic.ADD
                    && candidate.Op0Reg == tableLoad.MemBase
                    && candidate.Op1Reg == tableLoad.MemBase
                    && candidate.Op2Kind == Arm64OperandKind.Immediate);
            if (tableBaseAdd.Mnemonic != Arm64Mnemonic.ADD)
                continue;

            var tablePageLoad = nativeInstructions
                .TakeWhile(candidate => candidate.Address < tableBaseAdd.Address)
                .LastOrDefault(candidate =>
                    candidate.Mnemonic == Arm64Mnemonic.ADRP
                    && candidate.Op0Reg == tableLoad.MemBase);
            if (tablePageLoad.Mnemonic != Arm64Mnemonic.ADRP)
                continue;

            var indexAdjust = nativeInstructions
                .Take(branchIndex - 3)
                .LastOrDefault(candidate =>
                    candidate.Mnemonic == Arm64Mnemonic.ADD
                    && Arm64RegisterHelper.CanonicalName(candidate.Op0Reg)
                        == Arm64RegisterHelper.CanonicalName(tableLoad.MemAddendReg)
                    && Arm64RegisterHelper.CanonicalName(candidate.Op1Reg)
                        == Arm64RegisterHelper.CanonicalName(tableLoad.MemAddendReg)
                    && candidate.Op2Kind == Arm64OperandKind.Immediate);
            if (indexAdjust.Mnemonic != Arm64Mnemonic.ADD)
                continue;

            var tableAddress = checked(
                ResolveAdrpPageAddress(tablePageLoad.Address, tablePageLoad.Op1Imm)
                + (ulong)tableBaseAdd.Op2Imm);
            var branchBaseAddress = ResolveAdrAddress(branchBaseLoad.Address, branchBaseLoad.Op1Imm);
            var methodEnd = checked(context.UnderlyingPointer + (ulong)context.RawBytes.Length);
            var maximumEntryCount = checked((int)Math.Min(
                256UL,
                methodEnd > branchBaseAddress ? (methodEnd - branchBaseAddress) / sizeof(uint) : 0));
            if (maximumEntryCount == 0)
                continue;

            byte[] table;
            try
            {
                var rawTableAddress = context.AppContext.Binary.MapVirtualAddressToRaw(tableAddress);
                table = context.AppContext.Binary.Reader.ReadByteArrayAtRawAddress(
                    rawTableAddress,
                    maximumEntryCount);
            }
            catch
            {
                continue;
            }

            var targetAddresses = new List<ulong>();
            foreach (var entry in table)
            {
                var target = checked(branchBaseAddress + (ulong)entry * sizeof(uint));
                if (target < context.UnderlyingPointer
                    || target >= methodEnd
                    || (target - context.UnderlyingPointer) % sizeof(uint) != 0)
                    break;
                targetAddresses.Add(target);
            }

            if (targetAddresses.Count < 2
                || !TryResolveByteJumpTableTargets(
                    table.AsSpan(0, targetAddresses.Count),
                    branchBaseAddress,
                    context.UnderlyingPointer,
                    methodEnd,
                    out var validatedTargets))
                continue;

            var convertedPrefix = new List<Instruction>();
            var convertedAddresses = new List<ulong>();
            var prefixFlagState = Arm64FlagState.None;
            var prefixFallbackNzcv = 0L;
            for (var index = 0; index <= branchIndex - 4; index++)
            {
                var native = nativeInstructions[index];
                ConvertInstructionStatement(
                    native,
                    native.Address,
                    convertedPrefix,
                    convertedAddresses,
                    context,
                    ref prefixFlagState,
                    ref prefixFallbackNzcv);
            }

            var targetAnchors = validatedTargets
                .Distinct()
                .ToDictionary(target => target, _ => new Instruction(0, OpCode.Nop));
            var result = new List<Instruction>(convertedPrefix);
            var resultAddresses = new List<ulong>(convertedAddresses);
            var indexOperand = new Register(null, Arm64RegisterHelper.CanonicalName(tableLoad.MemAddendReg));
            for (var tableIndex = 0; tableIndex < validatedTargets.Length; tableIndex++)
            {
                var condition = new Register(null, $"JUMP_TABLE_CASE_{tableIndex}");
                result.Add(new Instruction(result.Count, OpCode.CheckEqual, condition, indexOperand, Imm(tableIndex)));
                resultAddresses.Add(branch.Address);
                result.Add(new Instruction(
                    result.Count,
                    OpCode.ConditionalJump,
                    targetAnchors[validatedTargets[tableIndex]],
                    condition));
                resultAddresses.Add(branch.Address);
            }

            result.Add(new Instruction(result.Count, OpCode.Jump, targetAnchors[validatedTargets[^1]]));
            resultAddresses.Add(branch.Address);
            var suffixFlagState = prefixFlagState;
            var suffixFallbackNzcv = prefixFallbackNzcv;
            for (var index = branchIndex + 1; index < nativeInstructions.Count; index++)
            {
                var native = nativeInstructions[index];
                if (targetAnchors.TryGetValue(native.Address, out var anchor))
                {
                    anchor.Index = result.Count;
                    result.Add(anchor);
                    resultAddresses.Add(native.Address);
                }

                ConvertInstructionStatement(
                    native,
                    native.Address,
                    result,
                    resultAddresses,
                    context,
                    ref suffixFlagState,
                    ref suffixFallbackNzcv);
            }

            for (var index = 0; index < result.Count; index++)
            {
                result[index].Index = index;
                if (result[index].OpCode is not (OpCode.Jump or OpCode.ConditionalJump)
                    || result[index].Operands[0] is not Immediate immediate)
                    continue;

                var targetIndex = resultAddresses.FindIndex(candidate => candidate == immediate.UnsignedValue);
                if (targetIndex < 0)
                {
                    instructions = [];
                    return false;
                }

                result[index].SetOperand(0, result[targetIndex]);
            }
            PruneUnconsumedHomogeneousFloatingReturnProjections(result, context);
            instructions = result;
            return true;
        }

        return false;
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

            var calledMethod = methodsAtAddress.Count == 1 ? methodsAtAddress[0] : context;
            // 某些 Unity 6 构建会让接口慢查表助手与 List<T>.AddWithResize 共用原生地址。
            // 前者真实返回 VirtualInvokeData*，后者为 void；在 CFG/SSA 消费关系建立前不能丢掉 X0。
            // 仅对该已知共享身份保留原始返回槽，MetadataResolver 会删除普通 void 的伪返回值，
            // InterfaceDispatchRecovery 则以 Phi、零偏移 methodPtr 和 vtable 链完成最终裁决。
            var preservePotentialInterfaceLookupResult = calledMethod.IsVoid
                && methodsAtAddress.Any(InterfaceDispatchRecovery.IsSharedAddWithResizeCandidate)
                && HasRecentSmallImmediateArgument(instructions, "X2", ushort.MaxValue);
            Register? returnRegister = preservePotentialInterfaceLookupResult
                ? new Register(null, nameof(Arm64Register.X0))
                : calledMethod.IsVoid
                    ? null
                    : Arm64CallingConventionResolver.ReturnRegister(calledMethod);
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
            if (!TryEmitCondition(
                    instruction.FinalOpConditionCode,
                    registerPrefix + "_CONDITION",
                    out var condition))
                return false;

            var destination = ConvertOperand(instruction, 0);
            var preservedTrue = new Register(null, registerPrefix + "_TRUE");
            var preservedFalse = new Register(null, registerPrefix + "_FALSE");
            Add(address, OpCode.Move, preservedTrue, ConvertOperand(instruction, 1));
            Add(address, OpCode.Move, preservedFalse, ConvertOperand(instruction, 2));
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
                Add(address, OpCode.CheckEqual, new Register(null, "Z"), ConvertOperand(instruction, 0), Imm(0));
                break;
            case Arm64Mnemonic.MOVK:
                {
                    // MOVK 保留目标寄存器其他半字；必须读原始编码的 hw 字段，不能从已移位的显示立即数反推。
                    var machineCode = ReadMachineCodeAtAddress(context, address);
                    if (!TryDecodeMoveKeepImmediate(
                            machineCode,
                            out _,
                            out _,
                            out var clearMask,
                            out var shiftedImmediate))
                    {
                        throw new InvalidOperationException(
                            $"MOVK原始编码无效：0x{machineCode:X8} @ 0x{address:X}");
                    }

                    if (instruction.Op0Kind == Arm64OperandKind.Register)
                        adrpOffsets.Remove(instruction.Op0Reg);

                    var destination = ConvertOperand(instruction, 0);
                    Add(address, OpCode.And, destination, destination, Imm(clearMask));
                    Add(address, OpCode.Or, destination, destination, Imm(shiftedImmediate));
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
                    Add(address, OpCode.Move, preservedTrue, ConvertOperand(instruction, 1));
                    Add(address, OpCode.Move, preservedFalse, ConvertOperand(instruction, 2));
                    Add(address, OpCode.Add, incrementedFalse, preservedFalse, Imm(1));
                    Add(address, OpCode.Move, destination, preservedTrue);
                    Add(address, OpCode.ConditionalJump, Imm(address + 4), condition);
                    Add(address, OpCode.Move, destination, incrementedFalse);
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
                    Add(address, OpCode.Move, preservedTrue, ConvertOperand(instruction, 1));
                    Add(address, OpCode.Move, preservedFalse, ConvertOperand(instruction, 2));
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
                    Add(address, OpCode.Move, preservedSource, ConvertOperand(instruction, 1));
                    AddInteger(address, OpCode.Add, incrementedSource, preservedSource, Imm(1));
                    Add(address, OpCode.Move, destination, incrementedSource);
                    Add(address, OpCode.ConditionalJump, Imm(address + 4), condition);
                    Add(address, OpCode.Move, destination, preservedSource);
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
                // UBFM dest, src, #<immr>, #<imms>
                // dest = (src >> #<immr>) & ((1 << #<imms>) - 1)
                {
                    var dest3 = ConvertOperand(instruction, 0);
                    Add(address, OpCode.Move, dest3, ConvertOperand(instruction, 1)); // dest = src
                    Add(address, OpCode.ShiftRight, dest3, dest3, ConvertOperand(instruction, 2)); // dest = dest >> #<immr>
                    var imms = (int)instruction.Op3Imm;
                    Add(address, OpCode.And, dest3, dest3, Imm((1 << imms) - 1)); // dest = dest & constexpr { ((1 << #<imms>) - 1) }
                }
                break;

            case Arm64Mnemonic.MUL:
                // 整数乘法保留目标寄存器位宽，供退SSA后的标量载体定型。
                AddInteger(address, OpCode.Multiply, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                break;
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
                    var dest = ConvertOperand(instruction, 0);
                    var src1 = ConvertOperand(instruction, 1);
                    var src2 = ConvertOperand(instruction, 2);

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
                        AddInteger(address, opCode, dest, src1, src2);
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

            case Arm64Mnemonic.INVALID:
            case Arm64Mnemonic.UNIMPLEMENTED:
                {
                    var machineCode = ReadMachineCodeAtAddress(context, address);
                    if (TryDecodeUnsignedHalfwordMoveToGeneral(
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
