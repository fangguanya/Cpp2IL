using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using System.Linq;
using System.Reflection;

namespace Cpp2IL.Core.Tests;

public class GenericInstanceFieldLayoutTests
{
    [TestCase(false, 56L)]
    [TestCase(true, 64L)]
    [Category("边界值")]
    public void 同名跨程序集泛型实参不共享错误布局(bool reverse, long expectedSize)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var assemblies = app.Assemblies.Take(2).ToArray();
        Assert.That(assemblies.Length, Is.EqualTo(2));
        var valueType = app.GetAssemblyByName("mscorlib")!.GetTypeByFullName("System.ValueType");
        var first = new InjectedTypeAnalysisContext(assemblies[0], "IdentityFixture", "SameName", valueType,
            TypeAttributes.Public | TypeAttributes.SequentialLayout);
        var second = new InjectedTypeAnalysisContext(assemblies[1], "IdentityFixture", "SameName", valueType,
            TypeAttributes.Public | TypeAttributes.SequentialLayout);
        first.InjectFieldContext("x", app.SystemTypes.SystemInt64Type, FieldAttributes.Public);
        second.InjectFieldContext("x", app.SystemTypes.SystemInt64Type, FieldAttributes.Public);
        second.InjectFieldContext("y", app.SystemTypes.SystemInt64Type, FieldAttributes.Public);
        var pair = app.GetAssemblyByName("mscorlib")!.GetTypeByFullName("System.Collections.Generic.KeyValuePair`2")!;
        var small = pair.MakeGenericInstanceType([app.SystemTypes.SystemInt64Type, first.MakeGenericInstanceType([app.SystemTypes.SystemInt32Type])]);
        var large = pair.MakeGenericInstanceType([app.SystemTypes.SystemInt64Type, second.MakeGenericInstanceType([app.SystemTypes.SystemInt32Type])]);
        Assert.That(small.FullName, Is.EqualTo(large.FullName));
        Assert.That(small, Is.Not.SameAs(large));
        var outer = CreateValue("IdentityCarrier");
        outer.InjectFieldContext("first", reverse ? large : small, FieldAttributes.Public);
        outer.InjectFieldContext("second", reverse ? small : large, FieldAttributes.Public);
        outer.InjectFieldContext("repeat", reverse ? large : small, FieldAttributes.Public);
        var instance = outer.MakeGenericInstanceType([app.SystemTypes.SystemInt32Type]);
        Assert.That(GenericInstanceFieldLayout.GetSizeAndAlignment(instance, 8), Is.EqualTo((expectedSize, 8L)));
        Assert.That(GenericInstanceFieldLayout.GetConcreteFieldLayout(instance)!.Select(field => field.Size),
            Is.EqualTo(reverse ? new long[] { 24, 16, 24 } : new long[] { 16, 24, 16 }));
    }

    [TestCase(4, 8L)]
    [TestCase(8, 16L)]
    [Category("基本功能")]
    public void 嵌套泛型字段复用布局且保留尾部填充(int pointerSize, long expectedSize)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var inner = CreateValue("NestedPointer");
        inner.InjectFieldContext("pointer", app.SystemTypes.SystemIntPtrType, FieldAttributes.Public);
        var nested = inner.MakeGenericInstanceType([app.SystemTypes.SystemInt32Type]);
        var outer = CreateValue("NestedPair");
        outer.InjectFieldContext("nested", nested, FieldAttributes.Public);
        outer.InjectFieldContext("length", app.SystemTypes.SystemInt32Type, FieldAttributes.Public);
        var instance = outer.MakeGenericInstanceType([app.SystemTypes.SystemInt32Type]);
        Assert.That(GenericInstanceFieldLayout.GetSizeAndAlignment(instance, pointerSize), Is.EqualTo((expectedSize, (long)pointerSize)));
        if (pointerSize == 8)
        {
            Assert.That(GenericInstanceFieldLayout.GetConcreteFieldLayout(instance)!.Select(field => field.Offset), Is.EqualTo(new long[] { 0, 8 }));
            Assert.That(Cpp2IL.Core.Utils.Arm64CallingConventionResolver.GeneralRegisterSlotCount(instance), Is.EqualTo(2));
        }
    }

    [Test]
    [Category("异常输入")]
    public void 值类型循环字段布局保持未知且不无限递归()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = CreateValue("RecursiveLayout");
        var instance = owner.MakeGenericInstanceType([app.SystemTypes.SystemInt32Type]);
        owner.InjectFieldContext("cycle", instance, FieldAttributes.Public);
        Assert.That(GenericInstanceFieldLayout.GetSizeAndAlignment(instance, 8), Is.Null);
    }

    [TestCase(0)]
    [TestCase(16)]
    [Category("异常输入")]
    public void 未知架构不猜测嵌套布局(int pointerSize)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = CreateValue("UnknownArchitecture");
        owner.InjectFieldContext("value", app.SystemTypes.SystemIntPtrType, FieldAttributes.Public);
        Assert.That(GenericInstanceFieldLayout.GetSizeAndAlignment(owner.MakeGenericInstanceType([app.SystemTypes.SystemInt32Type]), pointerSize), Is.Null);
    }

    [Test]
    [Category("异常输入")]
    public void 显式泛型布局不套用顺序布局()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = CreateValue("ExplicitLayout");
        owner.Attributes = (owner.Attributes & ~TypeAttributes.LayoutMask) | TypeAttributes.ExplicitLayout;
        owner.InjectFieldContext("value", app.SystemTypes.SystemIntPtrType, FieldAttributes.Public);
        Assert.That(GenericInstanceFieldLayout.GetSizeAndAlignment(owner.MakeGenericInstanceType([app.SystemTypes.SystemInt32Type]), 8), Is.Null);
    }

    private static InjectedTypeAnalysisContext CreateValue(string name)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        return new InjectedTypeAnalysisContext(app.SystemTypes.SystemObjectType.DeclaringAssembly, "LayoutFixture", name,
            app.GetAssemblyByName("mscorlib")!.GetTypeByFullName("System.ValueType"), TypeAttributes.Public | TypeAttributes.SequentialLayout);
    }

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
