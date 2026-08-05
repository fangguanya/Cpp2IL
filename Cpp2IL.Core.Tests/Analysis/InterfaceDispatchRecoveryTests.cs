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

    private static LocalVariable Local(string name)
        => new(name, new Register(null, name));
}
