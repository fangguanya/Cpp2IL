using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests.Analysis;

public class DeadCodeEliminationTests
{
    private static List<Instruction> Live(ISILControlFlowGraph graph)
        => graph.Blocks.SelectMany(b => b.Instructions).Where(i => i.OpCode != OpCode.Nop).ToList();

    [Test]
    public void RemovesDeadDefinitionButKeepsLiveOnes()
    {
        var x = new LocalVariable("x", new Register(null, "x"));
        var dead = new LocalVariable("dead", new Register(null, "dead"));

        var graph = new ISILControlFlowGraph(new List<Instruction>
        {
            new(0, OpCode.Move, x, Imm(5)),
            new(1, OpCode.Subtract, dead, x, Imm(1)), // dead's result is never read
            new(2, OpCode.Return, x),
        });

        DeadCodeEliminator.Run(graph);

        var live = Live(graph);
        Assert.That(live.Any(i => i.OpCode == OpCode.Subtract), Is.False, "dead computation should be removed");
        Assert.That(live.Any(i => i.OpCode == OpCode.Move), Is.True, "live definition should remain");
        Assert.That(live.Any(i => i.OpCode == OpCode.Return), Is.True, "terminator should remain");
    }

    [Test]
    public void RemovesDeadChainToFixpoint()
    {
        // x = 5; temp = x - 1; flag = temp < 0; return x
        // 'flag' is unused -> dead; that makes 'temp' unused -> dead too (cascade).
        var x = new LocalVariable("x", new Register(null, "x"));
        var temp = new LocalVariable("temp", new Register(null, "temp"));
        var flag = new LocalVariable("flag", new Register(null, "flag"));

        var graph = new ISILControlFlowGraph(new List<Instruction>
        {
            new(0, OpCode.Move, x, Imm(5)),
            new(1, OpCode.Subtract, temp, x, Imm(1)),
            new(2, OpCode.CheckLess, flag, temp, Imm(0)),
            new(3, OpCode.Return, x),
        });

        DeadCodeEliminator.Run(graph);

        var live = Live(graph);
        Assert.That(live.Any(i => i.OpCode == OpCode.CheckLess), Is.False, "dead flag should be removed");
        Assert.That(live.Any(i => i.OpCode == OpCode.Subtract), Is.False, "now-dead temp should be removed (cascade)");
        Assert.That(live.Count, Is.EqualTo(2), "only the live Move and Return should remain");
    }

    [Test]
    public void KeepsCallsEvenWhenResultUnused()
    {
        // A call with no observed result must be kept (side effects).
        var graph = new ISILControlFlowGraph(new List<Instruction>
        {
            new(0, OpCode.CallVoid, Imm(0xDEADBEEFUL)),
            new(1, OpCode.Return),
        });

        DeadCodeEliminator.Run(graph);

        Assert.That(Live(graph).Any(i => i.OpCode == OpCode.CallVoid), Is.True, "calls must never be removed");
    }

    [Test]
    public void ArrayOperandsCountTheirLocalsAsUses()
    {
        // arr and i are only read inside array operands, so their definitions have to survive
        var array = new LocalVariable("arr", new Register(null, "arr"));
        var index = new LocalVariable("i", new Register(null, "i"));
        var element = new LocalVariable("elem", new Register(null, "elem"));
        var length = new LocalVariable("len", new Register(null, "len"));
        var pointer = new LocalVariable("ptr", new Register(null, "ptr"));

        var graph = new ISILControlFlowGraph(new List<Instruction>
        {
            new(0, OpCode.Move, index, Imm(1)),
            new(1, OpCode.Move, element, new ArrayAccess(array, index)),
            new(2, OpCode.Move, length, new ArrayLength(array)),
            new(3, OpCode.Move, pointer, new AddressOf(new ArrayAccess(array, index))),
            new(4, OpCode.Add, element, element, length),
            new(5, OpCode.Add, element, element, pointer),
            new(6, OpCode.Return, element),
        });

        DeadCodeEliminator.Run(graph);

        Assert.That(Live(graph).Any(i => i.OpCode == OpCode.Move && ReferenceEquals(i.Operands[0], index)), Is.True,
            "index definition is used inside the array operands and must survive");
    }

    [Test]
    [Category("基本功能")]
    public void 删除没有可观察根的互相引用Phi环()
    {
        var first = new LocalVariable("first", new Register(null, "first"));
        var second = new LocalVariable("second", new Register(null, "second"));
        var result = new LocalVariable("result", new Register(null, "result"));
        var graph = new ISILControlFlowGraph(new List<Instruction>
        {
            new(0, OpCode.Phi, first, second, second),
            new(1, OpCode.Phi, second, first, first),
            new(2, OpCode.Move, result, Imm(1)),
            new(3, OpCode.Return, result),
        });

        DeadCodeEliminator.Run(graph);

        Assert.That(Live(graph).Any(instruction => instruction.OpCode == OpCode.Phi), Is.False);
    }

    [Test]
    [Category("边界值")]
    public void Phi环被返回值触达时完整保留依赖闭包()
    {
        var seed = new LocalVariable("seed", new Register(null, "seed"));
        var first = new LocalVariable("first", new Register(null, "first"));
        var second = new LocalVariable("second", new Register(null, "second"));
        var graph = new ISILControlFlowGraph(new List<Instruction>
        {
            new(0, OpCode.Move, seed, Imm(1)),
            new(1, OpCode.Phi, first, seed, second),
            new(2, OpCode.Phi, second, first, seed),
            new(3, OpCode.Return, first),
        });

        DeadCodeEliminator.Run(graph);

        Assert.Multiple(() =>
        {
            Assert.That(Live(graph).Count(instruction => instruction.OpCode == OpCode.Phi), Is.EqualTo(2));
            Assert.That(Live(graph).Any(instruction => instruction.OpCode == OpCode.Move), Is.True);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 非SSA重复定义被可观察使用时保守全部保留()
    {
        var value = new LocalVariable("value", new Register(null, "value"));
        var graph = new ISILControlFlowGraph(new List<Instruction>
        {
            new(0, OpCode.Move, value, Imm(1)),
            new(1, OpCode.Move, value, Imm(2)),
            new(2, OpCode.Return, value),
        });

        DeadCodeEliminator.Run(graph);

        Assert.That(Live(graph).Count(instruction => instruction.OpCode == OpCode.Move), Is.EqualTo(2));
    }

    [Test]
    [Category("基本功能")]
    public void Hfa调用实参保留全部标量分量定义()
    {
        var first = new LocalVariable("first", new Register(null, "V0"));
        var second = new LocalVariable("second", new Register(null, "V1"));
        var aggregate = new HomogeneousFloatingAggregateArgument(null!, [first, second]);
        var graph = new ISILControlFlowGraph(new List<Instruction>
        {
            new(0, OpCode.Move, first, Imm(1)),
            new(1, OpCode.Move, second, Imm(2)),
            new(2, OpCode.CallVoid, Imm(0x1234), aggregate),
            new(3, OpCode.Return),
        });

        DeadCodeEliminator.Run(graph);

        Assert.That(Live(graph).Count(instruction => instruction.OpCode == OpCode.Move), Is.EqualTo(2));
    }

    [Test]
    [Category("边界值")]
    public void Hfa内存分量保留基址与索引定义()
    {
        var baseLocal = new LocalVariable("base", new Register(null, "X8"));
        var indexLocal = new LocalVariable("index", new Register(null, "X9"));
        var aggregate = new HomogeneousFloatingAggregateArgument(
            null!,
            [new MemoryOperand(baseLocal, indexLocal, addend: 8, scale: 4)]);
        var graph = new ISILControlFlowGraph(new List<Instruction>
        {
            new(0, OpCode.Move, baseLocal, Imm(1)),
            new(1, OpCode.Move, indexLocal, Imm(2)),
            new(2, OpCode.CallVoid, Imm(0x1234), aggregate),
            new(3, OpCode.Return),
        });

        DeadCodeEliminator.Run(graph);

        Assert.That(Live(graph).Count(instruction => instruction.OpCode == OpCode.Move), Is.EqualTo(2));
    }

    [Test]
    [Category("异常输入")]
    public void Hfa常量分量不错误保留无关局部定义()
    {
        var unrelated = new LocalVariable("unrelated", new Register(null, "X8"));
        var aggregate = new HomogeneousFloatingAggregateArgument(null!, [Imm(1)]);
        var graph = new ISILControlFlowGraph(new List<Instruction>
        {
            new(0, OpCode.Move, unrelated, Imm(7)),
            new(1, OpCode.CallVoid, Imm(0x1234), aggregate),
            new(2, OpCode.Return),
        });

        DeadCodeEliminator.Run(graph);

        Assert.That(Live(graph).Any(instruction => instruction.OpCode == OpCode.Move), Is.False);
    }
}
