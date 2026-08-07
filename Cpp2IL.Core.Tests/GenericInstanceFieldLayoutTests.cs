using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class GenericInstanceFieldLayoutTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 未装箱Enumerator尾字段从十六字节偏移恢复具体元素类型()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var enumerator = CreateEnumerator(app.SystemTypes.SystemStringType);

        var field = GenericInstanceFieldLayout.FindConcreteFieldAtOffset(enumerator, 0x10);

        Assert.Multiple(() =>
        {
            Assert.That(field, Is.Not.Null);
            Assert.That(field!.Name, Is.EqualTo("current"));
            Assert.That(field.FieldType, Is.SameAs(app.SystemTypes.SystemStringType));
        });
    }

    [Test]
    [Category("边界值")]
    public void 未装箱Enumerator零偏移对应具体List字段()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var stringType = app.SystemTypes.SystemStringType;
        var enumerator = CreateEnumerator(stringType);

        var field = GenericInstanceFieldLayout.FindConcreteFieldAtOffset(enumerator, 0);

        Assert.Multiple(() =>
        {
            Assert.That(field, Is.Not.Null);
            Assert.That(field!.Name, Is.EqualTo("list"));
            Assert.That(field.FieldType, Is.TypeOf<GenericInstanceTypeAnalysisContext>());
            Assert.That(field.FieldType.FullName, Does.Contain("System.String"));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 超出Enumerator布局的偏移不返回伪字段()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var enumerator = CreateEnumerator(app.SystemTypes.SystemStringType);

        Assert.That(
            GenericInstanceFieldLayout.FindConcreteFieldAtOffset(enumerator, 0x18),
            Is.Null);
    }

    private static GenericInstanceTypeAnalysisContext CreateEnumerator(TypeAnalysisContext elementType)
    {
        var definition = Cpp2IlApi.CurrentAppContext!
            .GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1+Enumerator")!;
        return definition.MakeGenericInstanceType([elementType]);
    }
}
