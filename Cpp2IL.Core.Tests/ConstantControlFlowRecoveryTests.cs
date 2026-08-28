using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests;

public class ConstantControlFlowRecoveryTests
{
    private static int Run(
        ISILControlFlowGraph graph,
        params (LocalVariable Local, long[] Values)[] carriers)
    {
        var domains = carriers.ToDictionary(
            carrier => carrier.Local,
            carrier => new HashSet<long>(carrier.Values));
        return ConstantControlFlowRecovery.Run(
            graph,
            new IntegerControlStatePlan(domains));
    }

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

        var rewritten = Run(graph);

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

        var rewritten = Run(graph);

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

        var rewritten = Run(graph);

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

        var rewritten = Run(graph, (state, [0]));

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
        var selector = new LocalVariable("selector", new Register(null, "W0"));
        var condition = new LocalVariable("condition", new Register(null, "W8"));
        var oneDefinition = new Instruction(3, OpCode.Move, state, new Immediate(1));
        var comparison = new Instruction(4, OpCode.CheckNotEqual, condition, state, new Immediate(0));
        var target = new Instruction(7, OpCode.Return, new Immediate(1));
        var branch = new Instruction(5, OpCode.ConditionalJump, target, condition);
        var graph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.ConditionalJump, oneDefinition, selector),
            new Instruction(1, OpCode.Move, state, new Immediate(0)),
            new Instruction(2, OpCode.Jump, comparison),
            oneDefinition,
            comparison,
            branch,
            new Instruction(6, OpCode.Return, new Immediate(0)),
            target,
        ]);

        var rewritten = Run(graph, (state, [0, 1]));

        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Is.Zero);
            Assert.That(branch.OpCode, Is.EqualTo(OpCode.ConditionalJump));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 条件选择的顺序假边被边约束删除()
    {
        var selector = new LocalVariable("selector", new Register(null, "W0"));
        var state = new LocalVariable("state", new Register(null, "W1"));
        var firstCondition = new LocalVariable("firstCondition", new Register(null, "W2"));
        var matchedOther = new LocalVariable("matchedOther", new Register(null, "W3"));
        var invalidOther = new LocalVariable("invalidOther", new Register(null, "W4"));
        var badTarget = new Instruction(7, OpCode.Throw, new Immediate(0));
        var selectedTarget = new Instruction(8, OpCode.Return, new Immediate(1));
        var firstBranch = new Instruction(
            2,
            OpCode.ConditionalJump,
            selectedTarget,
            firstCondition);
        var impossibleBranch = new Instruction(
            5,
            OpCode.ConditionalJump,
            badTarget,
            invalidOther);
        var graph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.ConditionalSelect, state, selector, new Immediate(0), new Immediate(7)),
            new Instruction(1, OpCode.CheckEqual, firstCondition, state, new Immediate(0)),
            firstBranch,
            new Instruction(3, OpCode.CheckEqual, matchedOther, state, new Immediate(7)),
            new Instruction(4, OpCode.Not, invalidOther, matchedOther),
            impossibleBranch,
            new Instruction(6, OpCode.Return, new Immediate(0)),
            badTarget,
            selectedTarget,
        ]);

        var rewritten = Run(graph, (state, [0, 7]));

        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Is.EqualTo(1));
            Assert.That(firstBranch.OpCode, Is.EqualTo(OpCode.ConditionalJump));
            Assert.That(impossibleBranch.OpCode, Is.EqualTo(OpCode.Jump));
            Assert.That(graph.Instructions.Any(instruction => instruction.OpCode == OpCode.Throw), Is.False);
        });
    }

    [Test]
    [Category("边界值")]
    public void 有限值域超出上限后提升Top并保留分支()
    {
        var state = new LocalVariable("state", new Register(null, "W1"));
        var finalCondition = new LocalVariable("finalCondition", new Register(null, "W2"));
        var valueCount = ConstantControlFlowRecovery.FiniteValueLimit + 1;
        var assignmentStart = valueCount + 1;
        var joinIndex = assignmentStart + (valueCount - 1) * 2;
        var join = new Instruction(
            joinIndex,
            OpCode.CheckEqual,
            finalCondition,
            state,
            new Immediate(0));
        var trueReturn = new Instruction(joinIndex + 3, OpCode.Return, new Immediate(1));
        var finalBranch = new Instruction(
            joinIndex + 1,
            OpCode.ConditionalJump,
            trueReturn,
            finalCondition);
        var assignments = Enumerable.Range(0, valueCount - 1)
            .Select(index => new Instruction(
                assignmentStart + index * 2,
                OpCode.Move,
                state,
                new Immediate(index)))
            .ToArray();
        var instructions = new List<Instruction>();
        for (var index = 0; index < assignments.Length; index++)
        {
            var selector = new LocalVariable($"selector{index}", new Register(null, $"W{index + 3}"));
            instructions.Add(new Instruction(index, OpCode.ConditionalJump, assignments[index], selector));
        }

        instructions.Add(new Instruction(valueCount - 1, OpCode.Move, state, new Immediate(valueCount - 1)));
        instructions.Add(new Instruction(valueCount, OpCode.Jump, join));
        foreach (var assignment in assignments)
        {
            instructions.Add(assignment);
            instructions.Add(new Instruction(assignment.Index + 1, OpCode.Jump, join));
        }
        instructions.Add(join);
        instructions.Add(finalBranch);
        instructions.Add(new Instruction(joinIndex + 2, OpCode.Return, new Immediate(0)));
        instructions.Add(trueReturn);

        var graph = new ISILControlFlowGraph(instructions);
        var rewritten = Run(
            graph,
            (state, Enumerable.Range(0, valueCount).Select(value => (long)value).ToArray()));

        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Is.Zero);
            Assert.That(finalBranch.OpCode, Is.EqualTo(OpCode.ConditionalJump));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 不支持的状态定义提升Top并保留分支()
    {
        var state = new LocalVariable("state", new Register(null, "W1"));
        var condition = new LocalVariable("condition", new Register(null, "W2"));
        var target = new Instruction(5, OpCode.Return, new Immediate(1));
        var branch = new Instruction(3, OpCode.ConditionalJump, target, condition);
        var graph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, state, new Immediate(7)),
            new Instruction(1, OpCode.Add, state, state, new Immediate(1)),
            new Instruction(2, OpCode.CheckEqual, condition, state, new Immediate(8)),
            branch,
            new Instruction(4, OpCode.Return, new Immediate(0)),
            target,
        ]);

        var rewritten = Run(graph, (state, [7, 8]));

        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Is.Zero);
            Assert.That(branch.OpCode, Is.EqualTo(OpCode.ConditionalJump));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 载体重定义后旧比较关系失效并保留分支()
    {
        var selector = new LocalVariable("selector", new Register(null, "W0"));
        var state = new LocalVariable("state", new Register(null, "W1"));
        var oldCondition = new LocalVariable("oldCondition", new Register(null, "W2"));
        var target = new Instruction(5, OpCode.Return, new Immediate(1));
        var branch = new Instruction(3, OpCode.ConditionalJump, target, oldCondition);
        var graph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.ConditionalSelect, state, selector, new Immediate(0), new Immediate(7)),
            new Instruction(1, OpCode.CheckEqual, oldCondition, state, new Immediate(0)),
            new Instruction(2, OpCode.Move, state, new Immediate(7)),
            branch,
            new Instruction(4, OpCode.Return, new Immediate(0)),
            target,
        ]);

        var rewritten = Run(graph, (state, [0, 7]));

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

        var rewritten = Run(graph, (first, [0, 1]), (second, [0, 1]));

        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Is.Zero);
            Assert.That(branch.OpCode, Is.EqualTo(OpCode.ConditionalJump));
        });
    }
}
