using System.Linq;
using Cpp2IL.Core.Il2CppApiFunctions;
using Cpp2IL.Core.Utils;
using Disarm;

namespace Cpp2IL.Core.Tests;

public class NewArm64KeyFunctionAddressesTests
{
    [Test]
    [Category("基本功能")]
    public void FirstDirectCallIsSelectedAfterOrdinaryInstructions()
    {
        var instructions = Decode(
            0x1F, 0x20, 0x03, 0xD5, // NOP
            0x02, 0x00, 0x00, 0x94, // BL 0x100C
            0x03, 0x00, 0x00, 0x94); // BL 0x1014

        var found = NewArm64KeyFunctionAddresses.TryGetFirstDirectCallTarget(instructions, out var target);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.True);
            Assert.That(target, Is.EqualTo(0x100CUL));
        }
    }

    [Test]
    [Category("边界值")]
    public void DirectCallAtMethodEntryIsAccepted()
    {
        var instructions = Decode(0x02, 0x00, 0x00, 0x94); // BL 0x1008

        var found = NewArm64KeyFunctionAddresses.TryGetFirstDirectCallTarget(instructions, out var target);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.True);
            Assert.That(target, Is.EqualTo(0x1008UL));
        }
    }

    [Test]
    [Category("异常输入")]
    public void UnconditionalBranchIsNotMistakenForCall()
    {
        var instructions = Decode(0x02, 0x00, 0x00, 0x14); // B 0x1008

        var found = NewArm64KeyFunctionAddresses.TryGetFirstDirectCallTarget(instructions, out var target);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.False);
            Assert.That(target, Is.Zero);
        }
    }

    private static Arm64Instruction[] Decode(params byte[] bytes) =>
        Disassembler.Disassemble(bytes, 0x1000, new Disassembler.Options(true, true, false)).ToList().ToArray();
}
