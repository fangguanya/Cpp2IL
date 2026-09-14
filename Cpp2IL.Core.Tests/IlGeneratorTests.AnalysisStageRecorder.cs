using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorTests
{
    [Test]
    [Category("基本功能")]
    [Category("边界值")]
    public void 阶段取证快照保留重复索引和同名类型的独立身份()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var context = 创建阶段取证上下文();
        var firstType = new InjectedTypeAnalysisContext(app.Assemblies[0], "Trace", "SameName",
            app.SystemTypes.SystemObjectType, System.Reflection.TypeAttributes.Public);
        var secondType = new InjectedTypeAnalysisContext(app.Assemblies[0], "Trace", "SameName",
            app.SystemTypes.SystemObjectType, System.Reflection.TypeAttributes.Public);
        var left = new LocalVariable("same", new Register(null, "X0", 1), firstType);
        var right = new LocalVariable("same", new Register(null, "X0", 2), secondType);
        var load = new Instruction(-1, OpCode.Move, left, new Immediate(1)) { MemoryAccessWidthBits = 64 };
        var copy = new Instruction(-1, OpCode.Move, right, left);
        context.ControlFlowGraph = new ISILControlFlowGraph([load, copy, new Instruction(-1, OpCode.Return)]);
        var snapshots = new List<AnalysisStageRecorder.Snapshot>();
        var recorder = new AnalysisStageRecorder(snapshots.Add);
        recorder.RegisterNativeEmission([load], 0, 0x1000, 0x1008);
        recorder.Capture("SSA", context);
        var frozen = JsonSerializer.Serialize(snapshots[0]);
        left.Type = app.SystemTypes.SystemInt32Type;
        load.MemoryAccessWidthBits = 32;
        copy.SetOperand(1, new Immediate(0));
        recorder.Capture("Rewrite", context);
        Assert.That(JsonSerializer.Serialize(snapshots[0]), Is.EqualTo(frozen), "后续原位改写不得污染先前快照。");
        var before = JsonSerializer.SerializeToElement(snapshots[0]);
        var after = JsonSerializer.SerializeToElement(snapshots[1]);
        var rows = before.GetProperty("Instructions").EnumerateArray().ToArray();
        Assert.That(rows.Select(row => row.GetProperty("id").GetInt32()).Distinct().Count(), Is.EqualTo(3));
        Assert.That(rows[0].GetProperty("nativeEmissionRange").GetProperty("Start").GetUInt64(), Is.EqualTo(0x1000));
        Assert.That(rows[1].GetProperty("nativeEmissionRange").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(rows[0].GetProperty("id").GetInt32(), Is.EqualTo(after.GetProperty("Instructions")[0].GetProperty("id").GetInt32()));
        Assert.That(before.GetProperty("Locals").EnumerateArray().Select(row => row.GetProperty("typeId").GetInt32()).Distinct().Count(), Is.EqualTo(2));
        Assert.That(rows[1].GetProperty("usedLocalIds").GetArrayLength(), Is.EqualTo(1));
        Assert.That(after.GetProperty("Instructions")[1].GetProperty("usedLocalIds").GetArrayLength(), Is.EqualTo(0));
    }

    [Test]
    [Category("基本功能")]
    public void 阶段取证数组及内存嵌套源保留SSA和精确偏移()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var context = 创建阶段取证上下文();
        var array = new LocalVariable("array", new Register(null, "X0", 4), app.SystemTypes.SystemInt32Type.MakeSzArrayType());
        var index = new LocalVariable("index", new Register(null, "X1", 7), app.SystemTypes.SystemInt32Type);
        var value = new LocalVariable("value", new Register(null, "V0", 9));
        context.ParameterLocals = [array, index];
        context.ParameterOperands = [new Register(null, "X0"), new Register(null, "X1")];
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, value, new ArrayAccess(array, index)) { MemoryAccessWidthBits = 64 },
            new Instruction(1, OpCode.Move, value, new MemoryOperand(array, index, 24, 4, MemoryIndexExtension.SignExtend32)),
            new Instruction(2, OpCode.Return)
        ]);
        AnalysisStageRecorder.Snapshot? snapshot = null;
        new AnalysisStageRecorder(value => snapshot = value).Capture("SSA", context);
        var json = JsonSerializer.SerializeToElement(snapshot);
        Assert.That(json.GetProperty("Instructions")[0].GetProperty("usedLocalIds").GetArrayLength(), Is.EqualTo(2));
        var memory = json.GetProperty("Instructions")[1].GetProperty("operands")[1];
        Assert.That(memory.GetProperty("Addend").GetInt32(), Is.EqualTo(24));
        Assert.That(memory.GetProperty("extension").GetString(), Is.EqualTo("SignExtend32"));
        Assert.That(json.GetProperty("Locals").EnumerateArray().Select(row => row.GetProperty("ssaVersion").GetInt32()), Is.EquivalentTo(new[] { 4, 7, 9 }));
        Assert.That(snapshot!.UnstructuredOperands, Is.Zero);
    }

    [Test]
    [Category("异常输入")]
    public void 阶段取证拒绝重复原生登记并显式记录递归操作数()
    {
        var context = 创建阶段取证上下文();
        var local = new LocalVariable("value", new Register(null, "X0"));
        var load = new Instruction(0, OpCode.Move, local, new Immediate(1));
        var recorder = new AnalysisStageRecorder(_ => { });
        Assert.Throws<ArgumentOutOfRangeException>(() => recorder.RegisterNativeEmission([load], 0, 4, 4));
        recorder.RegisterNativeEmission([load], 0, 4, 8);
        Assert.Throws<InvalidOperationException>(() => recorder.RegisterNativeEmission([load], 0, 4, 8));
        // 未识别操作数必须显式登记为未结构化，而非被当作完整来源证据。
        AnalysisStageRecorder.Snapshot? captured = null;
        recorder = new AnalysisStageRecorder(snapshot => captured = snapshot);
        context.ControlFlowGraph = new ISILControlFlowGraph([new Instruction(0, OpCode.Nop, new 未结构化取证操作数())]);
        recorder.Capture("Failure", context, failure: "输入证据缺失");
        Assert.That(captured!.UnstructuredOperands, Is.EqualTo(1));
        Assert.That(captured.Failure, Is.EqualTo("输入证据缺失"));
    }

    private sealed class 未结构化取证操作数 : IOperand { }

    [Test]
    [Category("基本功能")]
    [Category("边界值")]
    public void 阶段取证循环块目标及同名寄存器保留对象和数值身份()
    {
        var context = 创建阶段取证上下文();
        context.ControlFlowGraph = new ISILControlFlowGraph([new Instruction(0, OpCode.Return)]);
        var block = context.ControlFlowGraph.Blocks[0];
        var first = new LocalVariable("same", new Register(101, "same", 3));
        var second = new LocalVariable("same", new Register(202, "same", 3));
        context.ParameterLocals = [first, second];
        context.ParameterOperands = [first.Register, second.Register];
        block.Instructions.Clear();
        block.Instructions.Add(new Instruction(0, OpCode.Jump, block));
        block.Successors.Clear(); block.Successors.Add(block);
        block.Predecessors.Clear(); block.Predecessors.Add(block);
        AnalysisStageRecorder.Snapshot? snapshot = null;
        new AnalysisStageRecorder(value => snapshot = value).Capture("Loop", context);
        Assert.That(snapshot!.UnstructuredOperands, Is.Zero);
        var json = JsonSerializer.SerializeToElement(snapshot);
        var target = json.GetProperty("Instructions")[0].GetProperty("operands")[0];
        var rootBlock = json.GetProperty("Blocks")[0];
        Assert.That(target.GetProperty("kind").GetString(), Is.EqualTo("Block"));
        Assert.That(target.GetProperty("objectIdentityId").GetInt32(), Is.EqualTo(rootBlock.GetProperty("objectIdentityId").GetInt32()));
        Assert.That(json.GetProperty("Locals").EnumerateArray().Select(row => row.GetProperty("registerNumber").GetInt32()), Is.EquivalentTo(new[] { 101, 202 }));
        Assert.That(json.GetProperty("Parameters").EnumerateArray().Select(row => row.GetProperty("Number").GetInt32()), Is.EquivalentTo(new[] { 101, 202 }));
    }

    [Test]
    [Category("基本功能")]
    public void 阶段取证原始字段保留邻接声明和真实尺寸地址()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = app.GetAssemblyByName("mscorlib")!.GetTypeByFullName("System.Guid")!;
        var field = owner.Fields.First(f => !f.IsStatic);
        var receiver = new LocalVariable("owner", new Register(null, "opaque"), owner);
        var value = new LocalVariable("value", new Register(null, "other"), field.FieldType);
        var context = 创建阶段取证上下文();
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, value, new FieldReference(field, receiver, field.Offset)),
            new Instruction(1, OpCode.Return)]);
        AnalysisStageRecorder.Snapshot? snapshot = null;
        new AnalysisStageRecorder(item => snapshot = item).Capture("Field", context);
        var json = JsonSerializer.SerializeToElement(snapshot);
        var operand = json.GetProperty("Instructions")[0].GetProperty("operands")[1];
        var layout = json.GetProperty("FieldLayouts").EnumerateArray().Single(item =>
            item.GetProperty("ownerTypeId").GetInt32() == operand.GetProperty("ownerTypeId").GetInt32());
        Assert.That(layout.GetProperty("layoutProjectionFailure").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(layout.GetProperty("originalSizeAddress").GetUInt64(), Is.GreaterThan(0));
        Assert.That(layout.GetProperty("originalUnboxedSize").GetInt64(), Is.EqualTo(16));
        Assert.That(layout.GetProperty("fields").GetArrayLength(), Is.EqualTo(owner.Fields.Count));
        Assert.That(layout.GetProperty("fields").EnumerateArray().All(item => item.GetProperty("hasOriginalOffset").GetBoolean()), Is.True);
        Assert.That(layout.GetProperty("fields").EnumerateArray().Select(item => item.GetProperty("originalDeclaredOffset").GetInt32()),
            Is.EqualTo(owner.Fields.Select(item => item.DefaultOffset)));
    }

    [Test]
    [Category("边界值")]
    [Category("异常输入")]
    public void 阶段取证注入字段不冒充原始布局且快照不受后续偏移覆盖影响()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = new InjectedTypeAnalysisContext(app.Assemblies[0], "Trace", "Owner", app.SystemTypes.SystemObjectType,
            System.Reflection.TypeAttributes.Public);
        var field = owner.InjectFieldContext("Value", app.SystemTypes.SystemInt32Type, System.Reflection.FieldAttributes.Public);
        field.Offset = 16;
        var receiver = new LocalVariable("owner", new Register(null, "opaque"), owner);
        var value = new LocalVariable("value", new Register(null, "other"), field.FieldType);
        var context = 创建阶段取证上下文();
        context.ControlFlowGraph = new ISILControlFlowGraph([new Instruction(0, OpCode.Move, value, new FieldReference(field, receiver, 16))]);
        var snapshots = new List<AnalysisStageRecorder.Snapshot>();
        var recorder = new AnalysisStageRecorder(snapshots.Add);
        recorder.Capture("Before", context);
        var before = JsonSerializer.Serialize(snapshots[0]);
        field.Offset = 24;
        recorder.Capture("After", context);
        Assert.That(JsonSerializer.Serialize(snapshots[0]), Is.EqualTo(before));
        var oldLayout = JsonSerializer.SerializeToElement(snapshots[0]).GetProperty("FieldLayouts")[0];
        var newLayout = JsonSerializer.SerializeToElement(snapshots[1]).GetProperty("FieldLayouts")[0];
        Assert.That(oldLayout.GetProperty("boxedSize").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(oldLayout.GetProperty("fields")[0].GetProperty("hasOriginalOffset").GetBoolean(), Is.False);
        Assert.That(oldLayout.GetProperty("fields")[0].GetProperty("observedDeclaredOffset").GetInt32(), Is.EqualTo(16));
        Assert.That(newLayout.GetProperty("fields")[0].GetProperty("observedDeclaredOffset").GetInt32(), Is.EqualTo(24));
    }

    [Test]
    [Category("基本功能")]
    public void 阶段取证在字段解析前保存具体泛型内存基址布局()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var guid = app.GetAssemblyByName("mscorlib")!.GetTypeByFullName("System.Guid")!;
        var owner = app.GetAssemblyByName("mscorlib")!.GetTypeByFullName("System.Nullable`1")!.MakeGenericInstanceType([guid]);
        var receiver = new LocalVariable("owner", new Register(null, "opaque"), owner.MakeByReferenceType());
        var value = new LocalVariable("value", new Register(null, "other"));
        var context = 创建阶段取证上下文();
        context.ControlFlowGraph = new ISILControlFlowGraph([new Instruction(0, OpCode.Move, value, new MemoryOperand(receiver, null, 0))]);
        AnalysisStageRecorder.Snapshot? snapshot = null;
        new AnalysisStageRecorder(item => snapshot = item).Capture("Memory", context);
        var json = JsonSerializer.SerializeToElement(snapshot);
        var layout = json.GetProperty("FieldLayouts")[0];
        Assert.That(layout.GetProperty("concreteLayoutAvailable").GetBoolean(), Is.True);
        Assert.That(layout.GetProperty("originalSizeIsConcreteInstance").GetBoolean(), Is.False);
        Assert.That(layout.GetProperty("fields").EnumerateArray().Select(item => item.GetProperty("concreteOffset").GetInt64()), Is.EqualTo(new long[] { 0, 16 }));
        var typeIds = json.GetProperty("Types").EnumerateArray().Select(item => item.GetProperty("id").GetInt32()).ToHashSet();
        Assert.That(layout.GetProperty("fields").EnumerateArray().All(item => typeIds.Contains(item.GetProperty("typeId").GetInt32())), Is.True);
    }

    [Test]
    [Category("基本功能")]
    [Category("边界值")]
    public void 阶段取证集合计数及只读表完整保留来源和UTF16代码单元()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var listType = app.GetAssemblyByName("mscorlib")!.GetTypeByFullName("System.Collections.Generic.List`1")!
            .MakeGenericInstanceType([app.SystemTypes.SystemStringType]);
        var list = new LocalVariable("list", new Register(null, "a"), listType);
        var text = new LocalVariable("text", new Register(null, "b"), app.SystemTypes.SystemStringType);
        var index = new LocalVariable("index", new Register(null, "c"), app.SystemTypes.SystemInt32Type);
        var result = new LocalVariable("result", new Register(null, "d"));
        var context = 创建阶段取证上下文();
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, result, new ListCount(list, listType)),
            new Instruction(1, OpCode.Move, result, new StringLength(text)),
            new Instruction(2, OpCode.Move, result, new MetadataStringTableLookup(index, [new StringLiteral("值")], new StringLiteral("默认"))),
            new Instruction(3, OpCode.Move, result, new ReadOnlyUInt16TableLookup(new string(new[] { '\0', '\uD800', '\uFFFF' }), index))]);
        AnalysisStageRecorder.Snapshot? snapshot = null;
        new AnalysisStageRecorder(item => snapshot = item).Capture("Tables", context);
        Assert.That(snapshot!.UnstructuredOperands, Is.Zero);
        var json = JsonSerializer.SerializeToElement(snapshot);
        var instructions = json.GetProperty("Instructions");
        Assert.That(instructions[0].GetProperty("operands")[1].GetProperty("kind").GetString(), Is.EqualTo("ListCount"));
        Assert.That(instructions[1].GetProperty("operands")[1].GetProperty("kind").GetString(), Is.EqualTo("StringLength"));
        Assert.That(instructions[2].GetProperty("operands")[1].GetProperty("defaultValue").GetProperty("value").GetString(), Is.EqualTo("默认"));
        Assert.That(instructions[3].GetProperty("operands")[1].GetProperty("codeUnits").EnumerateArray().Select(item => item.GetInt32()),
            Is.EqualTo(new[] { 0, 0xD800, 0xFFFF }));
        Assert.That(instructions.EnumerateArray().All(item => item.GetProperty("projectionFailure").ValueKind == JsonValueKind.Null), Is.True);
    }

    private static MethodAnalysisContext 创建阶段取证上下文()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = new InjectedTypeAnalysisContext(app.Assemblies[0], "Trace", "Host",
            app.SystemTypes.SystemObjectType, System.Reflection.TypeAttributes.Public);
        return new InjectedMethodAnalysisContext(owner, "Trace", app.SystemTypes.SystemVoidType,
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static, [], []);
    }
}
