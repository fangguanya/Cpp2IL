using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class CachedDelegateCarrierRecoveryTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 静态委托缓存菱形把无定义调用载体接回缓存字段()
    {
        var fixture = CreateFixture();

        var recovered = CachedDelegateCarrierRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Call.Operands[1], Is.TypeOf<FieldReference>());
            Assert.That(((FieldReference)fixture.Call.Operands[1]).Field, Is.SameAs(fixture.CacheField));
        });
    }

    [Test]
    [Category("边界值")]
    public void 已有真实定义的委托调用载体保持原局部()
    {
        var fixture = CreateFixture(defineCarrier: true);

        var recovered = CachedDelegateCarrierRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Call.Operands[1], Is.SameAs(fixture.Carrier));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 空值门与写回字段不一致时保持红门()
    {
        var fixture = CreateFixture(mismatchStoreField: true);

        var recovered = CachedDelegateCarrierRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Call.Operands[1], Is.SameAs(fixture.Carrier));
        });
    }

    private static Fixture CreateFixture(bool defineCarrier = false, bool mismatchStoreField = false)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var mscorlib = app.GetAssemblyByName("mscorlib")!;
        var funcDefinition = mscorlib.GetTypeByFullName("System.Func`2")!;
        var delegateType = funcDefinition.MakeGenericInstanceType(
            [app.SystemTypes.SystemStringType, app.SystemTypes.SystemInt32Type]);
        var owner = new InjectedTypeAnalysisContext(
            app.Assemblies[0],
            "Cpp2IL.Core.Tests",
            "DelegateCacheOwner",
            app.SystemTypes.SystemObjectType,
            TypeAttributes.Public | TypeAttributes.Class);
        var method = new InjectedMethodAnalysisContext(
            owner,
            "UseCache",
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Public | MethodAttributes.Static,
            []);
        var consumer = new InjectedMethodAnalysisContext(
            owner,
            "Consume",
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Public | MethodAttributes.Static,
            [delegateType]);
        var cacheField = new InjectedFieldAnalysisContext(
            "Cached",
            delegateType,
            FieldAttributes.Private | FieldAttributes.Static,
            owner);
        var otherField = new InjectedFieldAnalysisContext(
            "OtherCached",
            delegateType,
            FieldAttributes.Private | FieldAttributes.Static,
            owner);
        var fieldOwner = new LocalVariable("staticFields", new Register(null, "X8"), owner);
        var comparedField = new FieldReference(cacheField, fieldOwner, 0x10);
        var storedField = new FieldReference(
            mismatchStoreField ? otherField : cacheField,
            fieldOwner,
            0x18);
        var condition = new LocalVariable(
            "hasCache",
            new Register(null, "W0"),
            app.SystemTypes.SystemBooleanType);
        var created = new LocalVariable("created", new Register(null, "X0", 1), delegateType);
        var carrier = new LocalVariable("carrier", new Register(null, "X23", 1), delegateType);

        var gate = NewBlock(
            0,
            new Instruction(0, OpCode.CheckNotEqual, condition, comparedField, new Immediate(0)));
        var creator = NewBlock(
            1,
            new Instruction(2, OpCode.Newobj, created, delegateType),
            new Instruction(3, OpCode.Move, storedField, created));
        var cached = NewBlock(2);
        var call = new Instruction(6, OpCode.CallVoid, consumer, carrier);
        var join = NewBlock(3, call);
        var exit = NewBlock(4);

        var jump = new Instruction(1, OpCode.ConditionalJump, cached, condition);
        gate.Instructions.Add(jump);
        creator.Instructions.Add(new Instruction(4, OpCode.Jump, join));
        cached.Instructions.Add(new Instruction(5, OpCode.Jump, join));
        if (defineCarrier)
            gate.Instructions.Insert(0, new Instruction(-1, OpCode.Move, carrier, comparedField));

        Connect(gate, creator);
        Connect(gate, cached);
        Connect(creator, join);
        Connect(cached, join);
        Connect(join, exit);

        var graph = new ISILControlFlowGraph([])
        {
            EntryBlock = gate,
            ExitBlock = exit,
            Blocks = [gate, creator, cached, join, exit]
        };
        method.ControlFlowGraph = graph;
        method.Locals = [fieldOwner, condition, created, carrier];
        method.ParameterLocals = [];

        return new Fixture(method, call, carrier, cacheField);
    }

    private static Block NewBlock(int id, params Instruction[] instructions)
        => new()
        {
            ID = id,
            Instructions = [.. instructions]
        };

    private static void Connect(Block from, Block to)
    {
        from.Successors.Add(to);
        to.Predecessors.Add(from);
    }

    private sealed record Fixture(
        MethodAnalysisContext Method,
        Instruction Call,
        LocalVariable Carrier,
        FieldAnalysisContext CacheField);
}
