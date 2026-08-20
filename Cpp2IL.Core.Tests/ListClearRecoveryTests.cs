using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class ListClearRecoveryTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 完整清零分支恢复为公开Clear调用()
    {
        var fixture = CreateFixture();

        var recovered = ListClearRecovery.Run(fixture.Method);

        var calls = fixture.Graph.Instructions.Where(instruction => instruction.IsCall).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(calls, Has.Count.EqualTo(1));
            Assert.That(calls[0].Operands[0],
                Is.InstanceOf<MethodAnalysisContext>().And.Property("Name").EqualTo("Clear"));
            Assert.That(calls[0].Operands[1], Is.SameAs(fixture.Receiver));
            Assert.That(fixture.Graph.Blocks, Does.Not.Contain(fixture.ClearBlock));
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("边界值")]
    public void W31整数零寄存器和空操作仍闭合同一Clear语义()
    {
        var fixture = CreateFixture(useW31Zero: true, includeNops: true);

        var recovered = ListClearRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Graph.Instructions.Single(instruction => instruction.IsCall).Operands[0],
                Is.InstanceOf<MethodAnalysisContext>().And.Property("Name").EqualTo("Clear"));
            Assert.That(fixture.Graph.Blocks, Does.Not.Contain(fixture.ClearBlock));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 数组清理接收者不同时保持原控制流()
    {
        var fixture = CreateFixture(mismatchItemsReceiver: true);
        var originalBlockCount = fixture.Graph.Blocks.Count;

        var recovered = ListClearRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Graph.Blocks, Has.Count.EqualTo(originalBlockCount));
            Assert.That(fixture.Graph.Instructions.Any(instruction =>
                instruction.IsCall
                && instruction.Operands[0] is MethodAnalysisContext
                {
                    Name: "Clear",
                    DeclaringType.FullName: "System.Array"
                }), Is.True);
        });
    }

    private static Fixture CreateFixture(
        bool useW31Zero = false,
        bool includeNops = false,
        bool mismatchItemsReceiver = false)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var elementType = app.SystemTypes.SystemStringType;
        var listType = listDefinition.MakeGenericInstanceType([elementType]);
        var genericElement = listDefinition.GenericParameters.Single();
        var itemsField = new InjectedFieldAnalysisContext(
            "_items",
            genericElement.MakeSzArrayType(),
            System.Reflection.FieldAttributes.Private,
            listDefinition);
        var sizeField = new InjectedFieldAnalysisContext(
            "_size",
            app.SystemTypes.SystemInt32Type,
            System.Reflection.FieldAttributes.Private,
            listDefinition);
        var versionField = new InjectedFieldAnalysisContext(
            "_version",
            app.SystemTypes.SystemInt32Type,
            System.Reflection.FieldAttributes.Private,
            listDefinition);
        var arrayClear = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Array")!
            .Methods.Single(method =>
            method.Name == "Clear" && method.Parameters.Count == 3);

        var receiver = Local("list", listType);
        var otherReceiver = Local("otherList", listType);
        var version = Local("version", app.SystemTypes.SystemInt32Type);
        var condition = Local("condition", app.SystemTypes.SystemBooleanType);
        IOperand zero = useW31Zero
            ? new LocalVariable("zero", new Register(null, "W31"), app.SystemTypes.SystemInt32Type)
            : new Immediate(0);

        FieldReference Field(FieldAnalysisContext field, LocalVariable? local = null)
            => new(field, local ?? receiver, 0);

        var instructions = new List<Instruction>();
        if (includeNops)
            instructions.Add(new Instruction(instructions.Count, OpCode.Nop));
        instructions.Add(new Instruction(instructions.Count, OpCode.Add, version, Field(versionField), new Immediate(1)));
        instructions.Add(new Instruction(instructions.Count, OpCode.Move, Field(sizeField), zero));
        instructions.Add(new Instruction(instructions.Count, OpCode.Move, Field(versionField), version));
        instructions.Add(new Instruction(instructions.Count, OpCode.CheckLess, condition, Field(sizeField), new Immediate(1)));
        var branch = new Instruction(instructions.Count, OpCode.ConditionalJump, new Immediate(-1), condition);
        instructions.Add(branch);
        if (includeNops)
            instructions.Add(new Instruction(instructions.Count, OpCode.Nop));
        var clearCall = new Instruction(
            instructions.Count,
            OpCode.CallVoid,
            arrayClear,
            Field(itemsField, mismatchItemsReceiver ? otherReceiver : receiver),
            new Immediate(0),
            Field(sizeField));
        instructions.Add(clearCall);
        var jump = new Instruction(instructions.Count, OpCode.Jump, new Immediate(-1));
        instructions.Add(jump);
        if (includeNops)
            instructions.Add(new Instruction(instructions.Count, OpCode.Nop));
        var merge = new Instruction(instructions.Count, OpCode.Return, receiver);
        instructions.Add(merge);
        branch.SetOperand(0, merge);
        jump.SetOperand(0, merge);

        var graph = new ISILControlFlowGraph(instructions);
        var method = (MethodAnalysisContext)RuntimeHelpers.GetUninitializedObject(typeof(MethodAnalysisContext));
        method.ControlFlowGraph = graph;
        var clearBlock = graph.FindBlockByInstruction(clearCall)!;
        return new Fixture(method, graph, receiver, clearBlock);
    }

    private static LocalVariable Local(string name, TypeAnalysisContext type)
        => new(name, new Register(null, name), type);

    private static bool ContainsListImplementationMember(ISILControlFlowGraph graph)
        => graph.Instructions.SelectMany(instruction => instruction.Operands).Any(operand =>
            operand is FieldReference { Field.Name: "_items" or "_size" or "_version" }
            || operand is MethodAnalysisContext
            {
                Name: "Clear",
                DeclaringType.FullName: "System.Array"
            });

    private sealed record Fixture(
        MethodAnalysisContext Method,
        ISILControlFlowGraph Graph,
        LocalVariable Receiver,
        Block ClearBlock);
}
