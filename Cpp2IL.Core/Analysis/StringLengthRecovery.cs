using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 把 IL2CPP 字符串布局字段读取恢复为公开的 <c>System.String.Length</c>。
/// </summary>
/// <remarks>
/// 原生代码直接读取 <c>System.String._stringLength</c>，而该字段在托管运行库中不可访问。
/// 只有字段声明类型、字段类型和读取方向全部精确匹配时才执行恢复。接收者的分析类型可能在控制流合并后
/// 被擦除为 <c>System.Object</c>，因此不把它作为字段身份条件；字段写入或相似名称字段保持原样，
/// 以免把运行库内部初始化或业务字段误判为属性访问。
/// </remarks>
public static class StringLengthRecovery
{
    public static int Run(MethodAnalysisContext method)
    {
        var recovered = 0;

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            for (var operandIndex = 0; operandIndex < instruction.Operands.Count; operandIndex++)
            {
                if (IsDestinationWrite(instruction, operandIndex)
                    || instruction.Operands[operandIndex] is not FieldReference field
                    || !IsExactStringLengthField(field))
                    continue;

                instruction.SetOperand(operandIndex, new StringLength(field.Local));
                recovered++;
            }
        }

        return recovered;
    }

    private static bool IsDestinationWrite(Instruction instruction, int operandIndex)
        => operandIndex == 0 && instruction.OpCode == OpCode.Move;

    private static bool IsExactStringLengthField(FieldReference field)
        => !field.Field.IsStatic
           && field.Field.Name == "_stringLength"
           && field.Field.DeclaringType.FullName == "System.String"
           && field.Field.FieldType.FullName == "System.Int32";
}
