using System.Collections.Generic;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests.Analysis;

public class InterfaceDispatchRecoveryTests
{
    [Test]
    [Category("基本功能")]
    public void EntryOffsetPlusSlotIsRecovered()
    {
        var index = Local("index");
        var definitions = new Dictionary<LocalVariable, Instruction>
        {
            [index] = new Instruction(0, OpCode.Add, index, new MemoryOperand(new Register(null, "X10")), new Immediate(2)),
        };
        var slots = new HashSet<int>();

        var matched = InterfaceDispatchRecovery.TryMatchEntryIndex(definitions, index, slots);

        Assert.Multiple(() =>
        {
            Assert.That(matched, Is.True);
            Assert.That(slots, Is.EquivalentTo(new[] { 2 }));
        });
    }

    [Test]
    [Category("边界值")]
    public void MaximumUnsignedSlotIsAccepted()
    {
        var index = Local("index");
        var definitions = new Dictionary<LocalVariable, Instruction>
        {
            [index] = new Instruction(0, OpCode.Add, index, new MemoryOperand(new Register(null, "X10")), new Immediate(ushort.MaxValue)),
        };
        var slots = new HashSet<int>();

        var matched = InterfaceDispatchRecovery.TryMatchEntryIndex(definitions, index, slots);

        Assert.Multiple(() =>
        {
            Assert.That(matched, Is.True);
            Assert.That(slots, Is.EquivalentTo(new[] { (int)ushort.MaxValue }));
        });
    }

    [Test]
    [Category("异常输入")]
    public void NegativeSlotIsRejectedWithoutMutation()
    {
        var index = Local("index");
        var definitions = new Dictionary<LocalVariable, Instruction>
        {
            [index] = new Instruction(0, OpCode.Add, index, new MemoryOperand(new Register(null, "X10")), new Immediate(-1)),
        };
        var slots = new HashSet<int>();

        var matched = InterfaceDispatchRecovery.TryMatchEntryIndex(definitions, index, slots);

        Assert.Multiple(() =>
        {
            Assert.That(matched, Is.False);
            Assert.That(slots, Is.Empty);
        });
    }

    [Test]
    [Category("基本功能")]
    public void 共享地址污染后的零偏移字段仍识别为对象类指针()
    {
        var receiver = Local("receiver");
        var klass = Local("klass");
        var definitions = new Dictionary<LocalVariable, Instruction>
        {
            // 字段实例仅承载零偏移形状；接口恢复不读取字段元数据本身。
            [klass] = new Instruction(0, OpCode.Move, klass, new FieldReference(null!, receiver, 0)),
        };

        Assert.Multiple(() =>
        {
            Assert.That(InterfaceDispatchRecovery.IsObjectHeaderClassLoad(new FieldReference(null!, receiver, 0)), Is.True);
            Assert.That(InterfaceDispatchRecovery.IsKlassLoad(definitions, klass, []), Is.True);
        });
    }

    [Test]
    [Category("边界值")]
    public void 双分支对象类指针Phi要求每路均为零偏移读取()
    {
        var receiverA = Local("receiverA");
        var receiverB = Local("receiverB");
        var klassA = Local("klassA");
        var klassB = Local("klassB");
        var merged = Local("merged");
        var definitions = new Dictionary<LocalVariable, Instruction>
        {
            [klassA] = new Instruction(0, OpCode.Move, klassA, new MemoryOperand(receiverA)),
            [klassB] = new Instruction(1, OpCode.Move, klassB, new FieldReference(null!, receiverB, 0)),
            [merged] = new Instruction(2, OpCode.Phi, merged, klassA, klassB),
        };

        Assert.That(InterfaceDispatchRecovery.IsKlassLoad(definitions, merged, []), Is.True);
    }

    [Test]
    [Category("异常输入")]
    public void 非零偏移字段不冒充对象类指针()
    {
        var receiver = Local("receiver");
        var klass = Local("klass");
        var definitions = new Dictionary<LocalVariable, Instruction>
        {
            [klass] = new Instruction(0, OpCode.Move, klass, new FieldReference(null!, receiver, 1)),
        };

        Assert.Multiple(() =>
        {
            Assert.That(InterfaceDispatchRecovery.IsObjectHeaderClassLoad(new FieldReference(null!, receiver, 1)), Is.False);
            Assert.That(InterfaceDispatchRecovery.IsKlassLoad(definitions, klass, []), Is.False);
        });
    }

    private static LocalVariable Local(string name)
        => new(name, new Register(null, name));
}
