using System.Collections.Generic;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests;

public class HiddenReturnBufferRecoveryTests
{
    [Test]
    [Category("基本功能")]
    public void DirectAddressMoveResolvesReturnLocal()
    {
        var stack = Local("stack_-D8", 1);
        var x8 = Local("X8", 2);
        var definition = new Instruction(0, OpCode.Move, x8, new AddressOf(stack));

        var resolved = HiddenReturnBufferRecovery.TryResolveAddressedLocal(
            x8,
            new Dictionary<LocalVariable, Instruction> { [x8] = definition },
            out var actual);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolved, Is.True);
            Assert.That(actual, Is.SameAs(stack));
        }
    }

    [Test]
    [Category("边界值")]
    public void CopyChainKeepsExactReturnLocalIdentity()
    {
        var stack = Local("stack_0", 1);
        var address = Local("X8", 2);
        var copy = Local("X8", 3);
        var definitions = new Dictionary<LocalVariable, Instruction>
        {
            [address] = new Instruction(0, OpCode.Move, address, new AddressOf(stack)),
            [copy] = new Instruction(1, OpCode.Move, copy, address),
        };

        Assert.That(
            HiddenReturnBufferRecovery.TryResolveAddressedLocal(copy, definitions, out var actual),
            Is.True);
        Assert.That(actual, Is.SameAs(stack));
    }

    [Test]
    [Category("异常输入")]
    public void MissingDefinitionIsRejected()
    {
        var unresolved = Local("X8", 1);

        Assert.That(
            HiddenReturnBufferRecovery.TryResolveAddressedLocal(
                unresolved,
                new Dictionary<LocalVariable, Instruction>(),
                out _),
            Is.False);
    }

    private static LocalVariable Local(string name, int number)
        => new(name, new Register(number, name));
}
