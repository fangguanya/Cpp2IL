using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests;

public class PostSsaDeadStoreEliminatorTests
{
    [Test]
    [Category("基本功能")]
    public void 最后一次读取后的绝对槽局部写入被删除()
    {
        var value = new LocalVariable("value", new Register(null, "X24"));
        var first = new Instruction(0, OpCode.Move, value, new Immediate(0));
        var observe = new Instruction(1, OpCode.CallVoid, new Immediate(0x1000), value);
        var dead = new Instruction(2, OpCode.Move, value, new MemoryOperand(null, null, 0x59EE370, 0));
        var graph = new ISILControlFlowGraph([
            first,
            observe,
            dead,
            new Instruction(3, OpCode.Return),
        ]);

        var removed = PostSsaDeadStoreEliminator.Run(graph);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.EqualTo(1));
            Assert.That(first.OpCode, Is.EqualTo(OpCode.Move));
            Assert.That(dead.OpCode, Is.EqualTo(OpCode.Nop));
        });
    }

    [Test]
    [Category("边界值")]
    public void 跨块后继读取保留前驱中的最后定义()
    {
        var value = new LocalVariable("value", new Register(null, "X24"));
        var target = new Instruction(3, OpCode.Return, value);
        var definition = new Instruction(0, OpCode.Move, value, new Immediate(7));
        var graph = new ISILControlFlowGraph([
            definition,
            new Instruction(1, OpCode.Jump, target),
            new Instruction(2, OpCode.Return, new Immediate(0)),
            target,
        ]);

        var removed = PostSsaDeadStoreEliminator.Run(graph);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.Zero);
            Assert.That(definition.OpCode, Is.EqualTo(OpCode.Move));
        });
    }

    [Test]
    [Category("边界值")]
    public void 退SSA二地址读改写保留计数器初始化链()
    {
        var temporary = new LocalVariable("temporary", new Register(null, "TEMP"));
        var counter = new LocalVariable("counter", new Register(null, "X19"));
        var createMinusOne = new Instruction(0, OpCode.Not, temporary, new Immediate(0));
        var initialize = new Instruction(1, OpCode.Move, counter, temporary);
        var increment = new Instruction(2, OpCode.Add, counter, counter, new Immediate(1));
        var graph = new ISILControlFlowGraph([
            createMinusOne,
            initialize,
            increment,
            new Instruction(3, OpCode.Return, counter),
        ]);

        var removed = PostSsaDeadStoreEliminator.Run(graph);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.Zero);
            Assert.That(createMinusOne.OpCode, Is.EqualTo(OpCode.Not));
            Assert.That(initialize.OpCode, Is.EqualTo(OpCode.Move));
            Assert.That(increment.OpCode, Is.EqualTo(OpCode.Add));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 未使用返回值的调用仍作为有副作用指令保留()
    {
        var result = new LocalVariable("result", new Register(null, "X0"));
        var call = new Instruction(0, OpCode.Call, new Immediate(0x2000), result);
        var graph = new ISILControlFlowGraph([
            call,
            new Instruction(1, OpCode.Return),
        ]);

        var removed = PostSsaDeadStoreEliminator.Run(graph);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.Zero);
            Assert.That(call.OpCode, Is.EqualTo(OpCode.Call));
            Assert.That(graph.Instructions.Count(instruction => instruction.OpCode == OpCode.Call), Is.EqualTo(1));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 间接跳转读取的寄存器定义保持存活()
    {
        var target = new LocalVariable("target", new Register(null, "X3"));
        var definition = new Instruction(0, OpCode.Move, target, new Immediate(0x4000));
        var indirectJump = new Instruction(1, OpCode.IndirectJump, target);
        var graph = new ISILControlFlowGraph([
            definition,
            indirectJump,
        ]);

        var removed = PostSsaDeadStoreEliminator.Run(graph);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.Zero);
            Assert.That(definition.OpCode, Is.EqualTo(OpCode.Move));
            Assert.That(indirectJump.OpCode, Is.EqualTo(OpCode.IndirectJump));
        });
    }
}
