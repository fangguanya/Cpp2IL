using Cpp2IL.Core.OutputFormats;

namespace Cpp2IL.Core.Tests;

public class DllIlRecoveryFailureLedgerTests
{
    [TestCase("CIL 栈验证失败：method=A", "CIL_STACK_IMBALANCE")]
    [TestCase("CIL 标签验证失败：method=A", "CIL_LABEL_INVALID")]
    [TestCase("Type and field resolution not settling!", "TYPE_FIELD_NOT_SETTLING")]
    [TestCase("Late call and address type resolution not settling!", "LATE_CALL_TYPE_NOT_SETTLING")]
    [TestCase("Stack state not settling!", "STACK_STATE_NOT_SETTLING")]
    [TestCase("unknown analysis failure", "ANALYSIS_FAILURE")]
    [Category("基本功能")]
    [Category("边界值")]
    [Category("异常输入")]
    public void 故障必须进入稳定类别(string detail, string expected)
    {
        Assert.That(AsmResolverDllOutputFormatIlRecovery.ClassifyFailure(detail), Is.EqualTo(expected));
    }
}
