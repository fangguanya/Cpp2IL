using System;
using System.Collections.Generic;
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
    [Category("基本功能")]
    public void SelectExactKeepsOnlyRequestedMethodSignature()
    {
        var selected = IsilDumpSelectionHelper.SelectExact(
            new[]
            {
                "System.Void LoadTrack1Data()",
                "System.Void LoadTrack2Data()",
                "System.Void LoadTrack3Data()"
            },
            new[] { "System.Void LoadTrack1Data()" },
            value => value,
            "ISIL 方法");

        Assert.That(selected, Is.EqualTo(new[] { "System.Void LoadTrack1Data()" }));
    }

    [Test]
    [Category("边界值")]
    public void SelectExactDistinguishesMethodOverloadSignatures()
    {
        var selected = IsilDumpSelectionHelper.SelectExact(
            new[] { "System.Void Load(System.Int32)", "System.Void Load(System.String)" },
            new[] { "System.Void Load(System.String)" },
            value => value,
            "ISIL 方法");

        Assert.That(selected, Is.EqualTo(new[] { "System.Void Load(System.String)" }));
    }

    [Test]
    [Category("异常输入")]
    public void SelectExactRejectsAmbiguousMethodSignatureAcrossTypes()
    {
        Assert.Throws<InvalidOperationException>(() => IsilDumpSelectionHelper.SelectExact(
            new[] { "System.Void MoveNext()", "System.Void MoveNext()" },
            new[] { "System.Void MoveNext()" },
            value => value,
            "ISIL 方法"));
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

    [Test]
    [Category("基本功能")]
    public void ExpandDescendantClosureIncludesRecursiveCompilerGeneratedTypes()
    {
        var candidates = new[]
        {
            "Sibling",
            "Container",
            "Container+<Run>d__1",
            "Container+<Run>d__1+IteratorState"
        };
        var children = new Dictionary<string, string[]>
        {
            ["Container"] = ["Container+<Run>d__1"],
            ["Container+<Run>d__1"] = ["Container+<Run>d__1+IteratorState"]
        };

        var selected = IsilDumpSelectionHelper.ExpandDescendantClosure(
            candidates,
            new[] { "Container" },
            value => children.TryGetValue(value, out var nested)
                ? nested
                : Array.Empty<string>());

        Assert.That(selected, Is.EqualTo(new[]
        {
            "Container",
            "Container+<Run>d__1",
            "Container+<Run>d__1+IteratorState"
        }));
    }

    [Test]
    [Category("边界值")]
    public void ExpandDescendantClosureKeepsNestedRootBoundaryAndDeduplicatesCycles()
    {
        var candidates = new[] { "Container", "Container+Nested", "Container+Sibling" };
        var children = new Dictionary<string, string[]>
        {
            ["Container"] = ["Container+Nested", "Container+Sibling"],
            ["Container+Nested"] = ["Container+Nested"]
        };

        var selected = IsilDumpSelectionHelper.ExpandDescendantClosure(
            candidates,
            new[] { "Container+Nested", "Container+Nested" },
            value => children.TryGetValue(value, out var nested)
                ? nested
                : Array.Empty<string>());

        Assert.That(selected, Is.EqualTo(new[] { "Container+Nested" }));
    }

    [Test]
    [Category("异常输入")]
    public void ExpandDescendantClosureRejectsChildOutsideCandidateUniverse()
    {
        Assert.Throws<InvalidOperationException>(() =>
            IsilDumpSelectionHelper.ExpandDescendantClosure(
                new[] { "Container" },
                new[] { "Container" },
                _ => new[] { "Container+Missing" }));
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
