using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class DeadAddressCarrierRecoveryTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 未使用的局部取址载体被删除()
    {
        var fixture = CreateFixture(usesAddress: false, usesInCall: false);

        var removed = DeadAddressCarrierRecovery.Run(fixture.Method);

        Assert.That(removed, Is.EqualTo(1));
        Assert.That(fixture.AddressMove.OpCode, Is.EqualTo(OpCode.Nop));
    }

    [Test]
    [Category("边界值")]
    public void 参与内存解引用的取址载体保持()
    {
        var fixture = CreateFixture(usesAddress: true, usesInCall: false);

        var removed = DeadAddressCarrierRecovery.Run(fixture.Method);

        Assert.That(removed, Is.EqualTo(0));
        Assert.That(fixture.AddressMove.OpCode, Is.EqualTo(OpCode.Move));
    }

    [Test]
    [Category("异常输入")]
    public void 传给调用的取址载体保持()
    {
        var fixture = CreateFixture(usesAddress: false, usesInCall: true);

        var removed = DeadAddressCarrierRecovery.Run(fixture.Method);

        Assert.That(removed, Is.EqualTo(0));
        Assert.That(fixture.AddressMove.OpCode, Is.EqualTo(OpCode.Move));
    }

    [Test]
    [Category("基本功能")]
    public void 未使用的托管字段地址计算被删除()
    {
        var fixture = CreateManagedFieldAddressFixture(usesAddress: false, usesInCall: false);

        var removed = DeadAddressCarrierRecovery.Run(fixture.Method);

        Assert.That(removed, Is.EqualTo(1));
        Assert.That(fixture.AddressAdd.OpCode, Is.EqualTo(OpCode.Nop));
    }

    [Test]
    [Category("边界值")]
    public void 参与内存写入的托管字段地址计算保持()
    {
        var fixture = CreateManagedFieldAddressFixture(usesAddress: true, usesInCall: false);

        var removed = DeadAddressCarrierRecovery.Run(fixture.Method);

        Assert.That(removed, Is.EqualTo(0));
        Assert.That(fixture.AddressAdd.OpCode, Is.EqualTo(OpCode.Add));
    }

    [Test]
    [Category("异常输入")]
    public void 传给调用的托管字段地址计算保持()
    {
        var fixture = CreateManagedFieldAddressFixture(usesAddress: false, usesInCall: true);

        var removed = DeadAddressCarrierRecovery.Run(fixture.Method);

        Assert.That(removed, Is.EqualTo(0));
        Assert.That(fixture.AddressAdd.OpCode, Is.EqualTo(OpCode.Add));
    }

    private static Fixture CreateFixture(bool usesAddress, bool usesInCall)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var objectType = app.SystemTypes.SystemObjectType;
        var address = new LocalVariable("address", new Register(null, "X0"), new PointerTypeAnalysisContext(objectType));
        var value = new LocalVariable("value", new Register(null, "X1"), objectType);
        var instructions = new List<Instruction>
        {
            new(0, OpCode.Move, address, new AddressOf(value))
        };

        if (usesAddress)
            instructions.Add(new Instruction(1, OpCode.Move, value, new MemoryOperand(address)));
        else if (usesInCall)
            instructions.Add(new Instruction(1, OpCode.CallVoid, new Immediate(1), address));
        else
            instructions.Add(new Instruction(1, OpCode.Nop));

        instructions.Add(new Instruction(2, OpCode.Return));
        var graph = new ISILControlFlowGraph(instructions);
        var method = (MethodAnalysisContext)RuntimeHelpers.GetUninitializedObject(typeof(MethodAnalysisContext));
        method.ControlFlowGraph = graph;
        return new Fixture(method, graph, instructions[0]);
    }

    private static ManagedFieldAddressFixture CreateManagedFieldAddressFixture(
        bool usesAddress,
        bool usesInCall)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var receiver = new LocalVariable(
            "receiver",
            new Register(null, "X0"),
            app.SystemTypes.SystemObjectType);
        var address = new LocalVariable("address", new Register(null, "X8"));
        var value = new LocalVariable(
            "value",
            new Register(null, "X1"),
            app.SystemTypes.SystemInt32Type);
        var instructions = new List<Instruction>
        {
            new(0, OpCode.Add, address, receiver, new Immediate(0x20))
        };

        if (usesAddress)
            instructions.Add(new Instruction(1, OpCode.Move, new MemoryOperand(address), value));
        else if (usesInCall)
            instructions.Add(new Instruction(1, OpCode.CallVoid, new Immediate(1), address));
        else
            instructions.Add(new Instruction(1, OpCode.Nop));

        instructions.Add(new Instruction(2, OpCode.Return));
        var graph = new ISILControlFlowGraph(instructions);
        var method = (MethodAnalysisContext)RuntimeHelpers.GetUninitializedObject(typeof(MethodAnalysisContext));
        method.ControlFlowGraph = graph;
        return new ManagedFieldAddressFixture(method, instructions[0]);
    }

    private sealed record Fixture(MethodAnalysisContext Method, ISILControlFlowGraph Graph, Instruction AddressMove);

    private sealed record ManagedFieldAddressFixture(
        MethodAnalysisContext Method,
        Instruction AddressAdd);
}
