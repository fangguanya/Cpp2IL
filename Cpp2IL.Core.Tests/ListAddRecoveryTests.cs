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
    [Category("边界值")]
    public void 快路径重新读取同一Items字段时仍闭合为Add()
    {
        var fixture = CreateFixture(
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType,
            useDirectItemsFieldForAddress: true);

        var recovered = ListAddRecovery.Run(fixture.Method);

        var call = fixture.Graph.Instructions.Single(instruction => instruction.IsCall);
        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(((MethodAnalysisContext)call.Operands[0]).Name, Is.EqualTo("Add"));
            Assert.That(call.Operands[1], Is.SameAs(fixture.Receiver));
            Assert.That(call.Operands[2], Is.SameAs(fixture.Value));
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("边界值")]
    public void 快慢路径含相同载体复制时保留一次复制并闭合Add()
    {
        var fixture = CreateFixture(
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType,
            includeMatchingCarrierMove: true);

        var recovered = ListAddRecovery.Run(fixture.Method);
        var carrierMoves = fixture.Graph.Instructions.Where(instruction =>
            instruction.OpCode == OpCode.Move
            && instruction.Operands.Count == 2
            && ReferenceEquals(instruction.Operands[0], fixture.Carrier)).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(carrierMoves, Has.Count.EqualTo(1));
            Assert.That(carrierMoves[0].Operands[1], Is.SameAs(fixture.Value));
            Assert.That(fixture.Graph.Instructions.Single(instruction => instruction.IsCall).Operands[0],
                Is.InstanceOf<MethodAnalysisContext>().And.Property("Name").EqualTo("Add"));
        });
    }

    [Test]
    [Category("边界值")]
    public void 慢路径含已解析运行时元数据读取时仍闭合Add()
    {
        var fixture = CreateFixture(
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType,
            includeRuntimeMetadataPrefix: true);

        var recovered = ListAddRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Graph.Instructions.Any(instruction =>
                instruction.Destination is LocalVariable { Type: RuntimeClassTypeAnalysisContext
                    or RgctxTableTypeAnalysisContext
                    or RuntimeMethodInfoAnalysisContext }), Is.False);
            Assert.That(((MethodAnalysisContext)fixture.Graph.Instructions.Single(instruction => instruction.IsCall).Operands[0]).Name,
                Is.EqualTo("Add"));
        });
    }

    [Test]
    [Category("边界值")]
    public void 方法句柄在头部及快慢路径来源不同时仍按隐藏元数据删除()
    {
        var fixture = CreateFixture(
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType,
            includeDivergentRuntimeMetadataCarrier: true);

        var recovered = ListAddRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Graph.Instructions.Any(instruction =>
                instruction.Destination is LocalVariable { Type: RuntimeMethodInfoAnalysisContext }), Is.False);
            Assert.That(((MethodAnalysisContext)fixture.Graph.Instructions.Single(instruction => instruction.IsCall).Operands[0]).Name,
                Is.EqualTo("Add"));
        });
    }

    [Test]
    [Category("边界值")]
    public void 连续添加复用状态载体并规范化索引时仍闭合Add()
    {
        var fixture = CreateCarriedStateFixture();

        var recovered = ListAddRecovery.Run(fixture.Method);

        var call = fixture.Graph.Instructions.Single(instruction => instruction.IsCall);
        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(((MethodAnalysisContext)call.Operands[0]).Name, Is.EqualTo("Add"));
            Assert.That(call.Operands[1], Is.SameAs(fixture.Receiver));
            Assert.That(call.Operands[2], Is.SameAs(fixture.Value));
            Assert.That(fixture.Graph.Blocks, Does.Not.Contain(fixture.FastBlock));
            Assert.That(fixture.Graph.Blocks, Does.Not.Contain(fixture.SlowBlock));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 连续添加索引掩码漂移时保持原控制流()
    {
        var fixture = CreateCarriedStateFixture(wrongMask: true);
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

    [Test]
    [Category("异常输入")]
    public void 连续添加状态载体来自不同集合时保持原控制流()
    {
        var fixture = CreateCarriedStateFixture(wrongSizeReceiver: true);
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

    [Test]
    [Category("异常输入")]
    public void 快慢路径载体来源不同时保持容量分支()
    {
        var fixture = CreateFixture(
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType,
            includeMatchingCarrierMove: true,
            mismatchCarrierSource: true);
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
        bool mismatchStoredValue = false,
        bool useDirectItemsFieldForAddress = false,
        bool includeMatchingCarrierMove = false,
        bool mismatchCarrierSource = false,
        bool includeRuntimeMetadataPrefix = false,
        bool includeDivergentRuntimeMetadataCarrier = false)
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
        var carrier = Local("carrier", elementType);

        FieldReference Field(FieldAnalysisContext field) => new(field, receiver, 0);
        var instructions = new List<Instruction>
        {
            new(0, OpCode.Move, items, Field(itemsField)),
            new(1, OpCode.Add, version, Field(versionField), new Immediate(1)),
            new(2, OpCode.Move, Field(versionField), version),
            new(3, OpCode.CheckGreaterOrEqualUnsigned, condition, Field(sizeField), new ArrayLength(items)),
            new(4, OpCode.ConditionalJump, new Immediate(-1), condition),
            new(5, OpCode.ShiftLeft, elementOffset, Field(sizeField), new Immediate(3)),
            new(6, OpCode.Add, elementAddress, useDirectItemsFieldForAddress ? Field(itemsField) : items, elementOffset),
            new(7, OpCode.Add, newSize, Field(sizeField), new Immediate(1)),
            new(8, OpCode.Move, Field(sizeField), newSize),
            new(9, OpCode.Move, new MemoryOperand(elementAddress, null, 0x20), mismatchStoredValue ? otherValue : value),
        };

        if (includeMatchingCarrierMove)
            instructions.Add(new Instruction(instructions.Count, OpCode.Move, carrier, mismatchCarrierSource ? otherValue : value));
        var fastJump = new Instruction(instructions.Count, OpCode.Jump, new Immediate(-1));
        instructions.Add(fastJump);

        var slowEntryIndex = instructions.Count;
        if (includeNops)
        {
            instructions.Add(new Instruction(instructions.Count, OpCode.Nop));
            instructions.Add(new Instruction(instructions.Count, OpCode.Nop));
        }

        if (includeRuntimeMetadataPrefix)
        {
            var referencedFrom = listDefinition.DeclaringAssembly;
            var runtimeClass = Local("runtimeClass", new RuntimeClassTypeAnalysisContext(listType, referencedFrom));
            var runtimeContext = Local("runtimeContext", new RgctxTableTypeAnalysisContext(listType, referencedFrom));
            var runtimeMethod = Local("runtimeMethod", new RuntimeMethodInfoAnalysisContext(addWithResize, referencedFrom));
            instructions.Add(new Instruction(
                instructions.Count,
                OpCode.Move,
                runtimeClass,
                new MemoryOperand(receiver, null, 0x20)));
            instructions.Add(new Instruction(
                instructions.Count,
                OpCode.Move,
                runtimeContext,
                new MemoryOperand(runtimeClass, null, 0xC0)));
            instructions.Add(new Instruction(
                instructions.Count,
                OpCode.Move,
                runtimeMethod,
                new MemoryOperand(runtimeContext, null, 0x70)));
        }
        var slowCallIndex = instructions.Count;
        instructions.Add(new Instruction(slowCallIndex, OpCode.CallVoid, addWithResizeTarget, receiver, value));
        var mergeEntryIndex = instructions.Count;
        if (includeNops)
            instructions.Add(new Instruction(instructions.Count, OpCode.Nop));
        var mergeIndex = instructions.Count;
        instructions.Add(new Instruction(mergeIndex, OpCode.Return, receiver));

        instructions[4].SetOperand(0, instructions[slowEntryIndex]);
        fastJump.SetOperand(0, instructions[mergeEntryIndex]);

        var graph = new ISILControlFlowGraph(instructions);
        var method = (MethodAnalysisContext)RuntimeHelpers.GetUninitializedObject(typeof(MethodAnalysisContext));
        method.ControlFlowGraph = graph;
        var slowBlock = graph.FindBlockByInstruction(instructions[slowCallIndex])!;
        var fastBlock = graph.FindBlockByInstruction(instructions[5])!;
        // Phi消除发生在CFG构建之后，实际产物会把索引为-1的边复制追加到既有调用块。
        if (includeMatchingCarrierMove)
            slowBlock.Instructions.Add(new Instruction(-1, OpCode.Move, carrier, value));
        if (includeDivergentRuntimeMetadataCarrier)
        {
            var metadataType = new RuntimeMethodInfoAnalysisContext(addWithResize, listDefinition.DeclaringAssembly);
            var metadataCarrier = Local("metadataCarrier", metadataType);
            var headBlock = graph.FindBlockByInstruction(instructions[0])!;
            headBlock.Instructions.Insert(
                1,
                new Instruction(-1, OpCode.Move, metadataCarrier, metadataType));
            fastBlock.Instructions.Insert(
                fastBlock.Instructions.Count - 1,
                new Instruction(-1, OpCode.Move, metadataCarrier, new Immediate(0)));
            slowBlock.Instructions.Insert(
                0,
                new Instruction(-1, OpCode.Move, metadataCarrier, new MemoryOperand(receiver, null, 0x70)));
        }
        return new Fixture(method, graph, receiver, value, carrier, fastBlock, slowBlock);
    }

    private static Fixture CreateCarriedStateFixture(
        bool wrongMask = false,
        bool wrongSizeReceiver = false)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var elementType = app.SystemTypes.SystemStringType;
        var listType = listDefinition.MakeGenericInstanceType([elementType]);
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
        var otherReceiver = Local("otherList", listType);
        var value = Local("value", elementType);
        var items = Local("items", elementType.MakeSzArrayType());
        var sizeState = Local("sizeState", app.SystemTypes.SystemInt32Type);
        var versionState = Local("versionState", app.SystemTypes.SystemInt32Type);
        var versionResult = Local("versionResult", app.SystemTypes.SystemInt32Type);
        var condition = Local("condition", app.SystemTypes.SystemBooleanType);
        var newSize = Local("newSize", app.SystemTypes.SystemInt32Type);
        var masked = Local("masked", app.SystemTypes.SystemIntPtrType);
        var biased = Local("biased", app.SystemTypes.SystemIntPtrType);
        var normalized = Local("normalized", app.SystemTypes.SystemIntPtrType);
        var elementOffset = Local("elementOffset", app.SystemTypes.SystemIntPtrType);
        var elementAddress = Local("elementAddress", app.SystemTypes.SystemIntPtrType);
        var unusedCarrier = Local("unusedCarrier", elementType);

        FieldReference Field(FieldAnalysisContext field, LocalVariable? owner = null)
            => new(field, owner ?? receiver, 0);
        var instructions = new List<Instruction>
        {
            new(0, OpCode.Move, sizeState, Field(sizeField, wrongSizeReceiver ? otherReceiver : receiver)),
            new(1, OpCode.Move, versionState, Field(versionField)),
            new(2, OpCode.Add, versionResult, versionState, new Immediate(1)),
            // 真实 ARM64 连续添加会把 items 读取排在版本加法之后。
            new(3, OpCode.Move, items, Field(itemsField)),
            new(4, OpCode.Move, Field(versionField), versionResult),
            new(5, OpCode.CheckGreaterOrEqualUnsigned, condition, sizeState, new ArrayLength(items)),
            new(6, OpCode.ConditionalJump, new Immediate(-1), condition),
            new(7, OpCode.Add, newSize, sizeState, new Immediate(1)),
            new(8, OpCode.And, masked, sizeState, new Immediate(wrongMask ? 0xFFFFFFFEL : 0xFFFFFFFFL)),
            new(9, OpCode.Xor, biased, masked, new Immediate(0x80000000L)),
            new(10, OpCode.Subtract, normalized, biased, new Immediate(0x80000000L)),
            new(11, OpCode.ShiftLeft, elementOffset, normalized, new Immediate(3)),
            new(12, OpCode.Add, elementAddress, items, elementOffset),
            new(13, OpCode.Move, Field(sizeField), newSize),
            new(14, OpCode.Move, new MemoryOperand(elementAddress, null, 0x20), value),
            new(15, OpCode.Jump, new Immediate(-1)),
            new(16, OpCode.CallVoid, addWithResizeTarget, receiver, value),
            new(17, OpCode.Return, receiver),
        };
        instructions[6].SetOperand(0, instructions[16]);
        instructions[15].SetOperand(0, instructions[17]);

        var graph = new ISILControlFlowGraph(instructions);
        var method = (MethodAnalysisContext)RuntimeHelpers.GetUninitializedObject(typeof(MethodAnalysisContext));
        method.ControlFlowGraph = graph;
        var slowBlock = graph.FindBlockByInstruction(instructions[16])!;
        // 慢路径刷新供下一次 Add 使用的状态载体；真实 Phi 消除同样把边复制追加到调用块。
        slowBlock.Instructions.Add(new Instruction(-1, OpCode.Move, newSize, Field(sizeField)));
        slowBlock.Instructions.Add(new Instruction(-1, OpCode.Move, versionResult, Field(versionField)));
        return new Fixture(
            method,
            graph,
            receiver,
            value,
            unusedCarrier,
            graph.FindBlockByInstruction(instructions[7])!,
            slowBlock);
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
        LocalVariable Carrier,
        Block FastBlock,
        Block SlowBlock);
}
