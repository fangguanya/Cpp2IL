using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

// Remove null and bounds checks which are explicit in il2cpp but implicit in IL
public static class InjectedCheckRemover
{
    public static void Run(MethodAnalysisContext method) => Run(method.ControlFlowGraph!, method);

    public static void Run(ISILControlFlowGraph cfg) => Run(cfg, null);

    private static void Run(ISILControlFlowGraph cfg, MethodAnalysisContext? method)
    {
        var defOf = BuildDefMap(cfg);
        var removedAny = false;

        foreach (var block in cfg.Blocks)
        {
            // 早期元数据保护段和SSA变换会重写块尾，但块类型缓存可能尚未同步；
            // 条件跳转指令及其目标才是删除注入检查的权威控制流证据。
            if (block.Instructions.Count == 0)
                continue;

            var terminator = block.Instructions[^1];

            if (terminator.OpCode != OpCode.ConditionalJump)
                continue;

            if (terminator.Operands[0] is not Block target
                || GetInjectedThrowType(target) is not { } thrownType
                || terminator.Operands[1] is not LocalVariable condition
                || !defOf.TryGetValue(condition, out var definition)
                || !IsInjectedCheck(definition, thrownType))
                continue;

            terminator.OpCode = OpCode.Nop;
            terminator.SetOperands();

            // CIL成员访问仍隐含空引用异常边；这里只移除IL2CPP显式检查控制流，
            // 保留异常Phi的保守类型证据，避免把剩余单一异常状态误传播为业务类型。
            block.Successors.Remove(target);
            target.Predecessors.Remove(block);
            block.CalculateBlockType();
            removedAny = true;
        }

        if (!removedAny)
            return;

        // delete any throw blocks
        cfg.RemoveUnreachableBlocks();
        if (method == null)
            DeadCodeEliminator.Run(cfg);
        else
            DeadCodeEliminator.Run(method);
    }

    private static bool IsInjectedCheck(Instruction definition, string thrownType) =>
        thrownType switch
        {
            "System.NullReferenceException" => definition is { OpCode: OpCode.CheckEqual } && definition.Operands[2] is Immediate { Value: 0 },
            "System.IndexOutOfRangeException" => definition.OpCode is >= OpCode.CheckEqual and <= OpCode.CheckLessOrEqualUnsigned,
            _ => false
        };

    // The full name of the exception if this block does nothing but throw an injected check's exception, else null.
    private static string? GetInjectedThrowType(Block block)
    {
        string? thrown = null;

        foreach (var instruction in block.Instructions)
        {
            switch (instruction.OpCode)
            {
                // 抛出块中的Phi只合并到该异常边上的寄存器状态，Throw不读取这些结果；
                // 它们是SSA结构证据，不构成业务副作用。
                case OpCode.Nop or OpCode.Interrupt or OpCode.Phi:
                case OpCode.Return when thrown != null:
                    continue;

                case OpCode.Throw when thrown == null
                    && instruction.Operands is [TypeAnalysisContext { FullName: "System.NullReferenceException" or "System.IndexOutOfRangeException" } exception]:
                    thrown = exception.FullName;
                    continue;

                default:
                    return null;
            }
        }

        return thrown;
    }

    private static Dictionary<LocalVariable, Instruction> BuildDefMap(ISILControlFlowGraph cfg)
    {
        var defs = new Dictionary<LocalVariable, Instruction>();

        foreach (var instruction in cfg.Instructions)
            if (instruction.Destination is LocalVariable local)
                defs[local] = instruction;

        return defs;
    }
}
