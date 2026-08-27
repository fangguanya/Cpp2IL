using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Tests;

public class IndirectTransferCallRewriterTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void Void尾调用恢复为CallVoid并追加Return()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = CreateMethod("Owner", app.SystemTypes.SystemVoidType);
        var target = CreateMethod("Target", app.SystemTypes.SystemVoidType);
        var receiver = new LocalVariable("receiver", new Register(null, "X0"), app.SystemTypes.SystemObjectType);
        var transfer = CreateRawTransfer(OpCode.IndirectJump, receiver);
        var block = CreateBlock(transfer);

        IndirectTransferCallRewriter.Rewrite(owner, transfer, block, target);

        Assert.Multiple(() =>
        {
            Assert.That(transfer.OpCode, Is.EqualTo(OpCode.CallVoid));
            Assert.That(transfer.Operands[0], Is.SameAs(target));
            Assert.That(transfer.Operands[1], Is.SameAs(receiver));
            Assert.That(block.Instructions[^1].OpCode, Is.EqualTo(OpCode.Return));
            Assert.That(block.Instructions[^1].Operands, Is.Empty);
        });
    }

    [Test]
    [Category("边界值")]
    public void 非Void尾调用保留精确返回槽()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = CreateMethod("Owner", app.SystemTypes.SystemInt32Type);
        var target = CreateMethod("Target", app.SystemTypes.SystemInt32Type);
        var receiver = new LocalVariable("receiver", new Register(null, "X0"), app.SystemTypes.SystemObjectType);
        var transfer = CreateRawTransfer(OpCode.IndirectJump, receiver);
        var block = CreateBlock(transfer);

        IndirectTransferCallRewriter.Rewrite(owner, transfer, block, target);

        Assert.Multiple(() =>
        {
            Assert.That(transfer.OpCode, Is.EqualTo(OpCode.Call));
            Assert.That(transfer.Operands[0], Is.SameAs(target));
            Assert.That(transfer.Operands[2], Is.SameAs(receiver));
            Assert.That(transfer.Operands[1], Is.TypeOf<LocalVariable>());
            Assert.That(block.Instructions[^1].Operands.Single(), Is.SameAs(transfer.Operands[1]));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 普通间接调用不追加尾返回()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = CreateMethod("Owner", app.SystemTypes.SystemVoidType);
        var target = CreateMethod("Target", app.SystemTypes.SystemVoidType);
        var receiver = new LocalVariable("receiver", new Register(null, "X0"), app.SystemTypes.SystemObjectType);
        var transfer = CreateRawTransfer(OpCode.IndirectCall, receiver);
        var block = CreateBlock(transfer);

        IndirectTransferCallRewriter.Rewrite(owner, transfer, block, target);

        Assert.Multiple(() =>
        {
            Assert.That(transfer.OpCode, Is.EqualTo(OpCode.CallVoid));
            Assert.That(block.Instructions, Has.Count.EqualTo(1));
        });
    }

    [Test]
    [Category("基本功能")]
    public void 返回槽与接收者同对象时分裂托管结果并保留接收者类型()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = CreateMethod("Owner", app.SystemTypes.SystemVoidType);
        var target = CreateMethod("Target", app.SystemTypes.SystemInt32Type);
        var receiver = new LocalVariable("receiver", new Register(null, "X0"), app.SystemTypes.SystemObjectType);
        var transfer = CreateRawTransfer(OpCode.IndirectCall, receiver);
        transfer.SetOperand(1, receiver);
        var block = CreateBlock(transfer);

        IndirectTransferCallRewriter.Rewrite(owner, transfer, block, target, receiver);

        Assert.Multiple(() =>
        {
            Assert.That(transfer.OpCode, Is.EqualTo(OpCode.Call));
            Assert.That(transfer.Operands[1], Is.TypeOf<LocalVariable>().And.Not.SameAs(receiver));
            Assert.That(((LocalVariable)transfer.Operands[1]).Type, Is.SameAs(app.SystemTypes.SystemInt32Type));
            Assert.That(transfer.Operands[2], Is.SameAs(receiver));
            Assert.That(receiver.Type, Is.SameAs(app.SystemTypes.SystemObjectType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 返回槽与接收者仅共享物理寄存器时保留既有局部身份()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = CreateMethod("Owner", app.SystemTypes.SystemVoidType);
        var target = CreateMethod("Target", app.SystemTypes.SystemInt32Type);
        var receiver = new LocalVariable("receiver", new Register(null, "X0"), app.SystemTypes.SystemObjectType);
        var transfer = CreateRawTransfer(OpCode.IndirectCall, receiver);
        var originalResult = (LocalVariable)transfer.Operands[1];
        var block = CreateBlock(transfer);

        IndirectTransferCallRewriter.Rewrite(owner, transfer, block, target, receiver);

        Assert.Multiple(() =>
        {
            Assert.That(transfer.Operands[1], Is.SameAs(originalResult));
            Assert.That(originalResult.Type, Is.SameAs(app.SystemTypes.SystemInt32Type));
            Assert.That(receiver.Type, Is.SameAs(app.SystemTypes.SystemObjectType));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 返回槽与接收者为不同对象但逻辑身份相同时仍分裂结果()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = CreateMethod("Owner", app.SystemTypes.SystemVoidType);
        var target = CreateMethod("Target", app.SystemTypes.SystemInt32Type);
        var receiver = new LocalVariable("shared", new Register(null, "X0", 3), app.SystemTypes.SystemObjectType);
        var transfer = CreateRawTransfer(OpCode.IndirectCall, receiver);
        var equivalentResult = new LocalVariable("shared", new Register(null, "X0", 3));
        transfer.SetOperand(1, equivalentResult);
        var block = CreateBlock(transfer);

        IndirectTransferCallRewriter.Rewrite(owner, transfer, block, target, receiver);

        Assert.Multiple(() =>
        {
            Assert.That(transfer.Operands[1], Is.Not.SameAs(equivalentResult));
            Assert.That(((LocalVariable)transfer.Operands[1]).Type, Is.SameAs(app.SystemTypes.SystemInt32Type));
            Assert.That(receiver.Type, Is.SameAs(app.SystemTypes.SystemObjectType));
            Assert.That(equivalentResult.Type, Is.Null);
        });
    }

    [Test]
    [Category("基本功能")]
    public void 返回槽携带不相容具体引用类型时分裂旧生命期()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = CreateMethod("Owner", app.SystemTypes.SystemVoidType);
        var target = CreateMethod("Target", app.SystemTypes.SystemInt32Type);
        var receiver = new LocalVariable("receiver", new Register(null, "X1"), app.SystemTypes.SystemObjectType);
        var transfer = CreateRawTransfer(OpCode.IndirectCall, receiver);
        var oldLifetime = new LocalVariable("oldArray", new Register(null, "X0"), app.SystemTypes.SystemStringType);
        transfer.SetOperand(1, oldLifetime);
        var block = CreateBlock(transfer);

        IndirectTransferCallRewriter.Rewrite(owner, transfer, block, target, receiver);

        Assert.Multiple(() =>
        {
            Assert.That(transfer.Operands[1], Is.Not.SameAs(oldLifetime));
            Assert.That(((LocalVariable)transfer.Operands[1]).Type, Is.SameAs(app.SystemTypes.SystemInt32Type));
            Assert.That(oldLifetime.Type, Is.SameAs(app.SystemTypes.SystemStringType));
        });
    }

    [Test]
    [Category("基本功能")]
    public void Object返回槽分裂后显式转换回具体Ssa结果()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = CreateMethod("Owner", app.SystemTypes.SystemVoidType);
        var target = CreateMethod("Current", app.SystemTypes.SystemObjectType);
        var receiver = new LocalVariable("enumerator", new Register(null, "X1"), app.SystemTypes.SystemObjectType);
        var concreteResult = new LocalVariable(
            "currentPerson",
            new Register(null, "X0", 9),
            app.SystemTypes.SystemStringType);
        var transfer = CreateRawTransfer(OpCode.IndirectCall, receiver);
        transfer.SetOperand(1, concreteResult);
        var block = CreateBlock(transfer);

        IndirectTransferCallRewriter.Rewrite(owner, transfer, block, target, receiver);

        var managedResult = (LocalVariable)transfer.Operands[1];
        Assert.Multiple(() =>
        {
            Assert.That(block.Instructions, Has.Count.EqualTo(2));
            Assert.That(managedResult, Is.Not.SameAs(concreteResult));
            Assert.That(managedResult.Type, Is.SameAs(app.SystemTypes.SystemObjectType));
            Assert.That(block.Instructions[1].OpCode, Is.EqualTo(OpCode.CastClass));
            Assert.That(block.Instructions[1].Operands[0], Is.SameAs(concreteResult));
            Assert.That(block.Instructions[1].Operands[1], Is.SameAs(managedResult));
            Assert.That(block.Instructions[1].Operands[2], Is.SameAs(app.SystemTypes.SystemStringType));
        });
    }

    [Test]
    [Category("边界值")]
    public void Object尾调用分裂结果但不写回无后继具体槽()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = CreateMethod("Owner", app.SystemTypes.SystemObjectType);
        var target = CreateMethod("Current", app.SystemTypes.SystemObjectType);
        var receiver = new LocalVariable("enumerator", new Register(null, "X1"), app.SystemTypes.SystemObjectType);
        var concreteResult = new LocalVariable(
            "currentPerson",
            new Register(null, "X0", 9),
            app.SystemTypes.SystemStringType);
        var transfer = CreateRawTransfer(OpCode.IndirectJump, receiver);
        transfer.SetOperand(1, concreteResult);
        var block = CreateBlock(transfer);

        IndirectTransferCallRewriter.Rewrite(owner, transfer, block, target, receiver);

        Assert.Multiple(() =>
        {
            Assert.That(block.Instructions, Has.Count.EqualTo(2));
            Assert.That(block.Instructions, Has.None.Matches<Instruction>(instruction =>
                instruction.OpCode == OpCode.CastClass));
            Assert.That(block.Instructions[^1].OpCode, Is.EqualTo(OpCode.Return));
            Assert.That(block.Instructions[^1].Operands.Single(), Is.SameAs(transfer.Operands[1]));
        });
    }

    [Test]
    [Category("异常输入")]
    public void Object返回槽与源操作数逻辑别名时拒绝具体槽回写()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = CreateMethod("Owner", app.SystemTypes.SystemVoidType);
        var target = CreateMethod("Current", app.SystemTypes.SystemObjectType);
        var receiver = new LocalVariable(
            "shared",
            new Register(null, "X0", 9),
            app.SystemTypes.SystemStringType);
        var equivalentResult = new LocalVariable(
            "shared",
            new Register(null, "X0", 9),
            app.SystemTypes.SystemStringType);
        var transfer = CreateRawTransfer(OpCode.IndirectCall, receiver);
        transfer.SetOperand(1, equivalentResult);
        var block = CreateBlock(transfer);

        IndirectTransferCallRewriter.Rewrite(owner, transfer, block, target, receiver);

        Assert.Multiple(() =>
        {
            Assert.That(transfer.Operands[1], Is.Not.SameAs(equivalentResult));
            Assert.That(block.Instructions, Has.Count.EqualTo(1));
            Assert.That(block.Instructions, Has.None.Matches<Instruction>(instruction =>
                instruction.OpCode == OpCode.CastClass));
            Assert.That(equivalentResult.Type, Is.SameAs(app.SystemTypes.SystemStringType));
        });
    }

    [TestCase("System.Object")]
    [TestCase("System.IntPtr")]
    [TestCase("System.UIntPtr")]
    [Category("边界值")]
    public void 返回槽为Abi占位类型时原地精确定型(string placeholderName)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = CreateMethod("Owner", app.SystemTypes.SystemVoidType);
        var target = CreateMethod("Target", app.SystemTypes.SystemInt32Type);
        var receiver = new LocalVariable("receiver", new Register(null, "X1"), app.SystemTypes.SystemObjectType);
        var placeholderType = app.GetAssemblyByName("mscorlib")!.GetTypeByFullName(placeholderName)!;
        var placeholder = new LocalVariable("result", new Register(null, "X0"), placeholderType);
        var transfer = CreateRawTransfer(OpCode.IndirectCall, receiver);
        transfer.SetOperand(1, placeholder);
        var block = CreateBlock(transfer);

        IndirectTransferCallRewriter.Rewrite(owner, transfer, block, target, receiver);

        Assert.Multiple(() =>
        {
            Assert.That(transfer.Operands[1], Is.SameAs(placeholder));
            Assert.That(placeholder.Type, Is.SameAs(app.SystemTypes.SystemInt32Type));
        });
    }

    private static InjectedMethodAnalysisContext CreateMethod(string name, TypeAnalysisContext returnType) =>
        new(
            Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemObjectType,
            name,
            returnType,
            MethodAttributes.Public,
            []);

    private static Instruction CreateRawTransfer(OpCode opCode, LocalVariable receiver)
    {
        var operands = new IOperand[]
        {
            new LocalVariable("target", new Register(null, "X9")),
            new LocalVariable("rawReturn", new Register(null, "X0"))
        }.Concat(Arm64CallingConventionResolver.ResolveForUnmanaged()).ToArray();

        // X0是虚调用接收者，覆盖原始寄存器快照中的同槽位。
        operands[2] = receiver;
        return new Instruction(0, opCode, operands.ToList());
    }

    private static Block CreateBlock(Instruction transfer) => new()
    {
        ID = 0,
        Instructions = [transfer]
    };
}
