using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests;

public class ConstantControlFlowRecoveryTests
{
    [Test]
    [Category("基本功能")]
    public void 恒真比较改写为无条件跳转并删除假边()
    {
        var condition = new LocalVariable("condition", new Register(null, "W8"));
        var target = new Instruction(3, OpCode.Return, new Immediate(1));
        var branch = new Instruction(1, OpCode.ConditionalJump, target, condition);
        var graph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.CheckEqual, condition, new Immediate(9), new Immediate(9)),
            branch,
            new Instruction(2, OpCode.Return, new Immediate(0)),
            target,
        ]);

        var rewritten = ConstantControlFlowRecovery.Run(graph);

        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Is.EqualTo(1));
            Assert.That(branch.OpCode, Is.EqualTo(OpCode.Jump));
            Assert.That(graph.Instructions.Any(instruction => instruction.Index == 2), Is.False);
        });
    }

    [Test]
    [Category("边界值")]
    public void 恒假比较保留顺序边并删除抛出分支()
    {
        var condition = new LocalVariable("condition", new Register(null, "W8"));
        var target = new Instruction(3, OpCode.Throw, new Immediate(0));
        var branch = new Instruction(1, OpCode.ConditionalJump, target, condition);
        var graph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.CheckNotEqual, condition, new Immediate(4), new Immediate(4)),
            branch,
            new Instruction(2, OpCode.Return, new Immediate(7)),
            target,
        ]);

        var rewritten = ConstantControlFlowRecovery.Run(graph);

        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Is.EqualTo(1));
            Assert.That(graph.Instructions.Any(instruction => instruction.OpCode == OpCode.Throw), Is.False);
            Assert.That(graph.Instructions.Any(instruction => instruction.OpCode == OpCode.ConditionalJump), Is.False);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 含未知业务值的比较保持原控制流()
    {
        var value = new LocalVariable("value", new Register(null, "W0"));
        var condition = new LocalVariable("condition", new Register(null, "W8"));
        var target = new Instruction(3, OpCode.Return, new Immediate(1));
        var branch = new Instruction(1, OpCode.ConditionalJump, target, condition);
        var graph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.CheckEqual, condition, value, new Immediate(9)),
            branch,
            new Instruction(2, OpCode.Return, new Immediate(0)),
            target,
        ]);

        var rewritten = ConstantControlFlowRecovery.Run(graph);

        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Is.Zero);
            Assert.That(branch.OpCode, Is.EqualTo(OpCode.ConditionalJump));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 多个相同零入边的Phi状态折叠为恒假分支()
    {
        var state = new LocalVariable("state", new Register(null, "X21"));
        var condition = new LocalVariable("condition", new Register(null, "W8"));
        var target = new Instruction(5, OpCode.Throw, new Immediate(0));
        var branch = new Instruction(3, OpCode.ConditionalJump, target, condition);
        var graph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, state, new Immediate(0)),
            new Instruction(1, OpCode.Move, state, new Immediate(0)),
            new Instruction(2, OpCode.CheckNotEqual, condition, state, new Immediate(0)),
            branch,
            new Instruction(4, OpCode.Return, new Immediate(7)),
            target,
        ]);

        var rewritten = ConstantControlFlowRecovery.Run(graph);

        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Is.EqualTo(1));
            Assert.That(graph.Instructions.Any(instruction => instruction.OpCode == OpCode.Throw), Is.False);
            Assert.That(graph.Instructions.Any(instruction => instruction.OpCode == OpCode.ConditionalJump), Is.False);
        });
    }

    [Test]
    [Category("边界值")]
    public void Phi入边常量不一致时保留条件分支()
    {
        var state = new LocalVariable("state", new Register(null, "X21"));
        var condition = new LocalVariable("condition", new Register(null, "W8"));
        var target = new Instruction(5, OpCode.Return, new Immediate(1));
        var branch = new Instruction(3, OpCode.ConditionalJump, target, condition);
        var graph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, state, new Immediate(0)),
            new Instruction(1, OpCode.Move, state, new Immediate(1)),
            new Instruction(2, OpCode.CheckNotEqual, condition, state, new Immediate(0)),
            branch,
            new Instruction(4, OpCode.Return, new Immediate(0)),
            target,
        ]);

        var rewritten = ConstantControlFlowRecovery.Run(graph);

        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Is.Zero);
            Assert.That(branch.OpCode, Is.EqualTo(OpCode.ConditionalJump));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 循环复制定义不得冒充常量Phi()
    {
        var first = new LocalVariable("first", new Register(null, "X21"));
        var second = new LocalVariable("second", new Register(null, "X22"));
        var condition = new LocalVariable("condition", new Register(null, "W8"));
        var target = new Instruction(5, OpCode.Return, new Immediate(1));
        var branch = new Instruction(3, OpCode.ConditionalJump, target, condition);
        var graph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, first, second),
            new Instruction(1, OpCode.Move, second, first),
            new Instruction(2, OpCode.CheckEqual, condition, first, new Immediate(0)),
            branch,
            new Instruction(4, OpCode.Return, new Immediate(0)),
            target,
        ]);

        var rewritten = ConstantControlFlowRecovery.Run(graph);

        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Is.Zero);
            Assert.That(branch.OpCode, Is.EqualTo(OpCode.ConditionalJump));
        });
    }
}
