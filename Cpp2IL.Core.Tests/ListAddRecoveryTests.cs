using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class ListAddRecoveryTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 完整引用类型容量菱形恢复为公开Add调用()
    {
        var fixture = CreateFixture(Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType);

        var recovered = ListAddRecovery.Run(fixture.Method);

        var calls = fixture.Graph.Instructions.Where(instruction => instruction.IsCall).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(calls, Has.Count.EqualTo(1));
            Assert.That(((MethodAnalysisContext)calls[0].Operands[0]).Name, Is.EqualTo("Add"));
            Assert.That(calls[0].Operands[1], Is.SameAs(fixture.Receiver));
            Assert.That(calls[0].Operands[2], Is.SameAs(fixture.Value));
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("边界值")]
    public void 值类型元素与原生空操作仍闭合为同一Add语义()
    {
        var fixture = CreateFixture(
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemInt32Type,
            includeNops: true);

        var recovered = ListAddRecovery.Run(fixture.Method);

        var call = fixture.Graph.Instructions.Single(instruction => instruction.IsCall);
        var target = (ConcreteGenericMethodAnalysisContext)call.Operands[0];
        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(target.Name, Is.EqualTo("Add"));
            Assert.That(target.TypeGenericParameters.Single().FullName, Is.EqualTo("System.Int32"));
            Assert.That(fixture.Graph.Blocks, Does.Not.Contain(fixture.FastBlock));
            Assert.That(fixture.Graph.Blocks, Does.Not.Contain(fixture.SlowBlock));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 快速路径写入不同元素时保持原控制流()
    {
        var fixture = CreateFixture(
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType,
            mismatchStoredValue: true);
        var originalBlockCount = fixture.Graph.Blocks.Count;

        var recovered = ListAddRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Graph.Blocks, Has.Count.EqualTo(originalBlockCount));
            Assert.That(fixture.Graph.Instructions.Any(instruction =>
                instruction.IsCall
                && instruction.Operands[0] is MethodAnalysisContext { Name: "AddWithResize" }), Is.True);
        });
    }

    private static Fixture CreateFixture(
        TypeAnalysisContext elementType,
        bool includeNops = false,
        bool mismatchStoredValue = false)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var listType = listDefinition.MakeGenericInstanceType([elementType]);
        // 2019 测试库存的 mscorlib 未公开登记私有 AddWithResize；夹具注入同签名成员，
        // 公开 Add 仍从真实 List<T> 元数据读取，保证目标选择覆盖生产路径。
        var genericElement = listDefinition.GenericParameters.Single();
        var addWithResize = new InjectedMethodAnalysisContext(
            listDefinition,
            "AddWithResize",
            app.SystemTypes.SystemVoidType,
            System.Reflection.MethodAttributes.Private,
            [genericElement]);
        var addWithResizeTarget = new ConcreteGenericMethodAnalysisContext(addWithResize, [elementType], []);
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

        var receiver = Local("list", listType);
        var value = Local("value", elementType);
        var otherValue = Local("otherValue", elementType);
        var items = Local("items", elementType.MakeSzArrayType());
        var version = Local("version", app.SystemTypes.SystemInt32Type);
        var condition = Local("condition", app.SystemTypes.SystemBooleanType);
        var elementOffset = Local("elementOffset", app.SystemTypes.SystemIntPtrType);
        var elementAddress = Local("elementAddress", app.SystemTypes.SystemIntPtrType);
        var newSize = Local("newSize", app.SystemTypes.SystemInt32Type);

        FieldReference Field(FieldAnalysisContext field) => new(field, receiver, 0);
        var instructions = new List<Instruction>
        {
            new(0, OpCode.Move, items, Field(itemsField)),
            new(1, OpCode.Add, version, Field(versionField), new Immediate(1)),
            new(2, OpCode.Move, Field(versionField), version),
            new(3, OpCode.CheckGreaterOrEqualUnsigned, condition, Field(sizeField), new ArrayLength(items)),
            new(4, OpCode.ConditionalJump, new Immediate(11), condition),
            new(5, OpCode.ShiftLeft, elementOffset, Field(sizeField), new Immediate(3)),
            new(6, OpCode.Add, elementAddress, items, elementOffset),
            new(7, OpCode.Add, newSize, Field(sizeField), new Immediate(1)),
            new(8, OpCode.Move, Field(sizeField), newSize),
            new(9, OpCode.Move, new MemoryOperand(elementAddress, null, 0x20), mismatchStoredValue ? otherValue : value),
            new(10, OpCode.Jump, new Immediate(includeNops ? 14 : 12)),
        };

        if (includeNops)
        {
            instructions.Add(new Instruction(11, OpCode.Nop));
            instructions.Add(new Instruction(12, OpCode.Nop));
        }

        var slowIndex = instructions.Count;
        instructions.Add(new Instruction(slowIndex, OpCode.CallVoid, addWithResizeTarget, receiver, value));
        if (includeNops)
            instructions.Add(new Instruction(slowIndex + 1, OpCode.Nop));
        var mergeIndex = instructions.Count;
        instructions.Add(new Instruction(mergeIndex, OpCode.Return, receiver));

        foreach (var instruction in instructions)
        {
            if (instruction.OpCode is not (OpCode.Jump or OpCode.ConditionalJump))
                continue;

            instruction.SetOperand(0, instructions[(int)((Immediate)instruction.Operands[0]).Value]);
        }

        var graph = new ISILControlFlowGraph(instructions);
        var method = (MethodAnalysisContext)RuntimeHelpers.GetUninitializedObject(typeof(MethodAnalysisContext));
        method.ControlFlowGraph = graph;
        var slowBlock = graph.FindBlockByInstruction(instructions[slowIndex])!;
        var fastBlock = graph.FindBlockByInstruction(instructions[5])!;
        return new Fixture(method, graph, receiver, value, fastBlock, slowBlock);
    }

    private static LocalVariable Local(string name, TypeAnalysisContext type)
        => new(name, new Register(null, name), type);

    private static bool ContainsListImplementationMember(ISILControlFlowGraph graph)
        => graph.Instructions.SelectMany(instruction => instruction.Operands).Any(operand =>
            operand is FieldReference { Field.Name: "_items" or "_size" or "_version" }
            || operand is MethodAnalysisContext { Name: "AddWithResize" });

    private sealed record Fixture(
        MethodAnalysisContext Method,
        ISILControlFlowGraph Graph,
        LocalVariable Receiver,
        LocalVariable Value,
        Block FastBlock,
        Block SlowBlock);
}
