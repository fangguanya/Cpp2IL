using System;
using System.Collections.Generic;
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

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorTests
{
    [TestCase(false, 0)]
    [TestCase(true, 0)]
    [TestCase(false, 1)]
    [TestCase(true, 1)]
    [TestCase(false, 2)]
    [TestCase(true, 2)]
    [Category("基本功能")]
    [Category("边界值")]
    public void 整字段聚合值按原始具体布局读取并装箱执行(bool nullKey, int callMode)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var generic = app.GetAssemblyByName("mscorlib")!.GetTypeByFullName("System.Collections.Generic.KeyValuePair`2")!;
        var pair = generic.MakeGenericInstanceType([app.SystemTypes.SystemObjectType, app.SystemTypes.SystemInt64Type]);
        var owner = new InjectedTypeAnalysisContext(app.Assemblies[0], "AggregateFixture", "Host",
            app.SystemTypes.SystemObjectType, System.Reflection.TypeAttributes.Public);
        var field = owner.InjectFieldContext("Payload", pair, System.Reflection.FieldAttributes.Public);
        field.Offset = 16;
        var source = new LocalVariable("source", new Register(null, "X0"), owner);
        // 等价的具体泛型上下文由不同解析入口独立构造，不应要求包装对象地址相同。
        var value = new LocalVariable("value", new Register(null, "V0", 1),
            generic.MakeGenericInstanceType([app.SystemTypes.SystemObjectType, app.SystemTypes.SystemInt64Type]));
        var boxed = new LocalVariable("boxed", new Register(null, "X1", 1), app.SystemTypes.SystemObjectType);
        var argumentType = callMode == 2 ? (TypeAnalysisContext)new ByRefTypeAnalysisContext(pair) : pair;
        var consumer = new InjectedMethodAnalysisContext(owner, "Consume", app.SystemTypes.SystemObjectType,
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static, [argumentType], ["value"]);
        var context = new InjectedMethodAnalysisContext(owner, "Read", app.SystemTypes.SystemObjectType,
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static, [owner], ["source"])
        {
            ControlFlowGraph = new ISILControlFlowGraph([
                new Instruction(0, OpCode.Move, value, new FieldReference(field, source, 16)) { MemoryAccessWidthBits = 128 },
                callMode == 0 ? new Instruction(1, OpCode.Box, boxed, value, pair)
                    : new Instruction(1, OpCode.Call, consumer, boxed, callMode == 2 ? new AddressOf(value) : value),
                new Instruction(2, OpCode.Return, boxed)
            ]),
            Locals = [source, value, boxed], ParameterLocals = [source], AnalysisWarnings = []
        };
        var core = new ModuleDefinition("mscorlib.dll");
        var coreAssembly = new AssemblyDefinition("mscorlib", new Version(4, 0, 0, 0));
        coreAssembly.Modules.Add(core);
        绑定AsmResolver系统类型(core, app.SystemTypes.SystemObjectType, "Object", TypeAttributes.Public);
        绑定AsmResolver系统类型(core, app.SystemTypes.SystemInt64Type, "Int64", TypeAttributes.Public | TypeAttributes.Sealed);
        var pairDefinition = new TypeDefinition("System.Collections.Generic", "KeyValuePair`2", TypeAttributes.Public | TypeAttributes.Sealed);
        core.TopLevelTypes.Add(pairDefinition);
        generic.PutExtraData("AsmResolverType", pairDefinition);
        var name = "WholeAggregate_" + Guid.NewGuid().ToString("N");
        var module = new ModuleDefinition(name + ".dll", new AssemblyReference(coreAssembly));
        var host = new TypeDefinition("AggregateFixture", "Host", TypeAttributes.Public | TypeAttributes.Class, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(host);
        owner.PutExtraData("AsmResolverType", host);
        var payload = new FieldDefinition("Payload", FieldAttributes.Public, pair.ToTypeSignature(module));
        host.Fields.Add(payload);
        field.PutExtraData("AsmResolverField", payload);
        var method = new MethodDefinition("Read", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Object, [new TypeDefOrRefSignature(host, false)]));
        method.ParameterDefinitions.Add(new ParameterDefinition(1, "source", (ParameterAttributes)0));
        host.Methods.Add(method);
        if (callMode != 0)
        {
            // 完整合成依赖只按真实签名装箱传入值；待验证的字段读取与调用由生产发射器生成。
            var consume = new MethodDefinition("Consume", MethodAttributes.Public | MethodAttributes.Static,
                MethodSignature.CreateStatic(module.CorLibTypeFactory.Object, [argumentType.ToTypeSignature(module)]));
            host.Methods.Add(consume);
            consumer.PutExtraData("AsmResolverMethod", consume);
            consume.CilMethodBody = new CilMethodBody();
            consume.CilMethodBody.Instructions.Add(CilOpCodes.Ldarg_0);
            if (callMode == 2)
                consume.CilMethodBody.Instructions.Add(CilOpCodes.Ldobj, pair.ToTypeSignature(module).ToTypeDefOrRef());
            consume.CilMethodBody.Instructions.Add(CilOpCodes.Box, pair.ToTypeSignature(module).ToTypeDefOrRef());
            consume.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
        }
        IlGenerator.GenerateIl(context, method);
        CilStackValidator.Validate(method.CilMethodBody!, context.FullName);
        Assert.That(method.CilMethodBody!.Instructions, Has.Exactly(callMode == 0 ? 1 : 0).Matches<CilInstruction>(instruction => instruction != null && instruction.OpCode == CilOpCodes.Box));
        var assembly = new AssemblyDefinition(name, new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        using var stream = new System.IO.MemoryStream();
        module.Write(stream);
        var runtime = System.Reflection.Assembly.Load(stream.ToArray()).GetType("AggregateFixture.Host", true)!;
        var instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(runtime);
        object? key = nullKey ? null : new object();
        var expected = new KeyValuePair<object?, long>(key, unchecked((long)0xFEDCBA9876543210UL));
        runtime.GetField("Payload")!.SetValue(instance, expected);
        var actual = (KeyValuePair<object?, long>)runtime.GetMethod("Read")!.Invoke(null, [instance])!;
        Assert.That(actual.Key, Is.SameAs(key));
        Assert.That(actual.Value, Is.EqualTo(expected.Value));
    }

    [TestCase("open_generic")]
    [TestCase("partial")]
    [TestCase("wrong_box_type")]
    [TestCase("arithmetic")]
    [TestCase("before_definition")]
    [Category("异常输入")]
    public void 整字段聚合值拒绝部分访问错误类型及非整体消费者(string mode)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var generic = app.GetAssemblyByName("mscorlib")!.GetTypeByFullName("System.Collections.Generic.KeyValuePair`2")!;
        var pair = generic.MakeGenericInstanceType(mode == "open_generic"
            ? generic.GenericParameters
            : [app.SystemTypes.SystemObjectType, app.SystemTypes.SystemInt64Type]);
        var owner = new InjectedTypeAnalysisContext(app.Assemblies[0], "AggregateFixture", "Invalid",
            app.SystemTypes.SystemObjectType, System.Reflection.TypeAttributes.Public);
        var field = owner.InjectFieldContext("Payload", pair, System.Reflection.FieldAttributes.Public);
        field.Offset = 16;
        var source = new LocalVariable("source", new Register(null, "X0"), owner);
        var value = new LocalVariable("value", new Register(null, "V0", 1), pair);
        var result = new LocalVariable("result", new Register(null, "X1", 1), app.SystemTypes.SystemObjectType);
        var load = new Instruction(0, OpCode.Move, value, new FieldReference(field, source, 16)) { MemoryAccessWidthBits = mode == "partial" ? 64 : 128 };
        var use = mode == "arithmetic"
            ? new Instruction(1, OpCode.Add, result, value, new Immediate(1))
            : new Instruction(1, OpCode.Box, result, value, mode == "wrong_box_type" ? app.SystemTypes.SystemInt64Type : pair);
        var graph = new ISILControlFlowGraph(mode == "before_definition"
            ? [use, load, new Instruction(2, OpCode.Return)] : [load, use, new Instruction(2, OpCode.Return)]);
        Assert.That(ManagedReferenceSpanRecovery.Describe(graph, 8), Is.Empty);
    }
    [TestCase("by_value", true)]
    [TestCase("by_ref", true)]
    [TestCase("instance_direct", true)]
    [TestCase("instance_address", true)]
    [TestCase("wrong_ref", false)]
    [TestCase("wrong_receiver", false)]
    [TestCase("address_to_value", false)]
    [TestCase("missing", false)]
    [TestCase("extra", false)]
    [TestCase("nested", false)]
    [TestCase("unrelated", false)]
    [Category("基本功能")]
    [Category("边界值")]
    [Category("异常输入")]
    public void 整字段聚合调用按真实实参位置和类型闭合(string mode, bool expected)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var generic = app.GetAssemblyByName("mscorlib")!.GetTypeByFullName("System.Collections.Generic.KeyValuePair`2")!;
        var pair = generic.MakeGenericInstanceType([app.SystemTypes.SystemObjectType, app.SystemTypes.SystemInt64Type]);
        var value = new LocalVariable("value", new Register(null, "RAW"), pair);
        var result = new LocalVariable("result", new Register(null, "RESULT"), app.SystemTypes.SystemObjectType);
        var instance = mode.StartsWith("instance_", StringComparison.Ordinal) || mode == "wrong_receiver";
        var parameter = mode == "by_ref" ? new ByRefTypeAnalysisContext(pair)
            : mode == "wrong_ref" ? new ByRefTypeAnalysisContext(app.SystemTypes.SystemInt64Type) : (TypeAnalysisContext)pair;
        var target = new InjectedMethodAnalysisContext(mode == "wrong_receiver" ? app.SystemTypes.SystemObjectType : pair,
            "Consume", app.SystemTypes.SystemObjectType, System.Reflection.MethodAttributes.Public
                | (instance ? 0 : System.Reflection.MethodAttributes.Static), instance ? [] : [parameter], instance ? [] : ["value"]);
        IOperand actual = mode is "by_ref" or "wrong_ref" or "address_to_value" or "instance_address"
            ? new AddressOf(value) : mode == "nested" ? new AddressOf(new AddressOf(value))
            : mode == "unrelated" ? new Immediate(0) : value;
        var operands = new List<IOperand> { target, result, actual };
        if (mode == "missing") operands.RemoveAt(2);
        if (mode == "extra") operands.Add(value);
        var instruction = new Instruction(0, OpCode.Call, operands);
        Assert.That(ManagedReferenceSpanRecovery.IsExactWholeValueCall(instruction, value), Is.EqualTo(expected));
    }

}
