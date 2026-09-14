using System;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Tests;

public class OriginalRecoveryIdentitySelectionTests
{
    private static readonly OriginalRecoveryMethodIdentity First = new("程序集一", 0x06000001, 4096);
    private static readonly OriginalRecoveryMethodIdentity Second = new("程序集二", 0x06000001, 4096);

    [Test]
    [Category("基本功能")]
    public void 同名同地址不同程序集只选择完整身份且只计算一次()
    {
        var count = 0;
        var rows = new[] { (Name: ".ctor", Identity: First), (Name: ".ctor", Identity: Second) };
        var selected = IsilDumpSelectionHelper.SelectExactIdentity(rows, [Second], row =>
        {
            count++;
            return row.Identity;
        }, "测试原始身份");
        Assert.That(selected, Is.EqualTo(new[] { rows[1] }));
        Assert.That(count, Is.EqualTo(rows.Length));
    }

    [Test]
    [Category("边界值")]
    public void 身份请求反序仍保持源顺序()
    {
        var selected = IsilDumpSelectionHelper.SelectExactIdentity(new[] { First, Second },
            new[] { Second, First }, row => row, "测试原始身份");
        Assert.That(selected, Is.EqualTo(new[] { First, Second }));
    }

    [Test]
    [Category("边界值")]
    public void 空身份请求保持全范围且不求值身份选择器()
    {
        var selected = IsilDumpSelectionHelper.SelectExactIdentity<OriginalRecoveryMethodIdentity, OriginalRecoveryMethodIdentity>(
            new[] { First, Second }, [], _ => throw new InvalidOperationException("不应求值"), "测试原始身份");
        Assert.That(selected.Count, Is.EqualTo(2));
    }

    [TestCase("地址漂移")]
    [TestCase("token漂移")]
    [TestCase("程序集漂移")]
    [Category("异常输入")]
    public void 原始身份任意分量漂移都拒绝(string kind)
    {
        var changed = kind switch
        {
            "地址漂移" => First with { NativeAddress = 4100 },
            "token漂移" => First with { Token = 0x06000002 },
            _ => First with { Assembly = "其他程序集" }
        };
        Assert.Throws<InvalidOperationException>(() => IsilDumpSelectionHelper.SelectExactIdentity(
            new[] { First }, new[] { changed }, row => row, "测试原始身份"));
    }

    [Test]
    [Category("异常输入")]
    public void 原始身份请求重复拒绝()
        => Assert.Throws<ArgumentException>(() => IsilDumpSelectionHelper.SelectExactIdentity(
            new[] { First }, new[] { First, First }, row => row, "测试原始身份"));

    [Test]
    [Category("异常输入")]
    public void 原始候选身份歧义拒绝()
        => Assert.Throws<InvalidOperationException>(() => IsilDumpSelectionHelper.SelectExactIdentity(
            new[] { First, First }, new[] { First }, row => row, "测试原始身份"));
}
