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

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorTests
{
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    [Category("基本功能")]
    [Category("边界值")]
    [Category("异常输入")]
    public void 引用数组宽快照跨修改及GC按原始位置读取全部元素(bool strings, bool dynamicIndex)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var element = strings ? app.SystemTypes.SystemStringType : app.SystemTypes.SystemObjectType;
        var arrayType = element.MakeSzArrayType();
        var owner = new InjectedTypeAnalysisContext(app.Assemblies[0], "ArraySpan", "Host",
            app.SystemTypes.SystemObjectType, System.Reflection.TypeAttributes.Public);
        var fields = new[] { app.SystemTypes.SystemObjectType, element }.Select((type, index) =>
        {
            var field = owner.InjectFieldContext("Field" + index, type, System.Reflection.FieldAttributes.Public);
            field.Offset = 16 + index * 8;
            return field;
        }).ToArray();
        var array = new LocalVariable("array", new Register(null, "array"), arrayType);
        var target = new LocalVariable("target", new Register(null, "target"), owner);
        var indexLocal = new LocalVariable("index", new Register(null, "index"), app.SystemTypes.SystemInt32Type);
        var value = new LocalVariable("snapshot", new Register(null, "not_a_vector_name"), element);
        var mutate = owner.InjectMethodContext("Mutate", app.SystemTypes.SystemVoidType,
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static, [arrayType]);
        var context = new InjectedMethodAnalysisContext(owner, "Copy", app.SystemTypes.SystemVoidType,
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static,
            [arrayType, owner, app.SystemTypes.SystemInt32Type], ["array", "target", "index"]);
        context.ParameterLocals = [array, target, indexLocal];
        context.Locals = [array, target, indexLocal, value];
        context.AnalysisWarnings = [];
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, value, new ArrayAccess(array, dynamicIndex ? indexLocal : new Immediate(1))) { MemoryAccessWidthBits = 128 },
            new Instruction(1, OpCode.CallVoid, mutate, array),
            new Instruction(2, OpCode.Move, new FieldReference(fields[0], target, 16), value) { MemoryAccessWidthBits = 128 },
            new Instruction(3, OpCode.Return)
        ]);
        var core = new ModuleDefinition("mscorlib.dll");
        var coreAssembly = new AssemblyDefinition("mscorlib", new Version(4, 0, 0, 0));
        coreAssembly.Modules.Add(core);
        绑定AsmResolver系统类型(core, app.SystemTypes.SystemObjectType, "Object", TypeAttributes.Public);
        绑定AsmResolver系统类型(core, app.SystemTypes.SystemStringType, "String", TypeAttributes.Public | TypeAttributes.Sealed);
        绑定AsmResolver系统类型(core, app.SystemTypes.SystemInt32Type, "Int32", TypeAttributes.Public | TypeAttributes.Sealed);
        var name = "ArraySpan_" + Guid.NewGuid().ToString("N");
        var module = new ModuleDefinition(name + ".dll", new AssemblyReference(coreAssembly));
        var host = new TypeDefinition("ArraySpan", "Host", TypeAttributes.Public, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(host);
        owner.PutExtraData("AsmResolverType", host);
        foreach (var field in fields)
        {
            var definition = new FieldDefinition(field.Name, FieldAttributes.Public, field.FieldType.ToTypeSignature(module));
            host.Fields.Add(definition);
            field.PutExtraData("AsmResolverField", definition);
        }
        var mutation = new MethodDefinition("Mutate", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void, [arrayType.ToTypeSignature(module)]));
        host.Methods.Add(mutation);
        mutate.PutExtraData("AsmResolverMethod", mutation);
        mutation.CilMethodBody = new CilMethodBody();
        var body = mutation.CilMethodBody.Instructions;
        body.Add(CilOpCodes.Ldarg_0);
        body.Add(CilOpCodes.Ldc_I4_0);
        body.Add(CilOpCodes.Ldarg_0);
        body.Add(CilOpCodes.Ldlen);
        body.Add(CilOpCodes.Conv_I4);
        body.Add(CilOpCodes.Call, module.DefaultImporter.ImportMethod(typeof(Array).GetMethod("Clear", [typeof(Array), typeof(int), typeof(int)])!));
        body.Add(CilOpCodes.Call, module.DefaultImporter.ImportMethod(typeof(GC).GetMethod("Collect", Type.EmptyTypes)!));
        body.Add(CilOpCodes.Ret);
        var method = new MethodDefinition("Copy", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void,
                [arrayType.ToTypeSignature(module), new TypeDefOrRefSignature(host, false), module.CorLibTypeFactory.Int32]));
        foreach (var (parameter, index) in new[] { "array", "target", "index" }.Select((name, index) => (name, index)))
            method.ParameterDefinitions.Add(new ParameterDefinition((ushort)(index + 1), parameter, (ParameterAttributes)0));
        host.Methods.Add(method);
        IlGenerator.GenerateIl(context, method);
        CilStackValidator.Validate(method.CilMethodBody!, context.FullName);
        Assert.That(method.CilMethodBody!.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Ldelem_Ref), Is.EqualTo(2));
        var assembly = new AssemblyDefinition(name, new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        using var stream = new System.IO.MemoryStream();
        module.Write(stream);
        var runtime = System.Reflection.Assembly.Load(stream.ToArray()).GetType("ArraySpan.Host")!;
        var invoke = runtime.GetMethod("Copy")!;
        foreach (var nullElement in new[] { false, true })
        {
            object? first = nullElement ? null : strings ? "first" : new object();
            object second = strings ? "second" : new object();
            Array source = strings ? new string?[] { "sentinel", (string?)first, (string)second }
                : new object?[] { new object(), first, second };
            var destination = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(runtime);
            invoke.Invoke(null, [source, destination, 1]);
            Assert.That(runtime.GetField("Field0")!.GetValue(destination), Is.SameAs(first));
            Assert.That(runtime.GetField("Field1")!.GetValue(destination), Is.SameAs(second));
            Assert.That(source.Cast<object?>(), Is.All.Null);
        }
        // 非法数组先在原始读取点失败，既不执行中间修改，也不部分写入目标字段。
        foreach (var mode in new[] { "null", "short", "negative", "huge" })
        {
            if (!dynamicIndex && mode is "negative" or "huge") continue;
            Array? source = mode == "null" ? null : strings ? new[] { "sentinel", "retained" }
                : new object[] { new object(), new object() };
            var destination = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(runtime);
            var sentinel = new object();
            runtime.GetField("Field0")!.SetValue(destination, sentinel);
            var index = mode == "negative" ? -1 : mode == "huge" ? int.MaxValue : 1;
            var error = Assert.Throws<System.Reflection.TargetInvocationException>(() => invoke.Invoke(null, [source, destination, index]));
            Assert.That(error!.InnerException, mode == "null" ? Is.TypeOf<NullReferenceException>() : Is.TypeOf<IndexOutOfRangeException>());
            Assert.That(runtime.GetField("Field0")!.GetValue(destination), Is.SameAs(sentinel));
            if (source != null) Assert.That(source.GetValue(0), Is.Not.Null);
        }
    }

    [TestCase("valid", true)]
    [TestCase("unknown_array", false)]
    [TestCase("value_element", false)]
    [TestCase("negative", false)]
    [TestCase("overflow", false)]
    [TestCase("extra_use", false)]
    [TestCase("multiple_definitions", false)]
    [TestCase("before_definition", false)]
    [TestCase("partial_store", false)]
    [TestCase("downcast", false)]
    [Category("基本功能")]
    [Category("边界值")]
    [Category("异常输入")]
    public void 引用数组跨度拒绝不完整布局及消费者(string mode, bool expected)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var element = mode == "value_element" ? app.SystemTypes.SystemInt64Type : app.SystemTypes.SystemObjectType;
        var owner = new InjectedTypeAnalysisContext(app.Assemblies[0], "ArraySpan", "InvalidHost",
            app.SystemTypes.SystemObjectType, System.Reflection.TypeAttributes.Public);
        var fieldType = mode == "downcast" ? app.SystemTypes.SystemStringType : element;
        var fields = Enumerable.Range(0, 2).Select(index =>
        {
            var field = owner.InjectFieldContext("Field" + index, fieldType, System.Reflection.FieldAttributes.Public);
            field.Offset = 16 + index * 8;
            return field;
        }).ToArray();
        var source = new LocalVariable("source", new Register(null, "source"), mode == "unknown_array" ? null : element.MakeSzArrayType());
        var target = new LocalVariable("target", new Register(null, "target"), owner);
        var value = new LocalVariable("value", new Register(null, "value"), element);
        var load = new Instruction(0, OpCode.Move, value, new ArrayAccess(source,
            new Immediate(mode == "negative" ? -1 : mode == "overflow" ? int.MaxValue : 0))) { MemoryAccessWidthBits = 128 };
        var store = new Instruction(1, OpCode.Move, new FieldReference(fields[0], target, 16), value)
            { MemoryAccessWidthBits = mode == "partial_store" ? 64 : 128 };
        var instructions = (mode == "before_definition" ? new[] { store, load } : new[] { load, store }).ToList();
        if (mode == "extra_use") instructions.Add(new Instruction(2, OpCode.Return, value));
        if (mode == "multiple_definitions") instructions.Add(new Instruction(2, OpCode.Move, value, new Immediate(0)));
        instructions.Add(new Instruction(3, OpCode.Return));
        Assert.That(ManagedReferenceSpanRecovery.Describe(new ISILControlFlowGraph(instructions), 8).ContainsKey(load), Is.EqualTo(expected));
    }
}
