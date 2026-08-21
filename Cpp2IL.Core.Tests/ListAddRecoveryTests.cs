using System;
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
    public void 开放AddWithResize的直接零接收者残留被删除()
    {
        var call = new Instruction(
            0,
            OpCode.CallVoid,
            CreateOpenAddWithResizeTarget(),
            new Immediate(0),
            new Immediate(7));

        var suppressed = ListAddRecovery.TrySuppressResidualOpenGenericCall(call, [call]);

        Assert.Multiple(() =>
        {
            Assert.That(suppressed, Is.True);
            Assert.That(call.OpCode, Is.EqualTo(OpCode.Nop));
            Assert.That(call.Operands, Is.Empty);
        });
    }

    [Test]
    [Category("边界值")]
    public void 唯一写零局部的地址载体残留被删除()
    {
        var receiverStorage = Local(
            "receiverStorage",
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemInt64Type);
        var defineReceiver = new Instruction(0, OpCode.Move, receiverStorage, new Immediate(0));
        var call = new Instruction(
            1,
            OpCode.CallVoid,
            CreateOpenAddWithResizeTarget(),
            new AddressOf(receiverStorage),
            new Immediate(9));

        var suppressed = ListAddRecovery.TrySuppressResidualOpenGenericCall(
            call,
            [defineReceiver, call]);

        Assert.Multiple(() =>
        {
            Assert.That(suppressed, Is.True);
            Assert.That(call.OpCode, Is.EqualTo(OpCode.Nop));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 多定义地址载体保持原调用以免删除合并语义()
    {
        var receiverStorage = Local(
            "receiverStorage",
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemInt64Type);
        var firstDefinition = new Instruction(0, OpCode.Move, receiverStorage, new Immediate(0));
        var secondDefinition = new Instruction(1, OpCode.Move, receiverStorage, new Immediate(0));
        var call = new Instruction(
            2,
            OpCode.CallVoid,
            CreateOpenAddWithResizeTarget(),
            new AddressOf(receiverStorage),
            new Immediate(11));

        var suppressed = ListAddRecovery.TrySuppressResidualOpenGenericCall(
            call,
            [firstDefinition, secondDefinition, call]);

        Assert.Multiple(() =>
        {
            Assert.That(suppressed, Is.False);
            Assert.That(call.OpCode, Is.EqualTo(OpCode.CallVoid));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 精确List接收者把共享Object扩容调用重绑定到元素类型()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var listString = listDefinition.MakeGenericInstanceType([app.SystemTypes.SystemStringType]);
        var openTarget = CreateOpenAddWithResizeTarget();
        var objectTarget = new ConcreteGenericMethodAnalysisContext(
            openTarget.BaseMethodContext,
            [app.SystemTypes.SystemObjectType],
            []);
        var receiver = Local("receiver", listString);
        var value = Local("value", app.SystemTypes.SystemStringType);
        var call = new Instruction(0, OpCode.CallVoid, objectTarget, receiver, value);
        var graph = new ISILControlFlowGraph([call, new Instruction(1, OpCode.Return)]);

        var rebound = ListAddRecovery.RebindAddWithResizeCallsFromReceivers(graph);

        Assert.Multiple(() =>
        {
            Assert.That(rebound, Is.EqualTo(1));
            var target = (ConcreteGenericMethodAnalysisContext)call.Operands[0];
            Assert.That(target.TypeGenericParameters.Single(), Is.SameAs(app.SystemTypes.SystemStringType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 已匹配接收者的扩容调用保持稳定()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var listString = listDefinition.MakeGenericInstanceType([app.SystemTypes.SystemStringType]);
        var openTarget = CreateOpenAddWithResizeTarget();
        var stringTarget = new ConcreteGenericMethodAnalysisContext(
            openTarget.BaseMethodContext,
            [app.SystemTypes.SystemStringType],
            []);
        var receiver = Local("receiver", listString);
        var value = Local("value", app.SystemTypes.SystemStringType);
        var call = new Instruction(0, OpCode.CallVoid, stringTarget, receiver, value);
        var graph = new ISILControlFlowGraph([call, new Instruction(1, OpCode.Return)]);

        var rebound = ListAddRecovery.RebindAddWithResizeCallsFromReceivers(graph);

        Assert.That(rebound, Is.Zero);
    }

    [Test]
    [Category("异常输入")]
    public void 非List接收者不驱动扩容调用重绑定()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var openTarget = CreateOpenAddWithResizeTarget();
        var objectTarget = new ConcreteGenericMethodAnalysisContext(
            openTarget.BaseMethodContext,
            [app.SystemTypes.SystemObjectType],
            []);
        var receiver = Local("receiver", app.SystemTypes.SystemStringType);
        var value = Local("value", app.SystemTypes.SystemStringType);
        var call = new Instruction(0, OpCode.CallVoid, objectTarget, receiver, value);
        var graph = new ISILControlFlowGraph([call, new Instruction(1, OpCode.Return)]);

        var rebound = ListAddRecovery.RebindAddWithResizeCallsFromReceivers(graph);

        Assert.That(rebound, Is.Zero);
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
    [Category("基本功能")]
    public void Count公开读取参与容量菱形时恢复为公开Add调用()
    {
        var fixture = CreateFixture(
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType,
            usePublicCount: true);

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
    public void 三个独立Count操作数仍按同一接收者状态闭合()
    {
        var fixture = CreateFixture(
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType,
            usePublicCount: true);

        var countOperands = fixture.Graph.Instructions
            .SelectMany(instruction => instruction.Operands)
            .OfType<ListCount>()
            .ToList();
        Assert.That(countOperands, Has.Count.EqualTo(3));
        Assert.That(countOperands.Distinct(ReferenceEqualityComparer.Instance).ToList(), Has.Count.EqualTo(3));

        var recovered = ListAddRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Graph.Instructions.Count(instruction => instruction.IsCall), Is.EqualTo(1));
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("异常输入")]
    public void Count来自其它集合时保持原容量控制流()
    {
        var fixture = CreateFixture(
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType,
            usePublicCount: true,
            publicCountWrongReceiver: true);
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
    [Category("边界值")]
    public void Count容量菱形含死亡错型方法句柄时仍恢复公开Add()
    {
        var fixture = CreateFixture(
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType,
            usePublicCount: true,
            includeMistypedRuntimeMethodCarrier: true);

        var recovered = ListAddRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(((MethodAnalysisContext)fixture.Graph.Instructions.Single(instruction => instruction.IsCall)
                .Operands[0]).Name, Is.EqualTo("Add"));
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 错型方法句柄在汇合后先读时保持原容量控制流()
    {
        var fixture = CreateFixture(
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType,
            usePublicCount: true,
            includeMistypedRuntimeMethodCarrier: true,
            readMistypedRuntimeMethodCarrierFromMerge: true);
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
    [Category("基本功能")]
    public void 快慢路径分别构造同一字段读取时恢复为公开Add调用()
    {
        var fixture = CreateFixture(
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType,
            useEquivalentFieldValues: true);

        var recovered = ListAddRecovery.Run(fixture.Method);

        var call = fixture.Graph.Instructions.Single(instruction => instruction.IsCall);
        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(((MethodAnalysisContext)call.Operands[0]).Name, Is.EqualTo("Add"));
            Assert.That(call.Operands[2], Is.InstanceOf<FieldReference>());
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("基本功能")]
    public void 快慢路径分别构造同一数组元素读取时恢复为公开Add调用()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var fixture = CreateFixture(app.SystemTypes.SystemObjectType);
        var sourceArray = Local("sourceArray", app.SystemTypes.SystemObjectType.MakeSzArrayType());
        var index = Local("sourceIndex", app.SystemTypes.SystemInt32Type);
        ReplaceElementValues(
            fixture,
            new ArrayAccess(sourceArray, index),
            new ArrayAccess(sourceArray, index));

        var recovered = ListAddRecovery.Run(fixture.Method);

        var call = fixture.Graph.Instructions.Single(instruction => instruction.IsCall);
        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(call.Operands[0], Is.InstanceOf<MethodAnalysisContext>());
            Assert.That(((MethodAnalysisContext)call.Operands[0]).Name, Is.EqualTo("Add"));
            Assert.That(call.Operands[2], Is.InstanceOf<ArrayAccess>());
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("边界值")]
    public void 数组元素读取使用两个同值立即数索引时仍恢复公开Add调用()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var fixture = CreateFixture(app.SystemTypes.SystemObjectType);
        var sourceArray = Local("sourceArray", app.SystemTypes.SystemObjectType.MakeSzArrayType());
        ReplaceElementValues(
            fixture,
            new ArrayAccess(sourceArray, new Immediate(0)),
            new ArrayAccess(sourceArray, new Immediate(0)));

        var recovered = ListAddRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Graph.Instructions.Count(instruction =>
                instruction.IsCall
                && instruction.Operands[0] is MethodAnalysisContext { Name: "Add" }), Is.EqualTo(1));
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 快慢路径数组元素读取的数组载体不同时保留原容量分支()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var fixture = CreateFixture(app.SystemTypes.SystemObjectType);
        var sourceArray = Local("sourceArray", app.SystemTypes.SystemObjectType.MakeSzArrayType());
        var otherArray = Local("otherArray", app.SystemTypes.SystemObjectType.MakeSzArrayType());
        var index = Local("sourceIndex", app.SystemTypes.SystemInt32Type);
        ReplaceElementValues(
            fixture,
            new ArrayAccess(sourceArray, index),
            new ArrayAccess(otherArray, index));
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
    [Category("边界值")]
    public void 慢路径在扩容调用后重载同一字段载体时保留重载并恢复Add()
    {
        var fixture = CreateFixture(
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType,
            useEquivalentFieldValues: true);
        AddSlowFieldCarrierRefresh(fixture, mismatchOffset: false);

        var recovered = ListAddRecovery.Run(fixture.Method);

        var calls = fixture.Graph.Instructions.Where(instruction => instruction.IsCall).ToList();
        var carrierReloads = fixture.Graph.Instructions.Where(instruction =>
            instruction is { OpCode: OpCode.Move, Operands: [var destination, FieldReference] }
            && ReferenceEquals(destination, fixture.Carrier)).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(calls, Has.Count.EqualTo(1));
            Assert.That(((MethodAnalysisContext)calls[0].Operands[0]).Name, Is.EqualTo("Add"));
            Assert.That(carrierReloads, Has.Count.EqualTo(2));
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("基本功能")]
    public void 慢路径在扩容调用后重载同一数组长度时保留重载并恢复Add()
    {
        var fixture = CreateFixture(Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType);
        var carrier = AddSlowArrayLengthCarrierRefresh(fixture, mismatchArray: false);

        var recovered = ListAddRecovery.Run(fixture.Method);

        var lengthReloads = fixture.Graph.Instructions.Where(instruction =>
            instruction is { OpCode: OpCode.Move, Operands: [var destination, ArrayLength] }
            && ReferenceEquals(destination, carrier)).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(lengthReloads, Has.Count.EqualTo(2));
            Assert.That(fixture.Graph.Instructions.Count(instruction =>
                instruction.IsCall
                && instruction.Operands[0] is MethodAnalysisContext { Name: "Add" }), Is.EqualTo(1));
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("边界值")]
    public void 数组长度的前后读取为不同对象实例时仍按同一数组载体闭合()
    {
        var fixture = CreateFixture(Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType);
        var carrier = AddSlowArrayLengthCarrierRefresh(fixture, mismatchArray: false);

        var recovered = ListAddRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Graph.Instructions.Count(instruction =>
                instruction is { OpCode: OpCode.Move, Operands: [var destination, ArrayLength] }
                && ReferenceEquals(destination, carrier)), Is.EqualTo(2));
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 慢路径重载另一数组的长度时保留原容量控制流()
    {
        var fixture = CreateFixture(Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType);
        AddSlowArrayLengthCarrierRefresh(fixture, mismatchArray: true);
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
    public void 慢路径重载字段偏移不同则保持原容量控制流()
    {
        var fixture = CreateFixture(
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType,
            useEquivalentFieldValues: true);
        AddSlowFieldCarrierRefresh(fixture, mismatchOffset: true);
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
    [Category("基本功能")]
    public void 慢路径在扩容调用后重载同一栈槽载体时恢复公开Add()
    {
        var fixture = CreateFixture(Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType);
        AddSlowMemoryCarrierRefresh(fixture, mismatchOffset: false);

        var recovered = ListAddRecovery.Run(fixture.Method);

        var calls = fixture.Graph.Instructions.Where(instruction => instruction.IsCall).ToList();
        var carrierReloads = fixture.Graph.Instructions.Where(instruction =>
            instruction is { OpCode: OpCode.Move, Operands: [var destination, MemoryOperand] }
            && ReferenceEquals(destination, fixture.Carrier)).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(calls, Has.Count.EqualTo(1));
            Assert.That(((MethodAnalysisContext)calls[0].Operands[0]).Name, Is.EqualTo("Add"));
            Assert.That(carrierReloads, Has.Count.EqualTo(2));
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("边界值")]
    public void 同一栈槽零偏移在扩容调用两侧保持同一读取身份()
    {
        var fixture = CreateFixture(Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType);
        AddSlowMemoryCarrierRefresh(fixture, mismatchOffset: false, offset: 0);

        var recovered = ListAddRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Graph.Instructions.Any(instruction =>
                instruction.IsCall
                && instruction.Operands[0] is MethodAnalysisContext { Name: "Add" }), Is.True);
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 慢路径重载栈槽偏移不同则保持原容量控制流()
    {
        var fixture = CreateFixture(Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType);
        AddSlowMemoryCarrierRefresh(fixture, mismatchOffset: true);
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

    [TestCase("字段")]
    [TestCase("接收者")]
    [TestCase("偏移")]
    [Category("异常输入")]
    public void 快慢路径字段身份任一不同则保持原容量控制流(string mismatch)
    {
        var fixture = CreateFixture(
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType,
            useEquivalentFieldValues: true,
            mismatchValueField: mismatch == "字段",
            mismatchValueFieldOwner: mismatch == "接收者",
            mismatchValueFieldOffset: mismatch == "偏移");
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
    [Category("基本功能")]
    public void 快慢路径独立整数常量值相同时恢复为公开Add()
    {
        var fixture = CreateFixture(Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemInt32Type);
        SetImmediateValues(fixture, fastValue: 87, slowValue: 87);

        var recovered = ListAddRecovery.Run(fixture.Method);

        var call = fixture.Graph.Instructions.Single(instruction => instruction.IsCall);
        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(((MethodAnalysisContext)call.Operands[0]).Name, Is.EqualTo("Add"));
            Assert.That(call.Operands[2], Is.EqualTo(new Immediate(87)));
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("边界值")]
    public void 快慢路径相同负整数常量仍按精确数值恢复()
    {
        var fixture = CreateFixture(Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemInt32Type);
        SetImmediateValues(fixture, fastValue: -1, slowValue: -1);

        var recovered = ListAddRecovery.Run(fixture.Method);

        var call = fixture.Graph.Instructions.Single(instruction => instruction.IsCall);
        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(call.Operands[2], Is.EqualTo(new Immediate(-1)));
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 快慢路径整数常量值不同时保留原容量控制流()
    {
        var fixture = CreateFixture(Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemInt32Type);
        SetImmediateValues(fixture, fastValue: 87, slowValue: 88);
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
    [Category("基本功能")]
    public void Single快路径半字拼接与慢路径只读常量统一为公开Add()
    {
        var fixture = CreateFixture(Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemSingleType);
        var configured = ConfigureScalarFloatingConstant(fixture, includeMoveCarrier: false);

        var recovered = ListAddRecovery.Run(fixture.Method);

        var publicCall = fixture.Graph.Instructions.Single(instruction => instruction.IsCall);
        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(((MethodAnalysisContext)publicCall.Operands[0]).Name, Is.EqualTo("Add"));
            Assert.That(publicCall.Operands[2], Is.InstanceOf<FloatLiteral>());
            Assert.That(
                BitConverter.SingleToInt32Bits(((FloatLiteral)publicCall.Operands[2]).Value),
                Is.EqualTo(unchecked((int)configured.Bits)));
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("边界值")]
    public void Single位模式经过唯一Move载体时仍精确恢复()
    {
        var fixture = CreateFixture(Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemSingleType);
        var configured = ConfigureScalarFloatingConstant(fixture, includeMoveCarrier: true);

        var recovered = ListAddRecovery.Run(fixture.Method);

        var publicValue = (FloatLiteral)fixture.Graph.Instructions
            .Single(instruction => instruction.IsCall)
            .Operands[2];
        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(
                BitConverter.SingleToInt32Bits(publicValue.Value),
                Is.EqualTo(unchecked((int)configured.Bits)));
            Assert.That(fixture.Graph.Instructions.Any(instruction =>
                ReferenceEquals(instruction.Destination, configured.Result)), Is.False);
        });
    }

    [Test]
    [Category("异常输入")]
    public void Single快路径位模式与慢路径常量不同时保留原容量控制流()
    {
        var fixture = CreateFixture(Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemSingleType);
        ConfigureScalarFloatingConstant(fixture, includeMoveCarrier: false, fastBitsXor: 0x00010000u);
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
    public void Single快路径局部常量存在多定义时保留原容量控制流()
    {
        var fixture = CreateFixture(Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemSingleType);
        var configured = ConfigureScalarFloatingConstant(fixture, includeMoveCarrier: false);
        var storeIndex = fixture.FastBlock.Instructions.FindIndex(instruction =>
            instruction is { OpCode: OpCode.Move, Operands: [MemoryOperand, _] });
        fixture.FastBlock.Instructions.Insert(
            storeIndex,
            new Instruction(-1, OpCode.Move, configured.Result, new Immediate(configured.Bits)));
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
    [Category("基本功能")]
    public void Hfa快速路径常量与慢路径分量统一为公开Add值()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var vector = CreateHfaValueType("ListAddVector2");
        var fixture = CreateFixture(vector);
        var constantAddress = app.Binary.GetVirtualAddressOfPrimaryExecutableSection();
        Assert.That(app.Binary.TryMapVirtualAddressToRaw(constantAddress, out _), Is.True);

        var store = fixture.Graph.Instructions.Single(instruction =>
            instruction is { OpCode: OpCode.Move, Operands: [MemoryOperand, _] });
        store.SetOperand(1, new MemoryOperand(addend: unchecked((long)constantAddress)));
        var slowCall = fixture.Graph.Instructions.Single(instruction =>
            instruction.IsCall
            && instruction.Operands[0] is MethodAnalysisContext { Name: "AddWithResize" });
        slowCall.SetOperand(
            2,
            new HomogeneousFloatingAggregateArgument(
                vector,
                [
                    Local("x", app.SystemTypes.SystemSingleType, "V0"),
                    Local("y", app.SystemTypes.SystemSingleType, "V1"),
                ]));

        var recovered = ListAddRecovery.Run(fixture.Method);

        var publicCall = fixture.Graph.Instructions.Single(instruction => instruction.IsCall);
        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(((MethodAnalysisContext)publicCall.Operands[0]).Name, Is.EqualTo("Add"));
            Assert.That(publicCall.Operands[2], Is.InstanceOf<HomogeneousFloatingAggregateArgument>());
            var publicValue = (HomogeneousFloatingAggregateArgument)publicCall.Operands[2];
            Assert.That(publicValue.Components, Has.All.InstanceOf<FloatLiteral>());
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("异常输入")]
    public void Hfa快速路径常量地址无效时保持原容量控制流()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var vector = CreateHfaValueType("ListAddInvalidVector2");
        var fixture = CreateFixture(vector);
        var originalBlockCount = fixture.Graph.Blocks.Count;
        var store = fixture.Graph.Instructions.Single(instruction =>
            instruction is { OpCode: OpCode.Move, Operands: [MemoryOperand, _] });
        store.SetOperand(1, new MemoryOperand(addend: long.MaxValue));
        var slowCall = fixture.Graph.Instructions.Single(instruction =>
            instruction.IsCall
            && instruction.Operands[0] is MethodAnalysisContext { Name: "AddWithResize" });
        slowCall.SetOperand(
            2,
            new HomogeneousFloatingAggregateArgument(
                vector,
                [
                    Local("x", app.SystemTypes.SystemSingleType, "V0"),
                    Local("y", app.SystemTypes.SystemSingleType, "V1"),
                ]));

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
    [Category("基本功能")]
    public void Hfa慢路径已定义位重解释分量与快路径打包常量统一为公开Add()
    {
        var vector = CreateHfaValueType("ListAddDefinedVector2");
        var fixture = CreateFixture(vector);
        ConfigureDefinedHfaConstant(fixture, vector, includeMoveCarrier: false);

        var recovered = ListAddRecovery.Run(fixture.Method);

        var publicCall = fixture.Graph.Instructions.Single(instruction => instruction.IsCall);
        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(((MethodAnalysisContext)publicCall.Operands[0]).Name, Is.EqualTo("Add"));
            Assert.That(publicCall.Operands[2], Is.InstanceOf<HomogeneousFloatingAggregateArgument>());
            Assert.That(
                ((HomogeneousFloatingAggregateArgument)publicCall.Operands[2]).Components,
                Has.All.InstanceOf<FloatLiteral>());
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("边界值")]
    public void Hfa位重解释分量经过唯一Move载体时仍逐位闭合()
    {
        var vector = CreateHfaValueType("ListAddDefinedCarrierVector2");
        var fixture = CreateFixture(vector);
        ConfigureDefinedHfaConstant(fixture, vector, includeMoveCarrier: true);

        var recovered = ListAddRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Graph.Instructions.Count(instruction =>
                instruction.IsCall
                && instruction.Operands[0] is MethodAnalysisContext { Name: "Add" }), Is.EqualTo(1));
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("异常输入")]
    public void Hfa已定义分量有一项位模式不同时保留原容量控制流()
    {
        var vector = CreateHfaValueType("ListAddMismatchedDefinedVector2");
        var fixture = CreateFixture(vector);
        var originalBlockCount = fixture.Graph.Blocks.Count;
        ConfigureDefinedHfaConstant(
            fixture,
            vector,
            includeMoveCarrier: false,
            secondBitsXor: 1u);

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
    public void 慢路径直接返回而快路径跳到共享返回时仍闭合Add()
    {
        var fixture = CreateFixture(
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType,
            slowPathReturnsDirectly: true);

        var recovered = ListAddRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Graph.Instructions.Count(instruction =>
                instruction.IsCall
                && instruction.Operands[0] is MethodAnalysisContext { Name: "Add" }),
                Is.EqualTo(1));
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
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
    [Category("基本功能")]
    public void 两个连续容量菱形按状态载体逐项闭合为公开Add()
    {
        var fixture = CreateChainedFixture(2);

        var recovered = ListAddRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(2));
            Assert.That(fixture.Graph.Instructions.Count(instruction =>
                instruction.IsCall
                && instruction.Operands[0] is MethodAnalysisContext { Name: "Add" }), Is.EqualTo(2));
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("边界值")]
    public void 三个连续容量菱形的末项不保留未使用状态刷新()
    {
        var fixture = CreateChainedFixture(3);

        var recovered = ListAddRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(3));
            Assert.That(fixture.Graph.Instructions.Count(instruction =>
                instruction.IsCall
                && instruction.Operands[0] is MethodAnalysisContext { Name: "Add" }), Is.EqualTo(3));
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 连续容量菱形的后项值不一致时保留其控制流和已定义状态载体()
    {
        var fixture = CreateChainedFixture(2, mismatchStoredValueAt: 1);

        var recovered = ListAddRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Graph.Instructions.Count(instruction =>
                instruction.IsCall
                && instruction.Operands[0] is MethodAnalysisContext { Name: "Add" }), Is.EqualTo(1));
            Assert.That(fixture.Graph.Instructions.Count(instruction =>
                instruction.IsCall
                && instruction.Operands[0] is MethodAnalysisContext { Name: "AddWithResize" }), Is.EqualTo(1));
            Assert.That(fixture.Graph.Instructions.Any(instruction =>
                instruction is { OpCode: OpCode.Move, Operands: [var destination, FieldReference] }
                && ReferenceEquals(destination, fixture.SizeStates[0])), Is.True);
            Assert.That(fixture.Graph.Instructions.Any(instruction =>
                instruction is { OpCode: OpCode.Move, Operands: [var destination, FieldReference] }
                && ReferenceEquals(destination, fixture.VersionStates[0])), Is.True);
        });
    }

    [Test]
    [Category("基本功能")]
    public void 两个连续整数追加的下一版本预更新按项闭合()
    {
        var fixture = CreateTailAdvancedChainedFixture(2);

        var recovered = ListAddRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(2));
            Assert.That(fixture.Graph.Instructions.Count(instruction =>
                instruction.IsCall
                && instruction.Operands[0] is MethodAnalysisContext { Name: "Add" }), Is.EqualTo(2));
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("边界值")]
    public void 快路径复用头部旧版本加二时与慢路径加一闭合()
    {
        var fixture = CreateTailAdvancedChainedFixture(2, fusedFastVersionAdvanceAt: 0);

        var recovered = ListAddRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(2));
            Assert.That(fixture.Graph.Instructions.Count(instruction =>
                instruction.IsCall
                && instruction.Operands[0] is MethodAnalysisContext { Name: "Add" }), Is.EqualTo(2));
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("边界值")]
    public void 快路径从头部版本结果加一时与慢路径字段加一闭合()
    {
        var fixture = CreateTailAdvancedChainedFixture(2, carriedFastVersionAdvanceAt: 0);

        var recovered = ListAddRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(2));
            Assert.That(fixture.Graph.Instructions.Count(instruction =>
                instruction.IsCall
                && instruction.Operands[0] is MethodAnalysisContext { Name: "Add" }), Is.EqualTo(2));
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("基本功能")]
    public void 慢边等价Items字段上下文仍保留重载并闭合连续追加()
    {
        var fixture = CreateTailAdvancedChainedFixture(2, distinctSlowItemsField: true);

        var recovered = ListAddRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(2));
            Assert.That(fixture.Graph.Instructions.Count(instruction =>
                instruction.IsCall
                && instruction.Operands[0] is MethodAnalysisContext { Name: "Add" }), Is.EqualTo(2));
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("边界值")]
    public void 三个连续整数追加的终项不残留版本预更新()
    {
        var fixture = CreateTailAdvancedChainedFixture(3);

        var recovered = ListAddRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(3));
            Assert.That(fixture.Graph.Instructions.Count(instruction =>
                instruction.IsCall
                && instruction.Operands[0] is MethodAnalysisContext { Name: "Add" }), Is.EqualTo(3));
            Assert.That(fixture.Graph.Instructions.Any(instruction =>
                instruction is { OpCode: OpCode.Add, Operands: [_, FieldReference { Field.Name: "_version" }, Immediate { Value: 1 }] }), Is.False);
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 连续整数追加的快慢版本增量不同时保留原控制流()
    {
        var fixture = CreateTailAdvancedChainedFixture(2, mismatchFastVersionAdvanceAt: 0);
        var originalBlockCount = fixture.Graph.Blocks.Count;

        var recovered = ListAddRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Graph.Blocks, Has.Count.EqualTo(originalBlockCount));
            Assert.That(fixture.Graph.Instructions.Count(instruction =>
                instruction.IsCall
                && instruction.Operands[0] is MethodAnalysisContext { Name: "AddWithResize" }), Is.EqualTo(2));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 两个接收者别名容量分支的共享快速尾按项恢复为公开Add()
    {
        var fixture = CreateSharedFastTailFixture([3, 7]);

        var recovered = ListAddRecovery.Run(fixture.Method);

        var allInstructions = fixture.Graph.Blocks.SelectMany(block => block.Instructions).ToList();
        var publicCalls = allInstructions.Where(instruction =>
            instruction.IsCall
            && instruction.Operands[0] is MethodAnalysisContext { Name: "Add" }).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(2));
            Assert.That(publicCalls, Has.Count.EqualTo(2));
            Assert.That(publicCalls.Select(call => ((Immediate)call.Operands[2]).Value),
                Is.EqualTo(new long[] { 3, 7 }));
            Assert.That(fixture.Graph.Blocks, Does.Not.Contain(fixture.SharedFastTail));
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("边界值")]
    public void 三个共享快速尾整数追加保留负值零值与最大值()
    {
        var fixture = CreateSharedFastTailFixture(
            [-1, 0, int.MaxValue],
            omitExplicitStagingJumpAt: 2);

        var recovered = ListAddRecovery.Run(fixture.Method);

        var values = fixture.Graph.Blocks.SelectMany(block => block.Instructions).Where(instruction =>
                instruction.IsCall
                && instruction.Operands[0] is MethodAnalysisContext { Name: "Add" })
            .Select(instruction => ((Immediate)instruction.Operands[2]).Value)
            .ToList();
        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(3));
            Assert.That(values, Is.EquivalentTo(new long[] { -1, 0, int.MaxValue }));
            Assert.That(fixture.Graph.Blocks, Does.Not.Contain(fixture.SharedFastTail));
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 共享快速尾某项快慢值不同时仅保留该项原容量控制流()
    {
        var fixture = CreateSharedFastTailFixture([3, 7], mismatchFastValueAt: 1);

        var recovered = ListAddRecovery.Run(fixture.Method);
        var allInstructions = fixture.Graph.Blocks.SelectMany(block => block.Instructions).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(allInstructions.Count(instruction =>
                instruction.IsCall
                && instruction.Operands[0] is MethodAnalysisContext { Name: "Add" }), Is.EqualTo(1));
            Assert.That(allInstructions.Count(instruction =>
                instruction.IsCall
                && instruction.Operands[0] is MethodAnalysisContext { Name: "AddWithResize" }), Is.EqualTo(1));
            Assert.That(fixture.Graph.Blocks, Does.Contain(fixture.SharedFastTail));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 两个分支共享快尾与慢调用时逐项恢复公开Add()
    {
        var fixture = CreateSharedFastAndSlowTailFixture([11, 29]);

        var recovered = ListAddRecovery.Run(fixture.Method);
        var calls = fixture.Graph.Instructions.Where(instruction => instruction.IsCall).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(2));
            Assert.That(calls.Count(instruction =>
                instruction.Operands[0] is MethodAnalysisContext { Name: "Add" }), Is.EqualTo(2));
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("边界值")]
    public void 共享快慢尾含隐式快边落入时仍逐项恢复()
    {
        var fixture = CreateSharedFastAndSlowTailFixture([int.MinValue, int.MaxValue], omitFastJumpAt: 1);

        var recovered = ListAddRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(2));
            Assert.That(fixture.Graph.Instructions.Count(instruction =>
                    instruction.IsCall
                    && instruction.Operands[0] is MethodAnalysisContext { Name: "Add" }), Is.EqualTo(2));
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 共享快慢尾某分支值不一致时只恢复已证明分支()
    {
        var fixture = CreateSharedFastAndSlowTailFixture([11, 29], mismatchFastValueAt: 1);

        var recovered = ListAddRecovery.Run(fixture.Method);
        var calls = fixture.Graph.Instructions.Where(instruction => instruction.IsCall).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(calls.Count(instruction =>
                instruction.Operands[0] is MethodAnalysisContext { Name: "Add" }), Is.EqualTo(1));
            Assert.That(calls.Count(instruction =>
                instruction.Operands[0] is MethodAnalysisContext { Name: "AddWithResize" }), Is.EqualTo(1));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 集合状态更新前夹有业务赋值时保留赋值并闭合Add()
    {
        var fixture = CreateInterleavedHeadFixture(touchesReceiver: false);

        var recovered = ListAddRecovery.Run(fixture.Method);

        var instructions = fixture.Graph.Instructions.ToList();
        var carrierMoveIndex = instructions.FindIndex(instruction =>
            instruction is { OpCode: OpCode.Move, Operands: [var destination, var source] }
            && ReferenceEquals(destination, fixture.Carrier)
            && ReferenceEquals(source, fixture.Value));
        var publicAddIndex = instructions.FindIndex(instruction =>
            instruction.IsCall
            && instruction.Operands[0] is MethodAnalysisContext { Name: "Add" });
        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(carrierMoveIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(publicAddIndex, Is.GreaterThan(carrierMoveIndex));
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 夹入指令读取集合接收者时保持原容量控制流()
    {
        var fixture = CreateInterleavedHeadFixture(touchesReceiver: true);
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

    [Test]
    [Category("基本功能")]
    public void 慢边显式跳向唯一汇合块时恢复公开Add()
    {
        var fixture = CreateFixture(
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType,
            slowPathExplicitJump: true);

        var recovered = ListAddRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Graph.Instructions.Count(instruction =>
                instruction.IsCall
                && instruction.Operands[0] is MethodAnalysisContext { Name: "Add" }), Is.EqualTo(1));
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("边界值")]
    public void 严格Items空守卫Count并行局部与共享快尾按两种分支方向恢复()
    {
        var fixture = CreateStandardStagedSharedFastTailFixture(useWrongThrowType: false);

        var recovered = ListAddRecovery.Run(fixture.Method);
        var instructions = fixture.Graph.Blocks.SelectMany(block => block.Instructions).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(2));
            Assert.That(instructions.Count(instruction =>
                instruction.IsCall
                && instruction.Operands[0] is MethodAnalysisContext { Name: "Add" }), Is.EqualTo(2));
            Assert.That(fixture.Graph.Blocks, Does.Not.Contain(fixture.SharedFastTail));
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("基本功能")]
    public void 共享快尾的纯跳转空边中继按原始多前驱身份逐项恢复()
    {
        var fixture = CreateStandardStagedSharedFastTailFixture(
            useWrongThrowType: false,
            includeEdgeCarrier: false);

        var recovered = ListAddRecovery.Run(fixture.Method);
        var instructions = fixture.Graph.Blocks.SelectMany(block => block.Instructions).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(2));
            Assert.That(instructions.Count(instruction =>
                instruction.IsCall
                && instruction.Operands[0] is MethodAnalysisContext { Name: "Add" }), Is.EqualTo(2));
            Assert.That(fixture.Graph.Blocks, Does.Not.Contain(fixture.SharedFastTail));
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("边界值")]
    public void 三个空边中继在共享快尾降为单前驱时仍全部恢复()
    {
        var fixture = CreateStandardStagedSharedFastTailFixture(
            useWrongThrowType: false,
            includeEdgeCarrier: false,
            branchCount: 3);

        var recovered = ListAddRecovery.Run(fixture.Method);
        var instructions = fixture.Graph.Blocks.SelectMany(block => block.Instructions).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(3));
            Assert.That(instructions.Count(instruction =>
                instruction.IsCall
                && instruction.Operands[0] is MethodAnalysisContext { Name: "Add" }), Is.EqualTo(3));
            Assert.That(fixture.Graph.Blocks, Does.Not.Contain(fixture.SharedFastTail));
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("边界值")]
    public void Typeof运行时类载体死亡时仍恢复公开Add()
    {
        var fixture = CreateFixture(
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType,
            includeTypeofRuntimeClassCarrier: true);

        var recovered = ListAddRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Graph.Instructions.Any(instruction =>
                instruction.Destination is LocalVariable { Type: RuntimeClassTypeAnalysisContext }), Is.False);
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("异常输入")]
    public void Items空守卫抛出非空引用异常时保留共享快尾()
    {
        var fixture = CreateStandardStagedSharedFastTailFixture(useWrongThrowType: true);
        var originalBlockCount = fixture.Graph.Blocks.Count;

        var recovered = ListAddRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Graph.Blocks, Has.Count.EqualTo(originalBlockCount));
            Assert.That(fixture.Graph.Blocks, Does.Contain(fixture.SharedFastTail));
            Assert.That(fixture.Graph.Blocks.SelectMany(block => block.Instructions).Count(instruction =>
                instruction.IsCall
                && instruction.Operands[0] is MethodAnalysisContext { Name: "AddWithResize" }), Is.EqualTo(2));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 空边中继共享快尾的快慢元素不一致时保持原图()
    {
        var fixture = CreateStandardStagedSharedFastTailFixture(
            useWrongThrowType: false,
            includeEdgeCarrier: false,
            mismatchSharedFastValue: true);
        var originalBlockCount = fixture.Graph.Blocks.Count;

        var recovered = ListAddRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Graph.Blocks, Has.Count.EqualTo(originalBlockCount));
            Assert.That(fixture.Graph.Blocks, Does.Contain(fixture.SharedFastTail));
            Assert.That(fixture.Graph.Blocks.SelectMany(block => block.Instructions).Count(instruction =>
                instruction.IsCall
                && instruction.Operands[0] is MethodAnalysisContext { Name: "AddWithResize" }), Is.EqualTo(2));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 慢边显式跳转与图后继不一致时保留原容量分支()
    {
        var fixture = CreateFixture(
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType,
            slowPathExplicitJump: true);
        var slowJump = fixture.SlowBlock.Instructions.Single(instruction => instruction.OpCode == OpCode.Jump);
        slowJump.SetOperand(0, fixture.Graph.EntryBlock);
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
    [Category("基本功能")]
    public void 已归一化数组元素写入恢复为公开Add调用()
    {
        var fixture = CreateFixture(
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType,
            useDirectArrayAccess: true);

        var earlyRecovered = ListAddRecovery.Run(fixture.Method);
        var recovered = ListAddRecovery.RunCompactArrayAccess(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(earlyRecovered, Is.Zero);
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Graph.Instructions.Count(instruction =>
                instruction.IsCall
                && instruction.Operands[0] is MethodAnalysisContext { Name: "Add" }), Is.EqualTo(1));
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("基本功能")]
    public void 已折叠数组访问保留UInt32索引归一化链时恢复公开Add调用()
    {
        var fixture = CreateFixture(
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType,
            useDirectArrayAccess: true,
            useNormalizedDirectArrayIndex: true);

        var earlyRecovered = ListAddRecovery.Run(fixture.Method);
        var recovered = ListAddRecovery.RunCompactArrayAccess(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(earlyRecovered, Is.Zero);
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Graph.Instructions.Count(instruction =>
                instruction.IsCall
                && instruction.Operands[0] is MethodAnalysisContext { Name: "Add" }), Is.EqualTo(1));
            Assert.That(fixture.Graph.Instructions.Any(instruction =>
                instruction.OpCode is OpCode.And or OpCode.Xor or OpCode.Subtract), Is.False);
        });
    }

    [Test]
    [Category("边界值")]
    public void 已折叠数组访问可从公开Count闭合UInt32索引归一化链()
    {
        var fixture = CreateFixture(
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType,
            usePublicCount: true,
            useDirectArrayAccess: true,
            useNormalizedDirectArrayIndex: true);

        var earlyRecovered = ListAddRecovery.Run(fixture.Method);
        var recovered = ListAddRecovery.RunCompactArrayAccess(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(earlyRecovered, Is.Zero);
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Graph.Instructions.Any(instruction =>
                instruction.IsCall
                && instruction.Operands[0] is MethodAnalysisContext { Name: "AddWithResize" }), Is.False);
        });
    }

    [TestCase(true, false, false)]
    [TestCase(false, true, false)]
    [TestCase(false, false, true)]
    [Category("异常输入")]
    public void 已折叠数组访问的UInt32索引证据不唯一或不等价时保留原控制流(
        bool wrongMask,
        bool wrongSource,
        bool duplicateDefinition)
    {
        var fixture = CreateFixture(
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType,
            useDirectArrayAccess: true,
            useNormalizedDirectArrayIndex: true,
            normalizedDirectArrayWrongMask: wrongMask,
            normalizedDirectArrayWrongSource: wrongSource,
            normalizedDirectArrayDuplicateDefinition: duplicateDefinition);
        var originalBlockCount = fixture.Graph.Blocks.Count;

        var earlyRecovered = ListAddRecovery.Run(fixture.Method);
        var recovered = ListAddRecovery.RunCompactArrayAccess(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(earlyRecovered, Is.Zero);
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Graph.Blocks, Has.Count.EqualTo(originalBlockCount));
            Assert.That(fixture.Graph.Instructions.Any(instruction =>
                instruction.IsCall
                && instruction.Operands[0] is MethodAnalysisContext { Name: "AddWithResize" }), Is.True);
        });
    }

    [Test]
    [Category("边界值")]
    public void 已归一化数组写入夹带等价载体时保留载体并恢复Add()
    {
        var fixture = CreateFixture(
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType,
            includeMatchingCarrierMove: true,
            useDirectArrayAccess: true);

        var earlyRecovered = ListAddRecovery.Run(fixture.Method);
        var recovered = ListAddRecovery.RunCompactArrayAccess(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(earlyRecovered, Is.Zero);
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Graph.Instructions.Any(instruction =>
                instruction is { OpCode: OpCode.Move, Operands: [var destination, _] }
                && ReferenceEquals(destination, fixture.Carrier)), Is.True);
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("边界值")]
    public void 数组写入仍保留唯一缩放地址证据时保持原恢复能力()
    {
        var fixture = CreateFixture(
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType,
            useDirectArrayAccess: true,
            retainDirectArrayAddressEvidence: true);

        var earlyRecovered = ListAddRecovery.Run(fixture.Method);
        var recovered = ListAddRecovery.RunCompactArrayAccess(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(earlyRecovered, Is.EqualTo(1));
            Assert.That(recovered, Is.Zero);
            Assert.That(fixture.Graph.Instructions.Count(instruction =>
                instruction.IsCall
                && instruction.Operands[0] is MethodAnalysisContext { Name: "Add" }), Is.EqualTo(1));
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("边界值")]
    public void 连续数组追加先恢复保留证据项再恢复紧凑项()
    {
        var fixture = CreateChainedFixture(
            addCount: 2,
            retainedArrayAccessAt: 0,
            compactArrayAccessAt: 1);

        var earlyRecovered = ListAddRecovery.Run(fixture.Method);
        var recovered = ListAddRecovery.RunCompactArrayAccess(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(earlyRecovered, Is.EqualTo(1));
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Graph.Instructions.Count(instruction =>
                instruction.IsCall
                && instruction.Operands[0] is MethodAnalysisContext { Name: "Add" }), Is.EqualTo(2));
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [Test]
    [Category("边界值")]
    public void 版本回写后的独立载体在紧凑追加中保留()
    {
        var fixture = CreateFixture(
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType,
            useDirectArrayAccess: true,
            includeHeadBusinessAfterVersionWrite: true);

        var earlyRecovered = ListAddRecovery.Run(fixture.Method);
        var recovered = ListAddRecovery.RunCompactArrayAccess(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(earlyRecovered, Is.Zero);
            Assert.That(recovered, Is.EqualTo(1));
            Assert.That(fixture.Graph.Instructions.Any(instruction =>
                instruction is { OpCode: OpCode.Move, Operands: [LocalVariable { Name: "headCarrier" }, Immediate { Value: 7 }] }), Is.True);
            Assert.That(ContainsListImplementationMember(fixture.Graph), Is.False);
        });
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    [Category("异常输入")]
    public void 已归一化数组写入的数组或索引不匹配时保留原控制流(
        bool wrongItems,
        bool wrongIndex)
    {
        var fixture = CreateFixture(
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType,
            useDirectArrayAccess: true,
            directArrayWrongItems: wrongItems,
            directArrayWrongIndex: wrongIndex);
        var originalBlockCount = fixture.Graph.Blocks.Count;

        var earlyRecovered = ListAddRecovery.Run(fixture.Method);
        var recovered = ListAddRecovery.RunCompactArrayAccess(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(earlyRecovered, Is.Zero);
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
        bool includeDivergentRuntimeMetadataCarrier = false,
        bool slowPathReturnsDirectly = false,
        bool useEquivalentFieldValues = false,
        bool mismatchValueField = false,
        bool mismatchValueFieldOwner = false,
        bool mismatchValueFieldOffset = false,
        bool usePublicCount = false,
        bool publicCountWrongReceiver = false,
        bool includeMistypedRuntimeMethodCarrier = false,
        bool readMistypedRuntimeMethodCarrierFromMerge = false,
        bool slowPathExplicitJump = false,
        bool includeTypeofRuntimeClassCarrier = false,
        bool useDirectArrayAccess = false,
        bool retainDirectArrayAddressEvidence = false,
        bool directArrayWrongItems = false,
        bool directArrayWrongIndex = false,
        bool includeHeadBusinessAfterVersionWrite = false,
        bool useNormalizedDirectArrayIndex = false,
        bool normalizedDirectArrayWrongMask = false,
        bool normalizedDirectArrayWrongSource = false,
        bool normalizedDirectArrayDuplicateDefinition = false)
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
        var valueOwner = Local("valueOwner", listType);
        var otherValueOwner = Local("otherValueOwner", listType);
        var value = Local("value", elementType);
        var otherValue = Local("otherValue", elementType);
        var items = Local("items", elementType.MakeSzArrayType());
        var otherItems = Local("otherItems", elementType.MakeSzArrayType());
        var version = Local("version", app.SystemTypes.SystemInt32Type);
        var condition = Local("condition", app.SystemTypes.SystemBooleanType);
        var elementOffset = Local("elementOffset", app.SystemTypes.SystemIntPtrType);
        var elementAddress = Local("elementAddress", app.SystemTypes.SystemIntPtrType);
        var newSize = Local("newSize", app.SystemTypes.SystemInt32Type);
        var maskedIndex = Local("maskedIndex", app.SystemTypes.SystemIntPtrType);
        var biasedIndex = Local("biasedIndex", app.SystemTypes.SystemIntPtrType);
        var normalizedIndex = Local("normalizedIndex", app.SystemTypes.SystemIntPtrType);
        var carrier = Local("carrier", elementType);
        var valueField = new InjectedFieldAnalysisContext(
            "_testValue",
            elementType,
            System.Reflection.FieldAttributes.Private,
            listDefinition);
        var otherValueField = new InjectedFieldAnalysisContext(
            "_otherTestValue",
            elementType,
            System.Reflection.FieldAttributes.Private,
            listDefinition);
        IOperand fastValue = value;
        IOperand slowValue = value;
        if (useEquivalentFieldValues)
        {
            fastValue = new FieldReference(valueField, valueOwner, 0x10);
            slowValue = new FieldReference(
                mismatchValueField ? otherValueField : valueField,
                mismatchValueFieldOwner ? otherValueOwner : valueOwner,
                mismatchValueFieldOffset ? 0x18 : 0x10);
        }

        FieldReference Field(FieldAnalysisContext field) => new(field, receiver, 0);
        IOperand SizeRead() => usePublicCount
            ? new ListCount(publicCountWrongReceiver ? otherValueOwner : receiver, listType)
            : Field(sizeField);
        var instructions = new List<Instruction>
        {
            new(0, OpCode.Move, items, Field(itemsField)),
            new(1, OpCode.Add, version, Field(versionField), new Immediate(1)),
            new(2, OpCode.Move, Field(versionField), version),
            new(3, OpCode.CheckGreaterOrEqualUnsigned, condition, SizeRead(), new ArrayLength(items)),
            new(4, OpCode.ConditionalJump, new Immediate(-1), condition),
        };
        if (!useDirectArrayAccess || retainDirectArrayAddressEvidence)
        {
            instructions.Add(new Instruction(instructions.Count, OpCode.ShiftLeft, elementOffset, SizeRead(), new Immediate(3)));
            instructions.Add(new Instruction(
                instructions.Count,
                OpCode.Add,
                elementAddress,
                useDirectItemsFieldForAddress ? Field(itemsField) : items,
                elementOffset));
        }
        instructions.Add(new Instruction(instructions.Count, OpCode.Add, newSize, SizeRead(), new Immediate(1)));
        if (useNormalizedDirectArrayIndex)
        {
            // 中文注释：复现 ArrayRecovery 已折叠缩放/地址，但保留 UInt32 到原生索引规范化的真实末态。
            instructions.Add(new Instruction(
                instructions.Count,
                OpCode.And,
                maskedIndex,
                normalizedDirectArrayWrongSource ? new Immediate(7) : SizeRead(),
                new Immediate(normalizedDirectArrayWrongMask ? 0xFFFFFFFEL : 0xFFFFFFFFL)));
            instructions.Add(new Instruction(
                instructions.Count,
                OpCode.Xor,
                biasedIndex,
                maskedIndex,
                new Immediate(0x80000000L)));
            instructions.Add(new Instruction(
                instructions.Count,
                OpCode.Subtract,
                normalizedIndex,
                biasedIndex,
                new Immediate(0x80000000L)));
            if (normalizedDirectArrayDuplicateDefinition)
                instructions.Add(new Instruction(instructions.Count, OpCode.Move, normalizedIndex, new Immediate(3)));
        }
        instructions.Add(new Instruction(instructions.Count, OpCode.Move, Field(sizeField), newSize));
        IOperand elementWrite = useDirectArrayAccess
            ? new ArrayAccess(
                directArrayWrongItems ? otherItems : items,
                directArrayWrongIndex
                    ? new Immediate(99)
                    : useNormalizedDirectArrayIndex
                        ? normalizedIndex
                        : SizeRead())
            : new MemoryOperand(elementAddress, null, 0x20);
        instructions.Add(new Instruction(
            instructions.Count,
            OpCode.Move,
            elementWrite,
            mismatchStoredValue ? otherValue : fastValue));

        if (includeMatchingCarrierMove)
            instructions.Add(new Instruction(instructions.Count, OpCode.Move, carrier, mismatchCarrierSource ? otherValue : value));
        if (includeMistypedRuntimeMethodCarrier)
        {
            var runtimeMethod = new RuntimeMethodInfoAnalysisContext(addWithResize, listDefinition.DeclaringAssembly);
            instructions.Add(new Instruction(instructions.Count, OpCode.Move, carrier, runtimeMethod));
        }
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
        instructions.Add(new Instruction(slowCallIndex, OpCode.CallVoid, addWithResizeTarget, receiver, slowValue));
        if (slowPathReturnsDirectly)
            instructions.Add(new Instruction(instructions.Count, OpCode.Return));
        Instruction? slowJump = null;
        if (slowPathExplicitJump)
        {
            slowJump = new Instruction(instructions.Count, OpCode.Jump, new Immediate(-1));
            instructions.Add(slowJump);
        }
        var mergeEntryIndex = instructions.Count;
        if (includeNops)
            instructions.Add(new Instruction(instructions.Count, OpCode.Nop));
        var mergeIndex = instructions.Count;
        instructions.Add(slowPathReturnsDirectly
            ? new Instruction(mergeIndex, OpCode.Return)
            : new Instruction(mergeIndex, OpCode.Return, receiver));

        instructions[4].SetOperand(0, instructions[slowEntryIndex]);
        fastJump.SetOperand(0, instructions[mergeEntryIndex]);
        slowJump?.SetOperand(0, instructions[mergeEntryIndex]);

        var graph = new ISILControlFlowGraph(instructions);
        if (slowPathReturnsDirectly || slowPathExplicitJump)
            graph.MergeCallBlocks();
        var method = (MethodAnalysisContext)RuntimeHelpers.GetUninitializedObject(typeof(MethodAnalysisContext));
        method.ControlFlowGraph = graph;
        if (includeHeadBusinessAfterVersionWrite)
        {
            // 中文注释：模拟生产方法在 _version 回写后载入与 List 无关的迭代器载体。
            var headCarrier = Local("headCarrier", app.SystemTypes.SystemIntPtrType);
            var headBlock = graph.FindBlockByInstruction(instructions[2])!;
            headBlock.Instructions.Insert(
                headBlock.Instructions.IndexOf(instructions[2]) + 1,
                new Instruction(-1, OpCode.Move, headCarrier, new Immediate(7)));
        }
        var slowBlock = graph.FindBlockByInstruction(instructions[slowCallIndex])!;
        var fastBlock = graph.FindBlockByInstruction(instructions[5])!;
        if (readMistypedRuntimeMethodCarrierFromMerge)
        {
            var mergeBlock = graph.FindBlockByInstruction(instructions[mergeIndex])!;
            mergeBlock.Instructions.Insert(0, new Instruction(-1, OpCode.Move, otherValue, carrier));
        }
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
        if (includeTypeofRuntimeClassCarrier)
        {
            var runtimeClass = Local(
                "typeofRuntimeClass",
                new RuntimeClassTypeAnalysisContext(elementType, listDefinition.DeclaringAssembly));
            fastBlock.Instructions.Insert(
                fastBlock.Instructions.Count - 1,
                new Instruction(-1, OpCode.Move, runtimeClass, elementType));
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

    private static ChainedFixture CreateChainedFixture(
        int addCount,
        int mismatchStoredValueAt = -1,
        int retainedArrayAccessAt = -1,
        int compactArrayAccessAt = -1)
    {
        if (addCount < 2)
            throw new ArgumentOutOfRangeException(nameof(addCount));

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
        var values = Enumerable.Range(0, addCount)
            .Select(index => Local($"value{index}", elementType)).ToList();
        var otherValue = Local("otherValue", elementType);
        var sizeStates = new List<LocalVariable>(addCount);
        var versionStates = new List<LocalVariable>(addCount);
        var instructions = new List<Instruction>();
        var slowTargets = new List<(
            Instruction Branch,
            Instruction SlowCall,
            LocalVariable SizeState,
            LocalVariable VersionState)>();
        var fastTargets = new List<(Instruction Jump, int NextHeadIndex)>();

        FieldReference Field(FieldAnalysisContext field) => new(field, receiver, 0);
        IOperand currentSize = Field(sizeField);
        IOperand currentVersion = Field(versionField);
        for (var index = 0; index < addCount; index++)
        {
            var items = Local($"items{index}", elementType.MakeSzArrayType());
            var version = Local($"version{index}", app.SystemTypes.SystemInt32Type);
            var condition = Local($"condition{index}", app.SystemTypes.SystemBooleanType);
            var newSize = Local($"newSize{index}", app.SystemTypes.SystemInt32Type);
            var elementOffset = Local($"elementOffset{index}", app.SystemTypes.SystemIntPtrType);
            var elementAddress = Local($"elementAddress{index}", app.SystemTypes.SystemIntPtrType);
            sizeStates.Add(newSize);
            versionStates.Add(version);

            instructions.Add(new Instruction(instructions.Count, OpCode.Move, items, Field(itemsField)));
            instructions.Add(new Instruction(instructions.Count, OpCode.Add, version, currentVersion, new Immediate(1)));
            instructions.Add(new Instruction(instructions.Count, OpCode.Move, Field(versionField), version));
            instructions.Add(new Instruction(
                instructions.Count,
                OpCode.CheckGreaterOrEqualUnsigned,
                condition,
                currentSize,
                new ArrayLength(items)));
            var branch = new Instruction(instructions.Count, OpCode.ConditionalJump, new Immediate(-1), condition);
            instructions.Add(branch);
            instructions.Add(new Instruction(instructions.Count, OpCode.Add, newSize, currentSize, new Immediate(1)));
            var useCompactArrayAccess = index == compactArrayAccessAt;
            var useArrayAccess = useCompactArrayAccess || index == retainedArrayAccessAt;
            if (!useCompactArrayAccess)
            {
                instructions.Add(new Instruction(
                    instructions.Count,
                    OpCode.ShiftLeft,
                    elementOffset,
                    currentSize,
                    new Immediate(3)));
                instructions.Add(new Instruction(
                    instructions.Count,
                    OpCode.Add,
                    elementAddress,
                    items,
                    elementOffset));
            }
            instructions.Add(new Instruction(instructions.Count, OpCode.Move, Field(sizeField), newSize));
            instructions.Add(new Instruction(
                instructions.Count,
                OpCode.Move,
                useArrayAccess
                    ? new ArrayAccess(items, currentSize)
                    : new MemoryOperand(elementAddress, null, 0x20),
                index == mismatchStoredValueAt ? otherValue : values[index]));
            var fastJump = new Instruction(instructions.Count, OpCode.Jump, new Immediate(-1));
            instructions.Add(fastJump);
            var slowCall = new Instruction(
                instructions.Count,
                OpCode.CallVoid,
                addWithResizeTarget,
                receiver,
                values[index]);
            instructions.Add(slowCall);
            slowTargets.Add((branch, slowCall, newSize, version));
            fastTargets.Add((fastJump, instructions.Count));
            currentSize = newSize;
            currentVersion = version;
        }

        var returnInstruction = new Instruction(instructions.Count, OpCode.Return, receiver);
        instructions.Add(returnInstruction);
        for (var index = 0; index < addCount; index++)
        {
            slowTargets[index].Branch.SetOperand(0, slowTargets[index].SlowCall);
            var nextHead = fastTargets[index].NextHeadIndex < instructions.Count
                ? instructions[fastTargets[index].NextHeadIndex]
                : returnInstruction;
            fastTargets[index].Jump.SetOperand(0, nextHead);
        }

        var graph = new ISILControlFlowGraph(instructions);
        foreach (var target in slowTargets)
        {
            var slowBlock = graph.FindBlockByInstruction(target.SlowCall)!;
            // Phi 消除把容量和版本的慢边复制追加到扩容调用块，生产 CFG 不为它们另建块。
            slowBlock.Instructions.Add(new Instruction(-1, OpCode.Move, target.SizeState, Field(sizeField)));
            slowBlock.Instructions.Add(new Instruction(-1, OpCode.Move, target.VersionState, Field(versionField)));
        }
        var method = (MethodAnalysisContext)RuntimeHelpers.GetUninitializedObject(typeof(MethodAnalysisContext));
        method.ControlFlowGraph = graph;
        return new ChainedFixture(method, graph, receiver, sizeStates, versionStates);
    }

    private static ChainedFixture CreateTailAdvancedChainedFixture(
        int addCount,
        int mismatchFastVersionAdvanceAt = -1,
        int fusedFastVersionAdvanceAt = -1,
        int carriedFastVersionAdvanceAt = -1,
        bool distinctSlowItemsField = false)
    {
        if (addCount < 2)
            throw new ArgumentOutOfRangeException(nameof(addCount));

        var app = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var elementType = app.SystemTypes.SystemInt32Type;
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
        var slowItemsField = distinctSlowItemsField
            ? new InjectedFieldAnalysisContext(
                "_items",
                genericElement.MakeSzArrayType(),
                System.Reflection.FieldAttributes.Private,
                listDefinition)
            : itemsField;

        var receiver = Local("list", listType);
        var items = Local("items", elementType.MakeSzArrayType());
        var initialVersion = Local("initialVersion", app.SystemTypes.SystemInt32Type);
        var sizeStates = new List<LocalVariable>(addCount);
        var versionStates = new List<LocalVariable>(addCount);
        var instructions = new List<Instruction>
        {
            new(0, OpCode.Move, items, new FieldReference(itemsField, receiver, 0)),
            new(1, OpCode.Add, initialVersion, new FieldReference(versionField, receiver, 0), new Immediate(1)),
            new(2, OpCode.Move, new FieldReference(versionField, receiver, 0), initialVersion),
        };
        var targets = new List<(Instruction Branch, Instruction FastJump, Instruction SlowCall, int NextHeadIndex)>();

        FieldReference Field(FieldAnalysisContext field) => new(field, receiver, 0);
        for (var index = 0; index < addCount; index++)
        {
            var condition = Local($"condition{index}", app.SystemTypes.SystemBooleanType);
            var newSize = Local($"newSize{index}", app.SystemTypes.SystemInt32Type);
            var elementOffset = Local($"elementOffset{index}", app.SystemTypes.SystemIntPtrType);
            var elementAddress = Local($"elementAddress{index}", app.SystemTypes.SystemIntPtrType);
            var value = new Immediate(80 + index);
            sizeStates.Add(newSize);

            instructions.Add(new Instruction(instructions.Count, OpCode.CheckGreaterOrEqualUnsigned, condition, Field(sizeField), new ArrayLength(items)));
            var branch = new Instruction(instructions.Count, OpCode.ConditionalJump, new Immediate(-1), condition);
            instructions.Add(branch);
            instructions.Add(new Instruction(instructions.Count, OpCode.Add, newSize, Field(sizeField), new Immediate(1)));
            instructions.Add(new Instruction(instructions.Count, OpCode.ShiftLeft, elementOffset, Field(sizeField), new Immediate(2)));
            instructions.Add(new Instruction(instructions.Count, OpCode.Add, elementAddress, items, elementOffset));
            instructions.Add(new Instruction(instructions.Count, OpCode.Move, Field(sizeField), newSize));
            instructions.Add(new Instruction(instructions.Count, OpCode.Move, new MemoryOperand(elementAddress, null, 0x20), value));

            if (index + 1 < addCount)
            {
                var fastVersion = Local($"fastVersion{index}", app.SystemTypes.SystemInt32Type);
                instructions.Add(new Instruction(
                    instructions.Count,
                    OpCode.Add,
                    fastVersion,
                    index == carriedFastVersionAdvanceAt
                        ? initialVersion
                        : Field(versionField),
                    new Immediate(
                        index == mismatchFastVersionAdvanceAt
                            ? 3
                            : index == fusedFastVersionAdvanceAt
                                ? 2
                                : 1)));
                instructions.Add(new Instruction(instructions.Count, OpCode.Move, Field(versionField), fastVersion));
            }

            var fastJump = new Instruction(instructions.Count, OpCode.Jump, new Immediate(-1));
            instructions.Add(fastJump);
            var slowCall = new Instruction(
                instructions.Count,
                OpCode.CallVoid,
                addWithResizeTarget,
                receiver,
                new Immediate(value.Value));
            instructions.Add(slowCall);

            if (index + 1 < addCount)
            {
                var slowVersion = Local($"slowVersion{index}", app.SystemTypes.SystemInt32Type);
                versionStates.Add(slowVersion);
                instructions.Add(new Instruction(instructions.Count, OpCode.Move, items, Field(slowItemsField)));
                instructions.Add(new Instruction(instructions.Count, OpCode.Add, slowVersion, Field(versionField), new Immediate(1)));
                instructions.Add(new Instruction(instructions.Count, OpCode.Move, Field(versionField), slowVersion));
            }

            targets.Add((branch, fastJump, slowCall, instructions.Count));
        }

        var returnInstruction = new Instruction(instructions.Count, OpCode.Return, receiver);
        instructions.Add(returnInstruction);
        foreach (var target in targets)
        {
            target.Branch.SetOperand(0, target.SlowCall);
            target.FastJump.SetOperand(
                0,
                target.NextHeadIndex < instructions.Count - 1
                    ? instructions[target.NextHeadIndex]
                    : returnInstruction);
        }

        var graph = new ISILControlFlowGraph(instructions);
        // 生产流水线在 ListAddRecovery 前合并调用块，使扩容调用与其状态尾部位于同一慢边块。
        graph.MergeCallBlocks();
        var method = (MethodAnalysisContext)RuntimeHelpers.GetUninitializedObject(typeof(MethodAnalysisContext));
        method.ControlFlowGraph = graph;
        return new ChainedFixture(method, graph, receiver, sizeStates, versionStates);
    }

    private static SharedFastTailFixture CreateSharedFastTailFixture(
        IReadOnlyList<int> values,
        int mismatchFastValueAt = -1,
        int omitExplicitStagingJumpAt = -1)
    {
        if (values.Count < 2)
            throw new ArgumentOutOfRangeException(nameof(values));

        var app = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var elementType = app.SystemTypes.SystemInt32Type;
        var listType = listDefinition.MakeGenericInstanceType([elementType]);
        var genericElement = listDefinition.GenericParameters.Single();
        var addWithResize = new InjectedMethodAnalysisContext(
            listDefinition,
            "AddWithResize",
            app.SystemTypes.SystemVoidType,
            System.Reflection.MethodAttributes.Private,
            [genericElement]);
        var addWithResizeTarget = new ConcreteGenericMethodAnalysisContext(
            addWithResize,
            [elementType],
            []);
        var itemsField = new InjectedFieldAnalysisContext(
            "_items",
            genericElement.MakeSzArrayType(),
            System.Reflection.FieldAttributes.Private,
            listDefinition);
        var versionField = new InjectedFieldAnalysisContext(
            "_version",
            app.SystemTypes.SystemInt32Type,
            System.Reflection.FieldAttributes.Private,
            listDefinition);
        var listHolderField = new InjectedFieldAnalysisContext(
            "_targetList",
            listType,
            System.Reflection.FieldAttributes.Private,
            listDefinition);

        var owner = Local("owner", listType);
        var items = Local("sharedItems", elementType.MakeSzArrayType());
        var sizeState = Local("sharedSize", app.SystemTypes.SystemInt32Type);
        var sizeAddress = Local("sharedSizeAddress", app.SystemTypes.SystemIntPtrType);
        var stagedValue = Local("sharedValue", elementType);
        var deadCarrier = Local("deadCarrier", app.SystemTypes.SystemInt32Type);
        var masked = Local("masked", app.SystemTypes.SystemIntPtrType);
        var biased = Local("biased", app.SystemTypes.SystemIntPtrType);
        var normalized = Local("normalized", app.SystemTypes.SystemIntPtrType);
        var elementOffset = Local("elementOffset", app.SystemTypes.SystemIntPtrType);
        var elementAddress = Local("elementAddress", app.SystemTypes.SystemIntPtrType);
        var newSize = Local("newSize", app.SystemTypes.SystemInt32Type);
        var instructions = new List<Instruction>();
        var targets = new List<(
            Instruction ReceiverNullBranch,
            Instruction ItemsNullBranch,
            Instruction CapacityBranch,
            Instruction StagingJump,
            Instruction SlowCall,
            Instruction SlowJump)>();

        FieldReference PublicReceiver() => new(listHolderField, owner, 0x10);
        for (var index = 0; index < values.Count; index++)
        {
            var stateReceiver = Local($"stateReceiver{index}", listType);
            var receiverNull = Local($"receiverNull{index}", app.SystemTypes.SystemBooleanType);
            var version = Local($"version{index}", app.SystemTypes.SystemInt32Type);
            var itemsNull = Local($"itemsNull{index}", app.SystemTypes.SystemBooleanType);
            var capacity = Local($"capacity{index}", app.SystemTypes.SystemBooleanType);

            instructions.Add(new Instruction(instructions.Count, OpCode.Move, stateReceiver, PublicReceiver()));
            instructions.Add(new Instruction(instructions.Count, OpCode.CheckEqual, receiverNull, PublicReceiver(), new Immediate(0)));
            var receiverNullBranch = new Instruction(
                instructions.Count,
                OpCode.ConditionalJump,
                new Immediate(-1),
                receiverNull);
            instructions.Add(receiverNullBranch);

            instructions.Add(new Instruction(
                instructions.Count,
                OpCode.Move,
                items,
                new FieldReference(itemsField, stateReceiver, 0)));
            instructions.Add(new Instruction(
                instructions.Count,
                OpCode.Add,
                version,
                new FieldReference(versionField, stateReceiver, 0),
                new Immediate(1)));
            instructions.Add(new Instruction(
                instructions.Count,
                OpCode.Move,
                new FieldReference(versionField, stateReceiver, 0),
                version));
            instructions.Add(new Instruction(
                instructions.Count,
                OpCode.CheckEqual,
                itemsNull,
                new FieldReference(itemsField, stateReceiver, 0),
                new Immediate(0)));
            var itemsNullBranch = new Instruction(
                instructions.Count,
                OpCode.ConditionalJump,
                new Immediate(-1),
                itemsNull);
            instructions.Add(itemsNullBranch);

            instructions.Add(new Instruction(
                instructions.Count,
                OpCode.Add,
                sizeAddress,
                PublicReceiver(),
                new Immediate(24)));
            instructions.Add(new Instruction(
                instructions.Count,
                OpCode.Move,
                sizeState,
                new MemoryOperand(sizeAddress)));
            instructions.Add(new Instruction(
                instructions.Count,
                OpCode.CheckGreaterOrEqualUnsigned,
                capacity,
                new MemoryOperand(sizeAddress),
                new ArrayLength(items)));
            var capacityBranch = new Instruction(
                instructions.Count,
                OpCode.ConditionalJump,
                new Immediate(-1),
                capacity);
            instructions.Add(capacityBranch);

            var fastValue = index == mismatchFastValueAt ? values[index] + 1L : values[index];
            instructions.Add(new Instruction(
                instructions.Count,
                OpCode.Move,
                stagedValue,
                new Immediate(fastValue)));
            instructions.Add(new Instruction(
                instructions.Count,
                OpCode.Move,
                deadCarrier,
                new Immediate(0)));
            var stagingJump = new Instruction(
                instructions.Count,
                OpCode.Jump,
                new Immediate(-1));
            instructions.Add(stagingJump);

            var slowCall = new Instruction(
                instructions.Count,
                OpCode.CallVoid,
                addWithResizeTarget,
                PublicReceiver(),
                new Immediate(values[index]));
            instructions.Add(slowCall);
            // 该载体在汇合点以后无引用，模拟原生调用隐藏参数覆盖，恢复后应删除。
            instructions.Add(new Instruction(
                instructions.Count,
                OpCode.Move,
                deadCarrier,
                new Immediate(99)));
            var slowJump = new Instruction(
                instructions.Count,
                OpCode.Jump,
                new Immediate(-1));
            instructions.Add(slowJump);
            targets.Add((
                receiverNullBranch,
                itemsNullBranch,
                capacityBranch,
                stagingJump,
                slowCall,
                slowJump));
        }

        var sharedTailStart = new Instruction(
            instructions.Count,
            OpCode.And,
            masked,
            sizeState,
            new Immediate(0xFFFFFFFFL));
        instructions.Add(sharedTailStart);
        instructions.Add(new Instruction(instructions.Count, OpCode.Xor, biased, masked, new Immediate(0x80000000L)));
        instructions.Add(new Instruction(instructions.Count, OpCode.Subtract, normalized, biased, new Immediate(0x80000000L)));
        instructions.Add(new Instruction(instructions.Count, OpCode.ShiftLeft, elementOffset, normalized, new Immediate(2)));
        instructions.Add(new Instruction(instructions.Count, OpCode.Add, elementAddress, items, elementOffset));
        instructions.Add(new Instruction(instructions.Count, OpCode.Add, newSize, sizeState, new Immediate(1)));
        instructions.Add(new Instruction(instructions.Count, OpCode.Move, new MemoryOperand(sizeAddress), newSize));
        instructions.Add(new Instruction(
            instructions.Count,
            OpCode.Move,
            new MemoryOperand(elementAddress, null, 0x20),
            stagedValue));
        var sharedTailJump = new Instruction(instructions.Count, OpCode.Jump, new Immediate(-1));
        instructions.Add(sharedTailJump);

        var merge = new Instruction(instructions.Count, OpCode.Return, owner);
        instructions.Add(merge);
        var nullThrows = values.Select(_ =>
        {
            var instruction = new Instruction(
                instructions.Count,
                OpCode.Throw,
                app.GetAssemblyByName("mscorlib")!
                    .GetTypeByFullName("System.NullReferenceException")!);
            instructions.Add(instruction);
            return instruction;
        }).ToList();

        for (var index = 0; index < targets.Count; index++)
        {
            targets[index].ReceiverNullBranch.SetOperand(0, nullThrows[index]);
            targets[index].ItemsNullBranch.SetOperand(0, nullThrows[index]);
            targets[index].CapacityBranch.SetOperand(0, targets[index].SlowCall);
            targets[index].StagingJump.SetOperand(0, sharedTailStart);
            targets[index].SlowJump.SetOperand(0, merge);
        }
        sharedTailJump.SetOperand(0, merge);

        var graph = new ISILControlFlowGraph(instructions);
        // 生产流水线在恢复器之前合并调用块，慢边调用与调用后载体因此属于同一块。
        graph.MergeCallBlocks();
        if (omitExplicitStagingJumpAt >= 0)
        {
            var stagingJump = targets[omitExplicitStagingJumpAt].StagingJump;
            graph.FindBlockByInstruction(stagingJump)!.Instructions.Remove(stagingJump);
        }
        var method = (MethodAnalysisContext)RuntimeHelpers.GetUninitializedObject(typeof(MethodAnalysisContext));
        method.ControlFlowGraph = graph;
        return new SharedFastTailFixture(
            method,
            graph,
            graph.FindBlockByInstruction(sharedTailStart)!);
    }

    /// <summary>
    /// 构造多个容量头分别载入元素值、随后同时汇入共享快尾与共享慢调用块的真实优化形态。
    /// </summary>
    private static SharedFastTailFixture CreateSharedFastAndSlowTailFixture(
        IReadOnlyList<int> values,
        int mismatchFastValueAt = -1,
        int omitFastJumpAt = -1)
    {
        if (values.Count != 2)
            throw new ArgumentOutOfRangeException(nameof(values));

        var app = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var elementType = app.SystemTypes.SystemSingleType;
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

        var receiver = Local("sharedBothList", listType);
        var items = Local("sharedBothItems", elementType.MakeSzArrayType());
        var sizeState = Local("sharedBothSize", app.SystemTypes.SystemInt32Type);
        var sharedFastValue = Local("sharedBothFastValue", app.SystemTypes.SystemInt32Type);
        var sharedSlowValue = Local("sharedBothSlowValue", elementType);
        var elementOffset = Local("sharedBothOffset", app.SystemTypes.SystemIntPtrType);
        var elementAddress = Local("sharedBothAddress", app.SystemTypes.SystemIntPtrType);
        var newSize = Local("sharedBothNewSize", app.SystemTypes.SystemInt32Type);
        var instructions = new List<Instruction>();
        var dispatchCondition = Local("sharedBothDispatch", app.SystemTypes.SystemBooleanType);
        var dispatch = new Instruction(
            instructions.Count,
            OpCode.ConditionalJump,
            new Immediate(-1),
            dispatchCondition);
        instructions.Add(dispatch);
        var headStarts = new List<Instruction>();
        var targets = new List<(
            Instruction CapacityBranch,
            Instruction FastJump,
            Instruction SlowJump)>();

        // 使用真实二进制中的可映射只读常量，精确复现 ARM64 快边位合成与慢边常量读取。
        var constantAddress = app.Binary.GetVirtualAddressOfPrimaryExecutableSection();
        Assert.That(app.Binary.TryMapVirtualAddressToRaw(constantAddress, out var rawAddress), Is.True);
        var constantBytes = app.Binary.Reader.ReadByteArrayAtRawAddress(rawAddress, sizeof(float));
        Assert.That(constantBytes, Has.Length.EqualTo(sizeof(float)));
        var slowBits = BitConverter.ToUInt32(constantBytes, 0);

        FieldReference Field(FieldAnalysisContext field) => new(field, receiver, 0);
        for (var index = 0; index < values.Count; index++)
        {
            var version = Local($"sharedBothVersion{index}", app.SystemTypes.SystemInt32Type);
            var condition = Local($"sharedBothCondition{index}", app.SystemTypes.SystemBooleanType);
            var maskedLow = Local($"sharedBothMaskedLow{values[index]}", app.SystemTypes.SystemInt32Type);
            var fastBits = slowBits ^ (index == mismatchFastValueAt ? 1u : 0u);
            var headStart = new Instruction(instructions.Count, OpCode.Move, sizeState, Field(sizeField));
            headStarts.Add(headStart);
            instructions.Add(headStart);
            instructions.Add(new Instruction(instructions.Count, OpCode.Move, items, Field(itemsField)));
            instructions.Add(new Instruction(
                instructions.Count,
                OpCode.Add,
                version,
                Field(versionField),
                new Immediate(1)));
            instructions.Add(new Instruction(instructions.Count, OpCode.Move, Field(versionField), version));
            instructions.Add(new Instruction(
                instructions.Count,
                OpCode.CheckGreaterOrEqualUnsigned,
                condition,
                sizeState,
                new ArrayLength(items)));
            var capacityBranch = new Instruction(
                instructions.Count,
                OpCode.ConditionalJump,
                new Immediate(-1),
                condition);
            instructions.Add(capacityBranch);

            instructions.Add(new Instruction(
                instructions.Count,
                OpCode.And,
                maskedLow,
                new Immediate(fastBits & 0xFFFFu),
                new Immediate(0xFFFF)));
            instructions.Add(new Instruction(
                instructions.Count,
                OpCode.Or,
                sharedFastValue,
                maskedLow,
                new Immediate(fastBits & 0xFFFF0000u)));
            var fastJump = new Instruction(instructions.Count, OpCode.Jump, new Immediate(-1));
            instructions.Add(fastJump);

            instructions.Add(new Instruction(
                instructions.Count,
                OpCode.Move,
                sharedSlowValue,
                new MemoryOperand(addend: unchecked((long)constantAddress))));
            var slowJump = new Instruction(instructions.Count, OpCode.Jump, new Immediate(-1));
            instructions.Add(slowJump);
            targets.Add((capacityBranch, fastJump, slowJump));
        }

        var sharedFastStart = new Instruction(
            instructions.Count,
            OpCode.ShiftLeft,
            elementOffset,
            sizeState,
            new Immediate(2));
        instructions.Add(sharedFastStart);
        instructions.Add(new Instruction(instructions.Count, OpCode.Add, elementAddress, items, elementOffset));
        instructions.Add(new Instruction(instructions.Count, OpCode.Add, newSize, sizeState, new Immediate(1)));
        instructions.Add(new Instruction(instructions.Count, OpCode.Move, Field(sizeField), newSize));
        instructions.Add(new Instruction(
            instructions.Count,
            OpCode.Move,
            new MemoryOperand(elementAddress, null, 0x20),
            sharedFastValue));
        var sharedFastJump = new Instruction(instructions.Count, OpCode.Jump, new Immediate(-1));
        instructions.Add(sharedFastJump);

        var sharedSlowStart = new Instruction(
            instructions.Count,
            OpCode.CallVoid,
            addWithResizeTarget,
            receiver,
            sharedSlowValue);
        instructions.Add(sharedSlowStart);
        var merge = new Instruction(instructions.Count, OpCode.Return, receiver);
        instructions.Add(merge);

        for (var index = 0; index < targets.Count; index++)
        {
            var slowEntry = instructions[targets[index].SlowJump.Index - 1];
            targets[index].CapacityBranch.SetOperand(0, slowEntry);
            targets[index].FastJump.SetOperand(0, sharedFastStart);
            targets[index].SlowJump.SetOperand(0, sharedSlowStart);
        }
        dispatch.SetOperand(0, headStarts[1]);
        sharedFastJump.SetOperand(0, merge);

        var graph = new ISILControlFlowGraph(instructions);
        if (omitFastJumpAt >= 0)
        {
            var fastJump = targets[omitFastJumpAt].FastJump;
            graph.FindBlockByInstruction(fastJump)!.Instructions.Remove(fastJump);
        }
        var method = (MethodAnalysisContext)RuntimeHelpers.GetUninitializedObject(typeof(MethodAnalysisContext));
        method.ControlFlowGraph = graph;
        return new SharedFastTailFixture(
            method,
            graph,
            graph.FindBlockByInstruction(sharedFastStart)!);
    }

    /// <summary>
    /// 构造多个标准 List.Add 容量菱形：快边先经过可选边载体 staging，
    /// 再共享同一个数组写入快尾。首项使用 <c>&gt;=</c> 跳慢边，次项使用
    /// <c>&lt;</c> 跳快边，同时覆盖严格 items 空守卫与 Count 并行局部。
    /// </summary>
    private static SharedFastTailFixture CreateStandardStagedSharedFastTailFixture(
        bool useWrongThrowType,
        bool includeEdgeCarrier = true,
        bool mismatchSharedFastValue = false,
        int branchCount = 2)
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
        var addWithResizeTarget = new ConcreteGenericMethodAnalysisContext(
            addWithResize,
            [elementType],
            []);
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

        var receiver = Local("standardSharedList", listType);
        var value = Local("standardSharedValue", elementType);
        var otherValue = Local("standardSharedOtherValue", elementType);
        var items = Local("standardSharedItems", elementType.MakeSzArrayType());
        var sizeState = Local("standardSharedSize", app.SystemTypes.SystemInt32Type);
        var carrier = Local("standardSharedCarrier", app.SystemTypes.SystemInt32Type);
        var masked = Local("standardSharedMasked", app.SystemTypes.SystemIntPtrType);
        var biased = Local("standardSharedBiased", app.SystemTypes.SystemIntPtrType);
        var normalized = Local("standardSharedNormalized", app.SystemTypes.SystemIntPtrType);
        var elementOffset = Local("standardSharedOffset", app.SystemTypes.SystemIntPtrType);
        var elementAddress = Local("standardSharedAddress", app.SystemTypes.SystemIntPtrType);
        var newSize = Local("standardSharedNewSize", app.SystemTypes.SystemInt32Type);
        var failureCarrier = Local("standardSharedFailureCarrier", app.SystemTypes.SystemInt32Type);
        var instructions = new List<Instruction>();
        var targets = new List<(
            Instruction GuardBranch,
            Instruction CapacityBranch,
            Instruction StagingStart,
            Instruction StagingJump,
            Instruction SlowStart,
            Instruction SlowJump,
            bool Reversed)>();

        FieldReference Field(FieldAnalysisContext field) => new(field, receiver, 0);

        (Instruction Start, Instruction Jump) AppendStaging(int index)
        {
            Instruction? start = null;
            if (includeEdgeCarrier)
            {
                start = new Instruction(
                    instructions.Count,
                    OpCode.Move,
                    carrier,
                    new Immediate(index + 1));
                instructions.Add(start);
            }
            var jump = new Instruction(instructions.Count, OpCode.Jump, new Immediate(-1));
            instructions.Add(jump);
            return (start ?? jump, jump);
        }

        (Instruction Start, Instruction Jump) AppendSlow(int index)
        {
            var start = new Instruction(
                instructions.Count,
                OpCode.CallVoid,
                addWithResizeTarget,
                receiver,
                value);
            instructions.Add(start);
            if (includeEdgeCarrier)
            {
                instructions.Add(new Instruction(
                    instructions.Count,
                    OpCode.Move,
                    carrier,
                    new Immediate(index + 1)));
            }
            var jump = new Instruction(instructions.Count, OpCode.Jump, new Immediate(-1));
            instructions.Add(jump);
            return (start, jump);
        }

        for (var index = 0; index < branchCount; index++)
        {
            var version = Local($"standardSharedVersion{index}", app.SystemTypes.SystemInt32Type);
            var itemsNull = Local($"standardSharedItemsNull{index}", app.SystemTypes.SystemBooleanType);
            var capacity = Local($"standardSharedCapacity{index}", app.SystemTypes.SystemBooleanType);
            instructions.Add(new Instruction(instructions.Count, OpCode.Move, items, Field(itemsField)));
            instructions.Add(new Instruction(
                instructions.Count,
                OpCode.Add,
                version,
                Field(versionField),
                new Immediate(1)));
            instructions.Add(new Instruction(instructions.Count, OpCode.Move, Field(versionField), version));
            instructions.Add(new Instruction(
                instructions.Count,
                OpCode.CheckEqual,
                itemsNull,
                Field(itemsField),
                new Immediate(0)));
            var guardBranch = new Instruction(
                instructions.Count,
                OpCode.ConditionalJump,
                new Immediate(-1),
                itemsNull);
            instructions.Add(guardBranch);

            // 快尾复用这一局部，容量比较则直接重读同一接收者的 Count。
            instructions.Add(new Instruction(
                instructions.Count,
                OpCode.Move,
                sizeState,
                new ListCount(receiver, listType)));
            var reversed = index % 2 == 1;
            instructions.Add(new Instruction(
                instructions.Count,
                reversed ? OpCode.CheckLessUnsigned : OpCode.CheckGreaterOrEqualUnsigned,
                capacity,
                new ListCount(receiver, listType),
                new ArrayLength(items)));
            var capacityBranch = new Instruction(
                instructions.Count,
                OpCode.ConditionalJump,
                new Immediate(-1),
                capacity);
            instructions.Add(capacityBranch);

            (Instruction Start, Instruction Jump) staging;
            (Instruction Start, Instruction Jump) slow;
            if (reversed)
            {
                slow = AppendSlow(index);
                staging = AppendStaging(index);
            }
            else
            {
                staging = AppendStaging(index);
                slow = AppendSlow(index);
            }
            targets.Add((
                guardBranch,
                capacityBranch,
                staging.Start,
                staging.Jump,
                slow.Start,
                slow.Jump,
                reversed));
        }

        var sharedTailStart = new Instruction(
            instructions.Count,
            OpCode.And,
            masked,
            sizeState,
            new Immediate(0xFFFFFFFFL));
        instructions.Add(sharedTailStart);
        instructions.Add(new Instruction(instructions.Count, OpCode.Xor, biased, masked, new Immediate(0x80000000L)));
        instructions.Add(new Instruction(instructions.Count, OpCode.Subtract, normalized, biased, new Immediate(0x80000000L)));
        instructions.Add(new Instruction(instructions.Count, OpCode.ShiftLeft, elementOffset, normalized, new Immediate(3)));
        instructions.Add(new Instruction(instructions.Count, OpCode.Add, elementAddress, items, elementOffset));
        instructions.Add(new Instruction(instructions.Count, OpCode.Add, newSize, sizeState, new Immediate(1)));
        instructions.Add(new Instruction(instructions.Count, OpCode.Move, Field(sizeField), newSize));
        instructions.Add(new Instruction(
            instructions.Count,
            OpCode.Move,
            new MemoryOperand(elementAddress, null, 0x20),
            mismatchSharedFastValue ? otherValue : value));
        var sharedTailJump = new Instruction(instructions.Count, OpCode.Jump, new Immediate(-1));
        instructions.Add(sharedTailJump);

        var merge = new Instruction(instructions.Count, OpCode.Return, receiver);
        instructions.Add(merge);
        var failureType = app.GetAssemblyByName("mscorlib")!.GetTypeByFullName(
            useWrongThrowType
                ? "System.InvalidOperationException"
                : "System.NullReferenceException")!;
        var failure = new Instruction(instructions.Count, OpCode.Throw, failureType);
        instructions.Add(failure);
        instructions.Add(new Instruction(
            instructions.Count,
            OpCode.Move,
            failureCarrier,
            new Immediate(0)));
        var failureJump = new Instruction(instructions.Count, OpCode.Jump, new Immediate(-1));
        instructions.Add(failureJump);
        var sink = new Instruction(instructions.Count, OpCode.Return, receiver);
        instructions.Add(sink);

        foreach (var target in targets)
        {
            target.GuardBranch.SetOperand(0, failure);
            target.CapacityBranch.SetOperand(0, target.Reversed ? target.StagingStart : target.SlowStart);
            target.StagingJump.SetOperand(0, sharedTailStart);
            target.SlowJump.SetOperand(0, merge);
        }
        sharedTailJump.SetOperand(0, merge);
        failureJump.SetOperand(0, sink);

        var graph = new ISILControlFlowGraph(instructions);
        graph.MergeCallBlocks();
        var method = (MethodAnalysisContext)RuntimeHelpers.GetUninitializedObject(typeof(MethodAnalysisContext));
        method.ControlFlowGraph = graph;
        return new SharedFastTailFixture(
            method,
            graph,
            graph.FindBlockByInstruction(sharedTailStart)!);
    }

    private static Fixture CreateInterleavedHeadFixture(bool touchesReceiver)
    {
        var fixture = CreateFixture(Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType);
        var head = fixture.Graph.Blocks.Single(block => block.Instructions.Any(instruction =>
            instruction is { OpCode: OpCode.Move, Operands: [_, FieldReference { Field.Name: "_items" }] }));
        var itemsLoadIndex = head.Instructions.FindIndex(instruction =>
            instruction is { OpCode: OpCode.Move, Operands: [_, FieldReference { Field.Name: "_items" }] });
        head.Instructions.Insert(
            itemsLoadIndex + 1,
            new Instruction(
                -1,
                OpCode.Move,
                fixture.Carrier,
                touchesReceiver ? fixture.Receiver : fixture.Value));
        return fixture;
    }

    private static void AddSlowFieldCarrierRefresh(Fixture fixture, bool mismatchOffset)
    {
        var slowCall = fixture.Graph.Instructions.Single(instruction =>
            instruction.IsCall
            && instruction.Operands[0] is MethodAnalysisContext { Name: "AddWithResize" });
        var field = (FieldReference)slowCall.Operands[2];
        var head = fixture.Graph.Blocks.Single(block => block.Instructions.Any(instruction =>
            instruction is { OpCode: OpCode.Move, Operands: [_, FieldReference { Field.Name: "_items" }] }));
        var itemsLoadIndex = head.Instructions.FindIndex(instruction =>
            instruction is { OpCode: OpCode.Move, Operands: [_, FieldReference { Field.Name: "_items" }] });
        head.Instructions.Insert(
            itemsLoadIndex + 1,
            new Instruction(
                3,
                OpCode.Move,
                fixture.Carrier,
                new FieldReference(field.Field, field.Local, field.Offset)));

        var slowBlock = fixture.Graph.FindBlockByInstruction(slowCall)!;
        slowBlock.Instructions.Add(new Instruction(
            slowCall.Index + 1,
            OpCode.Move,
            fixture.Carrier,
            new FieldReference(field.Field, field.Local, mismatchOffset ? field.Offset + 8 : field.Offset)));
    }

    /// <summary>
    /// 构造真实 ARM64 循环中的调用破坏重载：公开值位于固定栈槽，扩容调用后从同一槽恢复
    /// 寄存器载体；异常夹具只改变后置读取偏移。
    /// </summary>
    private static void AddSlowMemoryCarrierRefresh(
        Fixture fixture,
        bool mismatchOffset,
        long offset = 0x20)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var stackBase = Local("stackBase", app.SystemTypes.SystemIntPtrType);
        var slowCall = fixture.Graph.Instructions.Single(instruction =>
            instruction.IsCall
            && instruction.Operands[0] is MethodAnalysisContext { Name: "AddWithResize" });
        var head = fixture.Graph.Blocks.Single(block => block.Instructions.Any(instruction =>
            instruction is { OpCode: OpCode.Move, Operands: [_, FieldReference { Field.Name: "_items" }] }));
        var itemsLoadIndex = head.Instructions.FindIndex(instruction =>
            instruction is { OpCode: OpCode.Move, Operands: [_, FieldReference { Field.Name: "_items" }] });
        head.Instructions.Insert(
            itemsLoadIndex + 1,
            new Instruction(
                3,
                OpCode.Move,
                fixture.Carrier,
                new MemoryOperand(stackBase, null, offset)));

        var slowBlock = fixture.Graph.FindBlockByInstruction(slowCall)!;
        slowBlock.Instructions.Add(new Instruction(
            slowCall.Index + 1,
            OpCode.Move,
            fixture.Carrier,
            new MemoryOperand(stackBase, null, mismatchOffset ? offset + 8 : offset)));
    }

    private static LocalVariable AddSlowArrayLengthCarrierRefresh(Fixture fixture, bool mismatchArray)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var carrier = Local("arrayLengthCarrier", app.SystemTypes.SystemInt32Type);
        var sourceArray = Local("sourceArray", app.SystemTypes.SystemObjectType.MakeSzArrayType());
        var otherArray = Local("otherArray", app.SystemTypes.SystemObjectType.MakeSzArrayType());
        var slowCall = fixture.Graph.Instructions.Single(instruction =>
            instruction.IsCall
            && instruction.Operands[0] is MethodAnalysisContext { Name: "AddWithResize" });
        var head = fixture.Graph.Blocks.Single(block => block.Instructions.Any(instruction =>
            instruction is { OpCode: OpCode.Move, Operands: [_, FieldReference { Field.Name: "_items" }] }));
        var itemsLoadIndex = head.Instructions.FindIndex(instruction =>
            instruction is { OpCode: OpCode.Move, Operands: [_, FieldReference { Field.Name: "_items" }] });

        // 中文注释：调用前后的 ArrayLength 故意使用不同对象实例，只共享可证明的数组载体。
        head.Instructions.Insert(
            itemsLoadIndex + 1,
            new Instruction(3, OpCode.Move, carrier, new ArrayLength(sourceArray)));
        var slowBlock = fixture.Graph.FindBlockByInstruction(slowCall)!;
        slowBlock.Instructions.Add(new Instruction(
            slowCall.Index + 1,
            OpCode.Move,
            carrier,
            new ArrayLength(mismatchArray ? otherArray : sourceArray)));
        return carrier;
    }

    private static void SetImmediateValues(Fixture fixture, long fastValue, long slowValue)
    {
        var fastStore = fixture.Graph.Instructions.Single(instruction =>
            instruction is { OpCode: OpCode.Move, Operands: [MemoryOperand, _] });
        var slowCall = fixture.Graph.Instructions.Single(instruction =>
            instruction.IsCall
            && instruction.Operands[0] is MethodAnalysisContext { Name: "AddWithResize" });
        fastStore.SetOperand(1, new Immediate(fastValue));
        slowCall.SetOperand(2, new Immediate(slowValue));
    }

    /// <summary>
    /// 按真实二进制的可映射只读地址构造 Single 快慢路径等价夹具。
    /// </summary>
    private static (uint Bits, LocalVariable Result) ConfigureScalarFloatingConstant(
        Fixture fixture,
        bool includeMoveCarrier,
        uint fastBitsXor = 0)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var constantAddress = app.Binary.GetVirtualAddressOfPrimaryExecutableSection();
        Assert.That(app.Binary.TryMapVirtualAddressToRaw(constantAddress, out var rawAddress), Is.True);
        var bytes = app.Binary.Reader.ReadByteArrayAtRawAddress(rawAddress, sizeof(float));
        Assert.That(bytes, Has.Length.EqualTo(sizeof(float)));
        var slowBits = BitConverter.ToUInt32(bytes, 0);
        var fastBits = slowBits ^ fastBitsXor;

        var maskedLow = Local("singleMaskedLow", app.SystemTypes.SystemInt32Type);
        var combined = Local("singleCombined", app.SystemTypes.SystemInt32Type);
        var result = includeMoveCarrier
            ? Local("singleCarrier", app.SystemTypes.SystemInt32Type)
            : combined;
        var construction = new List<Instruction>
        {
            new(
                -1,
                OpCode.And,
                maskedLow,
                new Immediate(fastBits & 0xFFFFu),
                new Immediate(0xFFFF)),
            new(
                -1,
                OpCode.Or,
                combined,
                maskedLow,
                new Immediate(fastBits & 0xFFFF0000u)),
        };
        if (includeMoveCarrier)
            construction.Add(new Instruction(-1, OpCode.Move, result, combined));

        var fastStore = fixture.Graph.Instructions.Single(instruction =>
            instruction is { OpCode: OpCode.Move, Operands: [MemoryOperand, _] });
        var insertionIndex = fixture.FastBlock.Instructions.IndexOf(fastStore);
        foreach (var instruction in construction)
            fixture.FastBlock.Instructions.Insert(insertionIndex++, instruction);
        fastStore.SetOperand(1, result);

        var slowCall = fixture.Graph.Instructions.Single(instruction =>
            instruction.IsCall
            && instruction.Operands[0] is MethodAnalysisContext { Name: "AddWithResize" });
        slowCall.SetOperand(2, new MemoryOperand(addend: unchecked((long)constantAddress)));
        return (slowBits, result);
    }

    /// <summary>
    /// 按真实二进制中的连续八字节构造 Vector2 打包常量，并让慢路径通过位重解释局部传参。
    /// </summary>
    private static void ConfigureDefinedHfaConstant(
        Fixture fixture,
        TypeAnalysisContext aggregateType,
        bool includeMoveCarrier,
        uint secondBitsXor = 0)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var constantAddress = app.Binary.GetVirtualAddressOfPrimaryExecutableSection();
        Assert.That(app.Binary.TryMapVirtualAddressToRaw(constantAddress, out var rawAddress), Is.True);
        var bytes = app.Binary.Reader.ReadByteArrayAtRawAddress(rawAddress, sizeof(float) * 2);
        Assert.That(bytes, Has.Length.EqualTo(sizeof(float) * 2));
        var firstBits = BitConverter.ToUInt32(bytes, 0);
        var secondBits = BitConverter.ToUInt32(bytes, sizeof(float)) ^ secondBitsXor;

        var fastStore = fixture.Graph.Instructions.Single(instruction =>
            instruction is { OpCode: OpCode.Move, Operands: [MemoryOperand, _] });
        fastStore.SetOperand(1, new MemoryOperand(addend: unchecked((long)constantAddress)));

        var firstDecoded = Local("hfaFirstDecoded", app.SystemTypes.SystemSingleType, "V0");
        var secondDecoded = Local("hfaSecondDecoded", app.SystemTypes.SystemSingleType, "V1");
        var firstValue = includeMoveCarrier
            ? Local("hfaFirstCarrier", app.SystemTypes.SystemSingleType, "V0")
            : firstDecoded;
        var secondValue = includeMoveCarrier
            ? Local("hfaSecondCarrier", app.SystemTypes.SystemSingleType, "V1")
            : secondDecoded;
        var definitions = new List<Instruction>
        {
            new(
                -1,
                OpCode.ReinterpretIntegerBitsAsFloat,
                firstDecoded,
                new Immediate(unchecked((long)firstBits)),
                new Immediate(32)),
            new(
                -1,
                OpCode.ReinterpretIntegerBitsAsFloat,
                secondDecoded,
                new Immediate(unchecked((long)secondBits)),
                new Immediate(32)),
        };
        if (includeMoveCarrier)
        {
            definitions.Add(new Instruction(-1, OpCode.Move, firstValue, firstDecoded));
            definitions.Add(new Instruction(-1, OpCode.Move, secondValue, secondDecoded));
        }

        var slowCall = fixture.Graph.Instructions.Single(instruction =>
            instruction.IsCall
            && instruction.Operands[0] is MethodAnalysisContext { Name: "AddWithResize" });
        var insertionIndex = fixture.SlowBlock.Instructions.IndexOf(slowCall);
        foreach (var definition in definitions)
            fixture.SlowBlock.Instructions.Insert(insertionIndex++, definition);
        slowCall.SetOperand(
            2,
            new HomogeneousFloatingAggregateArgument(
                aggregateType,
                [firstValue, secondValue]));
    }

    private static InjectedTypeAnalysisContext CreateHfaValueType(string name)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var valueType = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.ValueType")!;
        var aggregate = app.InjectTypeIntoAllAssemblies(
                "Cpp2IL.Core.Tests",
                name,
                valueType,
                System.Reflection.TypeAttributes.Public
                | System.Reflection.TypeAttributes.Sealed
                | System.Reflection.TypeAttributes.SequentialLayout)
            .InjectedTypes[0];
        aggregate.InjectFieldContext("x", app.SystemTypes.SystemSingleType, System.Reflection.FieldAttributes.Public);
        aggregate.InjectFieldContext("y", app.SystemTypes.SystemSingleType, System.Reflection.FieldAttributes.Public);
        return aggregate;
    }

    private static ConcreteGenericMethodAnalysisContext CreateOpenAddWithResizeTarget()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var listDefinition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var genericParameter = listDefinition.GenericParameters.Single();
        var addWithResize = new InjectedMethodAnalysisContext(
            listDefinition,
            "AddWithResize",
            app.SystemTypes.SystemVoidType,
            System.Reflection.MethodAttributes.Private,
            [genericParameter]);
        return new ConcreteGenericMethodAnalysisContext(addWithResize, [genericParameter], []);
    }

    private static void ReplaceElementValues(Fixture fixture, IOperand fastValue, IOperand slowValue)
    {
        // 中文注释：统一替换夹具的快速数组写入值与慢速扩容参数，避免各测试重复定位容量菱形。
        var fastStore = fixture.Graph.Instructions.Single(instruction =>
            instruction is { OpCode: OpCode.Move, Operands: [MemoryOperand, _] });
        var slowCall = fixture.Graph.Instructions.Single(instruction =>
            instruction.IsCall
            && instruction.Operands[0] is MethodAnalysisContext { Name: "AddWithResize" });
        fastStore.SetOperand(1, fastValue);
        slowCall.SetOperand(2, slowValue);
    }

    private static LocalVariable Local(string name, TypeAnalysisContext type, string? registerName = null)
        => new(name, new Register(null, registerName ?? name), type);

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

    private sealed record ChainedFixture(
        MethodAnalysisContext Method,
        ISILControlFlowGraph Graph,
        LocalVariable Receiver,
        IReadOnlyList<LocalVariable> SizeStates,
        IReadOnlyList<LocalVariable> VersionStates);

    private sealed record SharedFastTailFixture(
        MethodAnalysisContext Method,
        ISILControlFlowGraph Graph,
        Block SharedFastTail);
}
