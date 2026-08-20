using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class InlineConstructorRecoveryTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 派生对象基类构造与只读字段写入折回派生构造器()
    {
        var fixture = CreateFixture(readonlyField: true, duplicateConstructor: false);

        var recovered = InlineConstructorRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.BaseCall.Operands[0], Is.SameAs(fixture.DerivedConstructor));
            Assert.That(fixture.BaseCall.Operands[2], Is.SameAs(fixture.Value));
            Assert.That(fixture.FieldStore.OpCode, Is.EqualTo(OpCode.Nop));
            Assert.That(fixture.FieldStore.Operands, Is.Empty);
        });
    }

    [Test]
    [Category("边界值")]
    public void 派生构造签名不唯一时保持内联布局()
    {
        var fixture = CreateFixture(readonlyField: true, duplicateConstructor: true);

        var recovered = InlineConstructorRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.BaseCall.Operands[0], Is.SameAs(fixture.BaseConstructor));
            Assert.That(fixture.FieldStore.OpCode, Is.EqualTo(OpCode.Move));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 可写字段初始化不得伪装成构造参数()
    {
        var fixture = CreateFixture(readonlyField: false, duplicateConstructor: false);

        var recovered = InlineConstructorRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.BaseCall.Operands[0], Is.SameAs(fixture.BaseConstructor));
            Assert.That(fixture.FieldStore.OpCode, Is.EqualTo(OpCode.Move));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 状态机同名状态字段写入折回构造器()
    {
        var fixture = CreateStateMachineFixture(stateMachineName: true, duplicateConstructor: false);

        var recovered = InlineConstructorRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.BaseCall.OpCode, Is.EqualTo(OpCode.CallVoid));
            Assert.That(fixture.BaseCall.Operands[0], Is.SameAs(fixture.Constructor));
            Assert.That(fixture.BaseCall.Operands[1], Is.SameAs(fixture.Receiver));
            Assert.That(fixture.BaseCall.Operands[2], Is.SameAs(fixture.State));
            Assert.That(fixture.StateStore.OpCode, Is.EqualTo(OpCode.Nop));
        });
    }

    [Test]
    [Category("边界值")]
    public void 普通类型同名字段写入保持字段赋值()
    {
        var fixture = CreateStateMachineFixture(stateMachineName: false, duplicateConstructor: false);

        var recovered = InlineConstructorRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.StateStore.OpCode, Is.EqualTo(OpCode.Move));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 状态机构造签名歧义时保持字段赋值()
    {
        var fixture = CreateStateMachineFixture(stateMachineName: true, duplicateConstructor: true);

        var recovered = InlineConstructorRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.StateStore.OpCode, Is.EqualTo(OpCode.Move));
        });
    }

    private static Fixture CreateFixture(bool readonlyField, bool duplicateConstructor)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var assembly = app.GetAssemblyByName("mscorlib")!;
        var baseType = new InjectedTypeAnalysisContext(
            assembly,
            "Fixture",
            "BaseScenario",
            app.SystemTypes.SystemObjectType,
            TypeAttributes.Public);
        var baseConstructor = baseType.InjectMethodContext(
            ".ctor",
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Public,
            app.SystemTypes.SystemBooleanType);
        var derivedType = new InjectedTypeAnalysisContext(
            assembly,
            "Fixture",
            "ConfiguredScenario",
            baseType,
            TypeAttributes.Public);
        var derivedConstructor = derivedType.InjectMethodContext(
            ".ctor",
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Public,
            app.SystemTypes.SystemStringType);
        if (duplicateConstructor)
            derivedType.InjectMethodContext(
                ".ctor",
                app.SystemTypes.SystemVoidType,
                MethodAttributes.Public,
                app.SystemTypes.SystemStringType);
        var field = derivedType.InjectFieldContext(
            "_content",
            app.SystemTypes.SystemStringType,
            FieldAttributes.Private | (readonlyField ? FieldAttributes.InitOnly : 0));
        var callerType = new InjectedTypeAnalysisContext(
            assembly,
            "Fixture",
            "Caller",
            app.SystemTypes.SystemObjectType,
            TypeAttributes.Public);
        var method = callerType.InjectMethodContext(
            "Run",
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Public | MethodAttributes.Static);
        var receiver = new LocalVariable("scenario", new Register(null, "X0"), derivedType);
        var value = new LocalVariable("content", new Register(null, "X1"), app.SystemTypes.SystemStringType);
        var allocation = new Instruction(1, OpCode.Newobj, receiver, new RuntimeClassTypeAnalysisContext(derivedType, assembly));
        var baseCall = new Instruction(2, OpCode.CallVoid, baseConstructor, receiver, new Immediate(0));
        var fieldStore = new Instruction(3, OpCode.Move, new FieldReference(field, receiver, 16), value);
        method.ControlFlowGraph = new ISILControlFlowGraph([
            allocation,
            baseCall,
            fieldStore,
            new Instruction(4, OpCode.Return),
        ]);
        return new Fixture(method, baseConstructor, derivedConstructor, baseCall, fieldStore, value);
    }

    private static StateMachineFixture CreateStateMachineFixture(
        bool stateMachineName,
        bool duplicateConstructor)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var assembly = app.GetAssemblyByName("mscorlib")!;
        var baseType = new InjectedTypeAnalysisContext(
            assembly,
            "Fixture",
            "StateMachineBase",
            app.SystemTypes.SystemObjectType,
            TypeAttributes.Public);
        var baseConstructor = baseType.InjectMethodContext(
            ".ctor",
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Public);
        var stateMachine = new InjectedTypeAnalysisContext(
            assembly,
            "Fixture",
            stateMachineName ? "<Run>d__1" : "IteratorFixture",
            baseType,
            TypeAttributes.NestedPrivate | TypeAttributes.Sealed);
        var constructor = stateMachine.InjectMethodContext(
            ".ctor",
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Public,
            app.SystemTypes.SystemInt32Type);
        constructor.Parameters[0].Name = "<>1__state";
        if (duplicateConstructor)
        {
            var duplicate = stateMachine.InjectMethodContext(
                ".ctor",
                app.SystemTypes.SystemVoidType,
                MethodAttributes.Public,
                app.SystemTypes.SystemInt32Type);
            duplicate.Parameters[0].Name = "<>1__state";
        }

        var stateField = stateMachine.InjectFieldContext(
            "<>1__state",
            app.SystemTypes.SystemInt32Type,
            FieldAttributes.Private);
        var callerType = new InjectedTypeAnalysisContext(
            assembly,
            "Fixture",
            "Caller",
            app.SystemTypes.SystemObjectType,
            TypeAttributes.Public);
        var method = callerType.InjectMethodContext(
            "Run",
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Public | MethodAttributes.Static);
        var receiver = new LocalVariable("stateMachine", new Register(null, "X0"), stateMachine);
        var state = new LocalVariable("state", new Register(null, "W1"), app.SystemTypes.SystemInt32Type);
        var allocation = new Instruction(
            1,
            OpCode.Newobj,
            receiver,
            new RuntimeClassTypeAnalysisContext(stateMachine, assembly));
        var baseCall = new Instruction(2, OpCode.CallVoid, baseConstructor, receiver);
        var stateStore = new Instruction(
            3,
            OpCode.Move,
            new FieldReference(stateField, receiver, 16),
            state);
        var graph = new ISILControlFlowGraph([new Instruction(100, OpCode.Return)]);
        graph.EntryBlock.Successors.Clear();
        graph.ExitBlock.Predecessors.Clear();
        var allocationBlock = new Block
        {
            ID = 2,
            Instructions = [allocation],
        };
        var initializationBlock = new Block
        {
            ID = 3,
            Instructions = [baseCall, stateStore, new Instruction(4, OpCode.Return)],
        };
        graph.EntryBlock.Successors.Add(allocationBlock);
        allocationBlock.Predecessors.Add(graph.EntryBlock);
        allocationBlock.Successors.Add(initializationBlock);
        initializationBlock.Predecessors.Add(allocationBlock);
        initializationBlock.Successors.Add(graph.ExitBlock);
        graph.ExitBlock.Predecessors.Add(initializationBlock);
        graph.Blocks = [graph.EntryBlock, graph.ExitBlock, allocationBlock, initializationBlock];
        method.ControlFlowGraph = graph;
        return new StateMachineFixture(
            method,
            constructor,
            receiver,
            baseCall,
            stateStore,
            state);
    }

    private sealed record Fixture(
        MethodAnalysisContext Method,
        MethodAnalysisContext BaseConstructor,
        MethodAnalysisContext DerivedConstructor,
        Instruction BaseCall,
        Instruction FieldStore,
        LocalVariable Value);

    private sealed record StateMachineFixture(
        MethodAnalysisContext Method,
        MethodAnalysisContext Constructor,
        LocalVariable Receiver,
        Instruction BaseCall,
        Instruction StateStore,
        LocalVariable State);
}
