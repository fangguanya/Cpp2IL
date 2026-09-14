using System;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorTests
{
    [TestCase(false, false, false)]
    [TestCase(true, false, false)]
    [TestCase(false, true, false)]
    [TestCase(true, true, false)]
    [TestCase(false, false, true)]
    [TestCase(false, true, true)]
    [Category("基本功能")]
    [Category("边界值")]
    public void 引用属性跨度真实PE通过全部访问器保留对象身份(bool isStatic, bool numeric, bool missingSetter)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var element = numeric ? app.SystemTypes.SystemDoubleType : app.SystemTypes.SystemObjectType;
        var owner = new InjectedTypeAnalysisContext(app.Assemblies[0], "PropertySpan", "Owner",
            app.SystemTypes.SystemObjectType, System.Reflection.TypeAttributes.Public);
        var callerType = new InjectedTypeAnalysisContext(app.Assemblies[0], "PropertySpan", "Caller",
            app.SystemTypes.SystemObjectType, System.Reflection.TypeAttributes.Public);
        var fields = Enumerable.Range(0, 4).Select(i =>
        {
            var field = owner.InjectFieldContext("<Value" + i + ">k__BackingField", element,
                System.Reflection.FieldAttributes.Private | (isStatic ? System.Reflection.FieldAttributes.Static : 0));
            field.Offset = (isStatic ? 0 : 16) + i * 8;
            return field;
        }).ToArray();
        var flags = System.Reflection.MethodAttributes.Public | (isStatic ? System.Reflection.MethodAttributes.Static : 0);
        var getters = fields.Select((_, i) => owner.InjectMethodContext("get_Value" + i, element, flags)).ToArray();
        var setters = fields.Select((_, i) => owner.InjectMethodContext("set_Value" + i, app.SystemTypes.SystemVoidType, flags, element)).ToArray();
        for (var i = 0; i < fields.Length; i++)
            owner.InjectPropertyContext("Value" + i, element, getters[i], setters[i], System.Reflection.PropertyAttributes.None);
        if (missingSetter) setters[3].OverrideAttributes = System.Reflection.MethodAttributes.Private;
        var receiver = new LocalVariable("owner", new Register(null, "owner"), owner);
        var value = new LocalVariable("snapshot", new Register(null, "unclassified"), element);
        var context = new InjectedMethodAnalysisContext(callerType, "Copy", app.SystemTypes.SystemVoidType,
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static, [owner], ["owner"]);
        context.ParameterLocals = [receiver];
        context.Locals = [receiver, value];
        context.AnalysisWarnings = [];
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, value, new FieldReference(fields[0], receiver, fields[0].Offset)) { MemoryAccessWidthBits = 128 },
            new Instruction(1, OpCode.Move, new FieldReference(fields[2], receiver, fields[2].Offset), value) { MemoryAccessWidthBits = 128 },
            new Instruction(2, OpCode.Return)
        ]);
        if (numeric)
            context.ControlFlowGraph.Blocks[0].Instructions.Insert(0,
                new Instruction(-1, OpCode.Move, value, new NativeBitPatternLiteral(0, 0, 128)) { MemoryAccessWidthBits = 128 });
        Assert.That(PropertyBackingFieldRecovery.Run(context), Is.Zero, "宽读取和写入均保留，不能压成首属性调用。");
        var core = new ModuleDefinition("mscorlib.dll");
        var coreAssembly = new AssemblyDefinition("mscorlib", new Version(4, 0, 0, 0));
        coreAssembly.Modules.Add(core);
        绑定AsmResolver系统类型(core, app.SystemTypes.SystemObjectType, "Object", TypeAttributes.Public);
        if (numeric) 绑定AsmResolver系统类型(core, element, "Double", TypeAttributes.Public | TypeAttributes.Sealed);
        var name = "PropertySpan_" + Guid.NewGuid().ToString("N");
        var module = new ModuleDefinition(name + ".dll", new AssemblyReference(coreAssembly));
        var host = new TypeDefinition("PropertySpan", "Owner", TypeAttributes.Public, module.CorLibTypeFactory.Object.Type);
        var caller = new TypeDefinition("PropertySpan", "Caller", TypeAttributes.Public, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(host);
        module.TopLevelTypes.Add(caller);
        owner.PutExtraData("AsmResolverType", host);
        callerType.PutExtraData("AsmResolverType", caller);
        var valueSignature = element.ToTypeSignature(module);
        for (var i = 0; i < fields.Length; i++)
        {
            var field = new FieldDefinition(fields[i].Name, FieldAttributes.Private | (isStatic ? FieldAttributes.Static : 0), valueSignature);
            host.Fields.Add(field);
            fields[i].PutExtraData("AsmResolverField", field);
            var getterSignature = isStatic ? MethodSignature.CreateStatic(valueSignature)
                : MethodSignature.CreateInstance(valueSignature);
            var setterSignature = isStatic ? MethodSignature.CreateStatic(module.CorLibTypeFactory.Void, [valueSignature])
                : MethodSignature.CreateInstance(module.CorLibTypeFactory.Void, [valueSignature]);
            var attributes = MethodAttributes.Public | (isStatic ? MethodAttributes.Static : 0);
            var getter = new MethodDefinition(getters[i].Name, attributes, getterSignature);
            var setter = new MethodDefinition(setters[i].Name, attributes, setterSignature);
            host.Methods.Add(getter);
            host.Methods.Add(setter);
            getters[i].PutExtraData("AsmResolverMethod", getter);
            setters[i].PutExtraData("AsmResolverMethod", setter);
            getter.CilMethodBody = new CilMethodBody();
            setter.CilMethodBody = new CilMethodBody();
            if (!isStatic) getter.CilMethodBody.Instructions.Add(CilOpCodes.Ldarg_0);
            getter.CilMethodBody.Instructions.Add(isStatic ? CilOpCodes.Ldsfld : CilOpCodes.Ldfld, field);
            getter.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
            if (!isStatic) setter.CilMethodBody.Instructions.Add(CilOpCodes.Ldarg_0);
            setter.CilMethodBody.Instructions.Add(isStatic ? CilOpCodes.Ldarg_0 : CilOpCodes.Ldarg_1);
            setter.CilMethodBody.Instructions.Add(isStatic ? CilOpCodes.Stsfld : CilOpCodes.Stfld, field);
            setter.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
        }
        var method = new MethodDefinition("Copy", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void, [new TypeDefOrRefSignature(host, false)]));
        method.ParameterDefinitions.Add(new ParameterDefinition(1, "owner", (ParameterAttributes)0));
        caller.Methods.Add(method);
        if (missingSetter)
        {
            Assert.Throws<UnresolvedCilSemanticException>(() => IlGenerator.GenerateIl(context, method));
            return;
        }
        IlGenerator.GenerateIl(context, method);
        CilStackValidator.Validate(method.CilMethodBody!, context.FullName);
        // 位载体还可能产生BitConverter调用；验收目标是四个精确访问器，数值转换由后面的按位往返独立验证。
        Assert.That(method.CilMethodBody!.Instructions.Where(i => i.OpCode == CilOpCodes.Call)
            .Select(i => ((IMethodDescriptor)i.Operand!).Name!.ToString())
            .Where(name => name.StartsWith("get_Value", StringComparison.Ordinal) || name.StartsWith("set_Value", StringComparison.Ordinal)),
            Is.EqualTo(new[] { "get_Value0", "get_Value1", "set_Value2", "set_Value3" }));
        Assert.That(method.CilMethodBody.Instructions.Any(i => i.OpCode == CilOpCodes.Stfld || i.OpCode == CilOpCodes.Ldfld
            || i.OpCode == CilOpCodes.Stsfld || i.OpCode == CilOpCodes.Ldsfld), Is.False, "跨类型私有字段不直接发射访问。");
        var assembly = new AssemblyDefinition(name, new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        using var stream = new System.IO.MemoryStream();
        module.Write(stream);
        var runtime = System.Reflection.Assembly.Load(stream.ToArray());
        var runtimeOwner = runtime.GetType("PropertySpan.Owner")!;
        var instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(runtimeOwner);
        foreach (var first in numeric ? new object?[] { -0.0, BitConverter.Int64BitsToDouble(0x7FF8000000001234) } : new object?[] { new object(), null })
        {
            object second = numeric ? BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8000000004321)) : new object();
            runtimeOwner.GetMethod("set_Value0")!.Invoke(instance, [first]);
            runtimeOwner.GetMethod("set_Value1")!.Invoke(instance, [second]);
            runtime.GetType("PropertySpan.Caller")!.GetMethod("Copy")!.Invoke(null, [instance]);
            var actualFirst = runtimeOwner.GetMethod("get_Value2")!.Invoke(instance, null);
            var actualSecond = runtimeOwner.GetMethod("get_Value3")!.Invoke(instance, null);
            if (numeric)
            {
                Assert.That(BitConverter.DoubleToInt64Bits((double)actualFirst!), Is.EqualTo(BitConverter.DoubleToInt64Bits((double)first!)));
                Assert.That(BitConverter.DoubleToInt64Bits((double)actualSecond!), Is.EqualTo(BitConverter.DoubleToInt64Bits((double)second)));
            }
            else
            {
                Assert.That(actualFirst, Is.SameAs(first));
                Assert.That(actualSecond, Is.SameAs(second));
            }
        }
    }
}
