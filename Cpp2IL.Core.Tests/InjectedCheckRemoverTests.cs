using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class InjectedCheckRemoverTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 空引用条件跳转及专用抛出块被删除()
    {
        var fixture = CreateFixture(0);

        InjectedCheckRemover.Run(fixture.Graph);

        Assert.Multiple(() =>
        {
            Assert.That(fixture.CheckBlock.Instructions.Any(instruction => instruction.OpCode == OpCode.ConditionalJump), Is.False);
            Assert.That(fixture.Graph.Blocks, Does.Not.Contain(fixture.ThrowBlock));
        });
    }

    [Test]
    [Category("边界值")]
    public void 块类型缓存陈旧时仍以尾部条件跳转为权威证据()
    {
        var fixture = CreateFixture(0, includeDeadPhi: true);
        fixture.CheckBlock.BlockType = BlockType.Fall;

        InjectedCheckRemover.Run(fixture.Graph);

        Assert.Multiple(() =>
        {
            Assert.That(fixture.CheckBlock.Successors, Has.Count.EqualTo(1));
            Assert.That(fixture.Graph.Blocks, Does.Not.Contain(fixture.ThrowBlock));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 非零比较保持原始业务分支()
    {
        var fixture = CreateFixture(1);
        var originalBlockCount = fixture.Graph.Blocks.Count;

        InjectedCheckRemover.Run(fixture.Graph);

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Graph.Blocks, Has.Count.EqualTo(originalBlockCount));
            Assert.That(fixture.CheckBlock.Instructions.Any(instruction => instruction.OpCode == OpCode.ConditionalJump), Is.True);
            Assert.That(fixture.Graph.Blocks, Does.Contain(fixture.ThrowBlock));
        });
    }

    private static Fixture CreateFixture(long comparedValue, bool includeDeadPhi = false)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var condition = new LocalVariable(
            "condition",
            new Register(null, "condition"),
            app.SystemTypes.SystemBooleanType);
        var value = new LocalVariable(
            "value",
            new Register(null, "value"),
            app.SystemTypes.SystemObjectType);
        var phiResult = new LocalVariable(
            "phiResult",
            new Register(null, "phiResult"),
            app.SystemTypes.SystemObjectType);
        var instructions = new List<Instruction>
        {
            new(0, OpCode.CheckEqual, condition, value, new Immediate(comparedValue)),
            new(1, OpCode.ConditionalJump, new Immediate(3), condition),
            new(2, OpCode.Return),
        };
        if (includeDeadPhi)
            instructions.Add(new Instruction(3, OpCode.Phi, phiResult, value, value));
        instructions.Add(new Instruction(
            instructions.Count,
            OpCode.Throw,
            app.GetAssemblyByName("mscorlib")!.GetTypeByFullName("System.NullReferenceException")!));
        instructions[1].SetOperand(0, instructions[3]);

        var graph = new ISILControlFlowGraph(instructions);
        return new Fixture(
            graph,
            graph.FindBlockByInstruction(instructions[1])!,
            graph.FindBlockByInstruction(instructions[^1])!);
    }

    private sealed record Fixture(
        ISILControlFlowGraph Graph,
        Block CheckBlock,
        Block ThrowBlock);
}
