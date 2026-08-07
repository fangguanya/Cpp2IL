using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Removes pure instructions whose result is never used. This eliminates, among other things, the
/// dead flag/temporary computations the x86 lifter emits eagerly for every comparison - a single
/// <c>cmp</c>/<c>test</c> produces all of CF/OF/SF/ZF/PF plus scratch temporaries, but the branch
/// that follows only consumes one of them.
///
/// Must run while the graph is still in SSA form (every local is assigned exactly once), so that a
/// global use count of zero is sufficient to prove a definition dead. Instructions are turned into
/// nops rather than spliced out; the structural cleanup happens later, out of SSA, where it is safe
/// for phi nodes.
/// </summary>
public static class DeadCodeEliminator
{
    public static void Run(MethodAnalysisContext method) => Run(method.ControlFlowGraph!);

    public static void Run(ISILControlFlowGraph cfg)
    {
        // 从调用、存储、返回和分支等可观察根反向标记其定义依赖。
        // 单纯按“使用次数为零”删除会保留互相引用但没有外部使用的Phi环；
        // SSA单一定义允许一次标记清扫精确删除整个死强连通分量。
        var definitions = new Dictionary<LocalVariable, List<Instruction>>();
        foreach (var block in cfg.Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (instruction.Destination is not LocalVariable destination)
                    continue;

                if (!definitions.TryGetValue(destination, out var localDefinitions))
                    definitions[destination] = localDefinitions = [];
                localDefinitions.Add(instruction);
            }
        }

        var live = new HashSet<Instruction>();
        var workList = new Stack<Instruction>();
        foreach (var instruction in cfg.Blocks.SelectMany(block => block.Instructions))
        {
            // 可删除运算写入普通局部时才是候选；存储等非局部目标始终是根。
            if (IsRemovable(instruction.OpCode) && instruction.Destination is LocalVariable)
                continue;

            if (live.Add(instruction))
                workList.Push(instruction);
        }

        while (workList.Count > 0)
        {
            var instruction = workList.Pop();
            foreach (var used in UsedLocals(instruction))
            {
                if (!definitions.TryGetValue(used, out var localDefinitions))
                    continue;

                // 正式路径处于SSA；若调用者给出非SSA图，则保守保留同名局部的全部定义。
                foreach (var definition in localDefinitions)
                    if (live.Add(definition))
                        workList.Push(definition);
            }
        }

        foreach (var instruction in cfg.Blocks.SelectMany(block => block.Instructions))
        {
            if (!IsRemovable(instruction.OpCode)
                || instruction.Destination is not LocalVariable
                || live.Contains(instruction))
                continue;

            instruction.OpCode = OpCode.Nop;
            instruction.SetOperands();
        }
    }

    /// <summary>
    /// Every local read by the instruction. The single write position - a plain local destination -
    /// is excluded. Memory and field operands always contribute their address/object locals as
    /// reads, even when they are the destination of a store.
    /// </summary>
    private static IEnumerable<LocalVariable> UsedLocals(Instruction instruction)
    {
        var destination = instruction.Destination as LocalVariable;

        foreach (var operand in instruction.Operands)
        {
            switch (operand)
            {
                case LocalVariable local when !ReferenceEquals(local, destination):
                    yield return local;
                    break;
                case MemoryOperand memory:
                    if (memory.Base is LocalVariable baseLocal)
                        yield return baseLocal;
                    if (memory.Index is LocalVariable indexLocal)
                        yield return indexLocal;
                    break;
                // A static field access doesn't read the storage pointer it was resolved from, so that
                // pointer (and the class load feeding it) is free to die.
                case FieldReference { Field.IsStatic: false, Local: { } fieldLocal }:
                    yield return fieldLocal;
                    break;
                // Handing out a slot's address is a read of it as far as we can tell, whatever the callee then does with it.
                case AddressOf { Target: LocalVariable addressed }:
                    yield return addressed;
                    break;
                case AddressOf { Target: ArrayAccess addressedElement }:
                    foreach (var used in ArrayAccessLocals(addressedElement))
                        yield return used;
                    break;
                case ArrayAccess access:
                    foreach (var used in ArrayAccessLocals(access))
                        yield return used;
                    break;
                case ArrayLength { Array: { } lengthArray }:
                    yield return lengthArray;
                    break;
            }
        }
    }

    private static IEnumerable<LocalVariable> ArrayAccessLocals(ArrayAccess access)
    {
        yield return access.Array;

        if (access.Index is LocalVariable index)
            yield return index;
    }

    /// <summary>
    /// Opcodes with no side effects, so removing a never-read result is safe. Calls, stores,
    /// returns and branches are intentionally excluded.
    /// </summary>
    private static bool IsRemovable(OpCode opCode) =>
        opCode switch
        {
            OpCode.Move or OpCode.Phi
                or OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide
                or OpCode.ShiftLeft or OpCode.ShiftRight
                or OpCode.And or OpCode.Or or OpCode.Xor
                or OpCode.Not or OpCode.Negate
                or OpCode.ConvertFloatingPointPrecision or OpCode.ConvertFloatToSignedInteger
                or OpCode.ConvertSignedIntegerToFloat
                or OpCode.ReinterpretIntegerBitsAsFloat or OpCode.ReinterpretFloatBitsAsInteger
                or OpCode.RoundFloatTowardPositiveInfinity or OpCode.RoundFloatTowardNegativeInfinity => true,
            >= OpCode.CheckEqual and <= OpCode.CheckLessOrEqualUnsigned => true,
            _ => false
        };
}
