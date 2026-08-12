using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using System.Linq;

namespace Cpp2IL.Core.Tests;

public class RgctxResolverTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 相同普通类型的运行时类包装器必须视为等价()
    {
        var type = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemStringType;
        var first = new RuntimeClassTypeAnalysisContext(type, type.DeclaringAssembly);
        var second = new RuntimeClassTypeAnalysisContext(type, type.DeclaringAssembly);

        Assert.That(RgctxResolver.DescribesSameThing(first, second), Is.True);
    }

    [Test]
    [Category("边界值")]
    public void 独立创建但结构相同的泛型上下文必须收敛()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var nullableDefinition = app.Assemblies
            .SelectMany(assembly => assembly.Types)
            .Single(type => type.FullName == "System.Nullable`1");
        var firstOwner = new GenericInstanceTypeAnalysisContext(
            nullableDefinition,
            [app.SystemTypes.SystemInt32Type]);
        var secondOwner = new GenericInstanceTypeAnalysisContext(
            nullableDefinition,
            [app.SystemTypes.SystemInt32Type]);
        var first = new RgctxTableTypeAnalysisContext(firstOwner, firstOwner.DeclaringAssembly);
        var second = new RgctxTableTypeAnalysisContext(secondOwner, secondOwner.DeclaringAssembly);

        Assert.Multiple(() =>
        {
            Assert.That(firstOwner, Is.Not.SameAs(secondOwner));
            Assert.That(RgctxResolver.DescribesSameThing(first, second), Is.True);
        });
    }

    [Test]
    [Category("异常输入")]
    public void 泛型实参不同的运行时类包装器不得合并()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var nullableDefinition = app.Assemblies
            .SelectMany(assembly => assembly.Types)
            .Single(type => type.FullName == "System.Nullable`1");
        var integerOwner = new GenericInstanceTypeAnalysisContext(
            nullableDefinition,
            [app.SystemTypes.SystemInt32Type]);
        var booleanOwner = new GenericInstanceTypeAnalysisContext(
            nullableDefinition,
            [app.SystemTypes.SystemBooleanType]);
        var first = new RuntimeClassTypeAnalysisContext(integerOwner, integerOwner.DeclaringAssembly);
        var second = new RuntimeClassTypeAnalysisContext(booleanOwner, booleanOwner.DeclaringAssembly);

        Assert.That(RgctxResolver.DescribesSameThing(first, second), Is.False);
    }
}
