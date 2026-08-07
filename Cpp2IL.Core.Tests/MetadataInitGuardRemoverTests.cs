using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests;

public class MetadataInitGuardRemoverTests
{
    [Test]
    [Category("基本功能")]
    public void ProvenMetadataGuardDropsConstantFlagTest()
    {
        var test = CreateFlagTest(0x05E7411F, 1);
        var guard = new Block { Instructions = [test] };

        var removed = MetadataInitGuardRemover.RemoveConstantMetadataFlagTests(guard);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(removed, Is.EqualTo(1));
            Assert.That(test.OpCode, Is.EqualTo(OpCode.Nop));
            Assert.That(test.Operands, Is.Empty);
        }
    }

    [Test]
    [Category("基本功能")]
    public void FlagTestInUniqueGuardPredecessorIsDropped()
    {
        var test = CreateFlagTest(0x05E7411F, 1);
        var prefix = new Block { Instructions = [test] };
        var guard = new Block();
        prefix.Successors.Add(guard);
        guard.Predecessors.Add(prefix);

        var removed = MetadataInitGuardRemover.RemoveConstantMetadataFlagTestsFromGuardPrefix(guard);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(removed, Is.EqualTo(1));
            Assert.That(test.OpCode, Is.EqualTo(OpCode.Nop));
        }
    }

    [Test]
    [Category("基本功能")]
    public void RecoveredFlagConditionChainIsDroppedTogether()
    {
        var loadedFlag = new LocalVariable("loadedFlag", new Register(null, "X8"));
        var testedBit = new LocalVariable("testedBit", new Register(null, "TEST_BIT_VALUE"));
        var condition = new LocalVariable("condition", new Register(null, "TEST_BIT_CONDITION"));
        var load = new Instruction(0, OpCode.Move, loadedFlag, new MemoryOperand(addend: 0x05E7411F));
        var and = new Instruction(1, OpCode.And, testedBit, loadedFlag, new Immediate(1));
        var comparison = new Instruction(2, OpCode.CheckNotEqual, condition, testedBit, new Immediate(0));
        var branch = new Instruction(3, OpCode.ConditionalJump, new Block(), condition);
        var guard = new Block { Instructions = [load, and, comparison, branch] };

        var removed = MetadataInitGuardRemover.RemoveConstantMetadataFlagTests(guard);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(removed, Is.EqualTo(3));
            Assert.That(load.OpCode, Is.EqualTo(OpCode.Nop));
            Assert.That(and.OpCode, Is.EqualTo(OpCode.Nop));
            Assert.That(comparison.OpCode, Is.EqualTo(OpCode.Nop));
            Assert.That(branch.OpCode, Is.EqualTo(OpCode.ConditionalJump));
        }
    }

    [Test]
    [Category("边界值")]
    public void ZeroAddressStillHasConstantMemoryShape()
    {
        Assert.That(
            MetadataInitGuardRemover.IsConstantMetadataFlagTest(CreateFlagTest(0, 1)),
            Is.True);
    }

    [Test]
    [Category("异常输入")]
    public void NonUnitMaskIsPreserved()
    {
        var test = CreateFlagTest(0x05E7411F, 2);
        var guard = new Block { Instructions = [test] };

        var removed = MetadataInitGuardRemover.RemoveConstantMetadataFlagTests(guard);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(removed, Is.Zero);
            Assert.That(test.OpCode, Is.EqualTo(OpCode.And));
        }
    }

    private static Instruction CreateFlagTest(long address, long mask) =>
        new(
            0,
            OpCode.And,
            new LocalVariable("flag", new Register(null, "TEST_BIT_VALUE")),
            new MemoryOperand(addend: address),
            new Immediate(mask));
}
