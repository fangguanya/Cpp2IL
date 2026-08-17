using System.Reflection;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class ManagedReturnValueRecoveryTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 唯一路径同一X0调用结果接回托管Return()
    {
        var fixture = CreateFixture();

        var recovered = ManagedReturnValueRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Return.Operands[0], Is.SameAs(fixture.Produced));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 唯一路径X0调用结果接回未定义X19托管Return()
    {
        var fixture = CreateFixture(returnRegister: "X19");

        var recovered = ManagedReturnValueRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Return.Operands[0], Is.SameAs(fixture.Produced));
        });
    }

    [Test]
    [Category("边界值")]
    public void 类型相同但物理返回寄存器不同时保持原返回值()
    {
        var fixture = CreateFixture(producerRegister: "X1");

        var recovered = ManagedReturnValueRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Return.Operands[0], Is.SameAs(fixture.OriginalReturn));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 返回块存在多个前驱时拒绝猜测来源()
    {
        var fixture = CreateFixture();
        var returnBlock = fixture.Caller.ControlFlowGraph!.Blocks.Single(block =>
            block.Instructions.Contains(fixture.Return));
        var extraPredecessor = new Block { ID = 999 };
        extraPredecessor.Successors.Add(returnBlock);
        returnBlock.Predecessors.Add(extraPredecessor);
        fixture.Caller.ControlFlowGraph.Blocks.Add(extraPredecessor);

        var recovered = ManagedReturnValueRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Return.Operands[0], Is.SameAs(fixture.OriginalReturn));
        });
    }

    [Test]
    [Category("异常输入")]
    public void X19返回局部已有定义时禁止覆盖真实数据流()
    {
        var fixture = CreateFixture(returnRegister: "X19", defineReturn: true);

        var recovered = ManagedReturnValueRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Return.Operands[0], Is.SameAs(fixture.OriginalReturn));
        });
    }

    private static Fixture CreateFixture(
        string producerRegister = "X0",
        string returnRegister = "X0",
        bool defineReturn = false)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = new InjectedTypeAnalysisContext(
            app.Assemblies[0],
            "Cpp2IL.Core.Tests",
            "ReturnOwner",
            app.SystemTypes.SystemObjectType,
            TypeAttributes.Public | TypeAttributes.Class);
        var producer = new InjectedMethodAnalysisContext(
            owner,
            "Produce",
            app.SystemTypes.SystemStringType,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        var caller = new InjectedMethodAnalysisContext(
            owner,
            "Caller",
            app.SystemTypes.SystemStringType,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        var produced = new LocalVariable(
            "produced",
            new Register(null, producerRegister, 1),
            app.SystemTypes.SystemStringType);
        var originalReturn = new LocalVariable(
            "returnValue",
            new Register(null, returnRegister, 2),
            app.SystemTypes.SystemStringType);
        var call = new Instruction(0, OpCode.Call, producer, produced);
        var returnInstruction = new Instruction(1, OpCode.Return, originalReturn);
        var instructions = new System.Collections.Generic.List<Instruction> { call };
        if (defineReturn)
            instructions.Add(new Instruction(1, OpCode.Move, originalReturn, produced));
        instructions.Add(returnInstruction);
        caller.ControlFlowGraph = new ISILControlFlowGraph(instructions);
        caller.Locals = [produced, originalReturn];
        caller.ParameterLocals = [];

        return new Fixture(caller, returnInstruction, originalReturn, produced);
    }

    private sealed record Fixture(
        MethodAnalysisContext Caller,
        Instruction Return,
        LocalVariable OriginalReturn,
        LocalVariable Produced);
}
