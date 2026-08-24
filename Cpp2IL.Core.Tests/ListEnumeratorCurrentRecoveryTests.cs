using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class ListEnumeratorCurrentRecoveryTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 唯一MoveNext循环把重叠子槽恢复为Current字段()
    {
        var fixture = CreateFixture();
        var observed = Local("X8", fixture.StringType);
        var read = new Instruction(2, OpCode.Move, observed, fixture.CurrentStack);
        fixture.Body.Instructions.Add(read);

        var rewritten = ListEnumeratorCurrentRecovery.Rewrite(fixture.Graph);

        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Is.EqualTo(1));
            Assert.That(read.Operands[1], Is.TypeOf<FieldReference>());
            var field = (FieldReference)read.Operands[1];
            Assert.That(field.Local, Is.SameAs(fixture.Enumerator));
            Assert.That(field.Field.Name, Is.EqualTo("current"));
            Assert.That(field.Field.FieldType, Is.SameAs(fixture.StringType));
            Assert.That(observed.Type, Is.SameAs(fixture.StringType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 同一循环体多次读取Current子槽全部绑定同一Enumerator()
    {
        var fixture = CreateFixture();
        fixture.Header.Instructions[0].SetOperand(
            2,
            Local("X0", fixture.EnumeratorType));
        var first = new Instruction(2, OpCode.Move,
            Local("X8", fixture.StringType), fixture.CurrentStack);
        var second = new Instruction(3, OpCode.Move,
            Local("X21", fixture.StringType),
            Local(fixture.CurrentStack.Register.Name, fixture.StringType));
        fixture.Body.Instructions.AddRange([first, second]);

        var rewritten = ListEnumeratorCurrentRecovery.Rewrite(fixture.Graph);

        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Is.EqualTo(2));
            Assert.That(first.Operands[1], Is.TypeOf<FieldReference>());
            Assert.That(second.Operands[1], Is.TypeOf<FieldReference>());
            Assert.That(((FieldReference)first.Operands[1]).Local, Is.SameAs(fixture.Enumerator));
            Assert.That(((FieldReference)second.Operands[1]).Local, Is.SameAs(fixture.Enumerator));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 循环体主动写入重叠子槽时拒绝建立字段别名()
    {
        var fixture = CreateFixture();
        var value = Local("X9", fixture.StringType);
        var write = new Instruction(2, OpCode.Move, fixture.CurrentStack, value);
        var observed = Local("X8", fixture.StringType);
        var read = new Instruction(3, OpCode.Move, observed, fixture.CurrentStack);
        fixture.Body.Instructions.AddRange([write, read]);

        var rewritten = ListEnumeratorCurrentRecovery.Rewrite(fixture.Graph);

        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Is.Zero);
            Assert.That(read.Operands[1], Is.SameAs(fixture.CurrentStack));
        });
    }

    private static Fixture CreateFixture()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var stringType = app.SystemTypes.SystemStringType;
        var boolType = app.SystemTypes.SystemBooleanType;
        var listDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var enumeratorDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1+Enumerator")!;
        var listType = listDefinition.MakeGenericInstanceType([stringType]);
        var enumeratorType = enumeratorDefinition.MakeGenericInstanceType([stringType]);
        var getEnumeratorDefinition = listDefinition.Methods.Single(method =>
            method.Name == "GetEnumerator" && method.Parameters.Count == 0);
        var moveNextDefinition = enumeratorDefinition.Methods.Single(method =>
            method.Name == "MoveNext" && method.Parameters.Count == 0);
        var getEnumerator = new ConcreteGenericMethodAnalysisContext(
            getEnumeratorDefinition, [stringType], []);
        var moveNext = new ConcreteGenericMethodAnalysisContext(
            moveNextDefinition, [stringType], []);

        var enumerator = Local("stack_-78", enumeratorType);
        var currentStack = Local("stack_-68", stringType);
        var receiver = Local("X0", listType);
        var condition = Local("W0", boolType);

        var graph = new ISILControlFlowGraph([]);
        graph.EntryBlock.Successors.Clear();
        graph.ExitBlock.Predecessors.Clear();
        var definition = new Block { ID = 2 };
        var header = new Block { ID = 3 };
        var body = new Block { ID = 4 };
        graph.Blocks = [graph.EntryBlock, graph.ExitBlock, definition, header, body];

        definition.Instructions.Add(new Instruction(
            0, OpCode.Call, getEnumerator, enumerator, receiver));
        header.Instructions.Add(new Instruction(
            1, OpCode.Call, moveNext, condition, new AddressOf(enumerator)));
        AddEdge(graph.EntryBlock, definition);
        AddEdge(definition, header);
        AddEdge(header, body);
        AddEdge(header, graph.ExitBlock);
        AddEdge(body, header);

        return new(graph, header, body, enumerator, enumeratorType, currentStack, stringType);
    }

    private static void AddEdge(Block source, Block target)
    {
        source.Successors.Add(target);
        target.Predecessors.Add(source);
    }

    private static LocalVariable Local(string name, TypeAnalysisContext? type) =>
        new(name, new Register(null, name), type);

    private sealed record Fixture(
        ISILControlFlowGraph Graph,
        Block Header,
        Block Body,
        LocalVariable Enumerator,
        TypeAnalysisContext EnumeratorType,
        LocalVariable CurrentStack,
        TypeAnalysisContext StringType);
}
