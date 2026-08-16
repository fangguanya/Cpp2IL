using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class PackedFieldStoreRecoveryTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 重叠宽零写恢复为三个精确字段写入()
    {
        var fixture = CreateFixture(
            firstAddend: 0,
            firstWidthBits: 64,
            firstValue: 0,
            includeSecondStore: true,
            secondAddend: 5,
            secondWidthBits: 64,
            observeBetweenStores: false);

        var replaced = PackedFieldStoreRecovery.Run(fixture.Method);
        var fieldStores = fixture.Graph.Instructions
            .Where(instruction => instruction.OpCode == OpCode.Move
                && instruction.Operands.FirstOrDefault() is FieldReference)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(replaced, Is.EqualTo(3));
            Assert.That(fixture.FirstStore.OpCode, Is.EqualTo(OpCode.Nop));
            Assert.That(fixture.SecondStore!.OpCode, Is.EqualTo(OpCode.Nop));
            Assert.That(
                fieldStores.Select(instruction => ((FieldReference)instruction.Operands[0]).Field.Name),
                Is.EqualTo(new[] { "FirstCount", "SecondCount", "Enabled" }));
            Assert.That(
                fieldStores.Select(instruction => ((FieldReference)instruction.Operands[0]).Offset),
                Is.EqualTo(new[] { 0x20, 0x24, 0x28 }));
            Assert.That(fieldStores.Select(instruction => instruction.Operands[1]),
                Is.All.Matches<Immediate>(operand => operand.Value == 0));
        });
    }

    [Test]
    [Category("边界值")]
    public void 只覆盖整数中间字节的写入保持原始内存语义()
    {
        var fixture = CreateFixture(
            firstAddend: 1,
            firstWidthBits: 16,
            firstValue: 0,
            includeSecondStore: false,
            secondAddend: 0,
            secondWidthBits: 0,
            observeBetweenStores: false);

        var replaced = PackedFieldStoreRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(replaced, Is.Zero);
            Assert.That(fixture.FirstStore.OpCode, Is.EqualTo(OpCode.Move));
            Assert.That(fixture.Graph.Instructions.Any(instruction =>
                instruction.Operands.FirstOrDefault() is FieldReference), Is.False);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 非零载荷不被误写为字段默认值()
    {
        var fixture = CreateFixture(
            firstAddend: 0,
            firstWidthBits: 64,
            firstValue: 1,
            includeSecondStore: true,
            secondAddend: 5,
            secondWidthBits: 64,
            observeBetweenStores: false);

        var replaced = PackedFieldStoreRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(replaced, Is.Zero);
            Assert.That(fixture.FirstStore.OpCode, Is.EqualTo(OpCode.Move));
            Assert.That(fixture.SecondStore!.OpCode, Is.EqualTo(OpCode.Move));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 两次宽写之间观察字段时保持原始执行顺序()
    {
        var fixture = CreateFixture(
            firstAddend: 0,
            firstWidthBits: 64,
            firstValue: 0,
            includeSecondStore: true,
            secondAddend: 5,
            secondWidthBits: 64,
            observeBetweenStores: true);

        var replaced = PackedFieldStoreRecovery.Run(fixture.Method);

        Assert.Multiple(() =>
        {
            Assert.That(replaced, Is.Zero);
            Assert.That(fixture.FirstStore.OpCode, Is.EqualTo(OpCode.Move));
            Assert.That(fixture.SecondStore!.OpCode, Is.EqualTo(OpCode.Move));
        });
    }

    private static Fixture CreateFixture(
        long firstAddend,
        int firstWidthBits,
        long firstValue,
        bool includeSecondStore,
        long secondAddend,
        int secondWidthBits,
        bool observeBetweenStores)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = new InjectedTypeAnalysisContext(
            app.Assemblies[0],
            "Cpp2IL.Core.Tests",
            "PackedOwner",
            app.SystemTypes.SystemObjectType,
            TypeAttributes.Public);
        var firstField = owner.InjectFieldContext(
            "FirstCount",
            app.SystemTypes.SystemInt32Type,
            FieldAttributes.Public);
        var secondField = owner.InjectFieldContext(
            "SecondCount",
            app.SystemTypes.SystemInt32Type,
            FieldAttributes.Public);
        var enabledField = owner.InjectFieldContext(
            "Enabled",
            app.SystemTypes.SystemBooleanType,
            FieldAttributes.Public);
        firstField.Offset = 0x20;
        secondField.Offset = 0x24;
        enabledField.Offset = 0x28;

        var method = new InjectedMethodAnalysisContext(
            owner,
            "Reset",
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Public,
            []);
        var receiver = new LocalVariable("receiver", new Register(null, "X19"), owner);
        var address = new LocalVariable("address", new Register(null, "X8"));
        var observed = new LocalVariable(
            "observed",
            new Register(null, "W9"),
            app.SystemTypes.SystemInt32Type);
        var addressAdd = new Instruction(
            0,
            OpCode.Add,
            address,
            receiver,
            new Immediate(0x20));
        var firstStore = new Instruction(
            1,
            OpCode.Move,
            new MemoryOperand(address, addend: firstAddend),
            new Immediate(firstValue))
        {
            MemoryAccessWidthBits = firstWidthBits
        };
        var instructions = new List<Instruction> { addressAdd, firstStore };
        if (observeBetweenStores)
            instructions.Add(new Instruction(
                2,
                OpCode.Move,
                observed,
                new FieldReference(secondField, receiver, secondField.Offset)));

        Instruction? secondStore = null;
        if (includeSecondStore)
        {
            secondStore = new Instruction(
                3,
                OpCode.Move,
                new MemoryOperand(address, addend: secondAddend),
                new Immediate(0))
            {
                MemoryAccessWidthBits = secondWidthBits
            };
            instructions.Add(secondStore);
        }

        instructions.Add(new Instruction(4, OpCode.Return));
        var graph = new ISILControlFlowGraph(instructions);
        method.ControlFlowGraph = graph;
        return new(method, graph, firstStore, secondStore);
    }

    private sealed record Fixture(
        MethodAnalysisContext Method,
        ISILControlFlowGraph Graph,
        Instruction FirstStore,
        Instruction? SecondStore);
}
