using System;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 把 IL2CPP 内联的 EmptyArray&lt;T&gt;.Value 单例读取恢复为公开 Array.Empty&lt;T&gt; 调用。
/// </summary>
public static class EmptyArrayRecovery
{
    /// <summary>
    /// 恢复当前方法中全部直接或嵌套空数组字段读取。
    /// </summary>
    public static int Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is null)
            return 0;

        var recovered = 0;
        var temporaryIndex = 0;
        foreach (var block in method.ControlFlowGraph.Blocks)
        {
            for (var instructionIndex = 0; instructionIndex < block.Instructions.Count; instructionIndex++)
            {
                var instruction = block.Instructions[instructionIndex];
                if (instruction is { OpCode: OpCode.Move, Operands: [LocalVariable destination, FieldReference source] }
                    && TryResolveEmptyCall(source, out var directCall))
                {
                    instruction.OpCode = OpCode.Call;
                    instruction.SetOperands(directCall, destination);
                    recovered++;
                    continue;
                }

                // 中文注释：Return、Call 等指令可直接携带字段引用；先插入唯一公开调用，
                // 再用定型临时局部替换原操作数，保持控制流与值身份不变。
                for (var operandIndex = 0; operandIndex < instruction.Operands.Count; operandIndex++)
                {
                    if (instruction.Operands[operandIndex] is not FieldReference embedded
                        || (instruction.OpCode == OpCode.Move && operandIndex == 0)
                        || !TryResolveEmptyCall(embedded, out var embeddedCall))
                        continue;

                    var temporary = new LocalVariable(
                        $"emptyArray{temporaryIndex}",
                        new Register(null, $"EMPTY_ARRAY_{temporaryIndex}"),
                        embedded.Field.FieldType);
                    temporaryIndex++;
                    block.Instructions.Insert(
                        instructionIndex,
                        new Instruction(instruction.Index, OpCode.Call, embeddedCall, temporary));
                    instructionIndex++;
                    instruction.SetOperand(operandIndex, temporary);
                    recovered++;
                }
            }
        }

        return recovered;
    }

    /// <summary>
    /// 只接受 System.EmptyArray&lt;T&gt;.Value 与 T[] 完全一致的标准单例布局。
    /// </summary>
    private static bool TryResolveEmptyCall(
        FieldReference source,
        out MethodAnalysisContext emptyCall)
    {
        emptyCall = null!;
        var owner = source.Field.DeclaringType;
        var definitionOwner = owner is GenericInstanceTypeAnalysisContext genericOwner
            ? genericOwner.GenericType
            : owner;
        if (definitionOwner.FullName != "System.EmptyArray`1"
            || source.Field.Name != "Value"
            || !source.Field.IsStatic
            || source.Field.FieldType is not SzArrayTypeAnalysisContext arrayType)
            return false;

        var arrayOwner = owner.DeclaringAssembly.GetTypeByFullName("System.Array");
        var candidates = arrayOwner?.Methods
            .Where(candidate => candidate.Name == "Empty"
                && candidate.IsStatic
                && candidate.Parameters.Count == 0
                && candidate.GenericParameters.Count == 1)
            .ToArray() ?? [];
        if (candidates.Length != 1)
            return false;

        emptyCall = candidates[0].MakeGenericInstanceMethod(arrayType.ElementType);
        return true;
    }
}
