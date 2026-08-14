using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class ListCountRecoveryTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 精确列表布局字段读取恢复为公开Count()
    {
        var fixture = CreateFixture();

        var recovered = ListCountRecovery.Run(fixture.Method);
        var count = fixture.Instruction.Operands[1] as ListCount;

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(count, Is.Not.Null);
            Assert.That(count!.Value, Is.SameAs(fixture.Receiver));
            Assert.That(count.ListType.FullName, Is.EqualTo("System.Collections.Generic.List`1<System.String>"));
            Assert.That(fixture.Graph.Instructions.SelectMany(instruction => instruction.Operands)
                .OfType<FieldReference>().Any(field => field.Field.Name == "_size"), Is.False);
        });
    }

    [Test]
    [Category("边界值")]
    public void 接收者类型被擦除时仍由具体字段保留泛型实参()
    {
        var fixture = CreateFixture(useErasedReceiver: true);

        var recovered = ListCountRecovery.Run(fixture.Method);
        var count = fixture.Instruction.Operands[1] as ListCount;

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(count, Is.Not.Null);
            Assert.That(count!.ListType.GenericArguments.Single().FullName, Is.EqualTo("System.String"));
            Assert.That(fixture.Receiver.Type?.FullName, Is.EqualTo("System.Object"));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 相似名称业务字段不改写()
    {
        var fixture = CreateFixture(useForeignOwner: true);

        var recovered = ListCountRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Instruction.Operands[1], Is.InstanceOf<FieldReference>());
        });
    }

    [Test]
    [Category("异常输入")]
    public void 列表布局字段写入保持为控制流红门()
    {
        var fixture = CreateFixture(writeField: true);

        var recovered = ListCountRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Instruction.Operands[0], Is.InstanceOf<FieldReference>());
        });
    }

    private static Fixture CreateFixture(bool writeField = false, bool useForeignOwner = false, bool useErasedReceiver = false)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var listType = listDefinition.MakeGenericInstanceType([app.SystemTypes.SystemStringType]);
        var owner = useForeignOwner ? app.SystemTypes.SystemObjectType : listDefinition;
        var baseField = new InjectedFieldAnalysisContext(
            "_size",
            app.SystemTypes.SystemInt32Type,
            FieldAttributes.Private,
            owner,
            offset: 24);
        FieldAnalysisContext field = useForeignOwner
            ? baseField
            : new ConcreteGenericFieldAnalysisContext(baseField, listType);
        var receiverType = useErasedReceiver ? app.SystemTypes.SystemObjectType : listType;
        var receiver = new LocalVariable("items", new Register(null, "X0"), receiverType);
        var count = new LocalVariable("count", new Register(null, "W8"), app.SystemTypes.SystemInt32Type);
        var fieldReference = new FieldReference(field, receiver, 24);
        var instruction = writeField
            ? new Instruction(0, OpCode.Move, fieldReference, new Immediate(0))
            : new Instruction(0, OpCode.Move, count, fieldReference);
        var graph = new ISILControlFlowGraph([
            instruction,
            new Instruction(1, OpCode.Return, writeField ? receiver : count),
        ]);
        var method = (MethodAnalysisContext)RuntimeHelpers.GetUninitializedObject(typeof(MethodAnalysisContext));
        method.ControlFlowGraph = graph;
        return new Fixture(method, graph, receiver, instruction);
    }

    private sealed record Fixture(
        MethodAnalysisContext Method,
        ISILControlFlowGraph Graph,
        LocalVariable Receiver,
        Instruction Instruction);
}
