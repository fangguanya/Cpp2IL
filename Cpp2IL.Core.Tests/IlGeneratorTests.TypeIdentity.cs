using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorTests
{
    [TestCase("same", true)]
    [TestCase("recreated_generic", true)]
    [TestCase("nested_array", true)]
    [TestCase("byref", true)]
    [TestCase("same_parameter_owner", true)]
    [TestCase("different_argument", false)]
    [TestCase("generic_vs_definition", false)]
    [TestCase("array_vs_element", false)]
    [TestCase("different_parameter_owner", false)]
    [TestCase("same_name_different_context", false)]
    [TestCase("same_name_argument", false)]
    [TestCase("null", false)]
    [TestCase("byref_vs_array", false)]
    [Category("基本功能")]
    [Category("边界值")]
    [Category("异常输入")]
    public void 精确类型身份比较保留定义及递归参数而非名称(string mode, bool expected)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var generic = app.GetAssemblyByName("mscorlib")!.GetTypeByFullName("System.Collections.Generic.KeyValuePair`2")!;
        TypeAnalysisContext? left = generic.MakeGenericInstanceType([app.SystemTypes.SystemObjectType, app.SystemTypes.SystemInt64Type]);
        TypeAnalysisContext? right = generic.MakeGenericInstanceType([app.SystemTypes.SystemObjectType, app.SystemTypes.SystemInt64Type]);
        switch (mode)
        {
            case "same": right = left; break;
            case "nested_array": left = left.MakeSzArrayType(); right = right.MakeSzArrayType(); break;
            case "byref": left = new ByRefTypeAnalysisContext(left); right = new ByRefTypeAnalysisContext(right); break;
            case "same_parameter_owner":
            case "different_parameter_owner":
                left = new GenericParameterTypeAnalysisContext("T", 0, Il2CppTypeEnum.IL2CPP_TYPE_VAR, 0, generic);
                right = new GenericParameterTypeAnalysisContext("T", 0, Il2CppTypeEnum.IL2CPP_TYPE_VAR, 0,
                    mode == "same_parameter_owner" ? generic : app.SystemTypes.SystemObjectType);
                break;
            case "different_argument": right = generic.MakeGenericInstanceType([app.SystemTypes.SystemObjectType, app.SystemTypes.SystemInt32Type]); break;
            case "generic_vs_definition": right = generic; break;
            case "array_vs_element": left = left.MakeSzArrayType(); break;
            case "byref_vs_array": left = new ByRefTypeAnalysisContext(left); right = right.MakeSzArrayType(); break;
            case "same_name_different_context":
            case "same_name_argument":
                left = new InjectedTypeAnalysisContext(app.Assemblies[0], "Identity", "Same", app.SystemTypes.SystemObjectType, TypeAttributes.Public);
                right = new InjectedTypeAnalysisContext(app.Assemblies[0], "Identity", "Same", app.SystemTypes.SystemObjectType, TypeAttributes.Public);
                Assert.That(left.FullName, Is.EqualTo(right.FullName));
                if (mode == "same_name_argument")
                {
                    left = generic.MakeGenericInstanceType([left, app.SystemTypes.SystemInt64Type]);
                    right = generic.MakeGenericInstanceType([right, app.SystemTypes.SystemInt64Type]);
                }
                break;
            case "null": left = right = null; break;
        }
        Assert.That(GenericCallRebinder.TypesEquivalent(left, right, requireDefinitionIdentity: true), Is.EqualTo(expected));
    }
}
