using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests.Analysis;

public class SimplifierTests
{
    private static MethodAnalysisContext CreateMethod(ISILControlFlowGraph graph, params LocalVariable[] locals)
    {
        var method = (MethodAnalysisContext)RuntimeHelpers.GetUninitializedObject(typeof(MethodAnalysisContext));
        method.ControlFlowGraph = graph;
        method.Locals = locals.ToList();
        method.ParameterLocals = [];
        return method;
    }

    [Test]
    public void DoesNotInlineAcrossJoinWhenLocalHasMultipleDefinitions()
    {
        var x  = new LocalVariable("x", new Register(null, "x"));
        var cond = new LocalVariable("cond", new Register(null, "cond"));
        var selected = new LocalVariable("selected", new Register(null, "selected"));
        var oddText = new LocalVariable("oddText", new Register(null, "oddText"));
        var evenText = new LocalVariable("evenText", new Register(null, "evenText"));

        var instructions = new List<Instruction>
        {
            new(0, OpCode.CheckNotEqual, cond, x, Imm(1)),
            new(1, OpCode.ConditionalJump, Imm(5), cond),
            new(2, OpCode.Move, oddText, Str("Odd second")),
            new(3, OpCode.Move, selected, oddText),
            new(4, OpCode.Jump, Imm(8)),
            new(5, OpCode.Move, evenText, Str("Even second")),
            new(6, OpCode.Move, selected, evenText),
            new(7, OpCode.Jump, Imm(8)),
            new(8, OpCode.CallVoid, Str("Console.WriteLine"), selected, Imm(0)),
            new(9, OpCode.Return),
        };

        foreach (var instruction in instructions)
        {
            if (instruction.OpCode is not (OpCode.Jump or OpCode.ConditionalJump))
                continue;

            instruction.SetOperand(0, instructions[(int)((Immediate)instruction.Operands[0]).Value]);
        }

        var graph = new ISILControlFlowGraph(instructions);
        var method = CreateMethod(graph, cond, selected, oddText, evenText);

        Simplifier.Simplify(method);

        var live = graph.Blocks.SelectMany(b => b.Instructions).ToList();
        var selectedDefinitions = live.Where(i => i.OpCode == OpCode.Move && ReferenceEquals(i.Destination, selected)).ToList();
        Assert.That(selectedDefinitions.Count, Is.EqualTo(2), "both branch assignments to selected must remain");

        var writeLineCall = live.Single(i => i.OpCode == OpCode.CallVoid && i.Operands[0] is StringLiteral { Value: "Console.WriteLine" });
        Assert.That(ReferenceEquals(writeLineCall.Operands[1], selected), Is.True,
            "join-point call must keep the selected local, not a branch-specific constant");
    }

    [Test]
    public void DoesNotCorruptUnrelatedConstantMemoryAddends()
    {
        var aLocal = new LocalVariable("a", new Register(null, "a"));
        var bLocal = new LocalVariable("b", new Register(null, "b"));
        var aDefinition = new Instruction(0, OpCode.Move, aLocal, new MemoryOperand(null, null, 0xAAAA, 0));
        var aConsumer = new Instruction(1, OpCode.CallVoid, Str("f"), aLocal, Imm(0));
        var bDefinition = new Instruction(2, OpCode.Move, bLocal, new MemoryOperand(null, null, 0xBBBB, 0));
        var bConsumer = new Instruction(3, OpCode.CallVoid, Str("g"), bLocal, Imm(0));
        var graph = new ISILControlFlowGraph([
            aDefinition,
            aConsumer,
            bDefinition,
            bConsumer,
            new Instruction(4, OpCode.Return),
        ]);

        // 中文注释：两个绝对地址读取各自在定义点形成快照；任何一次传播都不得改写另一地址。
        Simplifier.Simplify(CreateMethod(graph, aLocal, bLocal));

        Assert.Multiple(() =>
        {
            Assert.That(aDefinition.OpCode, Is.EqualTo(OpCode.Move), "第一个内存快照必须保留");
            Assert.That(((MemoryOperand)aDefinition.Operands[1]).Addend, Is.EqualTo(0xAAAAL),
                "第一个绝对地址不得漂移");
            Assert.That(aConsumer.Operands[1], Is.SameAs(aLocal), "第一个消费者必须读取已保存的快照");
            Assert.That(bDefinition.OpCode, Is.EqualTo(OpCode.Move), "第二个内存快照必须保留");
            Assert.That(((MemoryOperand)bDefinition.Operands[1]).Addend, Is.EqualTo(0xBBBBL),
                "第二个绝对地址不得被第一个地址污染");
            Assert.That(bConsumer.Operands[1], Is.SameAs(bLocal), "第二个消费者必须读取已保存的快照");
        });
    }

    [Test]
    public void 基本功能_可变读取必须保留定义点的一次快照()
    {
        // 中文注释：这些操作数都需要在 Move 所在位置求值一次；延迟到消费者处会观察到不同状态。
        var scenarios = new (string Name, Func<LocalVariable, IOperand> CreateSource)[]
        {
            ("字段读取", owner => new FieldReference(null!, owner, 8)),
            ("内存读取", owner => new MemoryOperand(owner, null, 16, 0)),
            ("数组元素读取", owner => new ArrayAccess(owner, Imm(0))),
            ("取地址", owner => new AddressOf(owner)),
            ("数组长度读取", owner => new ArrayLength(owner)),
            ("字符串长度读取", owner => new StringLength(owner)),
            ("列表数量读取", owner => new ListCount(owner, null!)),
            ("元数据字符串表读取", owner => new MetadataStringTableLookup(owner, [Str("甲")], Str("默认"))),
            ("只读数值表索引读取", owner => new ReadOnlyUInt16TableLookup("1", owner)),
        };

        foreach (var scenario in scenarios)
        {
            var owner = new LocalVariable($"owner_{scenario.Name}", new Register(null, $"owner_{scenario.Name}"));
            var snapshot = new LocalVariable($"snapshot_{scenario.Name}", new Register(null, $"snapshot_{scenario.Name}"));
            var definition = new Instruction(0, OpCode.Move, snapshot, scenario.CreateSource(owner));
            var consumer = new Instruction(1, OpCode.CallVoid, Str("Consume"), snapshot, Imm(0));
            var graph = new ISILControlFlowGraph([definition, consumer, new Instruction(2, OpCode.Return)]);

            Simplifier.Simplify(CreateMethod(graph, owner, snapshot));

            Assert.Multiple(() =>
            {
                Assert.That(definition.OpCode, Is.EqualTo(OpCode.Move), $"{scenario.Name}的快照定义必须保留");
                Assert.That(consumer.Operands[1], Is.SameAs(snapshot), $"{scenario.Name}不得被搬到消费者位置重新求值");
            });
        }
    }

    [Test]
    public void 基本功能_立即数与纯局部复制仍可串联优化()
    {
        var value = new LocalVariable("value", new Register(null, "value"));
        var alias = new LocalVariable("alias", new Register(null, "alias"));
        var consumer = new Instruction(2, OpCode.CallVoid, Str("Consume"), alias, Imm(0));
        var graph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, value, Imm(42)),
            new Instruction(1, OpCode.Move, alias, value),
            consumer,
            new Instruction(3, OpCode.Return),
        ]);

        Simplifier.Simplify(CreateMethod(graph, value, alias));

        var live = graph.Blocks.SelectMany(block => block.Instructions).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(consumer.Operands[1], Is.EqualTo(Imm(42)), "纯局部复制链最终应折叠为立即数");
            Assert.That(live.Any(instruction => instruction.OpCode == OpCode.Move), Is.False,
                "完成传播后不应遗留已失去用途的复制指令");
        });
    }

    [Test]
    public void 边界值_一条分支重定义不应终止其他分支传播()
    {
        var condition = new LocalVariable("condition", new Register(null, "condition"));
        var selected = new LocalVariable("selected", new Register(null, "selected"));
        var redefinitionRoot = new LocalVariable("redefinitionRoot", new Register(null, "redefinitionRoot"));
        var redefinedValue = new LocalVariable("redefinedValue", new Register(null, "redefinedValue"));
        var initialDefinition = new Instruction(0, OpCode.Move, selected, Imm(7));
        var fallthroughUse = new Instruction(2, OpCode.CallVoid, Str("UseOriginal"), selected, Imm(0));
        var redefinition = new Instruction(4, OpCode.Move, selected, new MemoryOperand(redefinitionRoot));
        var redefinedRead = new Instruction(5, OpCode.Move, redefinedValue, new MemoryOperand(selected));
        var instructions = new List<Instruction>
        {
            initialDefinition,
            new(1, OpCode.ConditionalJump, Imm(4), condition),
            fallthroughUse,
            new(3, OpCode.Return),
            redefinition,
            redefinedRead,
            new(6, OpCode.Return, redefinedValue),
        };

        // 中文注释：跳转分支先命中同一局部的重定义；顺序分支仍必须接收定义点的旧值。
        instructions[1].SetOperand(0, instructions[4]);
        var graph = new ISILControlFlowGraph(instructions);

        Simplifier.Simplify(CreateMethod(graph, condition, selected, redefinitionRoot, redefinedValue));

        var live = graph.Blocks.SelectMany(block => block.Instructions).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(fallthroughUse.Operands[1], Is.EqualTo(Imm(7)), "兄弟分支不得因另一分支重定义而漏掉传播");
            Assert.That(live.Contains(initialDefinition), Is.False, "旧值完成传播后必须从最终图中删除");
            Assert.That(redefinition.OpCode, Is.EqualTo(OpCode.Move), "可变读取形成的重定义必须保留");
            Assert.That(((MemoryOperand)redefinedRead.Operands[1]).Base, Is.SameAs(selected),
                "重定义分支必须继续读取自己的新快照");
        });
    }

    [Test]
    public void 异常输入_源局部在线性路径重定义时别名必须保持首次快照()
    {
        var firstOwner = new LocalVariable("firstOwner", new Register(null, "firstOwner"));
        var secondOwner = new LocalVariable("secondOwner", new Register(null, "secondOwner"));
        var snapshot = new LocalVariable("snapshot", new Register(null, "snapshot"));
        var alias = new LocalVariable("alias", new Register(null, "alias"));
        var aliasDefinition = new Instruction(1, OpCode.Move, alias, snapshot);
        var consumer = new Instruction(3, OpCode.CallVoid, Str("UseFirstSnapshot"), alias, Imm(0));
        var graph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, snapshot, new FieldReference(null!, firstOwner, 8)),
            aliasDefinition,
            new Instruction(2, OpCode.Move, snapshot, new FieldReference(null!, secondOwner, 16)),
            consumer,
            new Instruction(4, OpCode.Return),
        ]);

        // 中文注释：alias 保存第一次字段读取；snapshot 后续改写不得让 alias 观察第二次字段读取。
        Simplifier.Simplify(CreateMethod(graph, firstOwner, secondOwner, snapshot, alias));

        Assert.Multiple(() =>
        {
            Assert.That(aliasDefinition.OpCode, Is.EqualTo(OpCode.Move), "源局部重定义后必须保留别名快照定义");
            Assert.That(consumer.Operands[1], Is.SameAs(alias), "消费者必须继续使用第一次快照的别名");
        });
    }

    [Test]
    public void 边界值_源局部仅在一条分支重定义时汇合点必须保留别名()
    {
        var condition = new LocalVariable("condition", new Register(null, "condition"));
        var firstOwner = new LocalVariable("firstOwner", new Register(null, "firstOwner"));
        var secondOwner = new LocalVariable("secondOwner", new Register(null, "secondOwner"));
        var snapshot = new LocalVariable("snapshot", new Register(null, "snapshot"));
        var alias = new LocalVariable("alias", new Register(null, "alias"));
        var aliasDefinition = new Instruction(1, OpCode.Move, alias, snapshot);
        var consumer = new Instruction(5, OpCode.CallVoid, Str("UseMergedAlias"), alias, Imm(0));
        var instructions = new List<Instruction>
        {
            new(0, OpCode.Move, snapshot, new FieldReference(null!, firstOwner, 8)),
            aliasDefinition,
            new(2, OpCode.ConditionalJump, Imm(4), condition),
            new(3, OpCode.Jump, Imm(5)),
            new(4, OpCode.Move, snapshot, new FieldReference(null!, secondOwner, 16)),
            consumer,
            new(6, OpCode.Return),
        };

        // 中文注释：一条路径改写 source、另一条路径保持 source；汇合处不能选择任一路径的 source。
        instructions[2].SetOperand(0, instructions[4]);
        instructions[3].SetOperand(0, instructions[5]);
        var graph = new ISILControlFlowGraph(instructions);

        Simplifier.Simplify(CreateMethod(graph, condition, firstOwner, secondOwner, snapshot, alias));

        Assert.Multiple(() =>
        {
            Assert.That(aliasDefinition.OpCode, Is.EqualTo(OpCode.Move), "分支汇合前必须保留别名快照定义");
            Assert.That(consumer.Operands[1], Is.SameAs(alias), "汇合消费者必须读取路径无关的首次快照");
        });
    }

    [Test]
    public void 异常输入_未知复合操作数按可变读取保留快照()
    {
        var snapshot = new LocalVariable("snapshot", new Register(null, "snapshot"));
        var definition = new Instruction(0, OpCode.Move, snapshot, new 未知可变读取());
        var consumer = new Instruction(1, OpCode.CallVoid, Str("Consume"), snapshot, Imm(0));
        var graph = new ISILControlFlowGraph([definition, consumer, new Instruction(2, OpCode.Return)]);

        // 中文注释：未知操作数必须默认保守，避免后续新增读取类型被晚期传播静默重复执行。
        Simplifier.Simplify(CreateMethod(graph, snapshot));

        Assert.Multiple(() =>
        {
            Assert.That(definition.OpCode, Is.EqualTo(OpCode.Move), "未知读取的定义点必须保留");
            Assert.That(consumer.Operands[1], Is.SameAs(snapshot), "未知读取不得被直接内联");
        });
    }

    [Test]
    public void PropagatesCopyThroughMemoryBase()
    {
        var x = new LocalVariable("x", new Register(null, "x"));
        var y = new LocalVariable("y", new Register(null, "y"));
        var z = new LocalVariable("z", new Register(null, "z"));

        // x := y; z := [x]; f(z).  Copy propagation must rewrite the load's base x -> y (a base must
        // stay a local), so the surviving load reads [y].
        var instructions = new List<Instruction>
        {
            new(0, OpCode.Move, x, y),
            new(1, OpCode.Move, z, new MemoryOperand(x, null, 0, 0)),
            new(2, OpCode.CallVoid, Str("f"), z, Imm(0)),
            new(3, OpCode.Return),
        };

        var graph = new ISILControlFlowGraph(instructions);
        var method = CreateMethod(graph, x, y, z);

        Simplifier.Simplify(method);

        var live = graph.Blocks.SelectMany(b => b.Instructions).ToList();
        var load = live.Single(i => i.Operands.Any(o => o is MemoryOperand));
        var memory = (MemoryOperand)load.Operands.First(o => o is MemoryOperand);
        Assert.That(ReferenceEquals(memory.Base, y), Is.True, "the copy's source must be propagated into the memory base");
        Assert.That(live.Any(i => i.OpCode == OpCode.Move && ReferenceEquals(i.Operands[0], x)), Is.False,
            "the now-dead copy is removed");
    }

    private sealed class 未知可变读取 : IOperand
    {
    }
}
