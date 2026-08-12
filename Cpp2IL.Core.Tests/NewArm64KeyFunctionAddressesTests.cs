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

    [Test]
    [Category("基本功能")]
    public void 直接尾分支被识别为Arm64Thunk()
    {
        var instructions = Decode(
            0x02, 0x00, 0x00, 0x14, // B 0x1008
            0x1F, 0x20, 0x03, 0xD5); // NOP

        var thunks = NewArm64KeyFunctionAddresses.FindDirectTailThunkAddresses(instructions, 0x1008);

        Assert.That(thunks, Is.EqualTo(new[] { 0x1000UL }));
    }

    [Test]
    [Category("边界值")]
    public void 普通Bl调用不被误识别为Thunk()
    {
        var instructions = Decode(0x02, 0x00, 0x00, 0x94); // BL 0x1008

        var thunks = NewArm64KeyFunctionAddresses.FindDirectTailThunkAddresses(instructions, 0x1008);

        Assert.That(thunks, Is.Empty);
    }

    [Test]
    [Category("异常输入")]
    public void 忽略目录中的已知出口地址不重复晋级()
    {
        var instructions = Decode(
            0x02, 0x00, 0x00, 0x14, // B 0x1008
            0x01, 0x00, 0x00, 0x14); // B 0x1008

        var thunks = NewArm64KeyFunctionAddresses.FindDirectTailThunkAddresses(
            instructions,
            0x1008,
            new[] { 0x1000UL });

        Assert.That(thunks, Is.EqualTo(new[] { 0x1004UL }));
    }

    [Test]
    [Category("基本功能")]
    public void 普通返回前最后一个直接调用可定位IsInst()
    {
        var instructions = Decode(
            0x02, 0x00, 0x00, 0x94, // 直接调用 0x1008
            0xC0, 0x03, 0x5F, 0xD6); // 返回

        var found = NewArm64KeyFunctionAddresses.TryGetObjectIsInstCallTarget(instructions, out var target);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.True);
            Assert.That(target, Is.EqualTo(0x1008UL));
        }
    }

    [Test]
    [Category("边界值")]
    public void GetType后接间接尾调不得登记为IsInst()
    {
        var instructions = Decode(
            0x02, 0x00, 0x00, 0x94, // 直接调用 0x1008
            0x60, 0x00, 0x1F, 0xD6); // 通过 X3 间接尾调

        var found = NewArm64KeyFunctionAddresses.TryGetObjectIsInstCallTarget(instructions, out var target);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.False);
            Assert.That(target, Is.Zero);
        }
    }

    [Test]
    [Category("异常输入")]
    public void 无直接调用的方法不产生IsInst地址()
    {
        var instructions = Decode(0xC0, 0x03, 0x5F, 0xD6); // 返回

        var found = NewArm64KeyFunctionAddresses.TryGetObjectIsInstCallTarget(instructions, out var target);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.False);
            Assert.That(target, Is.Zero);
        }
    }

    [Test]
    [Category("基本功能")]
    public void Assignability核心的唯一高频尾跳板定位为IsInst()
    {
        var instructions = Decode(
            0x04, 0x00, 0x00, 0x14, // 0x1000: B 0x1010，候选尾跳板
            0xFF, 0xFF, 0xFF, 0x97, // 0x1004: BL 0x1000，调用者一
            0xFE, 0xFF, 0xFF, 0x97, // 0x1008: BL 0x1000，调用者二
            0xC0, 0x03, 0x5F, 0xD6, // 0x100C: RET
            0x04, 0x00, 0x00, 0x94, // 0x1010: BL 0x1020，assignability核心
            0xC0, 0x03, 0x5F, 0xD6, // 0x1014: RET
            0xC0, 0x03, 0x5F, 0xD6, // 0x1018: RET
            0xC0, 0x03, 0x5F, 0xD6, // 0x101C: RET
            0xC0, 0x03, 0x5F, 0xD6); // 0x1020: RET

        var found = NewArm64KeyFunctionAddresses.TryFindObjectIsInstTailThunk(
            instructions,
            0x1020,
            out var target);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.True);
            Assert.That(target, Is.EqualTo(0x1000UL));
        }
    }

    [Test]
    [Category("边界值")]
    public void 单个真实调用者足以证明唯一IsInst尾跳板()
    {
        var instructions = Decode(
            0x03, 0x00, 0x00, 0x14, // 0x1000: B 0x100C
            0xFF, 0xFF, 0xFF, 0x97, // 0x1004: BL 0x1000
            0xC0, 0x03, 0x5F, 0xD6, // 0x1008: RET
            0x02, 0x00, 0x00, 0x94, // 0x100C: BL 0x1014
            0xC0, 0x03, 0x5F, 0xD6, // 0x1010: RET
            0xC0, 0x03, 0x5F, 0xD6); // 0x1014: RET

        Assert.That(
            NewArm64KeyFunctionAddresses.TryFindObjectIsInstTailThunk(instructions, 0x1014, out var target),
            Is.True);
        Assert.That(target, Is.EqualTo(0x1000UL));
    }

    [Test]
    [Category("异常输入")]
    public void 同强度尾跳板保持未定位以阻止误绑定()
    {
        var instructions = Decode(
            0x06, 0x00, 0x00, 0x14, // 0x1000: B 0x1018
            0x07, 0x00, 0x00, 0x14, // 0x1004: B 0x1020
            0xFE, 0xFF, 0xFF, 0x97, // 0x1008: BL 0x1000
            0xFE, 0xFF, 0xFF, 0x97, // 0x100C: BL 0x1004
            0xC0, 0x03, 0x5F, 0xD6, // 0x1010: RET
            0xC0, 0x03, 0x5F, 0xD6, // 0x1014: RET
            0x04, 0x00, 0x00, 0x94, // 0x1018: BL 0x1028
            0xC0, 0x03, 0x5F, 0xD6, // 0x101C: RET
            0x02, 0x00, 0x00, 0x94, // 0x1020: BL 0x1028
            0xC0, 0x03, 0x5F, 0xD6, // 0x1024: RET
            0xC0, 0x03, 0x5F, 0xD6); // 0x1028: RET

        var found = NewArm64KeyFunctionAddresses.TryFindObjectIsInstTailThunk(
            instructions,
            0x1028,
            out var target);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.False);
            Assert.That(target, Is.Zero);
        }
    }

    private static Arm64Instruction[] Decode(params byte[] bytes) =>
        Disassembler.Disassemble(bytes, 0x1000, new Disassembler.Options(true, true, false)).ToList().ToArray();
}
