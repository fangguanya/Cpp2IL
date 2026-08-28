using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class TailReturnRecoveryTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 共享复制尾链改写为直接返回并删除失去前驱的适配器()
    {
        var fixture = CreateFixture(1);
        var branch = fixture.Branches.Single();
        var originalValue = branch.Instructions[0].Operands[1];

        var changed = TailReturnRecovery.Run(fixture.Method);

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

        var changed = TailReturnRecovery.Run(fixture.Method);

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

        var changed = TailReturnRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.Zero);
            Assert.That(branch.Instructions[^1].OpCode, Is.EqualTo(OpCode.Jump));
            Assert.That(branch.Successors, Is.EqualTo(new[] { fixture.Adapter }));
            Assert.That(fixture.Graph.Blocks, Has.Count.EqualTo(originalBlockCount));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 当前块Newobj定义共享返回值时保留直接返回()
    {
        var fixture = CreateFixture(1);
        var branch = fixture.Branches.Single();
        branch.Instructions[0] = new Instruction(
            0,
            OpCode.Newobj,
            fixture.SharedValue,
            fixture.Method.ReturnType);

        var changed = TailReturnRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.EqualTo(1));
            Assert.That(branch.Instructions[^1].OpCode, Is.EqualTo(OpCode.Return));
            Assert.That(branch.Instructions[^1].Operands[0], Is.SameAs(fixture.SharedValue));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 当前块托管调用定义共享返回值时保留直接返回()
    {
        var fixture = CreateFixture(1);
        var branch = fixture.Branches.Single();
        var producer = new InjectedMethodAnalysisContext(
            fixture.Method.DeclaringType!,
            "ProduceValue",
            fixture.Method.ReturnType,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        branch.Instructions[0] = new Instruction(0, OpCode.Call, producer, fixture.SharedValue);

        var changed = TailReturnRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.EqualTo(1));
            Assert.That(branch.Instructions[^1].OpCode, Is.EqualTo(OpCode.Return));
            Assert.That(branch.Instructions[^1].Operands[0], Is.SameAs(fixture.SharedValue));
        });
    }

    [Test]
    [Category("边界值")]
    public void 当前块把空常量写入共享引用返回值时保留直接返回()
    {
        var fixture = CreateFixture(1);
        var branch = fixture.Branches.Single();
        branch.Instructions[0] = new Instruction(0, OpCode.Move, fixture.SharedValue, new Immediate(0));

        var changed = TailReturnRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.EqualTo(1));
            Assert.That(branch.Instructions[^1].OpCode, Is.EqualTo(OpCode.Return));
            Assert.That(branch.Instructions[^1].Operands[0], Is.SameAs(fixture.SharedValue));
        });
    }

    [Test]
    [Category("边界值")]
    public void 返回入口参数时无需当前块重复赋值即可折叠()
    {
        var fixture = CreateFixture(1);
        var branch = fixture.Branches.Single();
        branch.Instructions.RemoveAt(0);
        fixture.Method.ParameterLocals.Add(fixture.SharedValue);

        var changed = TailReturnRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.EqualTo(1));
            Assert.That(branch.Instructions[^1].OpCode, Is.EqualTo(OpCode.Return));
            Assert.That(branch.Instructions[^1].Operands[0], Is.SameAs(fixture.SharedValue));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 空桥没有当前块到达定义时保持原始跳转()
    {
        var fixture = CreateFixture(1);
        var bridge = fixture.Branches.Single();
        bridge.Instructions.RemoveAt(0);

        var changed = TailReturnRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.Zero);
            Assert.That(bridge.Instructions[^1].OpCode, Is.EqualTo(OpCode.Jump));
            Assert.That(bridge.Successors, Is.EqualTo(new[] { fixture.Adapter }));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 当前块定义来源类型与方法返回类型冲突时保持原始跳转()
    {
        var fixture = CreateFixture(1);
        var branch = fixture.Branches.Single();
        var wrongValue = new LocalVariable(
            "wrongValue",
            new Register(null, "W9", 1),
            fixture.Method.AppContext.SystemTypes.SystemInt32Type);
        branch.Instructions[0] = new Instruction(0, OpCode.Move, fixture.SharedValue, wrongValue);
        fixture.Method.Locals.Add(wrongValue);

        var changed = TailReturnRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.Zero);
            Assert.That(branch.Instructions[^1].OpCode, Is.EqualTo(OpCode.Jump));
            Assert.That(branch.Successors, Is.EqualTo(new[] { fixture.Adapter }));
        });
    }

    [Test]
    [Category("异常输入")]
    public void Newobj目标局部类型被污染但实际分配类型不相容时保持原始跳转()
    {
        var fixture = CreateFixture(1);
        var branch = fixture.Branches.Single();
        branch.Instructions[0] = new Instruction(
            0,
            OpCode.Newobj,
            fixture.SharedValue,
            fixture.Method.AppContext.SystemTypes.SystemInt32Type);

        var changed = TailReturnRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.Zero);
            Assert.That(branch.Instructions[^1].OpCode, Is.EqualTo(OpCode.Jump));
            Assert.That(branch.Successors, Is.EqualTo(new[] { fixture.Adapter }));
        });
    }

    [Test]
    [Category("异常输入")]
    public void Add目标局部类型被污染为返回类型时保持原始跳转()
    {
        var fixture = CreateFixture(1);
        var branch = fixture.Branches.Single();
        branch.Instructions[0] = new Instruction(
            0,
            OpCode.Add,
            fixture.SharedValue,
            new Immediate(1),
            new Immediate(2));

        var changed = TailReturnRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.Zero);
            Assert.That(branch.Instructions[^1].OpCode, Is.EqualTo(OpCode.Jump));
            Assert.That(branch.Successors, Is.EqualTo(new[] { fixture.Adapter }));
        });
    }

    [Test]
    [Category("异常输入")]
    public void ByRef返回类型不能把零常量折叠为托管空引用()
    {
        var fixture = CreateFixture(1);
        fixture.Method.ReturnType = new ByRefTypeAnalysisContext(fixture.Method.ReturnType);
        var branch = fixture.Branches.Single();
        branch.Instructions[0] = new Instruction(0, OpCode.Move, fixture.SharedValue, new Immediate(0));

        var changed = TailReturnRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.Zero);
            Assert.That(branch.Instructions[^1].OpCode, Is.EqualTo(OpCode.Jump));
            Assert.That(branch.Successors, Is.EqualTo(new[] { fixture.Adapter }));
        });
    }

    [Test]
    [Category("异常输入")]
    public void Pointer返回类型不能把零常量折叠为托管空引用()
    {
        var fixture = CreateFixture(1);
        fixture.Method.ReturnType = new PointerTypeAnalysisContext(fixture.Method.ReturnType);
        var branch = fixture.Branches.Single();
        branch.Instructions[0] = new Instruction(0, OpCode.Move, fixture.SharedValue, new Immediate(0));

        var changed = TailReturnRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.Zero);
            Assert.That(branch.Instructions[^1].OpCode, Is.EqualTo(OpCode.Jump));
            Assert.That(branch.Successors, Is.EqualTo(new[] { fixture.Adapter }));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 值类型局部返回为Object需要装箱时保持原始跳转()
    {
        var fixture = CreateValueToReferenceFixture(
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemObjectType);

        var changed = TailReturnRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.Zero);
            Assert.That(fixture.Branch.Instructions[^1].OpCode, Is.EqualTo(OpCode.Jump));
            Assert.That(fixture.Branch.Successors, Is.EqualTo(new[] { fixture.Adapter }));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 值类型局部返回为已实现接口需要装箱时保持原始跳转()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var comparable = app.SystemTypes.SystemInt32Type.InterfaceContexts.Single(
            type => type.FullName == "System.IComparable");
        var fixture = CreateValueToReferenceFixture(comparable);

        var changed = TailReturnRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.Zero);
            Assert.That(fixture.Branch.Instructions[^1].OpCode, Is.EqualTo(OpCode.Jump));
            Assert.That(fixture.Branch.Successors, Is.EqualTo(new[] { fixture.Adapter }));
        });
    }

    private static ValueToReferenceFixture CreateValueToReferenceFixture(TypeAnalysisContext returnType)
    {
        var fixture = CreateFixture(1);
        fixture.Method.ReturnType = returnType;
        var branch = fixture.Branches.Single();
        var value = new LocalVariable(
            "value",
            new Register(null, "W9", 1),
            fixture.Method.AppContext.SystemTypes.SystemInt32Type);
        branch.Instructions[0] = new Instruction(0, OpCode.Move, fixture.SharedValue, value);
        fixture.Method.Locals.Add(value);
        return new ValueToReferenceFixture(fixture.Method, branch, fixture.Adapter);
    }

    private static Fixture CreateFixture(int branchCount)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = new InjectedTypeAnalysisContext(
            app.Assemblies[0],
            "Cpp2IL.Core.Tests",
            "TailReturnOwner",
            app.SystemTypes.SystemObjectType,
            TypeAttributes.Public | TypeAttributes.Class);
        var method = new InjectedMethodAnalysisContext(
            owner,
            "ReturnValue",
            app.SystemTypes.SystemStringType,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        var graph = new ISILControlFlowGraph([new Instruction(0, OpCode.Return)]);
        var entry = graph.EntryBlock;
        var exit = graph.ExitBlock;
        entry.Successors.Clear();
        exit.Predecessors.Clear();

        var sharedValue = new LocalVariable(
            "sharedValue",
            new Register(null, "X8", 1),
            app.SystemTypes.SystemStringType);
        var returnValue = new LocalVariable(
            "returnValue",
            new Register(null, "X0", 1),
            app.SystemTypes.SystemStringType);
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
        method.ControlFlowGraph = graph;
        method.Locals = [sharedValue, returnValue];
        method.ParameterLocals = [];
        return new Fixture(method, graph, branches, adapter, sharedValue);
    }

    private static void Connect(Block source, Block target)
    {
        source.Successors.Add(target);
        target.Predecessors.Add(source);
    }

    private sealed record Fixture(
        MethodAnalysisContext Method,
        ISILControlFlowGraph Graph,
        List<Block> Branches,
        Block Adapter,
        LocalVariable SharedValue);

    private sealed record ValueToReferenceFixture(
        MethodAnalysisContext Method,
        Block Branch,
        Block Adapter);
}
