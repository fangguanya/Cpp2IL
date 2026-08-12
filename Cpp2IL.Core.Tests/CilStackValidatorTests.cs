using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;

namespace Cpp2IL.Core.Tests;

public class CilStackValidatorTests
{
    [Test]
    [Category("基本功能")]
    public void 平衡方法体写入精确最大栈深度()
    {
        var body = new CilMethodBody();
        body.Instructions.Add(CilOpCodes.Ldc_I4_1);
        body.Instructions.Add(CilOpCodes.Pop);
        body.Instructions.Add(CilOpCodes.Ret);

        var result = CilStackValidator.Validate(body, "Fixture.Basic");

        Assert.Multiple(() =>
        {
            Assert.That(result.InstructionCount, Is.EqualTo(3));
            Assert.That(result.MaxStack, Is.EqualTo(1));
            Assert.That(body.MaxStack, Is.EqualTo(1));
            Assert.That(body.ComputeMaxStackOnBuild, Is.False);
        });
    }

    [Test]
    [Category("边界值")]
    public void 超过短方法体默认值的栈深度仍被精确记录()
    {
        var body = new CilMethodBody();
        for (var index = 0; index < 9; index++)
            body.Instructions.Add(CilOpCodes.Ldc_I4, index);
        for (var index = 0; index < 9; index++)
            body.Instructions.Add(CilOpCodes.Pop);
        body.Instructions.Add(CilOpCodes.Ret);

        var result = CilStackValidator.Validate(body, "Fixture.DeepStack");

        Assert.Multiple(() =>
        {
            Assert.That(result.MaxStack, Is.EqualTo(9));
            Assert.That(body.MaxStack, Is.EqualTo(9));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 空栈Pop以方法名和偏移报告失败()
    {
        var body = new CilMethodBody();
        body.Instructions.Add(CilOpCodes.Pop);
        body.Instructions.Add(CilOpCodes.Ret);

        var exception = Assert.Throws<DecompilerException>(() =>
            CilStackValidator.Validate(body, "Fixture.Invalid"));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain("CIL 栈验证失败"));
            Assert.That(exception.Message, Does.Contain("Fixture.Invalid"));
            Assert.That(exception.Message, Does.Contain("IL_0000"));
            Assert.That(exception.Message, Does.Contain("window=>IL_0000:Pop"));
            Assert.That(exception.Message, Does.Contain("IL_0001:Ret"));
        });
    }
}
