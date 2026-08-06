using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Disarm;
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
    Comparison,
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

public class NewArmV8InstructionSet : Cpp2IlInstructionSet
{
    [ThreadStatic]
    private static Dictionary<Arm64Register, ulong> adrpOffsets = new();

    private static Immediate Imm(long value) => new(value);
    private static Immediate Imm(ulong value) => new(unchecked((long)value));

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

    internal static bool IsExactlyRepresentableMovi(Arm64OperandKind immediateKind, long immediate)
    {
        return immediateKind == Arm64OperandKind.Immediate && immediate == 0;
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

        if (adrpOffsets == null!) // initializers for ThreadStatic fields only run on the first thread
            adrpOffsets = new();
        else
            adrpOffsets.Clear();

        var instructions = new List<Instruction>();
        var addresses = new List<ulong>();
        var flagState = Arm64FlagState.None;

        for (var index = 0; index < insns.Count; index++)
        {
            var instruction = insns[index];
            var address = ResolveInstructionAddress(
                context.UnderlyingPointer,
                index,
                instruction.Mnemonic,
                instruction.Address);
            ConvertInstructionStatement(instruction, address, instructions, addresses, context, ref flagState);
        }

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

    private void ConvertInstructionStatement(
        Arm64Instruction instruction,
        ulong address,
        List<Instruction> instructions,
        List<ulong> addresses,
        MethodAnalysisContext context,
        ref Arm64FlagState flagState)
    {
        var inputFlagState = flagState;

        Instruction Add(ulong address, OpCode opCode, params List<IOperand> operands)
        {
            addresses.Add(address);
            var newInstruction = new Instruction(instructions.Count, opCode, operands);
            instructions.Add(newInstruction);
            return newInstruction;
        }
        
        void AddCall(MethodAnalysisContext context, ulong address, ulong target)
        {
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
                return;
            }

            var calledMethod = methodsAtAddress.Count == 1 ? methodsAtAddress[0] : context;
            var returnRegister = GetReturnRegisterForContext(calledMethod);
            var call = returnRegister == null
                ? Add(address, OpCode.CallVoid, Imm(target))
                : Add(address, OpCode.Call, Imm(target), returnRegister);

            call.AddOperands(GetArgumentOperandsForCall(methodsAtAddress.First()));
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
            // 先保存两个源值，避免目标寄存器与任一源寄存器重叠时破坏假分支。
            Add(address, OpCode.Move, destination, preservedTrue);
            Add(address, OpCode.ConditionalJump, Imm(address + sizeof(uint)), condition);
            Add(address, OpCode.Move, destination, preservedFalse);
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

                if (instruction.Op1Kind == Arm64OperandKind.Memory && adrpOffsets.TryGetValue(instruction.MemBase, out var page) && instruction.MemOffset != 0 && instruction.MemAddendReg == Arm64Register.INVALID)
                {
                    //Maybe this is a bit hacky? But I really don't want to write paged load handling into ISIL itself, it's an Arm64 quirk
                    //LDR X0, [X1, #0x1000], where X1 was previously loaded with a page address via an ADRP instruction
                    //We just return the final address, it makes ISIL happier.
                    //TODO check if this is correct
                    var offset = instruction.MemOffset + (long)page;

                    //We're also trashing any possible ADRP offsets in the dest here, so let's clear that now we've possibly grabbed the value if we need it (it's common to store the page and final address in the same register)
                    if (instruction.Op0Kind == Arm64OperandKind.Register)
                        adrpOffsets.Remove(instruction.Op0Reg);

                    Add(address, OpCode.Move, ConvertOperand(instruction, 0), new MemoryOperand(addend: offset));
                    break;
                }

                //And again here we're trashing any possible ADRP offsets in the dest here, so let's clear that
                if (instruction.Op0Kind == Arm64OperandKind.Register)
                    adrpOffsets.Remove(instruction.Op0Reg);

                Add(address, OpCode.Move, ConvertOperand(instruction, 0), ConvertMoveSourceOperand(instruction));
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
                    // 当前ISIL以托管值表达向量清零；其他向量立即数需保留元素复制语义后再接入。
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
                Add(address, OpCode.Move, ConvertOperand(instruction, 1), ConvertStoreSourceOperand(instruction));
                break;
            case Arm64Mnemonic.STP:
                // store pair of registers (reg1, reg2, dest)
                {
                    var dest3 = ConvertOperand(instruction, 2);
                    if (dest3 is Register { Name: "X31" }) // if stack
                    {
                        Add(address, OpCode.Move, dest3, ConvertOperand(instruction, 0));
                        Add(address, OpCode.Move, dest3, ConvertOperand(instruction, 1));
                    }
                    else if (dest3 is MemoryOperand memory)
                    {
                        var firstRegister = ConvertOperand(instruction, 0);
                        var size = Arm64RegisterHelper.SizeBytes(instruction.Op0Reg);
                        Add(address, OpCode.Move, dest3, firstRegister); // [REG + offset] = REG1
                        memory = new MemoryOperand((Register)memory.Base!, addend: memory.Addend + size);
                        dest3 = memory;
                        Add(address, OpCode.Move, dest3, ConvertOperand(instruction, 1)); // [REG + offset + size] = REG2
                    }
                    else // reg pointer
                    {
                        var firstRegister = ConvertOperand(instruction, 0);
                        var size = Arm64RegisterHelper.SizeBytes(instruction.Op0Reg);
                        Add(address, OpCode.Move, dest3, firstRegister);
                        Add(address, OpCode.Add, dest3, dest3, Imm(size));
                        Add(address, OpCode.Move, dest3, ConvertOperand(instruction, 1));
                    }
                }
                break;
            case Arm64Mnemonic.ADRP:
                //Just handle as a move
                Add(address, OpCode.Move, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1));
                var pageAddress = address & ~0xFFFUL;
                adrpOffsets[instruction.Op0Reg] = (ulong)((long)pageAddress + instruction.Op1Imm);
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

                //TODO clean this mess up
                var memInternal = mem as MemoryOperand?;
                var mem2 = new MemoryOperand((Register)memInternal!.Value.Base!, addend: memInternal.Value.Addend + destRegSize);

                Add(address, OpCode.Move, dest1, mem);
                Add(address, OpCode.Move, dest2, mem2);
                break;
            case Arm64Mnemonic.BL:
                AddCall(context, address, instruction.BranchTarget);
                break;
            case Arm64Mnemonic.RET:
                var returnRegister = GetReturnRegisterForContext(context);
                if (returnRegister == null)
                    Add(address, OpCode.Return);
                else
                    Add(address, OpCode.Return, returnRegister);
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

                if (IsBranchOutsideMethod(target, context.UnderlyingPointer, context.RawBytes.Length))
                {
                    // 方法区间采用左闭右开语义；跳到相邻方法首地址属于尾调用，随后返回当前方法。
                    var returnRegister2 = GetReturnRegisterForContext(context);
                    AddCall(context, address, target);

                    if (returnRegister2 == null)
                        Add(address, OpCode.Return);
                    else
                        Add(address, OpCode.Return, returnRegister2);
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

                    // CCMP 条件成立时更新比较标志；否则使用 NZCV 立即数。当前 ISIL 精确保留后继 EQ/NE 所需的 Z 位。
                    var comparedZero = new Register(null, "CCMP_COMPARED_ZERO");
                    Add(
                        address,
                        OpCode.CheckEqual,
                        comparedZero,
                        ConvertOperand(instruction, 0),
                        ConvertOperand(instruction, 1));
                    Add(address, OpCode.Move, new Register(null, "Z"), comparedZero);
                    Add(address, OpCode.ConditionalJump, Imm(address + 4), ccmpCondition);
                    Add(
                        address,
                        OpCode.Move,
                        new Register(null, "Z"),
                        Imm((instruction.Op2Imm >> 2) & 1));
                    flagState = Arm64FlagState.ZeroOnly;
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
                    if (!TryGetSignedIntegerWidthBits(instruction.Op0Reg, out var destinationBits) ||
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
                        !TryGetSignedIntegerWidthBits(instruction.Op1Reg, out var sourceBits))
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
                    Add(address, OpCode.Add, incrementedSource, preservedSource, Imm(1));
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
            case Arm64Mnemonic.FMUL:
                //Multiply is (dest, src1, src2)
                Add(address, OpCode.Multiply, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                break;

            case Arm64Mnemonic.FDIV:
                Add(address, OpCode.Divide, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                break;

            case Arm64Mnemonic.FNMUL:
                {
                    var product = new Register(null, $"FNMUL_PRODUCT_{address:X}");
                    Add(address, OpCode.Multiply, product, ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                    Add(address, OpCode.Negate, ConvertOperand(instruction, 0), product);
                    break;
                }

            case Arm64Mnemonic.ADD:
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

                Add(address, OpCode.Add, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), addRight);
                break;
            case Arm64Mnemonic.FADD:
                // 浮点加法没有整数扩展寄存器格式。
                Add(address, OpCode.Add, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                break;

            case Arm64Mnemonic.SUB:
            case Arm64Mnemonic.FSUB:
                //Sub is (dest, src1, src2)
                Add(address, OpCode.Subtract, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                break;

            case Arm64Mnemonic.AND:
                //And is (dest, src1, src2)
                Add(address, OpCode.And, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                break;

            case Arm64Mnemonic.ADDS:
            case Arm64Mnemonic.SUBS:
            case Arm64Mnemonic.ANDS:
                {
                    var dest = ConvertOperand(instruction, 0);
                    var src1 = ConvertOperand(instruction, 1);
                    var src2 = ConvertOperand(instruction, 2);

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
                        Add(address, opCode, dest, subsCompareLeft, subsCompareRight);
                        Add(address, OpCode.CheckLess, new Register(null, "N"), dest, Imm(0));
                        flagState = Arm64FlagState.Comparison;
                    }
                    else
                    {
                        Add(address, opCode, dest, src1, src2);
                        flagState = Arm64FlagState.ZeroOnly;
                    }

                    Add(address, OpCode.CheckEqual, new Register(null, "Z"), dest, Imm(0));
                    break;
                }

            case Arm64Mnemonic.ORR:
                //Orr is (dest, src1, src2)
                Add(address, OpCode.Or, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                break;

            case Arm64Mnemonic.EOR:
                //Eor (aka xor) is (dest, src1, src2)
                Add(address, OpCode.Xor, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                break;

            case Arm64Mnemonic.INVALID:
            case Arm64Mnemonic.UNIMPLEMENTED:
                {
                    var machineCode = ReadMachineCodeAtAddress(context, address);
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
            var isPreIndexed = instruction.MemIsPreIndexed;

            if (reg == Arm64Register.INVALID)
                //Offset only
                return new MemoryOperand(addend: offset);

            //TODO Handle more stuff here
            return new MemoryOperand(new Register(null, Arm64RegisterHelper.CanonicalName(reg)), addend: offset);
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
            if (decodedInstruction.Mnemonic is not (
                    Arm64Mnemonic.INVALID or Arm64Mnemonic.UNIMPLEMENTED))
            {
                lines.Add(decodedInstruction.ToString());
                continue;
            }

            var machineCode = BinaryPrimitives.ReadUInt32LittleEndian(
                raw.Slice(index * sizeof(uint), sizeof(uint)));
            var address = checked(context.UnderlyingPointer + (ulong)index * sizeof(uint));
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

    private IOperand? GetReturnRegisterForContext(MethodAnalysisContext context)
    {
        var returnType = context.ReturnType;
        if (returnType.Namespace == nameof(System))
        {
            return returnType.Name switch
            {
                "Void" => null, //Void is no return
                "Double" => new Register(null, nameof(Arm64Register.V0)), //Builtin double is v0
                "Single" => new Register(null, nameof(Arm64Register.V0)), //Builtin float is v0
                _ => new Register(null, nameof(Arm64Register.X0)), //All other system types are x0 like any other pointer
            };
        }

        //TODO Do certain value types have different return registers?

        //Any user type is returned in x0
        return new Register(null, nameof(Arm64Register.X0));
    }

    private List<IOperand> GetArgumentOperandsForCall(MethodAnalysisContext contextBeingCalled)
    {
        var vectorCount = 0;
        var nonVectorCount = 0;

        var ret = new List<IOperand>();

        //Handle 'this' if it's an instance method
        if (!contextBeingCalled.IsStatic)
        {
            ret.Add(new Register(null, nameof(Arm64Register.X0)));
            nonVectorCount++;
        }

        foreach (var parameter in contextBeingCalled.Parameters)
        {
            var paramType = parameter.ParameterType;
            if (paramType.Namespace == nameof(System))
            {
                switch (paramType.Name)
                {
                    case "Single":
                    case "Double":
                        ret.Add(new Register(null, (Arm64Register.V0 + vectorCount++).ToString().ToUpperInvariant()));
                        break;
                    default:
                        ret.Add(new Register(null, (Arm64Register.X0 + nonVectorCount++).ToString().ToUpperInvariant()));
                        break;
                }
            }
            else
            {
                ret.Add(new Register(null, (Arm64Register.X0 + nonVectorCount++).ToString().ToUpperInvariant()));
            }
        }

        return ret;
    }
    
    private List<IOperand> GetArgumentOperandsForCall(MethodAnalysisContext contextBeingAnalyzed, ulong callAddr)
    {
        if (!contextBeingAnalyzed.AppContext.MethodsByAddress.TryGetValue(callAddr, out var methodsAtAddress))
            //TODO
            return [];

        return GetArgumentOperandsForCall(methodsAtAddress.First());
    }
}
