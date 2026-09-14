namespace Cpp2IL.Core.Tests.Isil;

using Cpp2IL.Core.ISIL;

public class IsilFormattingTests
{
	[Test]
	public void ToString_FormatsJumpWithHexTarget()
	{
		var instruction = new Instruction(12, OpCode.Jump, Imm(0x1Au));

		Assert.That(instruction.ToString(), Is.EqualTo("12 Jump 001A"));
	}

	[Test]
	public void ToString_FormatsConditionalJumpWithHexTargetAndCondition()
	{
		var condition = new Register(null, "ZF");
		var instruction = new Instruction(5, OpCode.ConditionalJump, Imm(0x2Bu), condition);

		Assert.That(instruction.ToString(), Is.EqualTo("5 ConditionalJump 002B, ZF"));
	}

	[TestCase(OpCode.CallVoid)]
	[TestCase(OpCode.Call)]
	public void ToString_FormatsCallLikeOpCodesWithHexTargetAndRemainingOperands(OpCode opcode)
	{
		var arg0 = new Register(null, "rcx");
		var instruction = new Instruction(42, opcode, Imm(0x1234), arg0, Str("hello"));

		Assert.That(instruction.ToString(), Is.EqualTo($"42 {opcode} 1234, rcx, \"hello\""));
	}

	[Test]
	public void ToString_UsesDefaultPathForNonSpecialOpcode()
	{
		var destination = new Register(null, "rax");
		var source = new Register(null, "rbx");
		var instruction = new Instruction(3, OpCode.Move, destination, source);

		Assert.That(instruction.ToString(), Is.EqualTo("3 Move rax, rbx"));
	}

	[Test]
	public void ToString_ReturnWithoutOperands_DoesNotHaveTrailingSpace()
	{
		var instruction = new Instruction(17, OpCode.Return);

		Assert.That(instruction.ToString(), Is.EqualTo("17 Return"));
	}

	[Test]
	[Category("基本功能")]
	public void ConditionalSelect_UsesConditionAndBothValuesWithoutCreatingControlFlow()
	{
		var destination = new Register(null, "x0");
		var condition = new Register(null, "z");
		var whenTrue = new Register(null, "x1");
		var whenFalse = new Register(null, "x2");
		var instruction = new Instruction(
			8,
			OpCode.ConditionalSelect,
			destination,
			condition,
			whenTrue,
			whenFalse);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(instruction.Destination, Is.EqualTo(destination));
			Assert.That(instruction.Sources, Is.EqualTo(new IOperand[] { condition, whenTrue, whenFalse }));
			Assert.That(instruction.IsFallThrough, Is.True);
		}
	}

    [TestCase(OpCode.VectorCompareUnsignedHigher)]
    [TestCase(OpCode.VectorCompareUnsignedHigherOrSame)]
    [Category("基本功能")]
    public void UnsignedVectorComparison_ExposesBothVectorSourcesToLivenessAnalysis(OpCode opCode)
	{
		var destination = new Register(null, "v0");
		var left = new Register(null, "v1");
		var right = new Register(null, "v2");
		var instruction = new Instruction(
			9,
            opCode,
			destination,
			left,
			right,
			new Immediate(8),
			new Immediate(8));

		Assert.That(instruction.Sources, Is.EqualTo(new IOperand[] { left, right }));
	}

    [TestCase(OpCode.VectorCompareFloatingGreaterThan)]
    [TestCase(OpCode.VectorCompareFloatingEqual)]
    [Category("基本功能")]
    public void FloatingVectorComparison_ExposesBothVectorSourcesToLivenessAnalysis(OpCode opCode)
	{
		var destination = new Register(null, "v0");
		var left = new Register(null, "v1");
		var right = new Register(null, "v2");
		var instruction = new Instruction(
			10,
			opCode,
			destination,
			left,
			right,
			new Immediate(2),
			new Immediate(32));

		Assert.That(instruction.Sources, Is.EqualTo(new IOperand[] { left, right }));
	}
}
