using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL.BinaryStructures;
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

    [Test]
    [Category("基本功能")]
    public void 类型RGCTXData的METHOD项恢复为精确MethodInfo()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var candidate = (from type in app.Assemblies.SelectMany(assembly => assembly.Types)
                         where type.Definition != null
                         from indexed in type.Definition!.RgctXs.Select((entry, index) => (entry, index))
                         where indexed.entry.type == Il2CppRGCTXDataType.IL2CPP_RGCTX_DATA_METHOD
                         select new { Owner = type, indexed.index }).FirstOrDefault();

        Assert.That(candidate, Is.Not.Null, "测试元数据应包含类型RGCTXData的METHOD项");
        var resolved = RgctxResolver.ResolveEntry(candidate!.Owner, candidate.index);

        Assert.That(resolved, Is.TypeOf<RuntimeMethodInfoAnalysisContext>());
    }

    [Test]
    [Category("边界值")]
    public void 类型RGCTXData负索引不生成伪上下文()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var nullableDefinition = app.Assemblies
            .SelectMany(assembly => assembly.Types)
            .Single(type => type.FullName == "System.Nullable`1");

        Assert.That(RgctxResolver.ResolveEntry(nullableDefinition, -1), Is.Null);
    }

    [Test]
    [Category("异常输入")]
    public void 无类型定义的泛型参数不生成伪RGCTXData()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var nullableDefinition = app.Assemblies
            .SelectMany(assembly => assembly.Types)
            .Single(type => type.FullName == "System.Nullable`1");

        Assert.That(RgctxResolver.ResolveEntry(nullableDefinition.GenericParameters.Single(), 0), Is.Null);
    }
}
