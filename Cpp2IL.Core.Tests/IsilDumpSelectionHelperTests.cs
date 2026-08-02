using System;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Tests;

public class IsilDumpSelectionHelperTests
{
    [Test]
    [Category("基本功能")]
    public void SelectExactKeepsSourceOrderForExactSubset()
    {
        var selected = IsilDumpSelectionHelper.SelectExact(
            new[] { "Assembly-CSharp", "UnityEngine.CoreModule", "mscorlib" },
            new[] { "mscorlib", "Assembly-CSharp" },
            value => value,
            "ISIL 程序集");

        Assert.That(selected, Is.EqualTo(new[] { "Assembly-CSharp", "mscorlib" }));
    }

    [Test]
    [Category("边界值")]
    public void SelectExactReturnsAllCandidatesForEmptyFilter()
    {
        var selected = IsilDumpSelectionHelper.SelectExact(
            new[] { "GameManager", "Game.GameVars" },
            Array.Empty<string>(),
            value => value,
            "ISIL 类型");

        Assert.That(selected, Is.EqualTo(new[] { "GameManager", "Game.GameVars" }));
    }

    [Test]
    [Category("异常输入")]
    public void SelectExactRejectsMissingIdentity()
    {
        Assert.Throws<InvalidOperationException>(() => IsilDumpSelectionHelper.SelectExact(
            new[] { "GameManager" },
            new[] { "Game.GameVars" },
            value => value,
            "ISIL 类型"));
    }

    [Test]
    [Category("异常输入")]
    public void SelectExactRejectsAmbiguousIdentity()
    {
        Assert.Throws<InvalidOperationException>(() => IsilDumpSelectionHelper.SelectExact(
            new[] { "GameManager", "GameManager" },
            new[] { "GameManager" },
            value => value,
            "ISIL 类型"));
    }

    [TestCase(" ", "")]
    [TestCase("GameManager", "GameManager")]
    [Category("异常输入")]
    public void NormalizeFiltersRejectsEmptyOrDuplicateValues(string first, string second)
    {
        Assert.Throws<ArgumentException>(() => IsilDumpSelectionHelper.NormalizeFilters(
            new[] { first, second },
            "ISIL 类型"));
    }
}
