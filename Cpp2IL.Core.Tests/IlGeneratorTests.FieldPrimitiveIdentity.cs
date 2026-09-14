using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorTests
{
    [TestCase(Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN, 1)]
    [TestCase(Il2CppTypeEnum.IL2CPP_TYPE_I1, 1)]
    [TestCase(Il2CppTypeEnum.IL2CPP_TYPE_U1, 1)]
    [TestCase(Il2CppTypeEnum.IL2CPP_TYPE_CHAR, 2)]
    [TestCase(Il2CppTypeEnum.IL2CPP_TYPE_I2, 2)]
    [TestCase(Il2CppTypeEnum.IL2CPP_TYPE_U2, 2)]
    [TestCase(Il2CppTypeEnum.IL2CPP_TYPE_I4, 4)]
    [TestCase(Il2CppTypeEnum.IL2CPP_TYPE_U4, 4)]
    [TestCase(Il2CppTypeEnum.IL2CPP_TYPE_I8, 8)]
    [TestCase(Il2CppTypeEnum.IL2CPP_TYPE_U8, 8)]
    [TestCase(Il2CppTypeEnum.IL2CPP_TYPE_R4, 4)]
    [TestCase(Il2CppTypeEnum.IL2CPP_TYPE_R8, 8)]
    [TestCase(Il2CppTypeEnum.IL2CPP_TYPE_I, 8)]
    [TestCase(Il2CppTypeEnum.IL2CPP_TYPE_U, 8)]
    [Category("基本功能")]
    public void 字段标量跨度使用原始核心库类型身份(Il2CppTypeEnum primitive, int width)
    {
        var type = Cpp2IlApi.CurrentAppContext!.SystemTypes.GetPrimitive(primitive);
        var reference = 创建身份跨度字段(type);
        Assert.That(ManagedFieldSpanRecoveryHelper.TryDescribe(reference, 8, width,
            out var layout, out var failure), Is.True, failure);
        Assert.That(layout.Segments, Has.Count.EqualTo(1));
        Assert.That(layout.Segments[0].SizeBytes, Is.EqualTo(width));
    }

    [TestCase("Single", false)]
    [TestCase("Single", true)]
    [TestCase("Int64", false)]
    [TestCase("Int64", true)]
    [TestCase("IntPtr", false)]
    [TestCase("IntPtr", true)]
    [Category("异常输入")]
    public void 同名非核心类型不取得原始标量布局(string name, bool isValueType)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var type = new InjectedTypeAnalysisContext(app.Assemblies[0], "System", name,
            isValueType ? app.SystemTypes.SystemValueTypeType : app.SystemTypes.SystemObjectType,
            TypeAttributes.Public);
        var reference = 创建身份跨度字段(type);
        Assert.That(type.FullName, Is.EqualTo("System." + name));
        Assert.That(ManagedFieldSpanRecoveryHelper.TryDescribe(reference, 8, name == "Single" ? 4 : 8,
            out _, out _), Is.False, "显示名称相同不构成核心库标量身份。");
    }

    [TestCase("Single")]
    [TestCase("Int64")]
    [TestCase("IntPtr")]
    [Category("边界值")]
    public void 同名引用字段仍保持GC引用槽(string name)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var type = new InjectedTypeAnalysisContext(app.Assemblies[0], "System", name,
            app.SystemTypes.SystemObjectType, TypeAttributes.Public);
        var reference = 创建身份跨度字段(type);
        Assert.That(ManagedFieldSpanRecoveryHelper.TryDescribe(reference, 8, 8,
            out var layout, out var failure, includeManagedReferences: true), Is.True, failure);
        Assert.That(layout.Segments[0].Kind,
            Is.EqualTo(ManagedFieldSpanRecoveryHelper.ScalarKind.ManagedReference));
    }

    [TestCase(4)]
    [TestCase(8)]
    [Category("边界值")]
    public void 原始本机整数跨度服从目标指针宽度(int pointerSize)
    {
        var reference = 创建身份跨度字段(Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemIntPtrType);
        Assert.That(ManagedFieldSpanRecoveryHelper.TryDescribe(reference, pointerSize, pointerSize,
            out var layout, out var failure), Is.True, failure);
        Assert.That(layout.Segments[0].Kind,
            Is.EqualTo(ManagedFieldSpanRecoveryHelper.ScalarKind.NativeInteger));
    }

    private static FieldReference 创建身份跨度字段(TypeAnalysisContext type)
    {
        // 只构造声明与原始偏移，不由测试名称赋予任何生产语义。
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = new InjectedTypeAnalysisContext(app.Assemblies[0], "FieldIdentity", "Host",
            app.SystemTypes.SystemObjectType, TypeAttributes.Public);
        var field = owner.InjectFieldContext("Value", type, FieldAttributes.Public);
        field.Offset = 16;
        return new FieldReference(field, new LocalVariable("receiver", new Register(null, "carrier"), owner), 16);
    }
}
