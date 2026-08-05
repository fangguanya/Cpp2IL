using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Tests;

public class Arm64CallingConventionResolverTests
{
    [Test]
    [Category("基本功能")]
    public void RawArgumentsCoverAllAapcs64RegistersInOrder()
    {
        Assert.That(
            Arm64CallingConventionResolver.RawArgumentRegisterNames,
            Is.EqualTo(new[]
            {
                "X0", "X1", "X2", "X3", "X4", "X5", "X6", "X7",
                "V0", "V1", "V2", "V3", "V4", "V5", "V6", "V7"
            }));
    }

    [Test]
    [Category("边界值")]
    public void IndirectCallAcceptsExactRawRegisterLayout()
    {
        var operands = new IOperand[]
        {
            new Register(null, "X9"),
            new Register(null, "X0")
        }.Concat(Arm64CallingConventionResolver.ResolveForUnmanaged()).ToList();
        var call = new Instruction(0, OpCode.IndirectCall, operands);

        Assert.That(Arm64CallingConventionResolver.HasRawArgumentLayout(call), Is.True);
    }

    [Test]
    [Category("边界值")]
    public void IndirectJumpAcceptsExactRawRegisterLayout()
    {
        var operands = new IOperand[]
        {
            new Register(null, "X9"),
            new Register(null, "X0")
        }.Concat(Arm64CallingConventionResolver.ResolveForUnmanaged()).ToList();
        var jump = new Instruction(0, OpCode.IndirectJump, operands);

        Assert.That(Arm64CallingConventionResolver.HasRawArgumentLayout(jump), Is.True);
    }

    [Test]
    [Category("异常输入")]
    public void MissingFinalFloatingRegisterIsRejected()
    {
        var operands = new IOperand[]
        {
            new Register(null, "X9"),
            new Register(null, "X0")
        }.Concat(Arm64CallingConventionResolver.ResolveForUnmanaged()).ToList();
        operands.RemoveAt(operands.Count - 1);
        var call = new Instruction(0, OpCode.IndirectCall, operands);

        Assert.That(Arm64CallingConventionResolver.HasRawArgumentLayout(call), Is.False);
    }
}
