using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests;

public class TailReturnRecoveryTests
{
    [Test]
    [Category("基本功能")]
    public void 共享复制尾链改写为直接返回并删除失去前驱的适配器()
    {
        var fixture = CreateFixture(1);
        var branch = fixture.Branches.Single();
        var originalValue = branch.Instructions[0].Operands[1];

        var changed = TailReturnRecovery.Run(fixture.Graph);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.EqualTo(1));
            Assert.That(branch.Instructions[^1].OpCode, Is.EqualTo(OpCode.Return));
            Assert.That(branch.Instructions[^1].Operands[0], Is.SameAs(fixture.SharedValue));
            Assert.That(branch.Instructions[0].Operands[1], Is.SameAs(originalValue));
            Assert.That(branch.Successors, Is.EqualTo(new[] { fixture.Graph.ExitBlock }));
            Assert.That(fixture.Graph.Blocks, Does.Not.Contain(fixture.Adapter));
        });
    }

    [Test]
    [Category("边界值")]
    public void 四百个命中分支一次性改写且全部直达出口()
    {
        const int branchCount = 400;
        var fixture = CreateFixture(branchCount);

        var changed = TailReturnRecovery.Run(fixture.Graph);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.EqualTo(branchCount));
            Assert.That(fixture.Branches.All(block => block.Instructions[^1].OpCode == OpCode.Return), Is.True);
            Assert.That(fixture.Branches.All(block => block.Successors.SequenceEqual([fixture.Graph.ExitBlock])), Is.True);
            Assert.That(fixture.Graph.ExitBlock.Predecessors.Intersect(fixture.Branches).Count(), Is.EqualTo(branchCount));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 尾链含调用副作用时保持原始跳转和控制流()
    {
        var fixture = CreateFixture(1);
        fixture.Adapter.Instructions.Insert(0, new Instruction(10, OpCode.CallVoid, new Immediate(0x1234)));
        var branch = fixture.Branches.Single();
        var originalBlockCount = fixture.Graph.Blocks.Count;

        var changed = TailReturnRecovery.Run(fixture.Graph);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.Zero);
            Assert.That(branch.Instructions[^1].OpCode, Is.EqualTo(OpCode.Jump));
            Assert.That(branch.Successors, Is.EqualTo(new[] { fixture.Adapter }));
            Assert.That(fixture.Graph.Blocks, Has.Count.EqualTo(originalBlockCount));
        });
    }

    private static Fixture CreateFixture(int branchCount)
    {
        var graph = new ISILControlFlowGraph([new Instruction(0, OpCode.Return)]);
        var entry = graph.EntryBlock;
        var exit = graph.ExitBlock;
        entry.Successors.Clear();
        exit.Predecessors.Clear();

        var sharedValue = new LocalVariable("sharedValue", new Register(null, "X8", 1));
        var returnValue = new LocalVariable("returnValue", new Register(null, "X0", 1));
        var adapter = new Block { ID = 1001 };
        var terminal = new Block { ID = 1002 };
        adapter.Instructions.Add(new Instruction(-1, OpCode.Move, returnValue, sharedValue));
        terminal.Instructions.Add(new Instruction(-1, OpCode.Return, returnValue));
        Connect(adapter, terminal);
        Connect(terminal, exit);

        var branches = new List<Block>(branchCount);
        for (var index = 0; index < branchCount; index++)
        {
            var branch = new Block { ID = index + 2 };
            branch.Instructions.Add(new Instruction(index * 2, OpCode.Move, sharedValue, new StringLiteral($"value-{index}")));
            branch.Instructions.Add(new Instruction(index * 2 + 1, OpCode.Jump, adapter));
            Connect(entry, branch);
            Connect(branch, adapter);
            branches.Add(branch);
        }

        graph.Blocks = [entry, exit, .. branches, adapter, terminal];
        return new Fixture(graph, branches, adapter, sharedValue);
    }

    private static void Connect(Block source, Block target)
    {
        source.Successors.Add(target);
        target.Predecessors.Add(source);
    }

    private sealed record Fixture(
        ISILControlFlowGraph Graph,
        List<Block> Branches,
        Block Adapter,
        LocalVariable SharedValue);
}
