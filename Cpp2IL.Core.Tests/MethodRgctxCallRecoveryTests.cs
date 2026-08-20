using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Tests;

public class MethodRgctxCallRecoveryTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 已定型MethodInfo直接恢复间接调用目标()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = CreateMethod("Owner", app.SystemTypes.SystemVoidType);
        var target = CreateMethod("Target", app.SystemTypes.SystemVoidType);
        var methodInfo = new LocalVariable(
            "methodInfo",
            new Register(null, "X3"),
            new RuntimeMethodInfoAnalysisContext(target, target.DeclaringType!.DeclaringAssembly));
        var transfer = CreateRawTransfer(OpCode.IndirectCall, methodInfo);
        owner.ControlFlowGraph = new ISILControlFlowGraph([transfer]);

        var changed = MetadataResolver.ResolveMethodRgctxCalls(owner);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(transfer.OpCode, Is.EqualTo(OpCode.CallVoid));
            Assert.That(transfer.Operands[0], Is.SameAs(target));
        });
    }

    [Test]
    [Category("边界值")]
    public void MethodInfo零偏移取方法指针后恢复尾调用与返回值()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = CreateMethod("Owner", app.SystemTypes.SystemInt32Type);
        var target = CreateMethod("Target", app.SystemTypes.SystemInt32Type);
        var methodInfo = new LocalVariable(
            "methodInfo",
            new Register(null, "X3"),
            new RuntimeMethodInfoAnalysisContext(target, target.DeclaringType!.DeclaringAssembly));
        var pointer = new LocalVariable("methodPointer", new Register(null, "X9"));
        var loadPointer = new Instruction(0, OpCode.Move, pointer, new MemoryOperand(methodInfo));
        var transfer = CreateRawTransfer(OpCode.IndirectJump, pointer);
        owner.ControlFlowGraph = new ISILControlFlowGraph([loadPointer, transfer]);

        var changed = MetadataResolver.ResolveMethodRgctxCalls(owner);
        var block = owner.ControlFlowGraph.Blocks.Single(candidate => candidate.Instructions.Contains(transfer));

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(transfer.OpCode, Is.EqualTo(OpCode.Call));
            Assert.That(transfer.Operands[0], Is.SameAs(target));
            Assert.That(block.Instructions[^1].OpCode, Is.EqualTo(OpCode.Return));
            Assert.That(block.Instructions[^1].Operands.Single(), Is.SameAs(transfer.Operands[1]));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 无方法身份的函数指针保持间接转移()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = CreateMethod("Owner", app.SystemTypes.SystemVoidType);
        var unknownMethodInfo = new LocalVariable("unknownMethodInfo", new Register(null, "X3"));
        var pointer = new LocalVariable("methodPointer", new Register(null, "X9"));
        var loadPointer = new Instruction(0, OpCode.Move, pointer, new MemoryOperand(unknownMethodInfo));
        var transfer = CreateRawTransfer(OpCode.IndirectJump, pointer);
        owner.ControlFlowGraph = new ISILControlFlowGraph([loadPointer, transfer]);

        var changed = MetadataResolver.ResolveMethodRgctxCalls(owner);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(transfer.OpCode, Is.EqualTo(OpCode.IndirectJump));
            Assert.That(
                owner.ControlFlowGraph.Blocks.SelectMany(block => block.Instructions).Count(),
                Is.EqualTo(2));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 同一目标局部存在多条定义时保持间接转移()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = CreateMethod("Owner", app.SystemTypes.SystemVoidType);
        var target = CreateMethod("Target", app.SystemTypes.SystemVoidType);
        var firstMethodInfo = new LocalVariable(
            "firstMethodInfo",
            new Register(null, "X3"),
            new RuntimeMethodInfoAnalysisContext(target, target.DeclaringType!.DeclaringAssembly));
        var secondMethodInfo = new LocalVariable("secondMethodInfo", new Register(null, "X4"));
        var pointer = new LocalVariable("methodPointer", new Register(null, "X9"));
        var firstDefinition = new Instruction(0, OpCode.Move, pointer, new MemoryOperand(firstMethodInfo));
        var secondDefinition = new Instruction(1, OpCode.Move, pointer, new MemoryOperand(secondMethodInfo));
        var transfer = CreateRawTransfer(OpCode.IndirectJump, pointer);
        owner.ControlFlowGraph = new ISILControlFlowGraph([firstDefinition, secondDefinition, transfer]);

        var changed = MetadataResolver.ResolveMethodRgctxCalls(owner);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(transfer.OpCode, Is.EqualTo(OpCode.IndirectJump));
        });
    }

    private static InjectedMethodAnalysisContext CreateMethod(string name, TypeAnalysisContext returnType) =>
        new(
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemObjectType,
            name,
            returnType,
            MethodAttributes.Public | MethodAttributes.Static,
            []);

    private static Instruction CreateRawTransfer(OpCode opCode, IOperand target)
    {
        var operands = new IOperand[]
        {
            target,
            new LocalVariable("rawReturn", new Register(null, "X0")),
        }.Concat(Arm64CallingConventionResolver.ResolveForUnmanaged()).ToList();

        return new Instruction(1, opCode, operands);
    }
}
