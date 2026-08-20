using System.Collections.Generic;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class ManagedReferenceAddressRecoveryTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 接口槽取址后的类型测试恢复为直接接口引用()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var enumeratorType = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.IEnumerator")!;
        var disposableType = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.IDisposable")!;
        var slot = Local("enumerator", "X0", enumeratorType);
        var carrier = Local("address", "X1", enumeratorType.MakeByReferenceType());
        var result = Local("disposable", "X2", disposableType);
        var addressMove = new Instruction(0, OpCode.Move, carrier, new AddressOf(slot));
        var typeTest = new Instruction(1, OpCode.IsInst, result, new MemoryOperand(carrier), disposableType);
        var instructions = new List<Instruction> { addressMove, typeTest, new(2, OpCode.Return) };

        var rewritten = ManagedReferenceAddressRecovery.Run(instructions);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rewritten, Is.EqualTo(1));
            Assert.That(addressMove.OpCode, Is.EqualTo(OpCode.Nop));
            Assert.That(typeTest.Operands[1], Is.SameAs(slot));
        }
    }

    [Test]
    [Category("边界值")]
    public void 未定型槽由唯一引用写入反推并折叠全部零偏移访问()
    {
        var objectType = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemObjectType;
        var slot = Local("slot", "X0", null);
        var carrier = Local("address", "X1", null);
        var loaded = Local("loaded", "X2", objectType);
        var replacement = Local("replacement", "X3", objectType);
        var addressMove = new Instruction(0, OpCode.Move, carrier, new AddressOf(slot));
        var read = new Instruction(1, OpCode.Move, loaded, new MemoryOperand(carrier));
        var write = new Instruction(2, OpCode.Move, new MemoryOperand(carrier), replacement);
        var instructions = new List<Instruction> { addressMove, read, write, new(3, OpCode.Return) };

        var rewritten = ManagedReferenceAddressRecovery.Run(instructions);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rewritten, Is.EqualTo(2));
            Assert.That(read.Operands[1], Is.SameAs(slot));
            Assert.That(write.Operands[0], Is.SameAs(slot));
            Assert.That(slot.Type, Is.SameAs(objectType));
        }
    }

    [Test]
    [Category("异常输入")]
    public void 值类型地址保持原生解引用语义()
    {
        var intType = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemInt32Type;
        var slot = Local("number", "X0", intType);
        var carrier = Local("address", "X1", intType.MakeByReferenceType());
        var loaded = Local("loaded", "X2", intType);
        var addressMove = new Instruction(0, OpCode.Move, carrier, new AddressOf(slot));
        var read = new Instruction(1, OpCode.Move, loaded, new MemoryOperand(carrier));
        var instructions = new List<Instruction> { addressMove, read, new(2, OpCode.Return) };

        var rewritten = ManagedReferenceAddressRecovery.Run(instructions);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rewritten, Is.EqualTo(0));
            Assert.That(addressMove.OpCode, Is.EqualTo(OpCode.Move));
            Assert.That(read.Operands[1], Is.InstanceOf<MemoryOperand>());
        }
    }

    [Test]
    [Category("异常输入")]
    public void 同一地址还参与ref调用时整条链保持不动()
    {
        var objectType = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemObjectType;
        var slot = Local("slot", "X0", objectType);
        var carrier = Local("address", "X1", objectType.MakeByReferenceType());
        var loaded = Local("loaded", "X2", objectType);
        var addressMove = new Instruction(0, OpCode.Move, carrier, new AddressOf(slot));
        var read = new Instruction(1, OpCode.Move, loaded, new MemoryOperand(carrier));
        var call = new Instruction(2, OpCode.CallVoid, new Immediate(1), carrier);
        var instructions = new List<Instruction> { addressMove, read, call, new(3, OpCode.Return) };

        var rewritten = ManagedReferenceAddressRecovery.Run(instructions);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rewritten, Is.EqualTo(0));
            Assert.That(addressMove.OpCode, Is.EqualTo(OpCode.Move));
            Assert.That(read.Operands[1], Is.InstanceOf<MemoryOperand>());
        }
    }

    [Test]
    [Category("边界值")]
    public void 非零偏移访问保持原生地址计算()
    {
        var objectType = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemObjectType;
        var slot = Local("slot", "X0", objectType);
        var carrier = Local("address", "X1", objectType.MakeByReferenceType());
        var loaded = Local("loaded", "X2", objectType);
        var addressMove = new Instruction(0, OpCode.Move, carrier, new AddressOf(slot));
        var read = new Instruction(1, OpCode.Move, loaded, new MemoryOperand(carrier, addend: 8));
        var instructions = new List<Instruction> { addressMove, read, new(2, OpCode.Return) };

        var rewritten = ManagedReferenceAddressRecovery.Run(instructions);

        Assert.That(rewritten, Is.EqualTo(0));
        Assert.That(read.Operands[1], Is.InstanceOf<MemoryOperand>());
    }

    private static LocalVariable Local(string name, string register, TypeAnalysisContext? type)
        => new(name, new Register(null, register), type);
}
