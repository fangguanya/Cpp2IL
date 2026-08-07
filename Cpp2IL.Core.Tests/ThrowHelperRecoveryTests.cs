using System;
using Cpp2IL.Core.Analysis;
using Disarm;
using Disarm.InternalDisassembly;

namespace Cpp2IL.Core.Tests;

public class ThrowHelperRecoveryTests
{
    [Test]
    [Category("基本功能")]
    public void Arm64AdrpAddRestoresExceptionName()
    {
        var body = new[]
        {
            new ThrowHelperRecovery.Arm64ThrowHelperInstruction(
                ThrowHelperRecovery.Arm64ThrowHelperOperation.Adrp,
                Address: 0x1004,
                Destination: Arm64Register.X2,
                Immediate: 0x2000),
            new ThrowHelperRecovery.Arm64ThrowHelperInstruction(
                ThrowHelperRecovery.Arm64ThrowHelperOperation.AddImmediate,
                Address: 0x1008,
                Destination: Arm64Register.X2,
                Source: Arm64Register.X2,
                Immediate: 0x491),
        };

        var result = ThrowHelperRecovery.FindArm64ExceptionName(
            body,
            address => address == 0x3491 ? "NullReferenceException" : null,
            _ => null);

        Assert.That(result, Is.EqualTo("NullReferenceException"));
    }

    [Test]
    [Category("边界值")]
    public void Arm64TailBranchUsesItsOnlySuccessor()
    {
        var body = new[]
        {
            new ThrowHelperRecovery.Arm64ThrowHelperInstruction(
                ThrowHelperRecovery.Arm64ThrowHelperOperation.Branch,
                Address: 0x2000,
                Target: 0x3000),
            new ThrowHelperRecovery.Arm64ThrowHelperInstruction(
                ThrowHelperRecovery.Arm64ThrowHelperOperation.AddImmediate,
                Address: 0x2004,
                Destination: Arm64Register.X2,
                Source: Arm64Register.X2,
                Immediate: 1),
        };

        var visitedTarget = 0UL;
        var result = ThrowHelperRecovery.FindArm64ExceptionName(
            body,
            _ => null,
            target =>
            {
                visitedTarget = target;
                return "IndexOutOfRangeException";
            });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo("IndexOutOfRangeException"));
            Assert.That(visitedTarget, Is.EqualTo(0x3000));
        }
    }

    [Test]
    [Category("异常输入")]
    public void Arm64RejectsNonExceptionStringAndMissingRegisterBase()
    {
        var body = new[]
        {
            new ThrowHelperRecovery.Arm64ThrowHelperInstruction(
                ThrowHelperRecovery.Arm64ThrowHelperOperation.AddImmediate,
                Address: 0x4000,
                Destination: Arm64Register.X1,
                Source: Arm64Register.X2,
                Immediate: 0x20),
            new ThrowHelperRecovery.Arm64ThrowHelperInstruction(
                ThrowHelperRecovery.Arm64ThrowHelperOperation.Adrp,
                Address: 0x4004,
                Destination: Arm64Register.X3,
                Immediate: 0x1000),
            new ThrowHelperRecovery.Arm64ThrowHelperInstruction(
                ThrowHelperRecovery.Arm64ThrowHelperOperation.AddImmediate,
                Address: 0x4008,
                Destination: Arm64Register.X3,
                Source: Arm64Register.X3,
                Immediate: 0x40),
        };

        var result = ThrowHelperRecovery.FindArm64ExceptionName(
            body,
            _ => "System",
            _ => null);

        Assert.That(result, Is.Null);
    }

    [Test]
    [Category("异常输入")]
    public void Arm64RequiresInstructionInput()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ThrowHelperRecovery.FindArm64ExceptionName(
                null!,
                _ => null,
                _ => null));
    }
}
