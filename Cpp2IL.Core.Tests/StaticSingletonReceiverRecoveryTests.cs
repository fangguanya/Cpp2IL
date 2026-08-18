using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class StaticSingletonReceiverRecoveryTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 完整TypeInfo静态单例链恢复实例字段接收者()
    {
        var fixture = CreateFixture();

        var recovered = StaticSingletonReceiverRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Call.Operands[1], Is.TypeOf<FieldReference>());
            Assert.That(((FieldReference)fixture.Call.Operands[1]).Field, Is.SameAs(fixture.ManagerField));
            Assert.That(fixture.OwnerDefinition.Operands[1], Is.TypeOf<FieldReference>());
            Assert.That(((FieldReference)fixture.OwnerDefinition.Operands[1]).Field, Is.SameAs(fixture.SingletonField));
        });
    }

    [Test]
    [Category("边界值")]
    public void 两种所有者具有相同偏移和字段类型时保持红门()
    {
        var fixture = CreateFixture(addAmbiguousOwner: true);

        var recovered = StaticSingletonReceiverRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Call.Operands[1], Is.TypeOf<MemoryOperand>());
        });
    }

    [Test]
    [Category("异常输入")]
    public void 静态表基址存在两个绝对根时禁止恢复()
    {
        var fixture = CreateFixture(addConflictingRoot: true);

        var recovered = StaticSingletonReceiverRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Call.Operands[1], Is.TypeOf<MemoryOperand>());
        });
    }

    [Test]
    [Category("基本功能")]
    public void 完整静态单例链恢复定型Move来源()
    {
        var fixture = CreateFixture(useMoveDestination: true);

        var recovered = StaticSingletonReceiverRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Call.Operands[1], Is.TypeOf<FieldReference>());
            Assert.That(((FieldReference)fixture.Call.Operands[1]).Field, Is.SameAs(fixture.ManagerField));
        });
    }

    [Test]
    [Category("边界值")]
    public void 定型Move存在两个字段所有者时保持红门()
    {
        var fixture = CreateFixture(addAmbiguousOwner: true, useMoveDestination: true);

        var recovered = StaticSingletonReceiverRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Call.Operands[1], Is.TypeOf<MemoryOperand>());
        });
    }

    [Test]
    [Category("异常输入")]
    public void 定型Move静态根冲突时禁止恢复()
    {
        var fixture = CreateFixture(addConflictingRoot: true, useMoveDestination: true);

        var recovered = StaticSingletonReceiverRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Call.Operands[1], Is.TypeOf<MemoryOperand>());
        });
    }

    private static Fixture CreateFixture(
        bool addAmbiguousOwner = false,
        bool addConflictingRoot = false,
        bool useMoveDestination = false)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var assembly = app.Assemblies[0];
        var managerType = new InjectedTypeAnalysisContext(
            assembly,
            "Cpp2IL.Core.Tests",
            "Manager",
            app.SystemTypes.SystemObjectType,
            TypeAttributes.Public | TypeAttributes.Class);
        var ownerType = new InjectedTypeAnalysisContext(
            assembly,
            "Cpp2IL.Core.Tests",
            "Owner",
            app.SystemTypes.SystemObjectType,
            TypeAttributes.Public | TypeAttributes.Class);
        var singletonField = new InjectedFieldAnalysisContext(
            "Inst",
            ownerType,
            FieldAttributes.Public | FieldAttributes.Static,
            ownerType,
            offset: 0);
        var managerField = new InjectedFieldAnalysisContext(
            "Manager",
            managerType,
            FieldAttributes.Public,
            ownerType,
            offset: 0x90);
        ownerType.Fields.Add(singletonField);
        ownerType.Fields.Add(managerField);

        var target = new InjectedMethodAnalysisContext(
            managerType,
            "Run",
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Public,
            []);
        var callerOwner = new InjectedTypeAnalysisContext(
            assembly,
            "Cpp2IL.Core.Tests",
            "CallerOwner",
            app.SystemTypes.SystemObjectType,
            TypeAttributes.Public | TypeAttributes.Class);
        var caller = new InjectedMethodAnalysisContext(
            callerOwner,
            "Caller",
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Public | MethodAttributes.Static,
            []);

        var tableBase = new LocalVariable("tableBase", new Register(null, "X28", 1));
        var runtimeClass = new LocalVariable("runtimeClass", new Register(null, "X8", 1));
        var staticStorage = new LocalVariable("staticStorage", new Register(null, "X8", 2));
        var owner = new LocalVariable("owner", new Register(null, "X8", 3));
        var typedOwner = new LocalVariable("typedOwner", new Register(null, "X9", 1), ownerType);
        var tableDefinition = new Instruction(0, OpCode.Move, tableBase, new MemoryOperand(addend: 0x1000));
        var runtimeClassDefinition = new Instruction(1, OpCode.Move, runtimeClass, new MemoryOperand(tableBase));
        var staticStorageDefinition = new Instruction(2, OpCode.Move, staticStorage, new MemoryOperand(runtimeClass, addend: 0xB8));
        var ownerDefinition = new Instruction(3, OpCode.Move, owner, new MemoryOperand(staticStorage));
        var managerResult = new LocalVariable("manager", new Register(null, "X0", 5), managerType);
        var call = useMoveDestination
            ? new Instruction(4, OpCode.Move, managerResult, new MemoryOperand(owner, addend: 0x90))
            : new Instruction(4, OpCode.CallVoid, target, new MemoryOperand(owner, addend: 0x90));
        var instructions = new System.Collections.Generic.List<Instruction>
        {
            tableDefinition,
            runtimeClassDefinition,
            staticStorageDefinition,
            ownerDefinition,
            call,
            new(5, OpCode.Return),
        };
        if (addConflictingRoot)
            instructions.Insert(1, new Instruction(0, OpCode.Move, tableBase, new MemoryOperand(addend: 0x2000)));

        caller.ControlFlowGraph = new ISILControlFlowGraph(instructions);
        caller.Locals = [tableBase, runtimeClass, staticStorage, owner, typedOwner, managerResult];
        caller.ParameterLocals = [];

        if (addAmbiguousOwner)
        {
            var ambiguousOwner = new InjectedTypeAnalysisContext(
                assembly,
                "Cpp2IL.Core.Tests",
                "AmbiguousOwner",
                app.SystemTypes.SystemObjectType,
                TypeAttributes.Public | TypeAttributes.Class);
            ambiguousOwner.Fields.Add(new InjectedFieldAnalysisContext(
                "Inst",
                ambiguousOwner,
                FieldAttributes.Public | FieldAttributes.Static,
                ambiguousOwner,
                offset: 0));
            ambiguousOwner.Fields.Add(new InjectedFieldAnalysisContext(
                "Manager",
                managerType,
                FieldAttributes.Public,
                ambiguousOwner,
                offset: 0x90));
            caller.Locals.Add(new LocalVariable("ambiguous", new Register(null, "X10", 1), ambiguousOwner));
        }

        return new Fixture(caller, call, ownerDefinition, singletonField, managerField);
    }

    private sealed record Fixture(
        MethodAnalysisContext Caller,
        Instruction Call,
        Instruction OwnerDefinition,
        FieldAnalysisContext SingletonField,
        FieldAnalysisContext ManagerField);
}
