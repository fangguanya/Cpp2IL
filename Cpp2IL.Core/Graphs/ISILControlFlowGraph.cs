using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Graphs;

public class ISILControlFlowGraph
{
    public Block EntryBlock;
    public Block ExitBlock;
    public int Count => Blocks.Count;
    public List<Block> Blocks;

    public List<Instruction> Instructions
    {
        get
        {
            // BFS search
            var visited = new HashSet<Block>();
            var queue = new Queue<Block>();
            var result = new List<Instruction>();

            queue.Enqueue(EntryBlock);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();

                if (!visited.Add(current))
                    continue;

                result.AddRange(current.Instructions);

                foreach (var successor in current.Successors)
                {
                    if (!visited.Contains(successor))
                        queue.Enqueue(successor);
                }
            }

            return result; // Should this be cached?
        }
    }

    private int idCounter;

    public ISILControlFlowGraph(List<Instruction> instructions)
    {
        EntryBlock = new Block
        {
            ID = idCounter++,
            BlockType = BlockType.Entry
        };

        ExitBlock = new Block
        {
            ID = idCounter++,
            BlockType = BlockType.Exit
        };

        Blocks =
        [
            EntryBlock,
            ExitBlock
        ];

        Build(instructions);
    }

    private bool TryGetTargetJumpInstructionIndex(Instruction instruction, out int jumpInstructionIndex)
    {
        jumpInstructionIndex = 0;
        try
        {
            jumpInstructionIndex = ((Instruction)instruction.Operands[0]).Index;
            return true;
        }
        catch
        {
            // ignore
        }

        return false;
    }

    public void RemoveUnreachableBlocks()
    {
        if (Blocks.Count == 0)
            return;

        // Get blocks reachable from entry
        var reachable = new List<Block>();
        var visited = new List<Block> { EntryBlock };
        reachable.Add(EntryBlock);

        var total = 0;
        while (total < reachable.Count)
        {
            var block = reachable[total];
            total++;

            foreach (var successor in block.Successors)
            {
                if (visited.Contains(successor))
                    continue;
                visited.Add(successor);
                reachable.Add(successor);
            }
        }

        // Get unreachable blocks
        var unreachable = Blocks.Where(block => !visited.Remove(block)).ToList();

        // Remove those
        foreach (var block in unreachable)
        {
            // Don't remove entry or exit
            if (block == EntryBlock || block == ExitBlock)
                continue;

            // Fully detach the block so no remaining block keeps a dangling reference to it.
            // (A reachable block can have an unreachable predecessor; leaving that reference
            // behind makes later passes such as dominator computation throw.)
            foreach (var successor in block.Successors)
                RemovePredecessorAndPhiInputs(successor, block);
            foreach (var predecessor in block.Predecessors)
                predecessor.Successors.Remove(block);

            block.Successors.Clear();
            block.Predecessors.Clear();
            Blocks.Remove(block);
        }
    }

    /// <summary>
    /// 把已经确认的托管抛出块收束到唯一退出边，并同步清理原调用块遗留的后继与Phi输入。
    /// 原生调用在CFG建立后才可能解析为Throw，因此不能继续沿原Call的顺序后继执行。
    /// </summary>
    internal void TerminateAtThrow(Block block)
    {
        if (block == null)
            throw new ArgumentNullException(nameof(block));
        if (!Blocks.Contains(block))
            throw new ArgumentException("抛出块必须属于当前控制流图。", nameof(block));
        if (block.Instructions.LastOrDefault()?.OpCode != OpCode.Throw)
            throw new InvalidOperationException("终结块的最后一条指令必须是Throw。");

        foreach (var successor in block.Successors.ToList())
            RemovePredecessorAndPhiInputs(successor, block);
        block.Successors.Clear();

        block.Successors.Add(ExitBlock);
        if (!ExitBlock.Predecessors.Contains(block))
            ExitBlock.Predecessors.Add(block);
        block.CalculateBlockType();
    }

    /// <summary>
    /// 从块中删除指定前驱的全部边，并同步删除所有Phi的同索引输入。
    /// SSA中Phi第一个操作数是目标，后续操作数与Predecessors严格按索引对应。
    /// 该函数用于彻底断开不可达块或已改写终结块；同一前驱产生的平行边必须一起清理。
    /// </summary>
    internal static int RemovePredecessorAndPhiInputs(Block block, Block predecessor)
    {
        var removed = 0;
        for (var predecessorIndex = block.Predecessors.Count - 1;
             predecessorIndex >= 0;
             predecessorIndex--)
        {
            if (!ReferenceEquals(block.Predecessors[predecessorIndex], predecessor))
                continue;

            foreach (var phi in block.Instructions.Where(instruction => instruction.OpCode == OpCode.Phi))
            {
                var operandIndex = predecessorIndex + 1;
                if (operandIndex < phi.Operands.Count)
                    phi.RemoveOperandAt(operandIndex);
            }

            block.Predecessors.RemoveAt(predecessorIndex);
            removed++;
        }

        return removed;
    }

    public void RemoveNops()
    {
        var usedAsTarget = new HashSet<Instruction>();

        // Get all instructions used as branch targets
        foreach (var block in Blocks)
        {
            foreach (var instr in block.Instructions)
            {
                foreach (var operand in instr.Operands)
                {
                    if (operand is Instruction target)
                        usedAsTarget.Add(target);
                }
            }
        }

        // Build replacement map for NOPs that are safe to replace
        var instructionReplacement = new Dictionary<Instruction, Instruction>();
        foreach (var block in Blocks)
        {
            Instruction? replacement = null;
            for (var i = block.Instructions.Count - 1; i >= 0; i--)
            {
                var instr = block.Instructions[i];
                if (instr.OpCode == OpCode.Nop)
                {
                    if (replacement != null && !usedAsTarget.Contains(instr))
                        instructionReplacement[instr] = replacement;
                }
                else
                {
                    replacement = instr;
                }
            }
        }

        // Update operands
        foreach (var block in Blocks)
        {
            foreach (var instr in block.Instructions)
            {
                for (var i = 0; i < instr.Operands.Count; i++)
                {
                    if (instr.Operands[i] is Instruction target && instructionReplacement.TryGetValue(target, out var newTarget))
                        instr.SetOperand(i, newTarget);
                }
            }
        }

        // Remove NOPs
        foreach (var block in Blocks)
        {
            block.Instructions.RemoveAll(i => i.OpCode == OpCode.Nop && !usedAsTarget.Contains(i));
        }
    }

    public void RemoveEmptyBlocks()
    {
        var toRemove = new List<Block>();

        foreach (var block in Blocks)
        {
            if (block == EntryBlock || block == ExitBlock)
                continue;

            if (block.Instructions.Count == 0)
            {
                // jumps into the removed block must be retargeted, which needs an unambiguous successor
                if (block.Successors.Count != 1 && HasJumpOperandTo(block))
                    continue;

                var jumpTarget = block.Successors.Count == 1 ? block.Successors[0] : null;

                // Redirect predecessors to successors
                foreach (var pred in block.Predecessors)
                {
                    if (pred.Instructions.Count > 0
                        && pred.Instructions[^1] is { OpCode: OpCode.Jump or OpCode.ConditionalJump } jump
                        && ReferenceEquals(jump.Operands[0], block))
                        jump.SetOperand(0, jumpTarget!);

                    pred.Successors.Remove(block);
                    foreach (var succ in block.Successors)
                    {
                        if (!pred.Successors.Contains(succ))
                            pred.Successors.Add(succ);
                    }
                }

                // Redirect successors to predecessors
                foreach (var succ in block.Successors)
                {
                    succ.Predecessors.Remove(block);
                    foreach (var pred in block.Predecessors)
                    {
                        if (!succ.Predecessors.Contains(pred))
                            succ.Predecessors.Add(pred);
                    }
                }

                toRemove.Add(block);
            }
        }

        foreach (var block in toRemove)
            Blocks.Remove(block);
    }

    private bool HasJumpOperandTo(Block block) =>
        block.Predecessors.Any(pred => pred.Instructions.Count > 0
            && pred.Instructions[^1] is { OpCode: OpCode.Jump or OpCode.ConditionalJump } jump
            && ReferenceEquals(jump.Operands[0], block));

    public void BuildUseDefLists(HashSet<Instruction>? clobberingAddressTakes = null)
    {
        foreach (var block in Blocks)
        {
            var use = new List<IOperand>();
            var def = new List<IOperand>();
            var usedRegisterNumbers = new HashSet<int>();
            var definedRegisterNumbers = new HashSet<int>();
            var usedLocals = new HashSet<LocalVariable>();
            var definedLocals = new HashSet<LocalVariable>();

            foreach (var instruction in block.Instructions)
            {
                // 中文注释：块级 Use 只包含“在本块首次定义之前读取”的变量。旧实现把
                // 本块先写后读也计为入口活值，pruned SSA 因而会把已经被新写入截断的
                // 物理寄存器历史生命期接入伪 Phi。先展开复合操作数，再按数据流身份判断，
                // 同时覆盖内存基址、字段接收者、数组索引和 ARM64 W/X 同槽别名。
                foreach (var source in instruction.Sources.SelectMany(EnumerateDataFlowVariables))
                {
                    switch (source)
                    {
                        case Register register when !definedRegisterNumbers.Contains(register.Number)
                                                    && usedRegisterNumbers.Add(register.Number):
                            use.Add(register);
                            break;
                        case LocalVariable local when !definedLocals.Contains(local)
                                                      && usedLocals.Add(local):
                            use.Add(local);
                            break;
                    }
                }

                AddDefinition(instruction.Destination);

                if (instruction.ImplicitDefinition is { } clobbered)
                    AddDefinition(clobbered);

                if (clobberingAddressTakes?.Contains(instruction) == true)
                {
                    foreach (var operand in instruction.Operands)
                        if (operand is AddressOf { Target: { } addressed })
                            AddDefinition(addressed);
                }
            }

            block.Use = use;
            block.Def = def;

            void AddDefinition(IOperand? operand)
            {
                switch (operand)
                {
                    case Register register when definedRegisterNumbers.Add(register.Number):
                        def.Add(register);
                        break;
                    case LocalVariable local when definedLocals.Add(local):
                        def.Add(local);
                        break;
                }
            }
        }
    }

    /// <summary>
    /// 展开操作数中真正参与活跃性分析的变量；复合操作数本身不是一个可版本化定义。
    /// </summary>
    private static IEnumerable<IOperand> EnumerateDataFlowVariables(IOperand operand)
    {
        switch (operand)
        {
            case Register or LocalVariable:
                yield return operand;
                break;
            case MemoryOperand memory:
                if (memory.Base != null)
                    foreach (var variable in EnumerateDataFlowVariables(memory.Base))
                        yield return variable;
                if (memory.Index != null)
                    foreach (var variable in EnumerateDataFlowVariables(memory.Index))
                        yield return variable;
                break;
            case FieldReference { Field.IsStatic: false } field:
                yield return field.Local;
                break;
            case ArrayAccess access:
                yield return access.Array;
                foreach (var variable in EnumerateDataFlowVariables(access.Index))
                    yield return variable;
                break;
            case ArrayLength length:
                yield return length.Array;
                break;
            case StringLength length:
                yield return length.Value;
                break;
            case ListCount count:
                yield return count.Value;
                break;
            case AddressOf address:
                foreach (var variable in EnumerateDataFlowVariables(address.Target))
                    yield return variable;
                break;
            case HomogeneousFloatingAggregateArgument aggregate:
                foreach (var component in aggregate.Components)
                foreach (var variable in EnumerateDataFlowVariables(component))
                    yield return variable;
                break;
            case MetadataStringTableLookup lookup:
                foreach (var variable in EnumerateDataFlowVariables(lookup.Index))
                    yield return variable;
                break;
            case ReadOnlyUInt16TableLookup lookup:
                foreach (var variable in EnumerateDataFlowVariables(lookup.Index))
                    yield return variable;
                break;
        }
    }

    public void MergeCallBlocks()
    {
        var toRemove = new List<Block>();

        for (var i = 0; i < Blocks.Count; i++)
        {
            var block = Blocks[i];
            if (block.BlockType != BlockType.Call) continue;

            if (block.Successors.Count != 1)
                continue;

            var nextBlock = block.Successors[0];

            // make sure that the next block only has one predecessor (this)
            if (nextBlock.Predecessors.Count != 1 || nextBlock.Predecessors[0] != block)
                continue;

            // merge instructions
            block.Instructions.AddRange(nextBlock.Instructions);
            block.Successors = nextBlock.Successors;

            // fix up successors predecessors
            foreach (var successor in nextBlock.Successors)
            {
                for (var j = 0; j < successor.Predecessors.Count; j++)
                {
                    if (successor.Predecessors[j] == nextBlock)
                        successor.Predecessors[j] = block;
                }
            }

            toRemove.Add(nextBlock);
        }

        // Remove all merged blocks
        foreach (var removed in toRemove)
            Blocks.Remove(removed);

        foreach (var block in Blocks)
            block.CalculateBlockType();
    }

    private void Build(List<Instruction> instructions)
    {
        if (instructions == null)
            throw new ArgumentNullException(nameof(instructions));

        var currentBlock = new Block() { ID = idCounter++ };
        AddBlock(currentBlock);
        AddDirectedEdge(EntryBlock, currentBlock);

        for (var i = 0; i < instructions.Count; i++)
        {
            var isLast = i == instructions.Count - 1;
            Block newBlock;

            switch (instructions[i].OpCode)
            {
                case OpCode.Jump:
                case OpCode.ConditionalJump:
                case OpCode.IndirectJump:
                    currentBlock.AddInstruction(instructions[i]);

                    if (!isLast)
                    {
                        newBlock = new Block() { ID = idCounter++ };
                        AddBlock(newBlock);

                        if (instructions[i].OpCode is OpCode.Jump or OpCode.IndirectJump)
                        {
                            if (TryGetTargetJumpInstructionIndex(instructions[i], out int jumpTargetIndex))
                                currentBlock.Dirty = true;
                            else
                                AddDirectedEdge(currentBlock, ExitBlock);
                        }
                        else
                        {
                            AddDirectedEdge(currentBlock, newBlock);
                            currentBlock.Dirty = true;
                        }

                        currentBlock.CalculateBlockType();
                        currentBlock = newBlock;
                    }
                    else
                    {
                        AddDirectedEdge(currentBlock, ExitBlock);

                        if (instructions[i].OpCode == OpCode.Jump)
                            currentBlock.Dirty = true;
                    }

                    break;

                case OpCode.Call:
                case OpCode.CallVoid:
                case OpCode.Return:
                case OpCode.Throw:
                    var exitsMethod = instructions[i].OpCode is OpCode.Return or OpCode.Throw;

                    currentBlock.AddInstruction(instructions[i]);

                    if (!isLast)
                    {
                        newBlock = new Block() { ID = idCounter++ };
                        AddBlock(newBlock);
                        AddDirectedEdge(currentBlock, exitsMethod ? ExitBlock : newBlock);
                        currentBlock.CalculateBlockType();
                        currentBlock = newBlock;
                    }
                    else
                    {
                        AddDirectedEdge(currentBlock, ExitBlock);
                        currentBlock.CalculateBlockType();
                    }

                    break;

                default:
                    currentBlock.AddInstruction(instructions[i]);
                    if (isLast)
                    {
                        AddDirectedEdge(currentBlock, ExitBlock);
                        currentBlock.CalculateBlockType();
                    }
                    break;
            }
        }

        for (var index = 0; index < Blocks.Count; index++)
        {
            var node = Blocks[index];
            if (node.Dirty)
                FixBlock(node);
        }

        // Connect blocks without successors to exit
        foreach (var block in Blocks)
        {
            if (block.Successors.Count == 0 && block != EntryBlock && block != ExitBlock)
                AddDirectedEdge(block, ExitBlock);
        }

        // Change branch targets to blocks
        foreach (var instruction in Blocks.SelectMany(block => block.Instructions))
        {
            if (instruction.Operands.Count > 0 && instruction.Operands[0] is Instruction target)
                instruction.SetOperand(0, FindBlockByInstruction(target)!);
        }
    }

    private void FixBlock(Block block, bool removeJmp = false)
    {
        if (block.BlockType is BlockType.Fall)
            return;

        var jump = block.Instructions.Last();

        var targetInstruction = jump.Operands[0] as Instruction;

        var destination = FindBlockByInstruction(targetInstruction);

        if (destination == null)
        {
            //We assume that we're tail calling another method somewhere. Need to verify if this breaks anywhere but it shouldn't in general
            block.BlockType = BlockType.TailCall;
            return;
        }


        int index = destination.Instructions.FindIndex(instruction => instruction == targetInstruction);

        var targetNode = SplitAndCreate(destination, index);

        AddDirectedEdge(block, targetNode);
        block.Dirty = false;

        if (removeJmp)
            block.Instructions.Remove(jump);
    }

    public Block? FindBlockByInstruction(Instruction? instruction)
    {
        if (instruction == null)
            return null;

        for (var i = 0; i < Blocks.Count; i++)
        {
            var block = Blocks[i];
            for (var j = 0; j < block.Instructions.Count; j++)
            {
                var instr = block.Instructions[j];
                if (instr == instruction)
                {
                    return block;
                }
            }
        }

        return null;
    }

    private Block SplitAndCreate(Block target, int index)
    {
        if (index < 0 || index >= target.Instructions.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        // Don't need to split...
        if (index == 0)
            return target;

        var newBlock = new Block() { ID = idCounter++ };

        // target split in two
        // targetFirstPart -> targetSecondPart aka newNode

        // Take the instructions for the secondPart
        var instructions = target.Instructions.GetRange(index, target.Instructions.Count - index);
        target.Instructions.RemoveRange(index, target.Instructions.Count - index);

        // Add those to the newNode
        newBlock.Instructions.AddRange(instructions);
        // Transfer control flow
        newBlock.BlockType = target.BlockType;
        target.BlockType = BlockType.Fall;

        // Transfer successors
        newBlock.Successors = target.Successors;
        if (target.Dirty)
            newBlock.Dirty = true;
        target.Dirty = false;
        target.Successors = [];

        // Correct the predecessors for all the successors
        foreach (var successor in newBlock.Successors)
        {
            for (int i = 0; i < successor.Predecessors.Count; i++)
            {
                if (successor.Predecessors[i].ID == target.ID)
                    successor.Predecessors[i] = newBlock;
            }
        }

        // Add newNode and connect it
        AddBlock(newBlock);
        AddDirectedEdge(target, newBlock);

        return newBlock;
    }

    private void AddDirectedEdge(Block from, Block to)
    {
        from.Successors.Add(to);
        to.Predecessors.Add(from);
    }

    protected void AddBlock(Block block) => Blocks.Add(block);
}
