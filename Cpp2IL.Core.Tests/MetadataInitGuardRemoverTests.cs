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

    [Test]
    [Category("基本功能")]
    public void AddressAddThenLoadRecognizesInitializedFlag()
    {
        var classAddress = new LocalVariable("classAddress", new Register(null, "X8"));
        var flagAddress = new LocalVariable("flagAddress", new Register(null, "X9"));
        var testedBit = new LocalVariable("testedBit", new Register(null, "TEST_BIT_VALUE"));
        var address = new Instruction(0, OpCode.Add, flagAddress, classAddress, new Immediate(0x135));
        var test = new Instruction(
            1,
            OpCode.And,
            testedBit,
            new MemoryOperand(baseRegister: flagAddress),
            new Immediate(1));
        var guard = new Block { Instructions = [address, test] };

        Assert.That(MetadataInitGuardRemover.HasInitialisedFlagTest(guard, 0x135), Is.True);
    }

    [Test]
    [Category("基本功能")]
    public void LoadedFlagThenBitTestRecognizesInitializedFlag()
    {
        var classAddress = new LocalVariable("classAddress", new Register(null, "X8"));
        var flagAddress = new LocalVariable("flagAddress", new Register(null, "X9"));
        var loadedFlag = new LocalVariable("loadedFlag", new Register(null, "X10"));
        var testedBit = new LocalVariable("testedBit", new Register(null, "TEST_BIT_VALUE"));
        var address = new Instruction(0, OpCode.Add, flagAddress, classAddress, new Immediate(0x135));
        var load = new Instruction(1, OpCode.Move, loadedFlag, new MemoryOperand(baseRegister: flagAddress));
        var test = new Instruction(2, OpCode.And, testedBit, loadedFlag, new Immediate(1));
        var guard = new Block { Instructions = [address, load, test] };

        Assert.That(MetadataInitGuardRemover.HasInitialisedFlagTest(guard, 0x135), Is.True);
    }

    [Test]
    [Category("边界值")]
    public void DirectInitializedFlagOffsetRemainsRecognized()
    {
        var classAddress = new LocalVariable("classAddress", new Register(null, "X8"));
        var testedBit = new LocalVariable("testedBit", new Register(null, "TEST_BIT_VALUE"));
        var test = new Instruction(
            0,
            OpCode.And,
            testedBit,
            new MemoryOperand(baseRegister: classAddress, addend: 0x135),
            new Immediate(1));
        var guard = new Block { Instructions = [test] };

        Assert.That(MetadataInitGuardRemover.HasInitialisedFlagTest(guard, 0x135), Is.True);
    }

    [Test]
    [Category("异常输入")]
    public void DifferentAddressAddIsNotClassifiedAsInitializedFlag()
    {
        var classAddress = new LocalVariable("classAddress", new Register(null, "X8"));
        var flagAddress = new LocalVariable("flagAddress", new Register(null, "X9"));
        var testedBit = new LocalVariable("testedBit", new Register(null, "TEST_BIT_VALUE"));
        var address = new Instruction(0, OpCode.Add, flagAddress, classAddress, new Immediate(0x134));
        var test = new Instruction(
            1,
            OpCode.And,
            testedBit,
            new MemoryOperand(baseRegister: flagAddress),
            new Immediate(1));
        var guard = new Block { Instructions = [address, test] };

        Assert.That(MetadataInitGuardRemover.HasInitialisedFlagTest(guard, 0x135), Is.False);
    }

    private static Instruction CreateFlagTest(long address, long mask) =>
        new(
            0,
            OpCode.And,
            new LocalVariable("flag", new Register(null, "TEST_BIT_VALUE")),
            new MemoryOperand(addend: address),
            new Immediate(mask));
}
