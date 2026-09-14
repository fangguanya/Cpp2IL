using System;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 统一验证向量指令的通道布局，并提供把一个元素复制到六十四位块所需的位模式常量。
/// 这里仅依据 ISIL 携带的原生布局证据，不读取托管类型名或业务成员身份。
/// </summary>
internal static class VectorCilRecoveryHelper
{
    internal readonly record struct Shape(int LaneCount, int ElementWidthBits)
    {
        internal int TotalWidthBits => checked(LaneCount * ElementWidthBits);
    }

    internal readonly record struct Description(
        LocalVariable Destination,
        IOperand Source,
        Shape Shape,
        int SourceLane)
    {
        internal bool ReadsVectorLane => SourceLane >= 0;
    }

    internal readonly record struct BinaryDescription(
        LocalVariable Destination,
        LocalVariable Left,
        LocalVariable Right,
        Shape Shape,
        OpCode Operation,
        int RightLane = -1);

    internal readonly record struct VariableShiftDescription(
        LocalVariable Destination,
        LocalVariable Value,
        LocalVariable Shift,
        Shape Shape);

    internal readonly record struct UnaryDescription(
        LocalVariable Destination,
        LocalVariable Source,
        Shape Shape);

    internal readonly record struct NarrowDescription(
        LocalVariable Destination,
        LocalVariable? PreservedDestination,
        LocalVariable Source,
        Shape DestinationShape,
        Shape SourceShape,
        bool IsUpper);

    internal readonly record struct BitwiseDescription(
        LocalVariable Destination,
        LocalVariable Value,
        LocalVariable Mask,
        Shape Shape);

    internal readonly record struct BitwiseSelectDescription(
        LocalVariable Destination,
        LocalVariable Mask,
        LocalVariable SelectedWhenSet,
        LocalVariable SelectedWhenClear,
        Shape Shape);

    internal readonly record struct IntegerComparisonDescription(
        LocalVariable Destination,
        LocalVariable Left,
        LocalVariable Right,
        Shape Shape);

    internal static bool TryDescribe(
        Instruction instruction,
        out Description description,
        out string failure)
    {
        description = default;
        failure = string.Empty;
        if (instruction.OpCode != OpCode.VectorDuplicate)
        {
            failure = "操作码不是 VectorDuplicate。";
            return false;
        }

        if (instruction.Operands.Count != 5
            || instruction.Operands[0] is not LocalVariable destination
            || instruction.Operands[2] is not Immediate laneCount
            || instruction.Operands[3] is not Immediate elementWidth
            || instruction.Operands[4] is not Immediate sourceLane)
        {
            failure = "VectorDuplicate 必须包含目标、来源、通道数、元素位宽和来源通道。";
            return false;
        }

        if (laneCount.Value is < 1 or > 16
            || elementWidth.Value is not (8 or 16 or 32 or 64))
        {
            failure = $"通道布局无效：laneCount={laneCount.Value}, elementWidth={elementWidth.Value}。";
            return false;
        }

        var totalWidth = checked(laneCount.Value * elementWidth.Value);
        if (totalWidth is not (64 or 128))
        {
            failure = $"DUP 目标总位宽必须为64或128，实际为{totalWidth}。";
            return false;
        }

        if (sourceLane.Value < -1)
        {
            failure = $"来源通道索引无效：{sourceLane.Value}。";
            return false;
        }

        if (sourceLane.Value >= 0)
        {
            if (instruction.Operands[1] is not LocalVariable)
            {
                failure = "向量通道来源必须是已进入 SSA 的局部量。";
                return false;
            }

            var sourceCapacity = 128 / elementWidth.Value;
            if (sourceLane.Value >= sourceCapacity)
            {
                failure = $"来源通道超出128位向量范围：lane={sourceLane.Value}, capacity={sourceCapacity}。";
                return false;
            }
        }

        description = new Description(
            destination,
            instruction.Operands[1],
            new Shape(checked((int)laneCount.Value), checked((int)elementWidth.Value)),
            checked((int)sourceLane.Value));
        return true;
    }

    internal static bool TryDescribeFloatingBinary(
        Instruction instruction,
        out BinaryDescription description,
        out string failure)
    {
        description = default;
        failure = string.Empty;
        if (instruction.OpCode is not (OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide
            or OpCode.VectorMultiplyByElement or OpCode.VectorCompareFloatingGreaterThan
            or OpCode.VectorCompareFloatingEqual))
        {
            failure = "操作码不是浮点向量二元运算。";
            return false;
        }

        var byElement = instruction.OpCode == OpCode.VectorMultiplyByElement;
        var operands = instruction.Operands;
        var rightLane = -1;
        if (byElement)
        {
            if (operands is not [LocalVariable, LocalVariable, LocalVariable,
                    Immediate lane, Immediate, Immediate]
                || lane.Value is < 0 or > 3)
            {
                failure = "按元素乘法必须包含双来源身份、有效来源通道及目标布局。";
                return false;
            }
            rightLane = (int)lane.Value;
            operands = new OperandList([operands[0], operands[1], operands[2], operands[4], operands[5]]);
        }
        if (operands is not
            [LocalVariable destination, LocalVariable left, LocalVariable right,
                Immediate laneCount, Immediate elementWidth])
        {
            failure = "浮点向量运算必须包含目标、双来源、通道数和元素位宽。";
            return false;
        }

        if (elementWidth.Value is not (32 or 64)
            || laneCount.Value is < 1 or > 4
            || checked(laneCount.Value * elementWidth.Value) is not (64 or 128)
            || byElement && (elementWidth.Value != 32 || rightLane >= 128 / elementWidth.Value))
        {
            failure = $"浮点向量通道布局无效：laneCount={laneCount.Value}, elementWidth={elementWidth.Value}。";
            return false;
        }

        description = new BinaryDescription(
            destination,
            left,
            right,
            new Shape(checked((int)laneCount.Value), checked((int)elementWidth.Value)),
            byElement ? OpCode.Multiply : instruction.OpCode,
            rightLane);
        return true;
    }

    internal static bool TryDescribeUnsignedVariableShift(
        Instruction instruction,
        out VariableShiftDescription description,
        out string failure)
    {
        description = default;
        failure = string.Empty;
        if (instruction.OpCode != OpCode.VectorShiftLeftUnsignedVariable)
        {
            failure = "操作码不是 VectorShiftLeftUnsignedVariable。";
            return false;
        }

        if (instruction.Operands is not
            [LocalVariable destination, LocalVariable value, LocalVariable shift,
                Immediate laneCount, Immediate elementWidth])
        {
            failure = "USHL 必须包含目标、数值来源、移位来源、通道数和元素位宽。";
            return false;
        }

        if (elementWidth.Value is not (8 or 16 or 32 or 64)
            || laneCount.Value is < 1 or > 16
            || checked(laneCount.Value * elementWidth.Value) is not (64 or 128))
        {
            failure = $"USHL 通道布局无效：laneCount={laneCount.Value}, elementWidth={elementWidth.Value}。";
            return false;
        }

        description = new VariableShiftDescription(
            destination,
            value,
            shift,
            new Shape(checked((int)laneCount.Value), checked((int)elementWidth.Value)));
        return true;
    }

    internal static bool TryDescribeFloatingLessThanZero(
        Instruction instruction,
        out UnaryDescription description,
        out string failure)
    {
        description = default;
        failure = string.Empty;
        if (instruction.OpCode != OpCode.VectorCompareFloatingLessThanZero)
        {
            failure = "操作码不是 VectorCompareFloatingLessThanZero。";
            return false;
        }

        if (instruction.Operands is not
            [LocalVariable destination, LocalVariable source, Immediate laneCount, Immediate elementWidth])
        {
            failure = "FCMLT 必须包含目标、来源、通道数和元素位宽。";
            return false;
        }

        if (elementWidth.Value is not (32 or 64)
            || laneCount.Value is < 1 or > 4
            || checked(laneCount.Value * elementWidth.Value) is not (64 or 128))
        {
            failure = $"FCMLT 通道布局无效：laneCount={laneCount.Value}, elementWidth={elementWidth.Value}。";
            return false;
        }

        description = new UnaryDescription(
            destination,
            source,
            new Shape(checked((int)laneCount.Value), checked((int)elementWidth.Value)));
        return true;
    }

    internal static bool TryDescribeFloatingToSignedInteger(
        Instruction instruction,
        out UnaryDescription description,
        out string failure)
    {
        description = default;
        failure = string.Empty;
        if (instruction.OpCode != OpCode.VectorConvertFloatToSignedInteger)
        {
            failure = "操作码不是 VectorConvertFloatToSignedInteger。";
            return false;
        }

        if (instruction.Operands is not
            [LocalVariable destination, LocalVariable source, Immediate laneCount, Immediate elementWidth])
        {
            failure = "FCVTZS 必须包含目标、来源、通道数和元素位宽。";
            return false;
        }

        if (elementWidth.Value is not (32 or 64)
            || laneCount.Value is < 1 or > 4
            || checked(laneCount.Value * elementWidth.Value) is not (64 or 128))
        {
            failure = $"FCVTZS 通道布局无效：laneCount={laneCount.Value}, elementWidth={elementWidth.Value}。";
            return false;
        }

        description = new UnaryDescription(
            destination,
            source,
            new Shape(checked((int)laneCount.Value), checked((int)elementWidth.Value)));
        return true;
    }

    internal static bool TryDescribeNarrowExtract(
        Instruction instruction,
        out NarrowDescription description,
        out string failure)
    {
        description = default;
        failure = string.Empty;
        var isUpper = instruction.OpCode == OpCode.VectorNarrowExtractUpper;
        if (!isUpper && instruction.OpCode != OpCode.VectorNarrowExtract)
        {
            failure = "操作码不是 VectorNarrowExtract 或 VectorNarrowExtractUpper。";
            return false;
        }

        LocalVariable destination;
        LocalVariable? preservedDestination;
        LocalVariable source;
        Immediate laneCount;
        Immediate sourceElementWidth;
        if (isUpper)
        {
            if (instruction.Operands is not
                [LocalVariable upperDestination, LocalVariable preserved, LocalVariable upperSource,
                    Immediate upperLaneCount, Immediate upperElementWidth])
            {
                failure = "XTN2 必须包含目标、目标旧值、来源、来源通道数和来源元素位宽。";
                return false;
            }

            destination = upperDestination;
            preservedDestination = preserved;
            source = upperSource;
            laneCount = upperLaneCount;
            sourceElementWidth = upperElementWidth;
        }
        else
        {
            if (instruction.Operands is not
                [LocalVariable lowerDestination, LocalVariable lowerSource,
                    Immediate lowerLaneCount, Immediate lowerElementWidth])
            {
                failure = "XTN 必须包含目标、来源、来源通道数和来源元素位宽。";
                return false;
            }

            destination = lowerDestination;
            preservedDestination = null;
            source = lowerSource;
            laneCount = lowerLaneCount;
            sourceElementWidth = lowerElementWidth;
        }

        if (sourceElementWidth.Value is not (16 or 32 or 64)
            || laneCount.Value is < 2 or > 8
            || checked(laneCount.Value * sourceElementWidth.Value) != 128)
        {
            failure = $"XTN 通道布局无效：laneCount={laneCount.Value}, sourceElementWidth={sourceElementWidth.Value}。";
            return false;
        }

        var narrowedElementWidth = checked((int)sourceElementWidth.Value / 2);
        var narrowedLaneCount = checked((int)laneCount.Value);
        var destinationShape = new Shape(
            isUpper ? narrowedLaneCount * 2 : narrowedLaneCount,
            narrowedElementWidth);
        var sourceShape = new Shape(narrowedLaneCount, checked((int)sourceElementWidth.Value));
        description = new NarrowDescription(
            destination,
            preservedDestination,
            source,
            destinationShape,
            sourceShape,
            isUpper);
        return true;
    }

    internal static bool TryDescribeUnsignedHigher(
        Instruction instruction,
        out IntegerComparisonDescription description,
        out string failure)
        => TryDescribeUnsignedComparison(
            instruction,
            OpCode.VectorCompareUnsignedHigher,
            "CMHI",
            out description,
            out failure);

    internal static bool TryDescribeUnsignedHigherOrSame(
        Instruction instruction,
        out IntegerComparisonDescription description,
        out string failure)
        => TryDescribeUnsignedComparison(
            instruction,
            OpCode.VectorCompareUnsignedHigherOrSame,
            "CMHS",
            out description,
            out failure);

    private static bool TryDescribeUnsignedComparison(
        Instruction instruction,
        OpCode expectedOpCode,
        string mnemonic,
        out IntegerComparisonDescription description,
        out string failure)
    {
        description = default;
        failure = string.Empty;
        if (instruction.OpCode != expectedOpCode)
        {
            failure = $"操作码不是 {expectedOpCode}。";
            return false;
        }

        if (instruction.Operands is not
            [LocalVariable destination, LocalVariable left, LocalVariable right,
                Immediate laneCount, Immediate elementWidth])
        {
            failure = $"{mnemonic} 必须包含目标、两个来源、通道数和元素位宽。";
            return false;
        }

        if (elementWidth.Value is not (8 or 16 or 32 or 64)
            || laneCount.Value is < 1 or > 16
            || checked(laneCount.Value * elementWidth.Value) is not (64 or 128))
        {
            failure = $"{mnemonic} 通道布局无效：laneCount={laneCount.Value}, elementWidth={elementWidth.Value}。";
            return false;
        }

        description = new IntegerComparisonDescription(
            destination,
            left,
            right,
            new Shape(checked((int)laneCount.Value), checked((int)elementWidth.Value)));
        return true;
    }

    internal static bool TryDescribeBitwiseInsert(
        Instruction instruction,
        out BitwiseDescription description,
        out string failure)
    {
        description = default;
        failure = string.Empty;
        if (instruction.OpCode != OpCode.VectorBitwiseInsert)
        {
            failure = "操作码不是 VectorBitwiseInsert。";
            return false;
        }

        if (instruction.Operands is not
            [LocalVariable destination, LocalVariable value, LocalVariable mask, Immediate vectorWidth])
        {
            failure = "BIT 必须包含目标、值来源、掩码来源和向量位宽。";
            return false;
        }

        if (vectorWidth.Value is not (64 or 128))
        {
            failure = $"BIT 向量位宽无效：vectorWidth={vectorWidth.Value}。";
            return false;
        }

        description = new BitwiseDescription(
            destination,
            value,
            mask,
            vectorWidth.Value == 64 ? new Shape(1, 64) : new Shape(2, 64));
        return true;
    }

    internal static bool TryDescribeBitwiseSelect(
        Instruction instruction,
        out BitwiseSelectDescription description,
        out string failure)
    {
        description = default;
        failure = string.Empty;
        if (instruction.OpCode != OpCode.VectorBitwiseSelect)
        {
            failure = "操作码不是 VectorBitwiseSelect。";
            return false;
        }

        if (instruction.Operands is not
            [LocalVariable destination, LocalVariable mask, LocalVariable selectedWhenSet,
                LocalVariable selectedWhenClear, Immediate vectorWidth])
        {
            failure = "BSL 必须包含目标、掩码来源、置位选择来源、清位选择来源和向量位宽。";
            return false;
        }

        if (vectorWidth.Value is not (64 or 128))
        {
            failure = $"BSL 向量位宽必须为64或128，实际为{vectorWidth.Value}。";
            return false;
        }

        description = new BitwiseSelectDescription(
            destination,
            mask,
            selectedWhenSet,
            selectedWhenClear,
            vectorWidth.Value == 64 ? new Shape(1, 64) : new Shape(2, 64));
        return true;
    }

    internal static ulong GetElementMask(int elementWidthBits) => elementWidthBits switch
    {
        8 => byte.MaxValue,
        16 => ushort.MaxValue,
        32 => uint.MaxValue,
        64 => ulong.MaxValue,
        _ => throw new ArgumentOutOfRangeException(nameof(elementWidthBits), elementWidthBits, "元素位宽无效。"),
    };

    internal static ulong GetReplicationMultiplier(int elementWidthBits) => elementWidthBits switch
    {
        8 => 0x0101010101010101UL,
        16 => 0x0001000100010001UL,
        32 => 0x0000000100000001UL,
        64 => 1UL,
        _ => throw new ArgumentOutOfRangeException(nameof(elementWidthBits), elementWidthBits, "元素位宽无效。"),
    };
}

