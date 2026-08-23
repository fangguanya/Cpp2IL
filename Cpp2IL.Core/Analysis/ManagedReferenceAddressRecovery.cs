using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 把“引用类型栈槽取址后立即按零偏移读取”的原生地址链恢复为直接托管引用访问。
///
/// IL2CPP会在异常清理路径中先把接口或对象局部量的地址写入临时寄存器，再从该地址
/// 读取同一个引用并执行isinst。若保留这层原生地址，IL生成器会发出ldloca，最终形成
/// IEnumerator*到object之类的非法转换。退SSA后的两个互斥退出边可能各自写入同一个
/// 地址载体；只要该载体的全部定义都取同一个引用槽地址，且全部读取都是零偏移解引用，
/// 就可以统一折叠。ref/out调用、值类型地址、不同槽定义和带偏移内存访问均保持原样。
/// </summary>
public static class ManagedReferenceAddressRecovery
{
    public static int Run(MethodAnalysisContext method) => Run(method.ControlFlowGraph!.Instructions);

    internal static int Run(IReadOnlyList<Instruction> instructions)
    {
        var definitions = CollectDefinitions(instructions);
        var rewritten = 0;

        foreach (var definitionGroup in definitions)
        {
            var carrier = definitionGroup.Key;
            var carrierDefinitions = definitionGroup.Value;
            if (!TryGetCommonAddressedSlot(carrierDefinitions, carrier, out var slot))
                continue;

            var dereferences = CollectExclusiveZeroOffsetDereferences(
                instructions,
                carrierDefinitions,
                carrier);
            if (dereferences == null || dereferences.Count == 0)
                continue;

            var slotType = ResolveManagedSlotType(slot, carrier, dereferences);
            if (slotType == null)
                continue;

            slot.Type ??= slotType;

            foreach (var use in dereferences)
            {
                use.Instruction.SetOperand(use.OperandIndex, slot);
                rewritten++;
            }

            foreach (var addressMove in carrierDefinitions)
            {
                addressMove.OpCode = OpCode.Nop;
                addressMove.SetOperands();
            }
        }

        return rewritten;
    }

    private static Dictionary<LocalVariable, List<Instruction>> CollectDefinitions(
        IReadOnlyList<Instruction> instructions)
    {
        var definitions = new Dictionary<LocalVariable, List<Instruction>>();
        foreach (var instruction in instructions)
        {
            if (instruction.Destination is not LocalVariable destination)
                continue;

            if (!definitions.TryGetValue(destination, out var items))
            {
                items = [];
                definitions[destination] = items;
            }

            items.Add(instruction);
        }

        return definitions;
    }

    /// <summary>
    /// 验证同一退SSA载体的全部定义都取同一个局部槽地址，并返回该唯一槽。
    /// </summary>
    private static bool TryGetCommonAddressedSlot(
        IReadOnlyList<Instruction> definitions,
        LocalVariable carrier,
        out LocalVariable slot)
    {
        slot = null!;
        foreach (var definition in definitions)
        {
            if (definition is not
                {
                    OpCode: OpCode.Move,
                    Operands:
                    [
                        LocalVariable destination,
                        AddressOf { Target: LocalVariable addressed }
                    ]
                }
                || !ReferenceEquals(destination, carrier))
                return false;

            if (slot == null)
            {
                slot = addressed;
                continue;
            }

            if (!ReferenceEquals(slot, addressed))
                return false;
        }

        return slot != null;
    }

    private static List<DereferenceUse>? CollectExclusiveZeroOffsetDereferences(
        IReadOnlyList<Instruction> instructions,
        IReadOnlyList<Instruction> addressMoves,
        LocalVariable carrier)
    {
        var addressMoveSet = new HashSet<Instruction>(addressMoves);
        var dereferences = new List<DereferenceUse>();
        foreach (var instruction in instructions)
        {
            for (var operandIndex = 0; operandIndex < instruction.Operands.Count; operandIndex++)
            {
                if (addressMoveSet.Contains(instruction) && operandIndex == 0)
                    continue;

                var operand = instruction.Operands[operandIndex];
                if (operand is MemoryOperand
                    {
                        Base: LocalVariable memoryBase,
                        Index: null,
                        Addend: 0,
                        Scale: 0
                    }
                    && ReferenceEquals(memoryBase, carrier))
                {
                    dereferences.Add(new DereferenceUse(instruction, operandIndex));
                    continue;
                }

                if (ContainsLocal(operand, carrier))
                    return null;
            }
        }

        return dereferences;
    }

    private static TypeAnalysisContext? ResolveManagedSlotType(
        LocalVariable slot,
        LocalVariable carrier,
        IReadOnlyList<DereferenceUse> dereferences)
    {
        if (slot.Type != null)
            return IsManagedReferenceType(slot.Type) ? slot.Type : null;

        var candidates = new List<TypeAnalysisContext>();
        if (carrier.Type is ByRefTypeAnalysisContext { ElementType: { } elementType }
            && IsManagedReferenceType(elementType))
            candidates.Add(elementType);

        foreach (var use in dereferences)
        {
            var instruction = use.Instruction;
            if (instruction.OpCode != OpCode.Move || instruction.Operands.Count < 2)
                continue;

            TypeAnalysisContext? candidate = use.OperandIndex switch
            {
                0 when instruction.Operands[1] is LocalVariable { Type: { } storedType } => storedType,
                > 0 when instruction.Operands[0] is LocalVariable { Type: { } loadedType } => loadedType,
                _ => null,
            };
            if (candidate != null && IsManagedReferenceType(candidate))
                candidates.Add(candidate);
        }

        if (candidates.Count == 0)
            return null;

        var selected = candidates[0];
        return candidates.All(candidate => GenericCallRebinder.TypesEquivalent(candidate, selected))
            ? selected
            : null;
    }

    private static bool ContainsLocal(IOperand operand, LocalVariable expected)
    {
        return operand switch
        {
            LocalVariable local => ReferenceEquals(local, expected),
            MemoryOperand memory => ReferenceEquals(memory.Base, expected)
                || ReferenceEquals(memory.Index, expected),
            FieldReference field => ReferenceEquals(field.Local, expected),
            AddressOf address => ReferenceEquals(address.Target, expected),
            ArrayAccess array => ReferenceEquals(array.Array, expected)
                || ReferenceEquals(array.Index, expected),
            _ => false,
        };
    }

    private static bool IsManagedReferenceType(TypeAnalysisContext type)
        => !type.IsValueType
           && type is not ByRefTypeAnalysisContext
           && type is not PointerTypeAnalysisContext;

    private sealed record DereferenceUse(Instruction Instruction, int OperandIndex);
}
