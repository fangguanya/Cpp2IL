using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using LibCpp2IL.Elf;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 把只读 ELF 段中的绝对地址标量加载恢复为 Single/Double 字面量。仅当同一算术指令
/// 已有唯一浮点类型证据时执行，且拒绝可写段、越界、未对齐和混合精度输入。
/// </summary>
public static class ReadOnlyScalarLiteralRecovery
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.AppContext.Binary is not ElfFile elf)
            return;

        var binary = method.AppContext.Binary;
        var instructions = method.ControlFlowGraph!.Instructions;
        var definitions = instructions
            .Where(candidate => candidate.Destination is LocalVariable)
            .GroupBy(candidate => (LocalVariable)candidate.Destination!)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<Instruction>)group.ToList());
        var uniqueDefinitions = RuntimeMetadataStringTableRecovery.BuildUniqueDefinitions(instructions);
        foreach (var block in method.ControlFlowGraph.Blocks)
        foreach (var instruction in block.Instructions)
        {
            ResolveIndexedUInt16Table(instruction, block, method, elf, definitions, uniqueDefinitions);
            for (var operandIndex = 1; operandIndex < instruction.Operands.Count; operandIndex++)
            {
                if (instruction.Operands[operandIndex] is not MemoryOperand
                    {
                        Base: null,
                        Index: null,
                        Scale: 0,
                        Addend: >= 0
                    } memory)
                    continue;

                if (!TryResolveFloatingType(
                        instruction,
                        operandIndex,
                        method.AppContext,
                        out var floatingType,
                        out var widthBits))
                    continue;

                var address = checked((ulong)memory.Addend);
                var byteCount = widthBits / 8;
                if ((address & (ulong)(byteCount - 1)) != 0
                    || !elf.IsReadOnlyFileBackedVirtualRange(address, byteCount)
                    || !binary.TryMapVirtualAddressToRaw(address, out var rawOffset)
                    || rawOffset < 0
                    || rawOffset > binary.RawLength - byteCount)
                    continue;

                var raw = binary.GetRawBinaryContent().Slice(checked((int)rawOffset), byteCount);
                if (!TryDecodeScalarLiteral(raw, binary.IsBigEndian, widthBits, out var literal))
                    continue;

                instruction.SetOperand(operandIndex, literal);
                if (instruction.OpCode is
                        OpCode.Move or OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide
                    && instruction.Destination is LocalVariable moveDestination)
                    moveDestination.Type = floatingType;
            }
        }
    }

    private static void ResolveIndexedUInt16Table(
        Instruction instruction,
        Block tableBlock,
        MethodAnalysisContext method,
        ElfFile elf,
        IReadOnlyDictionary<LocalVariable, IReadOnlyList<Instruction>> definitions,
        IReadOnlyDictionary<LocalVariable, Instruction> uniqueDefinitions)
    {
        if (instruction.OpCode != OpCode.Return
            || instruction.Operands.Count != 1
            || instruction.Operands[0] is not MemoryOperand
            {
                Index: not null,
                Scale: 2,
                Addend: >= 0
            } memory
            || method.ReturnType?.FullName != "System.Char")
            return;

        if (memory.Index is not LocalVariable tableIndex
            || !TryMatchDominatingUpperBound(
                method, tableBlock, tableIndex, uniqueDefinitions, out var maximumIndex)
            || maximumIndex < 0
            || maximumIndex >= int.MaxValue)
            return;

        var elementCount = checked((int)maximumIndex + 1);
        if (elementCount > int.MaxValue / sizeof(ushort))
            return;
        var byteCount = elementCount * sizeof(ushort);
        var baseAddress = memory.Base is null
            ? 0UL
            : MetadataResolver.ResolveConvergedAbsoluteSlotAddress(memory.Base, definitions, [], []);
        if (baseAddress is null)
            return;

        var addend = (ulong)memory.Addend;
        if (baseAddress.Value > ulong.MaxValue - addend)
            return;
        var address = baseAddress.Value + addend;
        var binary = method.AppContext.Binary;
        if (!TryValidateUInt16TableRange(
                address,
                byteCount,
                binary.RawLength,
                elf.IsReadOnlyFileBackedVirtualRange,
                binary.TryMapVirtualAddressToRaw,
                out var rawOffset))
            return;

        if (rawOffset > int.MaxValue)
            return;
        var raw = binary.GetRawBinaryContent().Slice((int)rawOffset, byteCount);
        if (!TryDecodeUInt16Table(raw, binary.IsBigEndian, elementCount, out var values))
            return;

        tableIndex.Type = method.AppContext.SystemTypes.SystemInt32Type;
        instruction.SetOperand(0, new ReadOnlyUInt16TableLookup(values, memory.Index!));
    }

    private static bool TryMatchDominatingUpperBound(
        MethodAnalysisContext method,
        Block tableBlock,
        LocalVariable tableIndex,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions,
        out long maximumIndex)
    {
        maximumIndex = -1;
        foreach (var guardBlock in method.ControlFlowGraph!.Blocks)
        {
            if (!method.DominatorInfo!.Dominates(guardBlock, tableBlock)
                || guardBlock.Instructions.LastOrDefault() is not
                {
                    OpCode: OpCode.ConditionalJump,
                    Operands: [Block jumpTarget, LocalVariable condition]
                }
                || guardBlock.Successors.Count != 2
                || method.DominatorInfo.Dominates(jumpTarget, tableBlock)
                || CanReach(jumpTarget, tableBlock)
                || !definitions.TryGetValue(condition, out var comparison)
                || comparison is not
                {
                    OpCode: OpCode.CheckGreaterUnsigned,
                    Operands: [LocalVariable, var comparedIndex, Immediate upperBound]
                }
                || !RuntimeMetadataStringTableRecovery.OperandsRepresentSameValue(
                    tableIndex, comparedIndex, definitions))
                continue;

            maximumIndex = upperBound.Value;
            return true;
        }

        return false;
    }

    internal static bool CanReach(Block start, Block target)
    {
        var pending = new Stack<Block>();
        var visited = new HashSet<Block>();
        pending.Push(start);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current))
                continue;
            if (ReferenceEquals(current, target))
                return true;
            foreach (var successor in current.Successors)
                pending.Push(successor);
        }

        return false;
    }

    internal static bool TryValidateUInt16TableRange(
        ulong address,
        int byteCount,
        long rawLength,
        Func<ulong, int, bool> isReadOnly,
        TryMapVirtualAddress map,
        out long rawOffset)
    {
        rawOffset = -1;
        return byteCount > 0
               && (address & 1) == 0
               && rawLength >= byteCount
               && isReadOnly(address, byteCount)
               && map(address, out rawOffset)
               && rawOffset >= 0
               && rawOffset <= rawLength - byteCount;
    }

    internal delegate bool TryMapVirtualAddress(ulong address, out long rawOffset);

    internal static bool TryDecodeUInt16Table(
        ReadOnlySpan<byte> raw,
        bool isBigEndian,
        int elementCount,
        out string values)
    {
        if (elementCount <= 0
            || elementCount > int.MaxValue / sizeof(ushort)
            || raw.Length != elementCount * sizeof(ushort))
        {
            values = string.Empty;
            return false;
        }

        var chars = new char[elementCount];
        for (var index = 0; index < elementCount; index++)
        {
            var element = raw.Slice(index * sizeof(ushort), sizeof(ushort));
            chars[index] = (char)(isBigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(element)
                : BinaryPrimitives.ReadUInt16LittleEndian(element));
        }

        values = new string(chars);
        return true;
    }

    internal static bool TryDecodeScalarLiteral(
        ReadOnlySpan<byte> raw,
        bool isBigEndian,
        int widthBits,
        out IOperand literal)
    {
        if (widthBits == 32 && raw.Length >= sizeof(int))
        {
            var bits = isBigEndian
                ? BinaryPrimitives.ReadInt32BigEndian(raw)
                : BinaryPrimitives.ReadInt32LittleEndian(raw);
            literal = new FloatLiteral(FloatingPointBitHelper.Int32BitsToSingle(bits));
            return true;
        }

        if (widthBits == 64 && raw.Length >= sizeof(long))
        {
            var bits = isBigEndian
                ? BinaryPrimitives.ReadInt64BigEndian(raw)
                : BinaryPrimitives.ReadInt64LittleEndian(raw);
            literal = new DoubleLiteral(BitConverter.Int64BitsToDouble(bits));
            return true;
        }

        literal = null!;
        return false;
    }

    private static bool TryResolveFloatingType(
        Instruction instruction,
        int operandIndex,
        ApplicationAnalysisContext appContext,
        out TypeAnalysisContext floatingType,
        out int widthBits)
    {
        TypeAnalysisContext? expectedType = null;
        if (instruction.IsCall
            && instruction.Operands[0] is MethodAnalysisContext called
            && TryResolveCallParameterIndex(instruction, called, operandIndex, out var parameterIndex))
        {
            expectedType = called.Parameters[parameterIndex].ParameterType;
        }
        else if (instruction is { OpCode: OpCode.Move, Operands: [LocalVariable destination, ..] }
                 && operandIndex == 1)
        {
            expectedType = destination.Type;
        }
        else if (instruction is { OpCode: OpCode.Move, Operands: [FieldReference field, ..] }
                 && operandIndex == 1)
        {
            expectedType = field.Field.FieldType;
        }
        else if (instruction.OpCode is
                 OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide)
        {
            var types = instruction.Operands
                .OfType<LocalVariable>()
                .Select(local => local.Type)
                .Where(type => type?.FullName is "System.Single" or "System.Double")
                .GroupBy(type => type!.FullName, System.StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
            if (types.Length == 1)
                expectedType = types[0];
        }

        if (expectedType?.FullName is not ("System.Single" or "System.Double"))
        {
            floatingType = null!;
            widthBits = 0;
            return false;
        }

        widthBits = expectedType.FullName == "System.Single" ? 32 : 64;
        floatingType = widthBits == 32
            ? appContext.SystemTypes.SystemSingleType
            : appContext.SystemTypes.SystemDoubleType;
        return true;
    }

    /// <summary>
    /// 根据 ISIL 调用操作数布局把当前操作数映射回托管形参。实例接收者和非 void
    /// 调用的返回值槽位均不属于形参；越界、静态/实例布局不一致时保持未知。
    /// </summary>
    internal static bool TryResolveCallParameterIndex(
        Instruction call,
        MethodAnalysisContext called,
        int operandIndex,
        out int parameterIndex)
    {
        var firstParameter = (call.OpCode == OpCode.Call ? 2 : 1)
                             + (called.IsStatic ? 0 : 1);
        parameterIndex = operandIndex - firstParameter;
        return parameterIndex >= 0 && parameterIndex < called.Parameters.Count;
    }
}
