using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class ManagedFieldReturnRecoveryTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 字段地址与默认槽汇合后恢复为字段值返回()
    {
        var fixture = CreateFixture(reverseAddOperands: false);

        var rewritten = ManagedFieldReturnRecovery.Rewrite(
            fixture.Graph,
            fixture.StringType,
            fixture.Locals);

        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Is.EqualTo(1));
            Assert.That(fixture.FieldDefinition.OpCode, Is.EqualTo(OpCode.Move));
            Assert.That(fixture.FieldDefinition.Operands[1], Is.TypeOf<FieldReference>());
            var field = (FieldReference)fixture.FieldDefinition.Operands[1];
            Assert.That(field.Local, Is.SameAs(fixture.Receiver));
            Assert.That(field.Field.Name, Is.EqualTo("Value"));
            Assert.That(field.Field.FieldType, Is.SameAs(fixture.StringType));
            Assert.That(fixture.ReturnInstruction.Operands[0], Is.TypeOf<LocalVariable>());
            Assert.That(fixture.FallbackBlock.Instructions, Has.Count.EqualTo(2));
            Assert.That(fixture.FallbackBlock.Instructions[1].Operands[1], Is.TypeOf<MemoryOperand>());
        });
    }

    [Test]
    [Category("边界值")]
    public void 常量位于加法左侧时仍使用同一精确字段布局()
    {
        var fixture = CreateFixture(reverseAddOperands: true);

        var rewritten = ManagedFieldReturnRecovery.Rewrite(
            fixture.Graph,
            fixture.StringType,
            fixture.Locals);

        var field = (FieldReference)fixture.FieldDefinition.Operands[1];
        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Is.EqualTo(1));
            Assert.That(field.Offset, Is.EqualTo(0x18));
            Assert.That(field.Field.Name, Is.EqualTo("Value"));
            Assert.That(((LocalVariable)fixture.ReturnInstruction.Operands[0]).Type,
                Is.SameAs(fixture.StringType));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 地址载体还有额外读取时拒绝改写返回生命期()
    {
        var fixture = CreateFixture(reverseAddOperands: false);
        fixture.ReturnBlock.Instructions.Insert(
            0,
            new Instruction(
                2,
                OpCode.Move,
                Local("X9", fixture.StringType),
                fixture.Carrier));

        var rewritten = ManagedFieldReturnRecovery.Rewrite(
            fixture.Graph,
            fixture.StringType,
            fixture.Locals);

        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Is.Zero);
            Assert.That(fixture.FieldDefinition.OpCode, Is.EqualTo(OpCode.Add));
            Assert.That(fixture.ReturnInstruction.Operands[0], Is.TypeOf<MemoryOperand>());
            Assert.That(fixture.FallbackBlock.Instructions, Has.Count.EqualTo(1));
        });
    }

    private static Fixture CreateFixture(bool reverseAddOperands)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var stringType = app.SystemTypes.SystemStringType;
        var owner = app.InjectTypeIntoAllAssemblies(
            "Cpp2IL.Core.Tests",
            $"ManagedFieldReturnOwner{(reverseAddOperands ? "Reverse" : "Direct")}",
            app.SystemTypes.SystemObjectType).InjectedTypes[0];
        owner.Fields.Add(new InjectedFieldAnalysisContext(
            "Weight",
            app.SystemTypes.SystemSingleType,
            FieldAttributes.Private,
            owner,
            0x10));
        owner.Fields.Add(new InjectedFieldAnalysisContext(
            "Value",
            stringType,
            FieldAttributes.Private,
            owner,
            0x18));

        var receiver = Local("X0", owner);
        var carrier = Local("X8", null);
        var offset = new Immediate(0x18);
        var fieldDefinition = reverseAddOperands
            ? new Instruction(0, OpCode.Add, carrier, offset, receiver)
            : new Instruction(0, OpCode.Add, carrier, receiver, offset);
        var fallbackDefinition = new Instruction(
            1,
            OpCode.Move,
            carrier,
            new MemoryOperand(addend: 0x1234));
        var returnInstruction = new Instruction(
            3,
            OpCode.Return,
            new MemoryOperand(carrier));

        var graph = new ISILControlFlowGraph([]);
        graph.EntryBlock.Successors.Clear();
        graph.ExitBlock.Predecessors.Clear();
        var fieldBlock = new Block { ID = 2 };
        var fallbackBlock = new Block { ID = 3 };
        var returnBlock = new Block { ID = 4 };
        fieldBlock.Instructions.Add(fieldDefinition);
        fallbackBlock.Instructions.Add(fallbackDefinition);
        returnBlock.Instructions.Add(returnInstruction);
        graph.Blocks =
        [
            graph.EntryBlock,
            graph.ExitBlock,
            fieldBlock,
            fallbackBlock,
            returnBlock
        ];
        AddEdge(graph.EntryBlock, fieldBlock);
        AddEdge(graph.EntryBlock, fallbackBlock);
        AddEdge(fieldBlock, returnBlock);
        AddEdge(fallbackBlock, returnBlock);
        AddEdge(returnBlock, graph.ExitBlock);

        return new(
            graph,
            fallbackBlock,
            returnBlock,
            fieldDefinition,
            returnInstruction,
            receiver,
            carrier,
            stringType,
            [receiver, carrier]);
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
        Block FallbackBlock,
        Block ReturnBlock,
        Instruction FieldDefinition,
        Instruction ReturnInstruction,
        LocalVariable Receiver,
        LocalVariable Carrier,
        TypeAnalysisContext StringType,
        List<LocalVariable> Locals);
}
