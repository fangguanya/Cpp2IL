using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class CalleeSavedManagedReceiverRecoveryTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 唯一具体List分配恢复未定义X19接收者并重绑ToArray()
    {
        var fixture = CreateFixture();

        var recovered = CalleeSavedManagedReceiverRecovery.Run(fixture.Caller);
        var rebound = (ConcreteGenericMethodAnalysisContext)fixture.Call.Operands[0];

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Call.Operands[2], Is.SameAs(fixture.FirstCandidate));
            Assert.That(rebound.TypeGenericParameters.Single().FullName, Is.EqualTo("System.String"));
            Assert.That(fixture.Result.Type?.FullName, Is.EqualTo("System.String[]"));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 唯一泛型Enumerator生产值恢复非泛型MoveNext接收者()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var mscorlib = app.GetAssemblyByName("mscorlib")!;
        var genericEnumerator = mscorlib
            .GetTypeByFullName("System.Collections.Generic.IEnumerator`1")!
            .MakeGenericInstanceType([app.SystemTypes.SystemStringType]);
        var nonGenericEnumerator = mscorlib.GetTypeByFullName("System.Collections.IEnumerator")!;
        var moveNext = nonGenericEnumerator.Methods.Single(method => method.Name == "MoveNext");
        var owner = new InjectedTypeAnalysisContext(
            app.Assemblies[0],
            "Cpp2IL.Core.Tests",
            "EnumeratorCallerOwner",
            app.SystemTypes.SystemObjectType,
            TypeAttributes.Public | TypeAttributes.Class);
        var producerTarget = new InjectedMethodAnalysisContext(
            owner,
            "CreateEnumerator",
            genericEnumerator,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        var caller = new InjectedMethodAnalysisContext(
            owner,
            "Caller",
            app.SystemTypes.SystemBooleanType,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        var produced = new LocalVariable("produced", new Register(null, "X0", 1), genericEnumerator);
        var saved = new LocalVariable("saved", new Register(null, "X20", 2), nonGenericEnumerator);
        var result = new LocalVariable("result", new Register(null, "W0", 3), app.SystemTypes.SystemBooleanType);
        var call = new Instruction(10, OpCode.Call, moveNext, result, saved);
        caller.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Call, producerTarget, produced),
            call,
            new Instruction(11, OpCode.Return, result),
        ]);
        caller.Locals = [produced, saved, result];
        caller.ParameterLocals = [];

        var recovered = CalleeSavedManagedReceiverRecovery.Run(caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(call.Operands[2], Is.SameAs(produced));
        });
    }

    [Test]
    [Category("边界值")]
    public void 两个相容生产值存在时保持未定义接收者作为红门()
    {
        var fixture = CreateFixture(addSecondCandidate: true);

        var recovered = CalleeSavedManagedReceiverRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Call.Operands[2], Is.SameAs(fixture.OriginalReceiver));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 接收者已有显式定义时禁止覆盖真实数据流()
    {
        var fixture = CreateFixture(defineReceiver: true);

        var recovered = CalleeSavedManagedReceiverRecovery.Run(fixture.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Call.Operands[2], Is.SameAs(fixture.OriginalReceiver));
        });
    }

    private static Fixture CreateFixture(bool addSecondCandidate = false, bool defineReceiver = false)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var toArray = listDefinition.Methods.Single(method =>
            method.Name == "ToArray" && method.Parameters.Count == 0);
        var objectList = listDefinition.MakeGenericInstanceType([app.SystemTypes.SystemObjectType]);
        var stringList = listDefinition.MakeGenericInstanceType([app.SystemTypes.SystemStringType]);
        var oldTarget = new ConcreteGenericMethodAnalysisContext(
            toArray,
            [app.SystemTypes.SystemObjectType],
            []);
        var firstCandidate = new LocalVariable("first", new Register(null, "X0", 1), stringList);
        var secondCandidate = new LocalVariable("second", new Register(null, "X0", 2), stringList);
        var receiver = new LocalVariable("saved", new Register(null, "X19", 3), objectList);
        var result = new LocalVariable("result", new Register(null, "X0", 4), oldTarget.ReturnType);
        var instructions = new System.Collections.Generic.List<Instruction>
        {
            new(0, OpCode.Newobj, firstCandidate, stringList),
        };
        if (addSecondCandidate)
            instructions.Add(new Instruction(1, OpCode.Newobj, secondCandidate, stringList));
        if (defineReceiver)
            instructions.Add(new Instruction(2, OpCode.Move, receiver, firstCandidate));
        var call = new Instruction(10, OpCode.Call, oldTarget, result, receiver);
        instructions.Add(call);
        instructions.Add(new Instruction(11, OpCode.Return, result));

        var owner = new InjectedTypeAnalysisContext(
            app.Assemblies[0],
            "Cpp2IL.Core.Tests",
            "CallerOwner",
            app.SystemTypes.SystemObjectType,
            TypeAttributes.Public | TypeAttributes.Class);
        var caller = new InjectedMethodAnalysisContext(
            owner,
            "Caller",
            oldTarget.ReturnType,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        caller.ControlFlowGraph = new ISILControlFlowGraph(instructions);
        caller.Locals = [firstCandidate, secondCandidate, receiver, result];
        caller.ParameterLocals = [];

        return new Fixture(caller, call, receiver, firstCandidate, result);
    }

    private sealed record Fixture(
        MethodAnalysisContext Caller,
        Instruction Call,
        LocalVariable OriginalReceiver,
        LocalVariable FirstCandidate,
        LocalVariable Result);
}
