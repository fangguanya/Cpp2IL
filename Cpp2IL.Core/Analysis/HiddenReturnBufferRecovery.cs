using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// 把AAPCS64的X8间接结构返回地址绑定为调用指令的真实返回局部变量。
/// </summary>
public static class HiddenReturnBufferRecovery
{
    public static void Run(MethodAnalysisContext method)
    {
        var definitions = new Dictionary<LocalVariable, Instruction>();
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.Destination is LocalVariable destination)
                definitions[destination] = instruction;
        }

        foreach (var call in method.ControlFlowGraph.Instructions)
        {
            if (call.OpCode is not (OpCode.Call or OpCode.CallVoid)
                || call.Operands.Count < 2
                || call.Operands[0] is not MethodAnalysisContext target)
                continue;

            var candidate = call.Operands[^1];
            if (candidate is not LocalVariable { Register.Name: "X8" })
                continue;

            if (call.OpCode != OpCode.Call
                || !target.ReturnType.IsValueType
                || X64CallingConventionResolver.IsFloatingPoint(target.ReturnType))
            {
                var nonReturnOperands = call.Operands.ToList();
                nonReturnOperands.RemoveAt(nonReturnOperands.Count - 1);
                call.SetOperands(nonReturnOperands);
                continue;
            }

            if (!TryResolveAddressedLocal(candidate, definitions, out var returnLocal))
            {
                method.AddWarning($"ARM64间接返回X8未解析到唯一局部变量：{target.FullName}");
                continue;
            }

            var operands = call.Operands.ToList();
            operands[1] = returnLocal;
            operands.RemoveAt(operands.Count - 1);
            call.SetOperands(operands);
        }
    }

    internal static bool TryResolveAddressedLocal(
        IOperand operand,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions,
        out LocalVariable addressed)
    {
        return TryResolveAddressedLocal(
            operand,
            definitions,
            new HashSet<LocalVariable>(),
            out addressed);
    }

    private static bool TryResolveAddressedLocal(
        IOperand operand,
        IReadOnlyDictionary<LocalVariable, Instruction> definitions,
        HashSet<LocalVariable> visited,
        out LocalVariable addressed)
    {
        if (operand is AddressOf { Target: LocalVariable direct })
        {
            addressed = direct;
            return true;
        }

        if (operand is not LocalVariable local
            || !visited.Add(local)
            || !definitions.TryGetValue(local, out var definition))
        {
            addressed = null!;
            return false;
        }

        if (definition.OpCode == OpCode.Move && definition.Operands.Count >= 2)
            return TryResolveAddressedLocal(definition.Operands[1], definitions, visited, out addressed);

        if (definition.OpCode != OpCode.Phi || definition.Sources.Count == 0)
        {
            addressed = null!;
            return false;
        }

        LocalVariable? common = null;
        foreach (var source in definition.Sources)
        {
            // 每个Phi输入使用独立访问集合，避免一个分支的遍历状态污染另一个分支。
            if (!TryResolveAddressedLocal(
                    source,
                    definitions,
                    new HashSet<LocalVariable>(visited),
                    out var candidate))
            {
                addressed = null!;
                return false;
            }

            common ??= candidate;
            if (!ReferenceEquals(common, candidate))
            {
                addressed = null!;
                return false;
            }
        }

        addressed = common!;
        return common != null;
    }
}
