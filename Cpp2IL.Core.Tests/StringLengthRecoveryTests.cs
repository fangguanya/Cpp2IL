using System.Reflection;
using System.Runtime.CompilerServices;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class StringLengthRecoveryTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 精确字符串布局字段读取恢复为公开Length()
    {
        var fixture = CreateFixture();

        var recovered = StringLengthRecovery.Run(fixture.Method);
        var stringLength = fixture.ReadInstruction.Operands[1] as StringLength;

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(stringLength, Is.Not.Null);
            Assert.That(stringLength!.Value, Is.SameAs(fixture.Receiver));
            Assert.That(fixture.Graph.Instructions.SelectMany(instruction => instruction.Operands)
                .OfType<FieldReference>().Any(field => field.Field.Name == "_stringLength"), Is.False);
        });
    }

    [Test]
    [Category("边界值")]
    public void 接收者类型被擦除时仍按精确字段恢复()
    {
        var fixture = CreateFixture(useErasedReceiver: true);

        var recovered = StringLengthRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.ReadInstruction.Operands[1], Is.InstanceOf<StringLength>());
            Assert.That(fixture.Receiver.Type?.FullName, Is.EqualTo("System.Object"));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 相似名称业务字段不改写()
    {
        var fixture = CreateFixture(useForeignOwner: true);

        var recovered = StringLengthRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.ReadInstruction.Operands[1], Is.InstanceOf<FieldReference>());
        });
    }

    [Test]
    [Category("异常输入")]
    public void 字符串布局字段写入保持原方向()
    {
        var fixture = CreateFixture(writeField: true);

        var recovered = StringLengthRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.ReadInstruction.Operands[0], Is.InstanceOf<FieldReference>());
        });
    }

    private static Fixture CreateFixture(
        bool writeField = false,
        bool useForeignOwner = false,
        bool useErasedReceiver = false)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var stringType = app.SystemTypes.SystemStringType;
        var owner = useForeignOwner ? app.SystemTypes.SystemObjectType : stringType;
        var field = new InjectedFieldAnalysisContext(
            "_stringLength",
            app.SystemTypes.SystemInt32Type,
            FieldAttributes.Private,
            owner,
            offset: 16);
        var receiverType = useErasedReceiver ? app.SystemTypes.SystemObjectType : stringType;
        var receiver = new LocalVariable("text", new Register(null, "X0"), receiverType);
        var length = new LocalVariable("length", new Register(null, "W8"), app.SystemTypes.SystemInt32Type);
        var fieldReference = new FieldReference(field, receiver, 16);
        var read = writeField
            ? new Instruction(0, OpCode.Move, fieldReference, new Immediate(0))
            : new Instruction(0, OpCode.Move, length, fieldReference);
        var graph = new ISILControlFlowGraph([
            read,
            new Instruction(1, OpCode.Return, writeField ? receiver : length),
        ]);
        var method = (MethodAnalysisContext)RuntimeHelpers.GetUninitializedObject(typeof(MethodAnalysisContext));
        method.ControlFlowGraph = graph;
        return new Fixture(method, graph, receiver, read);
    }

    private sealed record Fixture(
        MethodAnalysisContext Method,
        ISILControlFlowGraph Graph,
        LocalVariable Receiver,
        Instruction ReadInstruction);
}
