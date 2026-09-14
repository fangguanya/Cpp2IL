using System;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;
using FieldAttributes = AsmResolver.PE.DotNet.Metadata.Tables.FieldAttributes;
using MethodAttributes = AsmResolver.PE.DotNet.Metadata.Tables.MethodAttributes;
using ParameterAttributes = AsmResolver.PE.DotNet.Metadata.Tables.ParameterAttributes;
using TypeAttributes = AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorTests
{
    [TestCase(false, false, false)]
    [TestCase(false, false, true)]
    [TestCase(false, true, false)]
    [TestCase(false, true, true)]
    [TestCase(true, false, false)]
    [TestCase(true, false, true)]
    [TestCase(true, true, false)]
    [TestCase(true, true, true)]
    [Category("基本功能")]
    [Category("边界值")]
    public void 引用字段宽快照跨修改和GC保留对象身份及混合数值(bool isStatic, bool mixed, bool alias)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = new InjectedTypeAnalysisContext(app.Assemblies[0], "SpanFixture", "Host",
            app.SystemTypes.SystemObjectType, System.Reflection.TypeAttributes.Public);
        var attributes = System.Reflection.FieldAttributes.Public | (isStatic ? System.Reflection.FieldAttributes.Static : 0);
        var fields = Enumerable.Range(0, 4).Select(index => owner.InjectFieldContext("Field" + index,
            mixed && index % 2 == 1 ? app.SystemTypes.SystemInt64Type : app.SystemTypes.SystemObjectType, attributes)).ToArray();
        for (var index = 0; index < fields.Length; index++)
            fields[index].Offset = (isStatic ? 0 : 16) + index * 8;
        var source = new LocalVariable("source", new Register(null, "X0"), owner);
        var destination = new LocalVariable("destination", new Register(null, "X1"), owner);
        var replacement = new LocalVariable("replacement", new Register(null, "X2"), app.SystemTypes.SystemObjectType);
        var snapshot = new LocalVariable("snapshot", new Register(null, "V0", 1), app.SystemTypes.SystemObjectType);
        var sourceField = new FieldReference(fields[0], source, fields[0].Offset);
        var destinationIndex = isStatic && !alias ? 2 : 0;
        var destinationField = new FieldReference(fields[destinationIndex], destination, fields[destinationIndex].Offset);
        var collect = owner.InjectMethodContext("Collect", app.SystemTypes.SystemVoidType,
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static);
        var context = new InjectedMethodAnalysisContext(owner, "Copy", app.SystemTypes.SystemVoidType,
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static,
            [owner, owner, app.SystemTypes.SystemObjectType], ["source", "destination", "replacement"]);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, snapshot, sourceField) { MemoryAccessWidthBits = 128 },
            new Instruction(1, OpCode.Move, sourceField, replacement) { MemoryAccessWidthBits = 64 },
            new Instruction(2, OpCode.CallVoid, collect),
            new Instruction(3, OpCode.Move, destinationField, snapshot) { MemoryAccessWidthBits = 128 },
            new Instruction(4, OpCode.Return)
        ]);
        context.Locals = [source, destination, replacement, snapshot];
        context.ParameterLocals = [source, destination, replacement];
        context.AnalysisWarnings = [];

        var coreModule = new ModuleDefinition("mscorlib.dll");
        var coreAssembly = new AssemblyDefinition("mscorlib", new Version(4, 0, 0, 0));
        coreAssembly.Modules.Add(coreModule);
        绑定AsmResolver系统类型(coreModule, app.SystemTypes.SystemObjectType, "Object", TypeAttributes.Public);
        绑定AsmResolver系统类型(coreModule, app.SystemTypes.SystemInt64Type, "Int64", TypeAttributes.Public | TypeAttributes.Sealed);
        var name = "ManagedSpan_" + Guid.NewGuid().ToString("N");
        var module = new ModuleDefinition(name + ".dll", new AssemblyReference(coreAssembly));
        var host = new TypeDefinition("SpanFixture", "Host", TypeAttributes.Public | TypeAttributes.Class,
            module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(host);
        owner.PutExtraData("AsmResolverType", host);
        foreach (var field in fields)
        {
            var definition = new FieldDefinition(field.Name, FieldAttributes.Public | (isStatic ? FieldAttributes.Static : 0),
                field.FieldType.ToTypeSignature(module));
            host.Fields.Add(definition);
            field.PutExtraData("AsmResolverField", definition);
        }
        var collectDefinition = new MethodDefinition("Collect", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        host.Methods.Add(collectDefinition);
        collect.PutExtraData("AsmResolverMethod", collectDefinition);
        collectDefinition.CilMethodBody = new CilMethodBody();
        collectDefinition.CilMethodBody.Instructions.Add(CilOpCodes.Call,
            module.DefaultImporter.ImportMethod(typeof(GC).GetMethod("Collect", Type.EmptyTypes)!));
        collectDefinition.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
        var method = new MethodDefinition("Copy", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void,
                [new TypeDefOrRefSignature(host, false), new TypeDefOrRefSignature(host, false), module.CorLibTypeFactory.Object]));
        foreach (var (parameter, index) in new[] { "source", "destination", "replacement" }.Select((value, index) => (value, index)))
            method.ParameterDefinitions.Add(new ParameterDefinition((ushort)(index + 1), parameter, (ParameterAttributes)0));
        host.Methods.Add(method);
        IlGenerator.GenerateIl(context, method);
        CilStackValidator.Validate(method.CilMethodBody!, context.FullName);
        Assert.That(method.CilMethodBody!.Instructions.Any(instruction => instruction.OpCode == CilOpCodes.Conv_U8), Is.False);
        var assembly = new AssemblyDefinition(name, new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        using var stream = new System.IO.MemoryStream();
        module.Write(stream);
        var runtime = System.Reflection.Assembly.Load(stream.ToArray()).GetType("SpanFixture.Host", true)!;
        foreach (var nullFirst in new[] { false, true })
        {
            var from = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(runtime);
            var to = alias ? from : System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(runtime);
            object? first = nullFirst ? null : new object();
            object second = mixed ? (object)unchecked((long)0xFEDCBA9876543210UL) : new object();
            var changed = new object();
            runtime.GetField("Field0")!.SetValue(isStatic ? null : from, first);
            runtime.GetField("Field1")!.SetValue(isStatic ? null : from, second);
            runtime.GetMethod("Copy")!.Invoke(null, [from, to, changed]);
            Assert.That(runtime.GetField("Field" + destinationIndex)!.GetValue(isStatic ? null : to), Is.SameAs(first));
            var actualSecond = runtime.GetField("Field" + (destinationIndex + 1))!.GetValue(isStatic ? null : to);
            if (mixed) Assert.That(actualSecond, Is.EqualTo(second));
            else Assert.That(actualSecond, Is.SameAs(second));
        }
    }

    [TestCase("extra_use")]
    [TestCase("multiple_definitions")]
    [TestCase("partial_store")]
    [TestCase("before_definition")]
    [TestCase("gap")]
    [TestCase("unknown_overlap")]
    [Category("异常输入")]
    public void 引用字段宽快照拒绝不闭合定义使用及布局(string mode)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = new InjectedTypeAnalysisContext(app.Assemblies[0], "SpanFixture", "InvalidHost",
            app.SystemTypes.SystemObjectType, System.Reflection.TypeAttributes.Public);
        var first = owner.InjectFieldContext("First", app.SystemTypes.SystemObjectType, System.Reflection.FieldAttributes.Public);
        var second = owner.InjectFieldContext("Second", app.SystemTypes.SystemObjectType, System.Reflection.FieldAttributes.Public);
        first.Offset = 16;
        second.Offset = mode == "gap" ? 32 : 24;
        if (mode == "unknown_overlap")
        {
            var unknown = new InjectedTypeAnalysisContext(app.Assemblies[0], "SpanFixture", "Unknown",
                app.GetAssemblyByName("mscorlib")!.GetTypeByFullName("System.ValueType"), System.Reflection.TypeAttributes.Public);
            owner.InjectFieldContext("Overlap", unknown, System.Reflection.FieldAttributes.Public).Offset = 16;
        }
        var receiver = new LocalVariable("receiver", new Register(null, "X0"), owner);
        var value = new LocalVariable("value", new Register(null, "V0", 1), app.SystemTypes.SystemObjectType);
        var reference = new FieldReference(first, receiver, 16);
        var load = new Instruction(0, OpCode.Move, value, reference) { MemoryAccessWidthBits = 128 };
        var store = new Instruction(1, OpCode.Move, reference, value) { MemoryAccessWidthBits = mode == "partial_store" ? 64 : 128 };
        var instructions = mode == "before_definition" ? new[] { store, load }.ToList() : new[] { load, store }.ToList();
        if (mode == "extra_use") instructions.Add(new Instruction(2, OpCode.Return, value));
        else if (mode == "multiple_definitions") instructions.Add(new Instruction(2, OpCode.Move, value, new Immediate(0)));
        instructions.Add(new Instruction(3, OpCode.Return));
        var graph = new ISILControlFlowGraph(instructions);
        var plan = ManagedReferenceSpanRecovery.Describe(graph, 8);
        Assert.That(plan, Is.Empty);
        Assert.That(load.MemoryAccessWidthBits, Is.EqualTo(128));
        Assert.That(load.Operands[1], Is.SameAs(reference));
    }

    [TestCase(OpCode.Phi)]
    [TestCase(OpCode.Newobj)]
    [TestCase(OpCode.Move)]
    [Category("异常输入")]
    public void 引用字段宽快照索引保留无操作数指令交由原有严格门(OpCode operation)
    {
        var malformed = new Instruction(0, operation);
        var graph = new ISILControlFlowGraph([malformed, new Instruction(1, OpCode.Return)]);
        Assert.That(ManagedReferenceSpanRecovery.Describe(graph, 8), Is.Empty);
        Assert.That(malformed.OpCode, Is.EqualTo(operation));
        Assert.That(malformed.Operands, Is.Empty);
    }
}
