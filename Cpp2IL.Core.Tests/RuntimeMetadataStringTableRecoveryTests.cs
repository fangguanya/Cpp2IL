using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests;

public class RuntimeMetadataStringTableRecoveryTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 有界重定位字符串表恢复为托管选择并删除终点解引用()
    {
        var fixture = CreateFixture(maximumIndex: 2);
        var appContext = Cpp2IlApi.CurrentAppContext!;

        var changed = RuntimeMetadataStringTableRecovery.Rewrite(
            fixture.Graph,
            appContext.SystemTypes.SystemStringType,
            appContext.SystemTypes.SystemInt32Type,
            pointerSize: 8,
            (address, offset) => address == 0x1000
                ? new StringLiteral($"值{offset / 8}")
                : null,
            address => address == 0x3000 ? new StringLiteral(string.Empty) : null);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.EqualTo(1));
            Assert.That(fixture.TableLoad.Operands[1], Is.TypeOf<MetadataStringTableLookup>());
            var lookup = (MetadataStringTableLookup)fixture.TableLoad.Operands[1];
            Assert.That(lookup.Values.Select(value => value.Value),
                Is.EqualTo(new[] { "值0", "值1", "值2" }));
            Assert.That(lookup.DefaultValue.Value, Is.Empty);
            Assert.That(fixture.DefaultLoad.Operands[1], Is.EqualTo(new StringLiteral(string.Empty)));
            Assert.That(fixture.Return.Operands[0], Is.SameAs(fixture.Result));
            Assert.That(fixture.Result.Type, Is.SameAs(appContext.SystemTypes.SystemStringType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 最大二百五十六项只解析一次且完整提交()
    {
        var fixture = CreateFixture(maximumIndex: 255);
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var calls = new List<long>();

        var changed = RuntimeMetadataStringTableRecovery.Rewrite(
            fixture.Graph,
            appContext.SystemTypes.SystemStringType,
            appContext.SystemTypes.SystemInt32Type,
            pointerSize: 8,
            (address, offset) =>
            {
                calls.Add(offset);
                return address == 0x1000
                    ? new StringLiteral($"值{offset / 8}")
                    : null;
            },
            address => address == 0x3000 ? new StringLiteral("默认") : null);

        var lookup = (MetadataStringTableLookup)fixture.TableLoad.Operands[1];
        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.EqualTo(1));
            Assert.That(calls.Count, Is.EqualTo(256));
            Assert.That(calls.Distinct().Count(), Is.EqualTo(256));
            Assert.That(lookup.Values.Count, Is.EqualTo(256));
            Assert.That(lookup.Values[^1].Value, Is.EqualTo("值255"));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 任一重定位表项缺失时保持全部原始指令()
    {
        var fixture = CreateFixture(maximumIndex: 2);
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var originalTableMemory = fixture.TableLoad.Operands[1];
        var originalDefaultMemory = fixture.DefaultLoad.Operands[1];
        var originalReturnMemory = fixture.Return.Operands[0];

        var changed = RuntimeMetadataStringTableRecovery.Rewrite(
            fixture.Graph,
            appContext.SystemTypes.SystemStringType,
            appContext.SystemTypes.SystemInt32Type,
            pointerSize: 8,
            (address, offset) => address == 0x1000 && offset != 8
                ? new StringLiteral($"值{offset / 8}")
                : null,
            address => address == 0x3000 ? new StringLiteral(string.Empty) : null);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.Zero);
            Assert.That(fixture.TableLoad.Operands[1], Is.EqualTo(originalTableMemory));
            Assert.That(fixture.DefaultLoad.Operands[1], Is.EqualTo(originalDefaultMemory));
            Assert.That(fixture.Return.Operands[0], Is.EqualTo(originalReturnMemory));
            Assert.That(fixture.Result.Type, Is.Null);
        });
    }

    [Test]
    [Category("基本功能")]
    public void 退Ssa死写分析保留字符串表内嵌索引定义()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var index = new LocalVariable(
            "index",
            new Register(null, "W0"),
            appContext.SystemTypes.SystemInt32Type);
        var result = new LocalVariable(
            "result",
            new Register(null, "X0"),
            appContext.SystemTypes.SystemStringType);
        var indexDefinition = new Instruction(0, OpCode.Move, index, new Immediate(1));
        var lookupDefinition = new Instruction(
            1,
            OpCode.Move,
            result,
            new MetadataStringTableLookup(
                index,
                [new StringLiteral("甲"), new StringLiteral("乙")],
                new StringLiteral(string.Empty)));
        var returnInstruction = new Instruction(2, OpCode.Return, result);
        var graph = new ISILControlFlowGraph(
            [indexDefinition, lookupDefinition, returnInstruction]);

        PostSsaDeadStoreEliminator.Run(graph);

        Assert.Multiple(() =>
        {
            Assert.That(indexDefinition.OpCode, Is.EqualTo(OpCode.Move));
            Assert.That(lookupDefinition.OpCode, Is.EqualTo(OpCode.Move));
            Assert.That(returnInstruction.OpCode, Is.EqualTo(OpCode.Return));
        });
    }

    private static Fixture CreateFixture(long maximumIndex)
    {
        var graph = new ISILControlFlowGraph([]);
        var guard = new Block { ID = 2 };
        var table = new Block { ID = 3 };
        var fallback = new Block { ID = 4 };
        var terminal = new Block { ID = 5 };
        graph.Blocks = [graph.EntryBlock, guard, table, fallback, terminal, graph.ExitBlock];
        Connect(graph.EntryBlock, guard);
        Connect(guard, table);
        Connect(guard, fallback);
        Connect(table, terminal);
        Connect(fallback, terminal);
        Connect(terminal, graph.ExitBlock);

        var index = new LocalVariable("index", new Register(null, "W0"));
        var condition = new LocalVariable("越界", new Register(null, "NZCV"));
        var tableBase = new LocalVariable("tableBase", new Register(null, "X9"));
        var result = new LocalVariable("result", new Register(null, "X8"));
        var comparison = new Instruction(
            0,
            OpCode.CheckGreaterUnsigned,
            condition,
            index,
            new Immediate(maximumIndex));
        var branch = new Instruction(1, OpCode.ConditionalJump, fallback, condition);
        guard.Instructions.AddRange([comparison, branch]);

        var baseDefinition = new Instruction(
            2,
            OpCode.Add,
            tableBase,
            new Immediate(0xF00),
            new Immediate(0x100));
        var tableLoad = new Instruction(
            3,
            OpCode.Move,
            result,
            new MemoryOperand(
                tableBase,
                index,
                scale: 8,
                indexExtension: MemoryIndexExtension.ZeroExtend32));
        table.Instructions.AddRange([baseDefinition, tableLoad]);

        var defaultLoad = new Instruction(
            4,
            OpCode.Move,
            result,
            new MemoryOperand(addend: 0x3000));
        fallback.Instructions.Add(defaultLoad);
        var returnInstruction = new Instruction(
            5,
            OpCode.Return,
            new MemoryOperand(result));
        terminal.Instructions.Add(returnInstruction);

        return new Fixture(graph, result, tableLoad, defaultLoad, returnInstruction);
    }

    private static void Connect(Block source, Block destination)
    {
        source.Successors.Add(destination);
        destination.Predecessors.Add(source);
    }

    private sealed record Fixture(
        ISILControlFlowGraph Graph,
        LocalVariable Result,
        Instruction TableLoad,
        Instruction DefaultLoad,
        Instruction Return);
}
