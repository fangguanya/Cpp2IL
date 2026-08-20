using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests;

public class SsaSimplifierTests
{
    [Test]
    [Category("基本功能")]
    public void Ref出参槽调用后的读取不得替换为调用前零值()
    {
        var slot = Local("slot");
        var initialize = new Instruction(0, OpCode.Move, slot, new Immediate(0));
        var call = new Instruction(1, OpCode.CallVoid, new StringLiteral("WriteRef"), new AddressOf(slot));
        var readAfterCall = new Instruction(2, OpCode.Return, slot);
        var cfg = Graph(initialize, call, readAfterCall);

        SsaSimplifier.Run(cfg, []);

        Assert.That(readAfterCall.Operands[0], Is.SameAs(slot));
        Assert.That(initialize.OpCode, Is.EqualTo(OpCode.Move));
    }

    [Test]
    [Category("边界值")]
    public void 同一槽地址多次传递仍统一阻断旧值传播()
    {
        var slot = Local("slot");
        var initialize = new Instruction(0, OpCode.Move, slot, new Immediate(0));
        var firstCall = new Instruction(1, OpCode.CallVoid, new StringLiteral("First"), new AddressOf(slot));
        var secondCall = new Instruction(2, OpCode.CallVoid, new StringLiteral("Second"), new AddressOf(slot));
        var readAfterCalls = new Instruction(3, OpCode.Return, slot);
        var cfg = Graph(initialize, firstCall, secondCall, readAfterCalls);

        SsaSimplifier.Run(cfg, []);

        Assert.That(readAfterCalls.Operands[0], Is.SameAs(slot));
    }

    [Test]
    [Category("异常输入")]
    public void 未取地址的普通零值局部仍执行常量传播()
    {
        var source = Local("source");
        var destination = Local("destination");
        var initialize = new Instruction(0, OpCode.Move, source, new Immediate(0));
        var copy = new Instruction(1, OpCode.Move, destination, source);
        var use = new Instruction(2, OpCode.Return, destination);
        var cfg = Graph(initialize, copy, use);

        SsaSimplifier.Run(cfg, []);

        Assert.That(use.Operands[0], Is.TypeOf<Immediate>());
        Assert.That(((Immediate)use.Operands[0]).Value, Is.Zero);
    }

    private static ISILControlFlowGraph Graph(params Instruction[] instructions)
        => new([.. instructions]);

    private static LocalVariable Local(string name)
        => new(name, new Register(null, name));
}
