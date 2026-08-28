using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests.Analysis;

/// <summary>
/// Correctness tests for the dominator computation and SSA construction, exercising the two
/// shapes that the previous implementation got wrong: a diamond (single join) and a loop
/// (header reached by a back-edge).
/// </summary>
public class SsaAndDominators
{
    private static ISILControlFlowGraph BuildGraph(IReadOnlyList<Instruction> instructions)
    {
        // Resolve numeric jump targets to instruction references, as the real lifter does.
        foreach (var instruction in instructions)
        {
            if (instruction.OpCode is OpCode.Jump or OpCode.ConditionalJump)
                instruction.SetOperand(0, instructions[(int)((Immediate)instruction.Operands[0]).Value]);
        }

        return new ISILControlFlowGraph(instructions.ToList());
    }

    private static List<Instruction> Diamond()
    {
        var instructions = new List<Instruction>();
        void Add(int index, OpCode opCode, params object[] operands) => instructions.Add(new Instruction(index, opCode, Ops(operands)));

        Add(0, OpCode.Move, new Register(null, "x"), 0);                              // entry def of x
        Add(1, OpCode.ConditionalJump, 4, new Register(null, "cond"));               // branch
        Add(2, OpCode.Move, new Register(null, "x"), 1);                              // then: x = 1
        Add(3, OpCode.Jump, 5);
        Add(4, OpCode.Move, new Register(null, "x"), 2);                              // else: x = 2
        Add(5, OpCode.Move, new Register(null, "ret"), new Register(null, "x"));     // join: uses x
        Add(6, OpCode.Return, new Register(null, "ret"));

        return instructions;
    }

    private static List<Instruction> Loop()
    {
        var instructions = new List<Instruction>();
        void Add(int index, OpCode opCode, params object[] operands) => instructions.Add(new Instruction(index, opCode, Ops(operands)));

        Add(0, OpCode.Move, new Register(null, "i"), 0);                             // pre-header: i = 0
        Add(1, OpCode.CheckLess, new Register(null, "cmp"), new Register(null, "i"), 10); // header: cmp = i < 10
        Add(2, OpCode.Not, new Register(null, "cmp"), new Register(null, "cmp"));
        Add(3, OpCode.ConditionalJump, 7, new Register(null, "cmp"));               // exit loop
        Add(4, OpCode.Add, new Register(null, "i"), new Register(null, "i"), 1);    // body: i = i + 1
        Add(5, OpCode.Call, 0xDEADBEEF);
        Add(6, OpCode.Jump, 1);                                                      // back-edge to header
        Add(7, OpCode.Return);

        return instructions;
    }

    private static Block BlockWith(ISILControlFlowGraph graph, Func<Instruction, bool> predicate)
        => graph.Blocks.First(b => b.Instructions.Any(predicate));

    private static List<Instruction> Phis(ISILControlFlowGraph graph)
        => graph.Blocks.SelectMany(b => b.Instructions).Where(i => i.OpCode == OpCode.Phi).ToList();

    private static string? RegName(object operand) => operand is Register r ? r.Name : null;

    /// <summary>
    /// 把测试图中的SSA寄存器转换为局部变量，完整复现正式流水线在
    /// <see cref="SsaForm.Build(ISILControlFlowGraph, DominatorInfo)"/> 之后的表示。
    /// </summary>
    private static void ConvertRegistersToLocals(ISILControlFlowGraph graph)
    {
        var registers = graph.Blocks
            .SelectMany(block => block.Instructions)
            .SelectMany(instruction => instruction.Operands)
            .SelectMany(RegistersIn)
            .Distinct()
            .ToDictionary(register => register, register => new LocalVariable(register.ToString(), register));

        foreach (var instruction in graph.Blocks.SelectMany(block => block.Instructions))
        {
            for (var index = 0; index < instruction.Operands.Count; index++)
            {
                switch (instruction.Operands[index])
                {
                    case Register register:
                        instruction.SetOperand(index, registers[register]);
                        break;
                    case MemoryOperand memory:
                        if (memory.Base is Register memoryBase)
                            memory.Base = registers[memoryBase];
                        if (memory.Index is Register memoryIndex)
                            memory.Index = registers[memoryIndex];
                        instruction.SetOperand(index, memory);
                        break;
                }
            }
        }
    }

    private static IEnumerable<Register> RegistersIn(IOperand operand)
    {
        if (operand is Register register)
            yield return register;
        if (operand is MemoryOperand { Base: Register memoryBase })
            yield return memoryBase;
        if (operand is MemoryOperand { Index: Register memoryIndex })
            yield return memoryIndex;
    }

    private static string DescribeGraph(ISILControlFlowGraph graph, string stage) =>
        $"===== {stage} ====={Environment.NewLine}"
        + string.Join(Environment.NewLine, graph.Blocks.Select(block =>
            $"块{block.ID} 前驱[{string.Join(',', block.Predecessors.Select(item => item.ID))}] "
            + $"后继[{string.Join(',', block.Successors.Select(item => item.ID))}] "
            + $"指令[{string.Join(" | ", block.Instructions)}]"));

    [Test]
    public void DiamondDominatorsAreCorrect()
    {
        var graph = BuildGraph(Diamond());
        var dom = new DominatorInfo(graph);

        var branch = BlockWith(graph, i => i.OpCode == OpCode.ConditionalJump);
        var join = BlockWith(graph, i => i.OpCode == OpCode.Return);

        // The join is reached from both arms of the diamond...
        Assert.That(join.Predecessors.Count, Is.EqualTo(2));

        // ...so its immediate dominator is the branch block, not either arm.
        Assert.That(dom.ImmediateDominators[join], Is.EqualTo(branch));
        Assert.That(dom.Dominates(branch, join), Is.True);

        // Each arm has the join on its dominance frontier; the branch block does not.
        foreach (var arm in join.Predecessors)
        {
            Assert.That(arm, Is.Not.EqualTo(branch));
            Assert.That(dom.DominanceFrontier[arm], Does.Contain(join));
            Assert.That(dom.Dominates(arm, join), Is.False);
        }

        Assert.That(dom.DominanceFrontier[branch], Does.Not.Contain(join));
    }

    [Test]
    public void DiamondInsertsExactlyOnePhiAtTheJoin()
    {
        var graph = BuildGraph(Diamond());
        SsaForm.Build(graph, new DominatorInfo(graph));

        var join = BlockWith(graph, i => i.OpCode == OpCode.Return);
        var phis = Phis(graph);

        // x is defined on both arms and read at the join => exactly one phi, for x, at the join.
        Assert.That(phis.Count, Is.EqualTo(1));
        var phi = phis[0];
        Assert.That(join.Instructions, Does.Contain(phi));
        Assert.That(RegName(phi.Operands[0]), Is.EqualTo("x"));

        // One destination + one source per predecessor, positionally aligned.
        Assert.That(phi.Operands.Count, Is.EqualTo(1 + join.Predecessors.Count));

        // Every source is a version of x, and they are distinct (one per arm).
        var sources = phi.Operands.Skip(1).Cast<Register>().ToList();
        Assert.That(sources.All(s => s.Name == "x"), Is.True);
        Assert.That(sources.Select(s => s.Version).Distinct().Count(), Is.EqualTo(sources.Count));

        // The value read at the join is the phi's freshly-versioned result.
        var phiResult = (Register)phi.Operands[0];
        var useOfX = join.Instructions.First(i => i.OpCode == OpCode.Move && RegName(i.Operands[1]) == "x");
        Assert.That(((Register)useOfX.Operands[1]).Version, Is.EqualTo(phiResult.Version));
    }

    [Test]
    public void LoopInsertsHeaderPhiForCarriedVariable()
    {
        var graph = BuildGraph(Loop());
        SsaForm.Build(graph, new DominatorInfo(graph));

        // The header is the loop-carried join: it reads i, is reached by a back-edge, and so
        // has two predecessors (pre-header + latch).
        var header = BlockWith(graph, i => i.OpCode == OpCode.CheckLess);
        Assert.That(header.Predecessors.Count, Is.EqualTo(2));

        var iPhis = Phis(graph).Where(p => RegName(p.Operands[0]) == "i").ToList();
        Assert.That(iPhis.Count, Is.EqualTo(1));

        var phi = iPhis[0];
        Assert.That(header.Instructions, Does.Contain(phi));
        Assert.That(phi.Operands.Count, Is.EqualTo(1 + header.Predecessors.Count));
    }

    [Test]
    [Category("基本功能")]
    public void 汇合后不再读取的寄存器不插入死Phi()
    {
        var instructions = new List<Instruction>();
        void Add(int index, OpCode opCode, params object[] operands)
            => instructions.Add(new Instruction(index, opCode, Ops(operands)));

        Add(0, OpCode.ConditionalJump, 3, new Register(null, "cond"));
        Add(1, OpCode.Move, new Register(null, "x"), 1);
        Add(2, OpCode.Jump, 4);
        Add(3, OpCode.Move, new Register(null, "x"), 2);
        Add(4, OpCode.Return);
        var graph = BuildGraph(instructions);

        SsaForm.Build(graph, new DominatorInfo(graph));

        Assert.That(Phis(graph).Any(phi => RegName(phi.Operands[0]) == "x"), Is.False);
    }

    [Test]
    [Category("边界值")]
    public void 汇合入口读取寄存器时仍保留必要Phi()
    {
        var graph = BuildGraph(Diamond());

        SsaForm.Build(graph, new DominatorInfo(graph));

        Assert.That(Phis(graph).Count(phi => RegName(phi.Operands[0]) == "x"), Is.EqualTo(1));
    }

    /// <summary>
    /// 构造两个旧 X0 定义汇合后，由新写入选择先读取旧值还是先截断旧值的控制流。
    /// 显式 Number=0 用于同时验证 ARM64 W0/X0 的同槽覆盖语义。
    /// </summary>
    private static (ISILControlFlowGraph Graph, Instruction Kill, Instruction ReadAfterKill)
        RegisterReuseJoin(bool readBeforeKill, bool useNarrowAlias)
    {
        var instructions = new List<Instruction>();
        void Add(int index, OpCode opCode, params object[] operands)
            => instructions.Add(new Instruction(index, opCode, Ops(operands)));

        Register X0() => new(0, "X0");
        Register KillRegister() => useNarrowAlias ? new Register(0, "W0") : X0();

        Add(0, OpCode.ConditionalJump, 3, new Register(null, "condition"));
        Add(1, OpCode.Move, X0(), 1);
        Add(2, OpCode.Jump, 4);
        Add(3, OpCode.Move, X0(), 2);
        if (readBeforeKill)
            Add(4, OpCode.Move, new Register(null, "oldValue"), X0());
        var killIndex = instructions.Count;
        Add(killIndex, OpCode.Move, KillRegister(), new Register(null, "longLivedValue"));
        var readAfterKillIndex = instructions.Count;
        Add(readAfterKillIndex, OpCode.Move, new Register(null, "newValue"), X0());
        Add(instructions.Count, OpCode.Return);

        return (
            BuildGraph(instructions),
            instructions[killIndex],
            instructions[readAfterKillIndex]);
    }

    [Test]
    [Category("基本功能")]
    public void 汇合块先写后读必须截断旧寄存器生命期()
    {
        var fixture = RegisterReuseJoin(readBeforeKill: false, useNarrowAlias: false);

        SsaForm.Build(fixture.Graph, new DominatorInfo(fixture.Graph));

        var killDefinition = (Register)fixture.Kill.Operands[0];
        var readAfterKill = (Register)fixture.ReadAfterKill.Operands[1];
        Assert.Multiple(() =>
        {
            Assert.That(Phis(fixture.Graph).Any(phi => RegName(phi.Operands[0]) == "X0"), Is.False);
            Assert.That(readAfterKill.Version, Is.EqualTo(killDefinition.Version));
        });
    }

    [Test]
    [Category("边界值")]
    public void 汇合块覆盖前读取仍必须建立真实Phi()
    {
        var fixture = RegisterReuseJoin(readBeforeKill: true, useNarrowAlias: false);

        SsaForm.Build(fixture.Graph, new DominatorInfo(fixture.Graph));

        var phi = Phis(fixture.Graph).Single(instruction => RegName(instruction.Operands[0]) == "X0");
        var killDefinition = (Register)fixture.Kill.Operands[0];
        var readAfterKill = (Register)fixture.ReadAfterKill.Operands[1];
        Assert.Multiple(() =>
        {
            Assert.That(phi.Operands.Count, Is.EqualTo(3));
            Assert.That(readAfterKill.Version, Is.EqualTo(killDefinition.Version));
            Assert.That(readAfterKill.Version, Is.Not.EqualTo(((Register)phi.Operands[0]).Version));
        });
    }

    [Test]
    [Category("异常输入")]
    public void W0写入必须杀死同编号X0旧值而不建立伪Phi()
    {
        var fixture = RegisterReuseJoin(readBeforeKill: false, useNarrowAlias: true);

        SsaForm.Build(fixture.Graph, new DominatorInfo(fixture.Graph));

        var killDefinition = (Register)fixture.Kill.Operands[0];
        var readAfterKill = (Register)fixture.ReadAfterKill.Operands[1];
        Assert.Multiple(() =>
        {
            Assert.That(killDefinition.Name, Is.EqualTo("W0"));
            Assert.That(readAfterKill.Name, Is.EqualTo("W0"));
            Assert.That(readAfterKill.Version, Is.EqualTo(killDefinition.Version));
            Assert.That(Phis(fixture.Graph).Any(phi => ((Register)phi.Operands[0]).Number == 0), Is.False);
        });
    }

    /// <summary>
    /// 构造两条地址定义路径汇入同一内存写入的控制流。
    /// </summary>
    private static ISILControlFlowGraph MemoryStoreDiamond(bool includeIndex, bool constantAddress)
    {
        var instructions = new List<Instruction>();
        void Add(int index, OpCode opCode, params object[] operands)
            => instructions.Add(new Instruction(index, opCode, Ops(operands)));

        Add(0, OpCode.ConditionalJump, 4, new Register(null, "cond"));
        Add(1, OpCode.Move, new Register(null, "X12"), 0x1000);
        Add(2, OpCode.Move, new Register(null, "X13"), 1);
        Add(3, OpCode.Jump, 6);
        Add(4, OpCode.Move, new Register(null, "X12"), 0x2000);
        Add(5, OpCode.Move, new Register(null, "X13"), 2);
        var address = constantAddress
            ? new MemoryOperand(addend: 0x3000)
            : new MemoryOperand(
                new Register(null, "X12"),
                includeIndex ? new Register(null, "X13") : null,
                0x20,
                includeIndex ? 4 : 0);
        Add(6, OpCode.Move, address, new Register(null, "value"));
        Add(7, OpCode.Return);
        return BuildGraph(instructions);
    }

    [Test]
    [Category("基本功能")]
    public void 汇合内存写入的基址读取必须建立Phi()
    {
        var graph = MemoryStoreDiamond(includeIndex: false, constantAddress: false);

        SsaForm.Build(graph, new DominatorInfo(graph));

        Assert.That(Phis(graph).Count(phi => RegName(phi.Operands[0]) == "X12"), Is.EqualTo(1));
    }

    [Test]
    [Category("边界值")]
    public void 汇合内存写入的基址与索引必须分别建立Phi()
    {
        var graph = MemoryStoreDiamond(includeIndex: true, constantAddress: false);

        SsaForm.Build(graph, new DominatorInfo(graph));

        Assert.Multiple(() =>
        {
            Assert.That(Phis(graph).Count(phi => RegName(phi.Operands[0]) == "X12"), Is.EqualTo(1));
            Assert.That(Phis(graph).Count(phi => RegName(phi.Operands[0]) == "X13"), Is.EqualTo(1));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 常量地址写入不得为无关地址寄存器建立Phi()
    {
        var graph = MemoryStoreDiamond(includeIndex: true, constantAddress: true);

        SsaForm.Build(graph, new DominatorInfo(graph));

        Assert.Multiple(() =>
        {
            Assert.That(Phis(graph).Any(phi => RegName(phi.Operands[0]) == "X12"), Is.False);
            Assert.That(Phis(graph).Any(phi => RegName(phi.Operands[0]) == "X13"), Is.False);
        });
    }

    /// <summary>
    /// 模拟大型字符串分派：条件链的每个命中块都写入同一地址寄存器，最后在公共块解引用。
    /// </summary>
    private static ISILControlFlowGraph WideMemoryLoadJoin(int branchCount)
    {
        var instructions = new List<Instruction>();
        var successStart = branchCount + 1;
        var joinIndex = successStart + branchCount * 2;

        for (var index = 0; index < branchCount; index++)
        {
            var successIndex = successStart + index * 2;
            instructions.Add(new Instruction(
                index,
                OpCode.ConditionalJump,
                new Immediate(successIndex),
                new Register(null, $"condition{index}")));
        }

        // 所有条件均未命中时直接返回，不得为成功路径伪造返回地址。
        instructions.Add(new Instruction(branchCount, OpCode.Return));
        for (var index = 0; index < branchCount; index++)
        {
            var successIndex = successStart + index * 2;
            instructions.Add(new Instruction(
                successIndex,
                OpCode.Move,
                new Register(null, "X8"),
                new Immediate(0x1000 + index * 8)));
            instructions.Add(new Instruction(successIndex + 1, OpCode.Jump, new Immediate(joinIndex)));
        }

        instructions.Add(new Instruction(
            joinIndex,
            OpCode.Move,
            new Register(null, "X0"),
            new MemoryOperand(new Register(null, "X8"))));
        instructions.Add(new Instruction(joinIndex + 1, OpCode.Return, new Register(null, "X0")));
        return BuildGraph(instructions);
    }

    [Test]
    [Category("边界值")]
    public void 四百条地址定义汇入公共内存读取时必须建立完整Phi()
    {
        const int branchCount = 400;
        var graph = WideMemoryLoadJoin(branchCount);

        SsaForm.Build(graph, new DominatorInfo(graph));

        var join = BlockWith(graph, instruction =>
            instruction is { OpCode: OpCode.Move, Operands: [_, MemoryOperand] });
        var phi = join.Instructions.Single(instruction =>
            instruction.OpCode == OpCode.Phi && RegName(instruction.Operands[0]) == "X8");
        Assert.Multiple(() =>
        {
            Assert.That(join.Predecessors.Count, Is.EqualTo(branchCount));
            Assert.That(phi.Operands.Count, Is.EqualTo(branchCount + 1));
            Assert.That(phi.Operands.Skip(1).Cast<Register>().Select(register => register.Version).Distinct().Count(),
                Is.EqualTo(branchCount));
        });
    }

    private static ISILControlFlowGraph ConditionalSelectGraph(bool includeSecondValue)
    {
        var instructions = new List<Instruction>();
        void Add(int index, OpCode opCode, params object[] operands)
            => instructions.Add(new Instruction(index, opCode, Ops(operands)));

        Add(0, OpCode.Move, new Register(null, "x"), 10);
        if (includeSecondValue)
            Add(1, OpCode.Move, new Register(null, "y"), 30);
        var branchIndex = instructions.Count;
        var falseMoveIndex = branchIndex + 1;
        var returnIndex = includeSecondValue ? falseMoveIndex + 2 : falseMoveIndex + 1;
        Add(branchIndex, OpCode.ConditionalJump, returnIndex, new Register(null, "cond"));
        Add(falseMoveIndex, OpCode.Move, new Register(null, "x"), 20);
        if (includeSecondValue)
            Add(falseMoveIndex + 1, OpCode.Move, new Register(null, "y"), 40);
        if (includeSecondValue)
            Add(returnIndex, OpCode.Return, new Register(null, "x"), new Register(null, "y"));
        else
            Add(returnIndex, OpCode.Return, new Register(null, "x"));
        return BuildGraph(instructions);
    }

    private static ISILControlFlowGraph ConsecutiveConditionalSelectGraph()
    {
        var instructions = new List<Instruction>();
        void Add(int index, OpCode opCode, params object[] operands)
            => instructions.Add(new Instruction(index, opCode, Ops(operands)));

        // 精确模拟两条连续 ARM64 CSEL：真分支跳过同地址的假值写入，随后两路汇合。
        Add(0, OpCode.Move, new Register(null, "X8"), 10);
        Add(1, OpCode.Move, new Register(null, "X9"), 20);
        Add(2, OpCode.Move, new Register(null, "CSEL_TRUE"), new Register(null, "X8"));
        Add(3, OpCode.Move, new Register(null, "CSEL_FALSE"), new Register(null, "X9"));
        Add(4, OpCode.Move, new Register(null, "X8"), new Register(null, "CSEL_TRUE"));
        Add(5, OpCode.ConditionalJump, 7, new Register(null, "cond1"));
        Add(6, OpCode.Move, new Register(null, "X8"), new Register(null, "CSEL_FALSE"));

        Add(7, OpCode.Move, new Register(null, "X9"), 30);
        Add(8, OpCode.Move, new Register(null, "CSEL_TRUE"), new Register(null, "X9"));
        Add(9, OpCode.Move, new Register(null, "CSEL_FALSE"), new Register(null, "X8"));
        Add(10, OpCode.Move, new Register(null, "X8"), new Register(null, "CSEL_TRUE"));
        Add(11, OpCode.ConditionalJump, 13, new Register(null, "cond2"));
        Add(12, OpCode.Move, new Register(null, "X8"), new Register(null, "CSEL_FALSE"));
        Add(13, OpCode.Move, new Register(null, "X0"), new MemoryOperand(new Register(null, "X8")));
        Add(14, OpCode.Return, new Register(null, "X0"));
        return BuildGraph(instructions);
    }

    [Test]
    [Category("基本功能")]
    public void 连续条件选择经过SSA清理后必须保留两条分支()
    {
        var graph = ConsecutiveConditionalSelectGraph();
        SsaForm.Build(graph, new DominatorInfo(graph));
        ConvertRegistersToLocals(graph);
        var stageSsa = DescribeGraph(graph, "SSA建立后");

        SsaSimplifier.Run(graph, []);
        DeadCodeEliminator.Run(graph);
        var stageDce = DescribeGraph(graph, "SSA简化与死码清理后");
        SsaForm.Remove(graph);
        var stageRemove = DescribeGraph(graph, "退SSA后");
        CopyCoalescer.Run(graph);
        var stageCoalesce = DescribeGraph(graph, "副本合并后");

        var branches = graph.Blocks
            .Where(block => block.Instructions.LastOrDefault()?.OpCode == OpCode.ConditionalJump)
            .ToList();
        var graphDetail = string.Join(Environment.NewLine, stageSsa, stageDce, stageRemove, stageCoalesce);
        Assert.Multiple(() =>
        {
            Assert.That(branches.Count, Is.EqualTo(2), graphDetail);
            Assert.That(branches.All(block => block.Successors.Count == 2), Is.True, graphDetail);
        });
    }

    [Test]
    [Category("边界值")]
    public void 连续条件选择返回内存读取必须继续使用目标寄存器族()
    {
        var graph = ConsecutiveConditionalSelectGraph();
        SsaForm.Build(graph, new DominatorInfo(graph));
        ConvertRegistersToLocals(graph);
        SsaSimplifier.Run(graph, []);
        DeadCodeEliminator.Run(graph);
        SsaForm.Remove(graph);
        CopyCoalescer.Run(graph);

        var load = graph.Blocks.SelectMany(block => block.Instructions)
            .Single(instruction => instruction is { OpCode: OpCode.Move, Operands: [_, MemoryOperand] });
        var memoryBase = (LocalVariable)((MemoryOperand)load.Operands[1]).Base!;
        Assert.That(memoryBase.Register.Name, Is.EqualTo("X8"));
    }

    [Test]
    [Category("异常输入")]
    public void 条件选择的目标寄存器活值缺失时不得伪造其他寄存器来源()
    {
        var graph = ConsecutiveConditionalSelectGraph();
        SsaForm.Build(graph, new DominatorInfo(graph));
        ConvertRegistersToLocals(graph);
        SsaSimplifier.Run(graph, []);
        DeadCodeEliminator.Run(graph);

        var phis = graph.Blocks.SelectMany(block => block.Instructions)
            .Where(instruction => instruction.OpCode == OpCode.Phi)
            .ToList();
        var x8Phis = phis.Where(phi => ((LocalVariable)phi.Operands[0]).Register.Name == "X8").ToList();
        Assert.Multiple(() =>
        {
            Assert.That(x8Phis, Is.Not.Empty);
            Assert.That(x8Phis, Has.All.Matches<Instruction>(phi => phi.Operands.Skip(1)
                .OfType<LocalVariable>()
                .All(source => source.Register.Name == "X8")));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 退SSA必须拆分条件真分支的Phi关键边()
    {
        var graph = ConditionalSelectGraph(includeSecondValue: false);
        SsaForm.Build(graph, new DominatorInfo(graph));

        SsaForm.Remove(graph);

        var branch = BlockWith(graph, instruction => instruction.OpCode == OpCode.ConditionalJump);
        var edge = branch.Successors.Single(successor =>
            successor.Instructions.Any(instruction => instruction.OpCode == OpCode.Move)
            && successor.Instructions.Last().OpCode == OpCode.Jump);
        Assert.Multiple(() =>
        {
            Assert.That(branch.Instructions[^1].Operands[0], Is.SameAs(edge));
            Assert.That(edge.Predecessors, Is.EqualTo(new[] { branch }));
            Assert.That(edge.Instructions.Count(instruction => instruction.OpCode == OpCode.Move), Is.EqualTo(1));
        });
    }

    [Test]
    [Category("边界值")]
    public void 同一关键边的多个Phi必须共享一个边块()
    {
        var graph = ConditionalSelectGraph(includeSecondValue: true);
        SsaForm.Build(graph, new DominatorInfo(graph));

        SsaForm.Remove(graph);

        var branch = BlockWith(graph, instruction => instruction.OpCode == OpCode.ConditionalJump);
        var edgeBlocks = branch.Successors.Where(successor => successor.Instructions.Last().OpCode == OpCode.Jump).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(edgeBlocks.Count, Is.EqualTo(1));
            Assert.That(edgeBlocks[0].Instructions.Count(instruction => instruction.OpCode == OpCode.Move), Is.EqualTo(2));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 没有活跃Phi的条件分支不得创建边块()
    {
        var instructions = new List<Instruction>();
        void Add(int index, OpCode opCode, params object[] operands)
            => instructions.Add(new Instruction(index, opCode, Ops(operands)));
        Add(0, OpCode.ConditionalJump, 2, new Register(null, "cond"));
        Add(1, OpCode.Nop);
        Add(2, OpCode.Return);
        var graph = BuildGraph(instructions);
        SsaForm.Build(graph, new DominatorInfo(graph));
        var blockCount = graph.Blocks.Count;

        SsaForm.Remove(graph);

        Assert.That(graph.Blocks.Count, Is.LessThanOrEqualTo(blockCount));
    }

    // Two sibling leaves under the entry branch, one redefining x and one reading it. Both orders tested
    // since the rename walk can visit them either way round.
    private static List<Instruction> SiblingLeaves(bool defIsFallthrough)
    {
        var instructions = new List<Instruction>();
        void Add(int index, OpCode opCode, params object[] operands) => instructions.Add(new Instruction(index, opCode, Ops(operands)));

        Add(0, OpCode.Move, new Register(null, "x"), 0);                              // entry def of x
        Add(1, OpCode.ConditionalJump, 4, new Register(null, "cond"));
        if (defIsFallthrough)
        {
            Add(2, OpCode.Move, new Register(null, "x"), 1);                          // one arm: x = 1
            Add(3, OpCode.Jump, 6);
            Add(4, OpCode.Move, new Register(null, "ret"), new Register(null, "x")); // other arm: reads x
        }
        else
        {
            Add(2, OpCode.Move, new Register(null, "ret"), new Register(null, "x"));
            Add(3, OpCode.Jump, 6);
            Add(4, OpCode.Move, new Register(null, "x"), 1);
        }
        Add(5, OpCode.Jump, 6);
        Add(6, OpCode.Return);

        return instructions;
    }

    [TestCase(true)]
    [TestCase(false)]
    public void SiblingLeafDefinitionDoesNotLeakIntoOtherArm(bool defIsFallthrough)
    {
        var graph = BuildGraph(SiblingLeaves(defIsFallthrough));
        SsaForm.Build(graph, new DominatorInfo(graph));

        var entryDef = graph.Blocks.SelectMany(b => b.Instructions)
            .First(i => i.OpCode == OpCode.Move && RegName(i.Operands[0]) == "x" && i.Operands[1] is Immediate { Value: 0 });
        var use = graph.Blocks.SelectMany(b => b.Instructions)
            .First(i => i.OpCode == OpCode.Move && RegName(i.Operands[0]) == "ret");

        // the read arm is only reachable via the entry, so it can't see the sibling's version
        Assert.That(((Register)use.Operands[1]).Version, Is.EqualTo(((Register)entryDef.Operands[0]).Version));
    }

    [Test]
    [Category("基本功能")]
    public void 装箱数据地址被识别为只读输入()
    {
        const ulong boxTarget = 0x2162514;
        var value = new Register(null, "stack_-24");
        var call = new Instruction(
            0,
            OpCode.Call,
            new Immediate((long)boxTarget),
            new Register(null, "X0"),
            new Register(null, "X1"),
            new AddressOf(value));

        Assert.That(
            SsaForm.IsReadOnlyBoxDataAddress(call, 3, new HashSet<ulong> { boxTarget }),
            Is.True);
    }

    [Test]
    [Category("边界值")]
    public void 装箱类句柄位置不是数据地址()
    {
        const ulong boxTarget = 0x2162514;
        var call = new Instruction(
            0,
            OpCode.Call,
            new Immediate((long)boxTarget),
            new Register(null, "X0"),
            new AddressOf(new Register(null, "X1")),
            new Register(null, "X2"));

        Assert.That(
            SsaForm.IsReadOnlyBoxDataAddress(call, 2, new HashSet<ulong> { boxTarget }),
            Is.False);
    }

    [Test]
    [Category("异常输入")]
    public void 未知原生调用的取址参数仍按可能写回处理()
    {
        const ulong boxTarget = 0x2162514;
        var call = new Instruction(
            0,
            OpCode.Call,
            new Immediate(0x123456),
            new Register(null, "X0"),
            new Register(null, "X1"),
            new AddressOf(new Register(null, "stack_-24")));

        Assert.That(
            SsaForm.IsReadOnlyBoxDataAddress(call, 3, new HashSet<ulong> { boxTarget }),
            Is.False);
    }

    [Test]
    [Category("基本功能")]
    public void 纯地址计算沿用调用前栈槽版本()
    {
        var stack = new Register(null, "stack_-24");
        var pointer = new Register(null, "X1");
        var result = new Register(null, "X0");
        var instructions = new List<Instruction>
        {
            new(0, OpCode.Move, stack, new Immediate(1)),
            new(1, OpCode.Move, pointer, new AddressOf(stack)),
            new(2, OpCode.Move, result, stack),
            new(3, OpCode.Return, result),
        };
        var graph = BuildGraph(instructions);

        SsaForm.Build(graph, new DominatorInfo(graph));

        var stackDefinition = (Register)instructions[0].Operands[0];
        var addressed = (Register)((AddressOf)instructions[1].Operands[1]).Target;
        var laterRead = (Register)instructions[2].Operands[1];
        Assert.Multiple(() =>
        {
            Assert.That(addressed.Version, Is.EqualTo(stackDefinition.Version));
            Assert.That(laterRead.Version, Is.EqualTo(stackDefinition.Version));
        });
    }

    [Test]
    [Category("边界值")]
    public void 可能写回的调用地址生成新栈槽版本()
    {
        var stack = new Register(null, "stack_-24");
        var result = new Register(null, "X0");
        var instructions = new List<Instruction>
        {
            new(0, OpCode.Move, stack, new Immediate(1)),
            new(1, OpCode.CallVoid, new StringLiteral("mutate"), new AddressOf(stack)),
            new(2, OpCode.Move, result, stack),
            new(3, OpCode.Return, result),
        };
        var graph = BuildGraph(instructions);

        SsaForm.Build(graph, new DominatorInfo(graph));

        var stackDefinition = (Register)instructions[0].Operands[0];
        var addressed = (Register)((AddressOf)instructions[1].Operands[1]).Target;
        var laterRead = (Register)instructions[2].Operands[1];
        Assert.Multiple(() =>
        {
            Assert.That(addressed.Version, Is.Not.EqualTo(stackDefinition.Version));
            Assert.That(laterRead.Version, Is.EqualTo(addressed.Version));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 紧邻调用的地址载体折回并生成Ref写回版本()
    {
        var stack = new Register(null, "stack_-28");
        var pointer = new Register(null, "X0");
        var result = new Register(null, "X0");
        var use = new Register(null, "X19");
        var instructions = new List<Instruction>
        {
            new(0, OpCode.Move, pointer, new AddressOf(stack)),
            new(1, OpCode.Move, stack, new Immediate(0)),
            new(2, OpCode.Call, new Immediate(0x123456), result, pointer),
            new(3, OpCode.Move, use, stack),
            new(4, OpCode.Return, use),
        };
        var graph = BuildGraph(instructions);

        SsaForm.Build(graph, new DominatorInfo(graph));

        var initialized = (Register)instructions[1].Operands[0];
        var addressed = (Register)((AddressOf)instructions[2].Operands[2]).Target;
        var readAfterCall = (Register)instructions[3].Operands[1];
        Assert.Multiple(() =>
        {
            Assert.That(addressed.Version, Is.Not.EqualTo(initialized.Version));
            Assert.That(readAfterCall.Version, Is.EqualTo(addressed.Version));
        });
    }

    [Test]
    [Category("边界值")]
    public void 地址载体在调用前重定义时保持寄存器参数()
    {
        var stack = new Register(null, "stack_-28");
        var pointer = new Register(null, "X0");
        var instructions = new List<Instruction>
        {
            new(0, OpCode.Move, pointer, new AddressOf(stack)),
            new(1, OpCode.Move, pointer, new Immediate(7)),
            new(2, OpCode.CallVoid, new StringLiteral("consume"), pointer),
            new(3, OpCode.Return),
        };
        var graph = BuildGraph(instructions);

        SsaForm.Build(graph, new DominatorInfo(graph));

        Assert.That(instructions[2].Operands[1], Is.TypeOf<Register>());
    }

    [Test]
    [Category("边界值")]
    public void 地址计算后未初始化槽位时保持原生指针参数()
    {
        var stack = new Register(null, "stack_-70");
        var pointer = new Register(null, "X19");
        var instructions = new List<Instruction>
        {
            new(0, OpCode.Move, stack, new Immediate(5)),
            new(1, OpCode.Move, pointer, new AddressOf(stack)),
            new(2, OpCode.CallVoid, new StringLiteral("ReadStructReceiver"), pointer),
            new(3, OpCode.Return, stack),
        };
        var graph = BuildGraph(instructions);

        SsaForm.Build(graph, new DominatorInfo(graph));

        Assert.That(instructions[2].Operands[1], Is.TypeOf<Register>());
    }

    [Test]
    [Category("异常输入")]
    public void 装箱只读数据的地址载体保持非写回语义()
    {
        const ulong boxTarget = 0x2162514;
        var stack = new Register(null, "stack_-24");
        var pointer = new Register(null, "X2");
        var result = new Register(null, "X0");
        var klass = new Register(null, "X1");
        var instructions = new List<Instruction>
        {
            new(0, OpCode.Move, stack, new Immediate(1)),
            new(1, OpCode.Move, pointer, new AddressOf(stack)),
            new(2, OpCode.Call, new Immediate((long)boxTarget), result, klass, pointer),
            new(3, OpCode.Return, result),
        };
        var graph = BuildGraph(instructions);

        var rewritten = SsaForm.RewriteImmediateAddressCarriers(
            graph,
            new HashSet<ulong> { boxTarget });

        Assert.That(rewritten, Is.Zero);
        Assert.That(instructions[2].Operands[3], Is.TypeOf<Register>());
    }
}
