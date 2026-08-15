using System;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests;

public class ThrowTerminatorControlFlowTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 建图时Throw直接连接退出并裁掉顺序尾部()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var trailing = new LocalVariable("trailing", new Register(null, "X0"));
        var graph = new ISILControlFlowGraph(
        [
            new Instruction(0, OpCode.Throw, app.SystemTypes.SystemExceptionType),
            new Instruction(1, OpCode.Move, trailing, new Immediate(1))
        ]);

        graph.RemoveUnreachableBlocks();

        var throwBlock = graph.Blocks.Single(block =>
            block.Instructions.LastOrDefault()?.OpCode == OpCode.Throw);
        Assert.Multiple(() =>
        {
            Assert.That(throwBlock.BlockType, Is.EqualTo(BlockType.Interrupt));
            Assert.That(throwBlock.Successors, Is.EqualTo(new[] { graph.ExitBlock }));
            Assert.That(graph.Instructions.Any(instruction => instruction.Destination == trailing), Is.False);
        });
    }

    [Test]
    [Category("边界值")]
    public void 调用后改写Throw时同步删除后继Phi输入且保持其他前驱()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var call = new Instruction(0, OpCode.CallVoid, new Immediate(0x1234));
        var graph = new ISILControlFlowGraph(
        [
            call,
            new Instruction(1, OpCode.Return)
        ]);
        var throwBlock = graph.Blocks.Single(block => block.Instructions.Contains(call));
        var continuation = throwBlock.Successors.Single();
        var otherPredecessor = new Block { ID = 999 };
        var phiDestination = new LocalVariable("phi", new Register(null, "X1"));
        var fromThrow = new LocalVariable("fromThrow", new Register(null, "X2"));
        var fromOther = new LocalVariable("fromOther", new Register(null, "X3"));
        continuation.Predecessors.Add(otherPredecessor);
        otherPredecessor.Successors.Add(continuation);
        graph.Blocks.Add(otherPredecessor);
        continuation.Instructions.Insert(
            0,
            new Instruction(-1, OpCode.Phi, phiDestination, fromThrow, fromOther));
        call.OpCode = OpCode.Throw;
        call.SetOperands(app.SystemTypes.SystemExceptionType);

        graph.TerminateAtThrow(throwBlock);

        var phi = continuation.Instructions[0];
        Assert.Multiple(() =>
        {
            Assert.That(throwBlock.Successors, Is.EqualTo(new[] { graph.ExitBlock }));
            Assert.That(continuation.Predecessors, Is.EqualTo(new[] { otherPredecessor }));
            Assert.That(phi.Operands, Is.EqualTo(new IOperand[] { phiDestination, fromOther }));
            Assert.That(graph.ExitBlock.Predecessors.Count(block => block == throwBlock), Is.EqualTo(1));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 非Throw块拒绝终结为抛出路径()
    {
        var graph = new ISILControlFlowGraph([new Instruction(0, OpCode.Return)]);
        var returnBlock = graph.Blocks.Single(block =>
            block.Instructions.LastOrDefault()?.OpCode == OpCode.Return);

        Assert.That(
            () => graph.TerminateAtThrow(returnBlock),
            Throws.TypeOf<InvalidOperationException>());
    }
}
