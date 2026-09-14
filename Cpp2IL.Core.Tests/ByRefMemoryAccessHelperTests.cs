using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class ByRefMemoryAccessHelperTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    [Category("基本功能")]
    public void 嵌套无托管值类型通过完整布局证明()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var mscorlib = app.GetAssemblyByName("mscorlib")!;
        var pairDefinition = mscorlib.GetTypeByFullName("System.Collections.Generic.KeyValuePair`2")!;
        var nullableDefinition = mscorlib.GetTypeByFullName("System.Nullable`1")!;
        var nested = nullableDefinition.MakeGenericInstanceType([app.SystemTypes.SystemInt32Type]);
        var pair = pairDefinition.MakeGenericInstanceType([nested, app.SystemTypes.SystemInt32Type]);
        var size = GenericInstanceFieldLayout.GetSizeAndAlignment(pair, 8)?.Size;

        Assert.That(size, Is.GreaterThan(0));
        Assert.That(ByRefMemoryAccessHelper.HasExactKnownUnmanagedLayout(pair, 8, checked((int)size!.Value)), Is.True);
    }

    [Test]
    [Category("异常输入")]
    public void 嵌套引用值类型拒绝完整布局证明()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var mscorlib = app.GetAssemblyByName("mscorlib")!;
        var pairDefinition = mscorlib.GetTypeByFullName("System.Collections.Generic.KeyValuePair`2")!;
        var nullableDefinition = mscorlib.GetTypeByFullName("System.Nullable`1")!;
        var nestedReferenceValue = nullableDefinition.MakeGenericInstanceType([app.SystemTypes.SystemStringType]);
        var pair = pairDefinition.MakeGenericInstanceType([nestedReferenceValue, app.SystemTypes.SystemInt32Type]);
        var size = GenericInstanceFieldLayout.GetSizeAndAlignment(pair, 8)?.Size;

        Assert.That(size, Is.GreaterThan(0));
        Assert.That(ByRefMemoryAccessHelper.HasExactKnownUnmanagedLayout(pair, 8, checked((int)size!.Value)), Is.False);
    }

    [Test]
    [Category("边界值")]
    public void 注入闭合泛型标量聚合保持原始布局证明()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var pair = new InjectedTypeAnalysisContext(
            app.Assemblies[0],
            "ByRefLayout",
            "Pair`1",
            app.GetAssemblyByName("mscorlib")!.GetTypeByFullName("System.ValueType"),
            System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.SequentialLayout);
        var parameter = new GenericParameterTypeAnalysisContext(
            "T",
            0,
            LibCpp2IL.BinaryStructures.Il2CppTypeEnum.IL2CPP_TYPE_VAR,
            System.Reflection.GenericParameterAttributes.None,
            pair);
        pair.GenericParameters.Add(parameter);
        pair.InjectFieldContext("A", parameter, System.Reflection.FieldAttributes.Public);
        pair.InjectFieldContext("B", parameter, System.Reflection.FieldAttributes.Public);
        var concrete = pair.MakeGenericInstanceType([app.SystemTypes.SystemSingleType]);
        var size = GenericInstanceFieldLayout.GetSizeAndAlignment(concrete, 8)?.Size;

        Assert.That(size, Is.GreaterThan(0));
        Assert.That(ByRefMemoryAccessHelper.HasExactKnownUnmanagedLayout(concrete, 8, checked((int)size!.Value)), Is.True);
    }
}
