using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using System.Linq;
using System.Reflection;

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
    public void 开放泛型声明使用自身类型参数构造布局实例()
    {
        var definition = Cpp2IlApi.CurrentAppContext!
            .GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;

        var owner = GenericInstanceFieldLayout.CreateLayoutOwner(definition);

        Assert.Multiple(() =>
        {
            Assert.That(owner, Is.Not.Null);
            Assert.That(owner!.GenericType, Is.SameAs(definition));
            Assert.That(owner.GenericArguments, Is.EqualTo(definition.GenericParameters));
        });
    }

    [Test]
    [Category("边界值")]
    public void 泛型派生类型从基类实例字段末尾继续布局()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var baseType = app.InjectTypeIntoAllAssemblies(
            "Cpp2IL.Core.Tests",
            "GenericLayoutBase",
            app.SystemTypes.SystemObjectType).InjectedTypes[0];
        baseType.Fields.Add(new InjectedFieldAnalysisContext(
            "BaseReference",
            app.SystemTypes.SystemStringType,
            FieldAttributes.Public,
            baseType,
            0x18));

        var derivedType = app.InjectTypeIntoAllAssemblies(
            "Cpp2IL.Core.Tests",
            "GenericLayoutDerived",
            baseType).InjectedTypes[0];
        var declared = new InjectedFieldAnalysisContext(
            "DerivedReference",
            app.SystemTypes.SystemStringType,
            FieldAttributes.Public,
            derivedType,
            0);
        derivedType.Fields.Add(declared);
        var layoutType = new GenericInstanceTypeAnalysisContext(
            derivedType,
            [app.SystemTypes.SystemStringType]);

        Assert.That(
            GenericInstanceFieldLayout.FindFieldAtOffset(layoutType, 0x20),
            Is.SameAs(declared));
    }

    [Test]
    [Category("边界值")]
    public void 已具体化泛型实例保持原布局身份()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var definition = app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var concrete = definition.MakeGenericInstanceType([app.SystemTypes.SystemStringType]);

        Assert.That(GenericInstanceFieldLayout.CreateLayoutOwner(concrete), Is.SameAs(concrete));
    }

    [Test]
    [Category("异常输入")]
    public void 非泛型类型不伪造布局实例()
    {
        var objectType = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemObjectType;

        Assert.That(GenericInstanceFieldLayout.CreateLayoutOwner(objectType), Is.Null);
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
    [Category("基本功能")]
    public void KeyValuePair开放泛型布局保留键和值的精确字段类型()
    {
        var definition = Cpp2IlApi.CurrentAppContext!
            .GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.KeyValuePair`2")!;
        var aggregate = definition.MakeGenericInstanceType(definition.GenericParameters);

        var layout = GenericInstanceFieldLayout.GetConcreteFieldLayout(aggregate);
        Assert.That(layout, Is.Not.Null);
        var concreteLayout = layout!;

        Assert.Multiple(() =>
        {
            Assert.That(concreteLayout.Select(field => field.Field.Name), Is.EqualTo(new[] { "key", "value" }));
            Assert.That(concreteLayout.Select(field => field.Offset), Is.EqualTo(new long[] { 0, 8 }));
            Assert.That(concreteLayout.Select(field => field.Size), Is.EqualTo(new long[] { 8, 8 }));
            Assert.That(concreteLayout[0].Field.FieldType, Is.SameAs(definition.GenericParameters[0]));
            Assert.That(concreteLayout[1].Field.FieldType, Is.SameAs(definition.GenericParameters[1]));
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
