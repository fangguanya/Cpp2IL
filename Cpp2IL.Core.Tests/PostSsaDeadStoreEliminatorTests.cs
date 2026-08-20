using System.Reflection;
using System.Linq;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class PostSsaDeadStoreEliminatorTests
{
    [SetUp]
    public void 准备测试应用模型()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 最后一次读取后的绝对槽局部写入被删除()
    {
        var value = new LocalVariable("value", new Register(null, "X24"));
        var first = new Instruction(0, OpCode.Move, value, new Immediate(0));
        var observe = new Instruction(1, OpCode.CallVoid, 创建已解析静态消费者(), value);
        var dead = new Instruction(2, OpCode.Move, value, new MemoryOperand(null, null, 0x59EE370, 0));
        var graph = new ISILControlFlowGraph([
            first,
            observe,
            dead,
            new Instruction(3, OpCode.Return),
        ]);

        var removed = PostSsaDeadStoreEliminator.Run(graph);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.EqualTo(1));
            Assert.That(first.OpCode, Is.EqualTo(OpCode.Move));
            Assert.That(dead.OpCode, Is.EqualTo(OpCode.Nop));
        });
    }

    [Test]
    [Category("边界值")]
    public void 跨块后继读取保留前驱中的最后定义()
    {
        var value = new LocalVariable("value", new Register(null, "X24"));
        var target = new Instruction(3, OpCode.Return, value);
        var definition = new Instruction(0, OpCode.Move, value, new Immediate(7));
        var graph = new ISILControlFlowGraph([
            definition,
            new Instruction(1, OpCode.Jump, target),
            new Instruction(2, OpCode.Return, new Immediate(0)),
            target,
        ]);

        var removed = PostSsaDeadStoreEliminator.Run(graph);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.Zero);
            Assert.That(definition.OpCode, Is.EqualTo(OpCode.Move));
        });
    }

    [Test]
    [Category("边界值")]
    public void 退SSA二地址读改写保留计数器初始化链()
    {
        var temporary = new LocalVariable("temporary", new Register(null, "TEMP"));
        var counter = new LocalVariable("counter", new Register(null, "X19"));
        var createMinusOne = new Instruction(0, OpCode.Not, temporary, new Immediate(0));
        var initialize = new Instruction(1, OpCode.Move, counter, temporary);
        var increment = new Instruction(2, OpCode.Add, counter, counter, new Immediate(1));
        var graph = new ISILControlFlowGraph([
            createMinusOne,
            initialize,
            increment,
            new Instruction(3, OpCode.Return, counter),
        ]);

        var removed = PostSsaDeadStoreEliminator.Run(graph);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.Zero);
            Assert.That(createMinusOne.OpCode, Is.EqualTo(OpCode.Not));
            Assert.That(initialize.OpCode, Is.EqualTo(OpCode.Move));
            Assert.That(increment.OpCode, Is.EqualTo(OpCode.Add));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 未使用返回值的调用仍作为有副作用指令保留()
    {
        var result = new LocalVariable("result", new Register(null, "X0"));
        var call = new Instruction(0, OpCode.Call, new Immediate(0x2000), result);
        var graph = new ISILControlFlowGraph([
            call,
            new Instruction(1, OpCode.Return),
        ]);

        var removed = PostSsaDeadStoreEliminator.Run(graph);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.Zero);
            Assert.That(call.OpCode, Is.EqualTo(OpCode.Call));
            Assert.That(graph.Instructions.Count(instruction => instruction.OpCode == OpCode.Call), Is.EqualTo(1));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 间接跳转读取的寄存器定义保持存活()
    {
        var target = new LocalVariable("target", new Register(null, "X3"));
        var definition = new Instruction(0, OpCode.Move, target, new Immediate(0x4000));
        var indirectJump = new Instruction(1, OpCode.IndirectJump, target);
        var graph = new ISILControlFlowGraph([
            definition,
            indirectJump,
        ]);

        var removed = PostSsaDeadStoreEliminator.Run(graph);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.Zero);
            Assert.That(definition.OpCode, Is.EqualTo(OpCode.Move));
            Assert.That(indirectJump.OpCode, Is.EqualTo(OpCode.IndirectJump));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 未解析直接调用不保留发射器不会读取的地址参数死写()
    {
        var exceptionSlot = new LocalVariable("exceptionSlot", new Register(null, "stack_-A8"));
        var initialize = new Instruction(0, OpCode.Move, exceptionSlot, new Immediate(0));
        var unresolvedRuntimeCall = new Instruction(
            1,
            OpCode.CallVoid,
            new Immediate(0x1F54DA4),
            new AddressOf(exceptionSlot));
        var graph = new ISILControlFlowGraph([
            initialize,
            unresolvedRuntimeCall,
            new Instruction(2, OpCode.Return),
        ]);

        var removed = PostSsaDeadStoreEliminator.Run(graph);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.EqualTo(1));
            Assert.That(initialize.OpCode, Is.EqualTo(OpCode.Nop));
            Assert.That(unresolvedRuntimeCall.OpCode, Is.EqualTo(OpCode.CallVoid));
        });
    }

    [Test]
    [Category("边界值")]
    public void 已解析托管调用继续保留实际读取的地址参数定义()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var target = 创建已解析静态消费者();
        var value = new LocalVariable(
            "value",
            new Register(null, "stack_-A8"),
            app.SystemTypes.SystemObjectType);
        var initialize = new Instruction(0, OpCode.Move, value, new Immediate(0));
        var resolvedCall = new Instruction(1, OpCode.CallVoid, target, new AddressOf(value));
        var graph = new ISILControlFlowGraph([
            initialize,
            resolvedCall,
            new Instruction(2, OpCode.Return),
        ]);

        var removed = PostSsaDeadStoreEliminator.Run(graph);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.Zero);
            Assert.That(initialize.OpCode, Is.EqualTo(OpCode.Move));
            Assert.That(resolvedCall.OpCode, Is.EqualTo(OpCode.CallVoid));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 未解析调用自身作为可观察诊断保持存在()
    {
        var input = new LocalVariable("input", new Register(null, "X1"));
        var initialize = new Instruction(0, OpCode.Move, input, new Immediate(7));
        var unresolvedCall = new Instruction(1, OpCode.CallVoid, new Immediate(0xDEADBEEF), input);
        var graph = new ISILControlFlowGraph([
            initialize,
            unresolvedCall,
            new Instruction(2, OpCode.Return),
        ]);

        var removed = PostSsaDeadStoreEliminator.Run(graph);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.EqualTo(1));
            Assert.That(initialize.OpCode, Is.EqualTo(OpCode.Nop));
            Assert.That(graph.Instructions.Count(instruction => instruction.OpCode == OpCode.CallVoid), Is.EqualTo(1));
        });
    }

    private static InjectedMethodAnalysisContext 创建已解析静态消费者()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Object")!;
        return new InjectedMethodAnalysisContext(
            owner,
            "Consume",
            app.SystemTypes.SystemVoidType,
            MethodAttributes.Public | MethodAttributes.Static,
            [app.SystemTypes.SystemObjectType]);
    }
}
