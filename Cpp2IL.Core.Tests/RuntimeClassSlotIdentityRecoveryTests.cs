using System.Collections.Generic;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class RuntimeClassSlotIdentityRecoveryTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 同一初始化槽的后续类型读取继承唯一运行时类身份()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var type = app.SystemTypes.SystemStringType;
        var table = new LocalVariable("table", new Register(null, "X25", 1));
        var first = new LocalVariable(
            "first",
            new Register(null, "X0", 1),
            new RuntimeClassTypeAnalysisContext(type, type.DeclaringAssembly));
        var second = new LocalVariable("second", new Register(null, "X0", 2));
        var instructions = new Instruction[]
        {
            new(0, OpCode.Move, table, new MemoryOperand(addend: 0x59EFEA8)),
            new(1, OpCode.Move, first, new MemoryOperand(table)),
            new(2, OpCode.Move, second, new MemoryOperand(table)),
        };

        var groups = RuntimeClassSlotIdentityRecovery.Capture(
            instructions,
            new HashSet<ulong> { 0x59EFEA8 });
        var recovered = RuntimeClassSlotIdentityRecovery.Run(groups);

        Assert.Multiple(() =>
        {
            Assert.That(groups, Has.Count.EqualTo(1));
            Assert.That(recovered, Is.EqualTo(2));
            Assert.That(instructions[1].Operands[1], Is.SameAs(type));
            Assert.That(instructions[2].Operands[1], Is.SameAs(type));
            Assert.That(
                ((RuntimeClassTypeAnalysisContext)second.Type!).RepresentedType,
                Is.SameAs(type));
        });
    }

    [Test]
    [Category("边界值")]
    public void 退Ssa载体的多个同址定义共同收敛为同一运行时类槽()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var type = app.SystemTypes.SystemStringType;
        var table = new LocalVariable("table", new Register(null, "X23", 1));
        var first = new LocalVariable(
            "first",
            new Register(null, "X0", 1),
            new RuntimeClassTypeAnalysisContext(type, type.DeclaringAssembly));
        var second = new LocalVariable("second", new Register(null, "X0", 2));
        var instructions = new Instruction[]
        {
            new(0, OpCode.Move, table, new MemoryOperand(addend: 0x5A0D1E8)),
            new(1, OpCode.Move, first, new MemoryOperand(table)),
            new(2, OpCode.Move, table, new MemoryOperand(addend: 0x5A0D1E8)),
            new(3, OpCode.Move, second, new MemoryOperand(table)),
        };

        var groups = RuntimeClassSlotIdentityRecovery.Capture(
            instructions,
            new HashSet<ulong> { 0x5A0D1E8 });
        var recovered = RuntimeClassSlotIdentityRecovery.Run(groups);

        Assert.Multiple(() =>
        {
            Assert.That(groups, Has.Count.EqualTo(1));
            Assert.That(groups[0].Loads, Has.Count.EqualTo(2));
            Assert.That(recovered, Is.EqualTo(2));
            Assert.That(second.Type, Is.TypeOf<RuntimeClassTypeAnalysisContext>());
            Assert.That(instructions[3].Operands[1], Is.SameAs(type));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 退Ssa载体的多定义出现异址时拒绝生成运行时类槽()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var type = app.SystemTypes.SystemStringType;
        var table = new LocalVariable("table", new Register(null, "X23", 1));
        var first = new LocalVariable(
            "first",
            new Register(null, "X0", 1),
            new RuntimeClassTypeAnalysisContext(type, type.DeclaringAssembly));
        var instructions = new Instruction[]
        {
            new(0, OpCode.Move, table, new MemoryOperand(addend: 0x1000)),
            new(1, OpCode.Move, first, new MemoryOperand(table)),
            new(2, OpCode.Move, table, new MemoryOperand(addend: 0x2000)),
        };

        var groups = RuntimeClassSlotIdentityRecovery.Capture(
            instructions,
            new HashSet<ulong> { 0x1000, 0x2000 });

        Assert.Multiple(() =>
        {
            Assert.That(groups, Is.Empty);
            Assert.That(instructions[1].Operands[1], Is.TypeOf<MemoryOperand>());
        });
    }

    [Test]
    [Category("边界值")]
    public void 多个初始化槽只在各自组内传播且无种子组保持原样()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var type = app.SystemTypes.SystemStringType;
        var firstTable = new LocalVariable("firstTable", new Register(null, "X25", 1));
        var secondTable = new LocalVariable("secondTable", new Register(null, "X23", 1));
        var seed = new LocalVariable(
            "seed",
            new Register(null, "X0", 1),
            new RuntimeClassTypeAnalysisContext(type, type.DeclaringAssembly));
        var sameSlot = new LocalVariable("sameSlot", new Register(null, "X0", 2));
        var otherSlot = new LocalVariable("otherSlot", new Register(null, "X0", 3));
        var instructions = new Instruction[]
        {
            new(0, OpCode.Move, firstTable, new MemoryOperand(addend: 0x1000)),
            new(1, OpCode.Move, secondTable, new MemoryOperand(addend: 0x2000)),
            new(2, OpCode.Move, seed, new MemoryOperand(firstTable)),
            new(3, OpCode.Move, sameSlot, new MemoryOperand(firstTable)),
            new(4, OpCode.Move, otherSlot, new MemoryOperand(secondTable)),
        };

        var groups = RuntimeClassSlotIdentityRecovery.Capture(
            instructions,
            new HashSet<ulong> { 0x1000, 0x2000 });
        RuntimeClassSlotIdentityRecovery.Run(groups);

        Assert.Multiple(() =>
        {
            Assert.That(groups, Has.Count.EqualTo(2));
            Assert.That(sameSlot.Type, Is.TypeOf<RuntimeClassTypeAnalysisContext>());
            Assert.That(otherSlot.Type, Is.Null);
            Assert.That(instructions[4].Operands[1], Is.TypeOf<MemoryOperand>());
        });
    }

    [Test]
    [Category("异常输入")]
    public void 同一槽出现冲突运行时类或槽未初始化时整组保持原样()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var stringType = app.SystemTypes.SystemStringType;
        var objectType = app.SystemTypes.SystemObjectType;
        var table = new LocalVariable("table", new Register(null, "X25", 1));
        var first = new LocalVariable(
            "first",
            new Register(null, "X0", 1),
            new RuntimeClassTypeAnalysisContext(stringType, stringType.DeclaringAssembly));
        var second = new LocalVariable(
            "second",
            new Register(null, "X0", 2),
            new RuntimeClassTypeAnalysisContext(objectType, objectType.DeclaringAssembly));
        var unresolved = new LocalVariable("unresolved", new Register(null, "X0", 3));
        var instructions = new Instruction[]
        {
            new(0, OpCode.Move, table, new MemoryOperand(addend: 0x3000)),
            new(1, OpCode.Move, first, new MemoryOperand(table)),
            new(2, OpCode.Move, second, new MemoryOperand(table)),
            new(3, OpCode.Move, unresolved, new MemoryOperand(table)),
        };

        var missingGroups = RuntimeClassSlotIdentityRecovery.Capture(instructions, new HashSet<ulong>());
        var groups = RuntimeClassSlotIdentityRecovery.Capture(
            instructions,
            new HashSet<ulong> { 0x3000 });
        var recovered = RuntimeClassSlotIdentityRecovery.Run(groups);

        Assert.Multiple(() =>
        {
            Assert.That(missingGroups, Is.Empty);
            Assert.That(recovered, Is.Zero);
            Assert.That(unresolved.Type, Is.Null);
            Assert.That(instructions[3].Operands[1], Is.TypeOf<MemoryOperand>());
        });
    }
}
