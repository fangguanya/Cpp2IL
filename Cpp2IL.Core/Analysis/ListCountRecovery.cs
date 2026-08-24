using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 把 IL2CPP 的 <c>List&lt;T&gt;._size</c> 布局读取恢复为公开的 <c>List&lt;T&gt;.Count</c>。
/// </summary>
/// <remarks>
/// 该恢复器只接受具体泛型 List 实例、运行库原始 <c>_size</c> 字段、Int32 字段类型和读取方向。
/// 写入路径由 Add/Clear 等完整控制流恢复器负责；未闭合的写入继续保留，作为源码编译红门。
/// </remarks>
public static class ListCountRecovery
{
    private const string ListTypeFullName = "System.Collections.Generic.List`1";

    public static int Run(MethodAnalysisContext method)
    {
        var recovered = 0;

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            for (var operandIndex = 0; operandIndex < instruction.Operands.Count; operandIndex++)
            {
                if (IsDestinationWrite(instruction, operandIndex)
                    || !TryGetExactListCount(
                        instruction.Operands[operandIndex],
                        out var receiver,
                        out var listType))
                    continue;

                instruction.SetOperand(operandIndex, new ListCount(receiver, listType));
                recovered++;
            }
        }

        return recovered;
    }

    private static bool IsDestinationWrite(Instruction instruction, int operandIndex)
        => operandIndex == 0 && instruction.OpCode == OpCode.Move;

    private static bool TryGetExactListCount(
        IOperand operand,
        out LocalVariable receiver,
        out GenericInstanceTypeAnalysisContext listType)
    {
        if (operand is FieldReference field
            && TryGetExactListType(field, out listType))
        {
            receiver = field.Local;
            return true;
        }

        // 中文注释：属性 getter 的结果在字段偏移解析之后才可能从 IEnumerable<T>
        // 收紧为 List<T>。此时原生 [list+0x18] 仍是 MemoryOperand；以具体 List 布局
        // 唯一查回 _size 字段，避免再次运行整张 MetadataResolver 扫描。
        if (operand is MemoryOperand
            {
                Base: LocalVariable { Type: GenericInstanceTypeAnalysisContext candidate } local,
                Index: null,
                Scale: 0,
                Addend: var offset,
            }
            && candidate.GenericType.FullName == ListTypeFullName
            && candidate.GenericArguments.Count == 1
            && GenericInstanceFieldLayout.FindConcreteFieldAtOffset(candidate, offset) is { } layoutField
            && TryValidateSizeField(layoutField))
        {
            receiver = local;
            listType = candidate;
            return true;
        }

        receiver = null!;
        listType = null!;
        return false;
    }

    private static bool TryGetExactListType(
        FieldReference field,
        out GenericInstanceTypeAnalysisContext listType)
    {
        var fieldDefinition = field.Field is ConcreteGenericFieldAnalysisContext concreteField
            ? concreteField.BaseFieldContext
            : field.Field;
        // 中文注释：字段上下文可能仍来自 List<object> 共享布局，而接收者已由 Newobj/保存
        // 寄存器恢复为 List<T>；接收者是当前调用点的精确证据，应优先决定 Count 的实例类型。
        listType = field.Local.Type as GenericInstanceTypeAnalysisContext
                   ?? field.Field.DeclaringType as GenericInstanceTypeAnalysisContext
                   ?? null!;

        return TryValidateSizeField(fieldDefinition)
               && fieldDefinition.DeclaringType.FullName == ListTypeFullName
               && listType is not null
               && listType.GenericType.FullName == ListTypeFullName
               && listType.GenericArguments.Count == 1;
    }

    private static bool TryValidateSizeField(FieldAnalysisContext field)
    {
        var definition = field is ConcreteGenericFieldAnalysisContext concrete
            ? concrete.BaseFieldContext
            : field;
        return !definition.IsStatic
               && definition.Name == "_size"
               && definition.FieldType.FullName == "System.Int32"
               && definition.DeclaringType.FullName == ListTypeFullName;
    }
}
