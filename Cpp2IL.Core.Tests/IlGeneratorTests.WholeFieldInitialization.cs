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
using Cpp2IL.Core.Utils;
using Cpp2IL.Core.Utils.AsmResolver;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorTests
{
    [TestCase(false, false, "正常")]
    [TestCase(false, true, "正常")]
    [TestCase(true, false, "正常")]
    [TestCase(true, true, "正常")]
    [TestCase(false, false, "非构造器")]
    [TestCase(true, true, "非构造器")]
    [TestCase(false, true, "错误归属")]
    [TestCase(true, false, "错误归属")]
    [TestCase(false, false, "缺少特殊标志")]
    [TestCase(true, true, "缺少特殊标志")]
    [Category("基本功能")]
    [Category("边界值")]
    [Category("异常输入")]
    public void 整字段只读初始化通过精确构造器身份保留全部位(bool isStatic, bool useDouble, string boundary)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var scalar = useDouble ? app.SystemTypes.SystemDoubleType : app.SystemTypes.SystemSingleType;
        var pair = new InjectedTypeAnalysisContext(app.Assemblies[0], "WholeField", "Pair`1",
            app.GetAssemblyByName("mscorlib")!.GetTypeByFullName("System.ValueType"),
            System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.SequentialLayout);
        var parameter = new GenericParameterTypeAnalysisContext("T", 0, Il2CppTypeEnum.IL2CPP_TYPE_VAR,
            System.Reflection.GenericParameterAttributes.None, pair);
        pair.GenericParameters.Add(parameter);
        var fields = new[] { "A", "B" }.Select(name => pair.InjectFieldContext(name, parameter, System.Reflection.FieldAttributes.Public)).ToArray();
        var concrete = pair.MakeGenericInstanceType([scalar]);
        var owner = new InjectedTypeAnalysisContext(app.Assemblies[0], "WholeField", "Owner", app.SystemTypes.SystemObjectType, System.Reflection.TypeAttributes.Public);
        var caller = boundary == "错误归属"
            ? new InjectedTypeAnalysisContext(app.Assemblies[0], "WholeField", "Caller", app.SystemTypes.SystemObjectType, System.Reflection.TypeAttributes.Public) : owner;
        var field = owner.InjectFieldContext("Value", concrete, System.Reflection.FieldAttributes.Public | System.Reflection.FieldAttributes.InitOnly
            | (isStatic ? System.Reflection.FieldAttributes.Static : 0));
        field.Offset = isStatic ? 0 : 16;
        var receiver = new LocalVariable("this", new Register(null, "opaqueReceiver"), owner) { IsThis = !isStatic };
        var value = new LocalVariable("bits", new Register(null, "opaqueBits"), app.SystemTypes.SystemUInt64Type);
        var reference = new FieldReference(field, receiver, field.Offset);
        var width = useDouble ? 128 : 64;
        var low = useDouble ? 0x8000000000000000UL : 0x7FC0123480000000UL;
        var high = useDouble ? 0x7FF8000000001234UL : 0;
        Assert.That(ManagedFieldSpanRecoveryHelper.TryDescribe(reference, 8, width / 8, out var span, out var failure), Is.True, failure);
        Assert.That(span.WholeAggregateField, Is.SameAs(field));
        var methodName = boundary == "非构造器" ? "Assign" : isStatic ? ".cctor" : ".ctor";
        var flags = System.Reflection.MethodAttributes.Public | (isStatic ? System.Reflection.MethodAttributes.Static : 0)
            | (boundary == "缺少特殊标志" ? 0 : System.Reflection.MethodAttributes.SpecialName | System.Reflection.MethodAttributes.RTSpecialName);
        var context = new InjectedMethodAnalysisContext(caller, methodName, app.SystemTypes.SystemVoidType, flags, [], [])
        {
            Locals = [receiver, value], AnalysisWarnings = [],
            ControlFlowGraph = new ISILControlFlowGraph([
                new Instruction(0, OpCode.Move, value, new NativeBitPatternLiteral(low, high, width)) { MemoryAccessWidthBits = width },
                new Instruction(1, OpCode.Move, reference, value) { MemoryAccessWidthBits = width },
                new Instruction(2, OpCode.Return)])
        };
        var core = new ModuleDefinition("mscorlib.dll");
        var coreAssembly = new AssemblyDefinition("mscorlib", new Version(4, 0, 0, 0)); coreAssembly.Modules.Add(core);
        绑定AsmResolver系统类型(core, app.SystemTypes.SystemObjectType, "Object", TypeAttributes.Public);
        绑定AsmResolver系统类型(core, scalar, useDouble ? "Double" : "Single", TypeAttributes.Public | TypeAttributes.Sealed);
        绑定AsmResolver系统类型(core, app.SystemTypes.SystemUInt64Type, "UInt64", TypeAttributes.Public | TypeAttributes.Sealed);
        var name = "WholeField_" + Guid.NewGuid().ToString("N");
        var module = new ModuleDefinition(name + ".dll", new AssemblyReference(coreAssembly));
        var pairType = new TypeDefinition("WholeField", "Pair`1", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout,
            module.CorLibTypeFactory.CorLibScope.CreateTypeReference("System", "ValueType"));
        pairType.GenericParameters.Add(new GenericParameter("T", (GenericParameterAttributes)0)); module.TopLevelTypes.Add(pairType);
        pair.PutExtraData("AsmResolverType", pairType);
        foreach (var member in fields)
        {
            var definition = new FieldDefinition(member.Name, FieldAttributes.Public, parameter.ToTypeSignature(module));
            pairType.Fields.Add(definition); member.PutExtraData("AsmResolverField", definition);
        }
        var ownerType = new TypeDefinition("WholeField", "Owner", TypeAttributes.Public, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(ownerType); owner.PutExtraData("AsmResolverType", ownerType);
        var callerType = ownerType;
        if (!ReferenceEquals(caller, owner))
        {
            callerType = new TypeDefinition("WholeField", "Caller", TypeAttributes.Public, module.CorLibTypeFactory.Object.Type);
            module.TopLevelTypes.Add(callerType); caller.PutExtraData("AsmResolverType", callerType);
        }
        var target = new FieldDefinition("Value", (FieldAttributes)field.Attributes, concrete.ToTypeSignature(module));
        ownerType.Fields.Add(target); field.PutExtraData("AsmResolverField", target);
        var method = new MethodDefinition(methodName, (MethodAttributes)flags,
            isStatic ? MethodSignature.CreateStatic(module.CorLibTypeFactory.Void) : MethodSignature.CreateInstance(module.CorLibTypeFactory.Void));
        callerType.Methods.Add(method);
        if (!isStatic && boundary == "正常")
        {
            // 正向构造器保留真实 Object 初始化调用，不通过省略基类调用获取只读写入资格。
            var constructor = app.SystemTypes.SystemObjectType.Methods.Single(m => m.Name == ".ctor" && m.Parameters.Count == 0);
            var coreObject = core.TopLevelTypes.Single(t => t.Name == "Object");
            var definition = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RuntimeSpecialName,
                MethodSignature.CreateInstance(core.CorLibTypeFactory.Void));
            coreObject.Methods.Add(definition); constructor.PutExtraData("AsmResolverMethod", definition);
            context.ControlFlowGraph.Blocks[0].Instructions.Insert(0, new Instruction(-1, OpCode.CallVoid, constructor, receiver));
        }
        if (boundary != "正常")
        {
            Assert.That(Assert.Throws<UnresolvedCilSemanticException>(() => IlGenerator.GenerateIl(context, method))!.Message,
                Does.Contain("READONLY_NATIVE_STORE"));
            return;
        }
        IlGenerator.GenerateIl(context, method);
        CilStackValidator.Validate(method.CilMethodBody!, context.FullName);
        Assert.That(method.CilMethodBody!.Instructions.Count(i => i.OpCode == (isStatic ? CilOpCodes.Stsfld : CilOpCodes.Stfld) && ReferenceEquals(i.Operand, target)), Is.EqualTo(1));
        Assert.That(method.CilMethodBody.Instructions.Any(i => i.OpCode == CilOpCodes.Stind_I8), Is.False);
        var assembly = new AssemblyDefinition(name, new Version(1, 0, 0, 0)); assembly.Modules.Add(module);
        using var bytes = new System.IO.MemoryStream(); module.Write(bytes);
        var runtime = System.Reflection.Assembly.Load(bytes.ToArray()).GetType("WholeField.Owner")!;
        var instance = isStatic ? null : Activator.CreateInstance(runtime);
        var box = runtime.GetField("Value")!.GetValue(instance)!;
        var actual = new[] { "A", "B" }.Select(member => box.GetType().GetField(member)!.GetValue(box)!).ToArray();
        var bits = actual.Select(item => useDouble ? unchecked((ulong)BitConverter.DoubleToInt64Bits((double)item)) : unchecked((uint)BitConverter.SingleToInt32Bits((float)item))).ToArray();
        Assert.That(bits, Is.EqualTo(useDouble ? new[] { low, high } : new[] { low & uint.MaxValue, low >> 32 }));
    }
}
