using System;
using System.Collections.Generic;
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
using LibCpp2IL.BinaryStructures;
using FieldAttributes = AsmResolver.PE.DotNet.Metadata.Tables.FieldAttributes;
using GenericParameterAttributes = AsmResolver.PE.DotNet.Metadata.Tables.GenericParameterAttributes;
using MethodAttributes = AsmResolver.PE.DotNet.Metadata.Tables.MethodAttributes;
using ParameterAttributes = AsmResolver.PE.DotNet.Metadata.Tables.ParameterAttributes;
using TypeAttributes = AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorTests
{
    /// <summary>合成五字段封闭泛型值类型（三个引用加两个 Int32，共 32 字节），布局证明走具体泛型字段布局。</summary>
    private static (InjectedTypeAnalysisContext Box, GenericInstanceTypeAnalysisContext Concrete, FieldAnalysisContext[] Fields)
        构建五字段值类型(ApplicationAnalysisContext app)
    {
        var box = new InjectedTypeAnalysisContext(app.Assemblies[0], "StackAggregate", "Box`2",
            app.GetAssemblyByName("mscorlib")!.GetTypeByFullName("System.ValueType"),
            System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.SequentialLayout);
        var referenceParameter = new GenericParameterTypeAnalysisContext("TRef", 0, Il2CppTypeEnum.IL2CPP_TYPE_VAR,
            System.Reflection.GenericParameterAttributes.None, box);
        var numberParameter = new GenericParameterTypeAnalysisContext("TNum", 1, Il2CppTypeEnum.IL2CPP_TYPE_VAR,
            System.Reflection.GenericParameterAttributes.None, box);
        box.GenericParameters.Add(referenceParameter);
        box.GenericParameters.Add(numberParameter);
        var fields = new[] { "A", "B", "C", "D", "E" }.Select((name, index) => box.InjectFieldContext(name,
            index < 3 ? referenceParameter : numberParameter, System.Reflection.FieldAttributes.Public)).ToArray();
        var concrete = box.MakeGenericInstanceType([app.SystemTypes.SystemStringType, app.SystemTypes.SystemInt32Type]);
        return (box, concrete, fields);
    }

    /// <summary>构造“逐槽栈写入 + 取址栈临时 + 按值调用”的标准形态，供正向与反例共用。</summary>
    private static (InjectedMethodAnalysisContext Context, LocalVariable StackBase, Instruction Call,
        List<Instruction> SlotWrites, LocalVariable WideValue, LocalVariable ScalarValue, LocalVariable ConstantCarrier)
        构建栈聚合实参形态(
            ApplicationAnalysisContext app,
            TypeAnalysisContext parameterType,
            TypeAnalysisContext returnType,
            bool spill,
            Action<List<Instruction>, List<LocalVariable>>? mutate = null)
    {
        var owner = new InjectedTypeAnalysisContext(app.Assemblies[0], "StackAggregate", "Fixture",
            app.SystemTypes.SystemObjectType, System.Reflection.TypeAttributes.Public);
        var stringType = app.SystemTypes.SystemStringType;
        var fixtureFields = new[] { "F0", "F1", "F2" }
            .Select(name => owner.InjectFieldContext(name, stringType, System.Reflection.FieldAttributes.Public)).ToArray();
        for (var index = 0; index < fixtureFields.Length; index++)
            fixtureFields[index].Offset = 16 + index * 8;

        var consumer = new InjectedMethodAnalysisContext(owner, "Consume", returnType,
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static,
            [parameterType], ["value"]);
        var source = new LocalVariable("source", new Register(null, "X0"), owner);
        var wideValue = new LocalVariable("wide", new Register(null, "V0", 1), stringType);
        var scalarValue = new LocalVariable("scalar", new Register(null, "X19", 1), stringType);
        var reloaded = new LocalVariable("reloaded", new Register(null, "V0", 2), stringType);
        var spillSlot = new LocalVariable("spill", new Register(null, "stack_8"));
        var constantCarrier = new LocalVariable("constant", new Register(null, "X8"), app.SystemTypes.SystemInt32Type);
        var addressCarrier = new LocalVariable("address", new Register(null, "X0", 1));
        var stackBase = new LocalVariable("base", new Register(null, "stack_10"));
        var stackScalar = new LocalVariable("scalarSlot", new Register(null, "stack_20"));
        var stackDirect = new LocalVariable("directSlot", new Register(null, "stack_28"));
        var stackChained = new LocalVariable("chainedSlot", new Register(null, "stack_2C"));
        var result = new LocalVariable("result", new Register(null, "X0", 2), returnType);

        var instructions = new List<Instruction>
        {
            new Instruction(0, OpCode.Move, wideValue, new FieldReference(fixtureFields[0], source, 16)) { MemoryAccessWidthBits = 128 },
            new Instruction(1, OpCode.Move, scalarValue, new FieldReference(fixtureFields[2], source, 32)) { MemoryAccessWidthBits = 64 },
        };
        if (spill)
        {
            // 守卫调用破坏寄存器溢出/重载链：纯 Move 链走到底必须仍能证明末端字段来源。
            instructions.Add(new Instruction(2, OpCode.Move, spillSlot, scalarValue) { MemoryAccessWidthBits = 64 });
            instructions.Add(new Instruction(3, OpCode.Move, reloaded, spillSlot) { MemoryAccessWidthBits = 64 });
        }
        instructions.Add(new Instruction(4, OpCode.Move, constantCarrier, new Immediate(42)));
        instructions.Add(new Instruction(5, OpCode.Move, addressCarrier, new AddressOf(stackBase)));
        var slotWrites = new List<Instruction>
        {
            new Instruction(6, OpCode.Move, stackBase, wideValue) { MemoryAccessWidthBits = 128 },
            new Instruction(7, OpCode.Move, stackScalar, spill ? reloaded : scalarValue) { MemoryAccessWidthBits = 64 },
            new Instruction(8, OpCode.Move, stackDirect, new Immediate(7)) { MemoryAccessWidthBits = 32 },
            new Instruction(9, OpCode.Move, stackChained, constantCarrier) { MemoryAccessWidthBits = 32 },
        };
        instructions.AddRange(slotWrites);
        var call = new Instruction(10, OpCode.Call, consumer, result, addressCarrier);
        instructions.Add(call);
        instructions.Add(new Instruction(11, OpCode.Return, result));
        var locals = new List<LocalVariable>
        {
            source, wideValue, scalarValue, reloaded, spillSlot, constantCarrier, addressCarrier,
            stackBase, stackScalar, stackDirect, stackChained, result
        };
        mutate?.Invoke(instructions, locals);

        var context = new InjectedMethodAnalysisContext(owner, "Produce", returnType,
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static, [owner], ["source"])
        {
            ControlFlowGraph = new ISILControlFlowGraph(instructions),
            Locals = locals,
            ParameterLocals = [source],
            AnalysisWarnings = []
        };
        return (context, stackBase, call, slotWrites, wideValue, scalarValue, constantCarrier);
    }

    [TestCase("text", false)]
    [TestCase("text", true)]
    [TestCase("number", false)]
    [TestCase("number", true)]
    [Category("基本功能")]
    [Category("边界值")]
    public void 栈聚合实参恢复为整体值类型并生成可执行CIL(string mode, bool spill)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var stringType = app.SystemTypes.SystemStringType;
        var intType = app.SystemTypes.SystemInt32Type;
        var returnType = mode == "text" ? stringType : intType;
        var (box, concrete, boxFields) = 构建五字段值类型(app);
        var (context, stackBase, call, slotWrites, wideValue, scalarValue, constantCarrier) =
            构建栈聚合实参形态(app, concrete, returnType, spill);

        Assert.That(StackAggregateArgumentRecovery.Run(context), Is.True);
        Assert.That(stackBase.Type, Is.SameAs(concrete));
        Assert.That(call.Operands[2], Is.SameAs(stackBase));
        // 逐槽写入改写为精确字段写入：宽写指向首字段并保留原访问宽度，末端源沿复制链取尽。
        Assert.That(slotWrites[0].Operands[0], Is.TypeOf<FieldReference>());
        var stores = slotWrites.Select(write => (FieldReference)write.Operands[0]).ToArray();
        // 宽写指向所覆盖首字段；具体泛型布局字段须先还原为基础字段身份再比较。
        Assert.That(stores.Select(store => 基础字段(store.Field)).ToArray(),
            Is.EqualTo(new[] { boxFields[0], boxFields[2], boxFields[3], boxFields[4] }));
        Assert.That(stores.Select(store => store.Local).ToArray(), Has.All.SameAs(stackBase));
        Assert.That(slotWrites[0].Operands[1], Is.SameAs(wideValue));
        Assert.That(slotWrites[0].MemoryAccessWidthBits, Is.EqualTo(128));
        Assert.That(slotWrites[1].Operands[1], Is.SameAs(scalarValue));
        Assert.That(slotWrites[2].Operands[1], Is.TypeOf<Immediate>().With.Property("Value").EqualTo(7));
        Assert.That(slotWrites[3].Operands[1], Is.SameAs(constantCarrier));

        // 与生产管线同序：复制前递与死码删除负责清除取址定义和溢出/重载复制链。
        SsaSimplifier.Run(context);
        DeadCodeEliminator.Run(context);

        var actual = 生成并执行(mode, context, call, box, concrete, boxFields);
        Assert.That(actual, Is.EqualTo(mode == "text" ? (object)"abc" : 49));
    }

    [Test]
    [Category("基本功能")]
    [Category("边界值")]
    public void 栈聚合实参恢复接受单源Phi链与多字段零写()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var stringType = app.SystemTypes.SystemStringType;
        var (box, concrete, boxFields) = 构建五字段值类型(app);
        var owner = new InjectedTypeAnalysisContext(app.Assemblies[0], "StackAggregate", "Fixture",
            app.SystemTypes.SystemObjectType, System.Reflection.TypeAttributes.Public);
        var fixtureFields = new[] { "F0", "F1", "F2" }
            .Select(name => owner.InjectFieldContext(name, stringType, System.Reflection.FieldAttributes.Public)).ToArray();
        for (var index = 0; index < fixtureFields.Length; index++)
            fixtureFields[index].Offset = 16 + index * 8;
        var consumer = new InjectedMethodAnalysisContext(owner, "Consume", stringType,
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static,
            [concrete], ["value"]);
        var source = new LocalVariable("source", new Register(null, "X0"), owner);
        var wideValue = new LocalVariable("wide", new Register(null, "V0", 1), stringType);
        var scalarValue = new LocalVariable("scalar", new Register(null, "X19", 1), stringType);
        var phiSlot = new LocalVariable("phiSlot", new Register(null, "X19", 2), stringType);
        var addressCarrier = new LocalVariable("address", new Register(null, "X0", 1));
        var stackBase = new LocalVariable("base", new Register(null, "stack_10"));
        var stackScalar = new LocalVariable("scalarSlot", new Register(null, "stack_20"));
        var stackZero = new LocalVariable("zeroSlot", new Register(null, "stack_28"));
        var result = new LocalVariable("result", new Register(null, "X0", 2), stringType);
        var zeroWrite = new Instruction(6, OpCode.Move, stackZero, new Immediate(0)) { MemoryAccessWidthBits = 64 };
        var instructions = new List<Instruction>
        {
            new Instruction(0, OpCode.Move, wideValue, new FieldReference(fixtureFields[0], source, 16)) { MemoryAccessWidthBits = 128 },
            new Instruction(1, OpCode.Move, scalarValue, new FieldReference(fixtureFields[2], source, 32)) { MemoryAccessWidthBits = 64 },
            // 守卫分支裁除后遗留的单源 Phi：复制链跟踪必须看穿它。
            new Instruction(2, OpCode.Phi, phiSlot, scalarValue),
            new Instruction(3, OpCode.Move, addressCarrier, new AddressOf(stackBase)),
            new Instruction(4, OpCode.Move, stackBase, wideValue) { MemoryAccessWidthBits = 128 },
            new Instruction(5, OpCode.Move, stackScalar, phiSlot) { MemoryAccessWidthBits = 64 },
            // STR XZR 形态：一次六十四位零写覆盖 D、E 两个 Int32 字段。
            zeroWrite,
            new Instruction(7, OpCode.Call, consumer, result, addressCarrier),
            new Instruction(8, OpCode.Return, result),
        };
        var context = new InjectedMethodAnalysisContext(owner, "Produce", stringType,
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static, [owner], ["source"])
        {
            ControlFlowGraph = new ISILControlFlowGraph(instructions),
            Locals = [source, wideValue, scalarValue, phiSlot, addressCarrier, stackBase, stackScalar, stackZero, result],
            ParameterLocals = [source],
            AnalysisWarnings = []
        };

        Assert.That(StackAggregateArgumentRecovery.Run(context), Is.True);
        // 全零写由局部零初始化完整承担，整条指令归约为 Nop。
        Assert.That(zeroWrite.OpCode, Is.EqualTo(OpCode.Nop));
        Assert.That(zeroWrite.Operands, Is.Empty);

        SsaSimplifier.Run(context);
        DeadCodeEliminator.Run(context);
        var actual = 生成并执行("text", context, instructions[7], box, concrete, boxFields);
        Assert.That(actual, Is.EqualTo("abc"));
    }

    [Test]
    [Category("基本功能")]
    [Category("边界值")]
    public void 栈聚合实参恢复封闭值类型ByRef并保持地址载体()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var (box, concrete, boxFields) = 构建五字段值类型(app);
        var byRefType = new ByRefTypeAnalysisContext(concrete);
        var (context, stackBase, call, slotWrites, wideValue, scalarValue, constantCarrier) =
            构建栈聚合实参形态(app, byRefType, app.SystemTypes.SystemInt32Type, spill: true);
        var addressCarrier = (LocalVariable)call.Operands[2];

        Assert.That(StackAggregateArgumentRecovery.Run(context), Is.True);
        Assert.That(stackBase.Type, Is.SameAs(concrete));
        Assert.That(addressCarrier.Type, Is.SameAs(byRefType));
        // ref/out 的 ABI 实参是地址，恢复只定型栈对象，不能把 Call 操作数替换成值局部。
        Assert.That(call.Operands[2], Is.SameAs(addressCarrier));
        var stores = slotWrites.Select(write => (FieldReference)write.Operands[0]).ToArray();
        Assert.That(stores.Select(store => 基础字段(store.Field)).ToArray(),
            Is.EqualTo(new[] { boxFields[0], boxFields[2], boxFields[3], boxFields[4] }));
        Assert.That(slotWrites[0].Operands[1], Is.SameAs(wideValue));
        Assert.That(slotWrites[1].Operands[1], Is.SameAs(scalarValue));
        Assert.That(slotWrites[3].Operands[1], Is.SameAs(constantCarrier));
    }

    [Test]
    [Category("边界值")]
    public void 栈聚合实参恢复十六字节ByRef仍按完整布局定型()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var concrete = 构建双字段值类型(app).MakeGenericInstanceType([app.SystemTypes.SystemInt64Type]);
        var byRefType = new ByRefTypeAnalysisContext(concrete);
        var (context, stackBase, call, slotWrites, _, _, _) = 构建双槽完整形态(app, byRefType, hfa: false);
        var addressCarrier = (LocalVariable)call.Operands[2];

        Assert.That(StackAggregateArgumentRecovery.Run(context), Is.True);
        Assert.That(stackBase.Type, Is.SameAs(concrete));
        Assert.That(addressCarrier.Type, Is.SameAs(byRefType));
        Assert.That(call.Operands[2], Is.SameAs(addressCarrier));
        Assert.That(slotWrites.Select(write => write.Operands[0]), Has.All.TypeOf<FieldReference>());
    }

    /// <summary>为已恢复上下文生成真实程序集并执行对照：Fixture 的三个字符串字段置为 a/b/c。</summary>
    private static object? 生成并执行(
        string mode,
        MethodAnalysisContext context,
        Instruction call,
        InjectedTypeAnalysisContext box,
        GenericInstanceTypeAnalysisContext concrete,
        FieldAnalysisContext[] boxFields)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var stringType = app.SystemTypes.SystemStringType;
        var intType = app.SystemTypes.SystemInt32Type;
        var core = new ModuleDefinition("mscorlib.dll");
        var coreAssembly = new AssemblyDefinition("mscorlib", new Version(4, 0, 0, 0));
        coreAssembly.Modules.Add(core);
        绑定AsmResolver系统类型(core, app.SystemTypes.SystemObjectType, "Object", TypeAttributes.Public);
        绑定AsmResolver系统类型(core, stringType, "String", TypeAttributes.Public | TypeAttributes.Sealed);
        绑定AsmResolver系统类型(core, intType, "Int32", TypeAttributes.Public | TypeAttributes.Sealed);
        var name = "StackAggregate_" + Guid.NewGuid().ToString("N");
        var module = new ModuleDefinition(name + ".dll", new AssemblyReference(coreAssembly));
        var boxType = new TypeDefinition("StackAggregate", "Box`2",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout,
            module.CorLibTypeFactory.CorLibScope.CreateTypeReference("System", "ValueType"));
        boxType.GenericParameters.Add(new GenericParameter("TRef", (GenericParameterAttributes)0));
        boxType.GenericParameters.Add(new GenericParameter("TNum", (GenericParameterAttributes)0));
        module.TopLevelTypes.Add(boxType);
        box.PutExtraData("AsmResolverType", boxType);
        var fieldDefinitions = new Dictionary<string, FieldDefinition>();
        foreach (var member in boxFields)
        {
            var definition = new FieldDefinition(member.Name, FieldAttributes.Public,
                member.FieldType.ToTypeSignature(module));
            boxType.Fields.Add(definition);
            member.PutExtraData("AsmResolverField", definition);
            fieldDefinitions[member.Name] = definition;
        }
        var host = new TypeDefinition("StackAggregate", "Fixture", TypeAttributes.Public | TypeAttributes.Class,
            module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(host);
        var ownerContext = (TypeAnalysisContext)context.DeclaringType!;
        ownerContext.PutExtraData("AsmResolverType", host);
        var owner = (InjectedTypeAnalysisContext)ownerContext;
        foreach (var fixtureField in owner.Fields.Where(field => field.Name.StartsWith("F", StringComparison.Ordinal)))
        {
            var definition = new FieldDefinition(fixtureField.Name, FieldAttributes.Public,
                fixtureField.FieldType.ToTypeSignature(module));
            host.Fields.Add(definition);
            fixtureField.PutExtraData("AsmResolverField", definition);
        }
        var consumerContext = (MethodAnalysisContext)call.Operands[0];
        var returnSignature = mode == "text" ? module.CorLibTypeFactory.String : module.CorLibTypeFactory.Int32;
        var consumer = new MethodDefinition("Consume", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(returnSignature, [concrete.ToTypeSignature(module)]));
        host.Methods.Add(consumer);
        consumerContext.PutExtraData("AsmResolverMethod", consumer);
        consumer.CilMethodBody = new CilMethodBody();
        var closedReference = concrete.ToTypeSignature(module).ToTypeDefOrRef();
        IFieldDescriptor 字段引用(string fieldName)
            => new MemberReference(closedReference, fieldName, fieldDefinitions[fieldName].Signature!);
        var consumerBody = consumer.CilMethodBody.Instructions;
        if (mode == "text")
        {
            var concat = module.DefaultImporter!.ImportMethod(
                typeof(string).GetMethod(nameof(string.Concat), [typeof(string), typeof(string)])!);
            consumerBody.Add(CilOpCodes.Ldarg_0);
            consumerBody.Add(CilOpCodes.Ldfld, 字段引用("A"));
            consumerBody.Add(CilOpCodes.Ldarg_0);
            consumerBody.Add(CilOpCodes.Ldfld, 字段引用("B"));
            consumerBody.Add(CilOpCodes.Call, concat);
            consumerBody.Add(CilOpCodes.Ldarg_0);
            consumerBody.Add(CilOpCodes.Ldfld, 字段引用("C"));
            consumerBody.Add(CilOpCodes.Call, concat);
        }
        else
        {
            consumerBody.Add(CilOpCodes.Ldarg_0);
            consumerBody.Add(CilOpCodes.Ldfld, 字段引用("D"));
            consumerBody.Add(CilOpCodes.Ldarg_0);
            consumerBody.Add(CilOpCodes.Ldfld, 字段引用("E"));
            consumerBody.Add(CilOpCodes.Add);
        }
        consumerBody.Add(CilOpCodes.Ret);
        var method = new MethodDefinition("Produce", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(returnSignature, [new TypeDefOrRefSignature(host, false)]));
        method.ParameterDefinitions.Add(new ParameterDefinition(1, "source", (ParameterAttributes)0));
        host.Methods.Add(method);

        IlGenerator.GenerateIl(context, method);
        CilStackValidator.Validate(method.CilMethodBody!, context.FullName);
        Assert.That(method.CilMethodBody!.Instructions.Any(instruction => instruction.OpCode == CilOpCodes.Ldloca
            || instruction.OpCode == CilOpCodes.Ldloca_S), Is.True);

        var assembly = new AssemblyDefinition(name, new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        using var stream = new System.IO.MemoryStream();
        module.Write(stream);
        var runtime = System.Reflection.Assembly.Load(stream.ToArray()).GetType("StackAggregate.Fixture", true)!;
        var fixture = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(runtime);
        runtime.GetField("F0")!.SetValue(fixture, "a");
        runtime.GetField("F1")!.SetValue(fixture, "b");
        runtime.GetField("F2")!.SetValue(fixture, "c");
        return runtime.GetMethod("Produce")!.Invoke(null, [fixture]);
    }

    [TestCase("small_by_value")]
    [TestCase("incomplete")]
    [TestCase("gap_on_field")]
    [TestCase("overlap")]
    [TestCase("hfa")]
    [TestCase("byref_incomplete")]
    [TestCase("extra_consumer")]
    [TestCase("no_dominance")]
    [TestCase("open_generic")]
    [TestCase("multiple_definitions")]
    [Category("异常输入")]
    [Category("边界值")]
    public void 栈聚合实参恢复拒绝不完整不支配或被篡改的覆盖(string mode)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var (box, concrete, _) = 构建五字段值类型(app);
        TypeAnalysisContext parameterType = mode switch
        {
            // 十六字节按值形参仍按两槽寄存器传递，AddressOf 形态不得恢复（address_to_value 语义不变）。
            "small_by_value" => 构建双字段值类型(app).MakeGenericInstanceType([app.SystemTypes.SystemInt64Type]),
            // HFA 由 V 寄存器逐分量传递，绝不经过栈临时取址。
            "hfa" => 构建纯浮点值类型(app),
            "byref_incomplete" => new ByRefTypeAnalysisContext(concrete),
            "open_generic" => box.MakeGenericInstanceType(box.GenericParameters),
            _ => concrete,
        };
        Action<List<Instruction>, List<LocalVariable>>? mutate = mode switch
        {
            "incomplete" or "byref_incomplete" => (List<Instruction> instructions, List<LocalVariable> _) =>
            {
                // 删除最后一槽写入，覆盖不再完整。
                instructions.RemoveAt(9);
                Renumber(instructions);
            },
            "gap_on_field" => (List<Instruction> instructions, List<LocalVariable> _) =>
            {
                // 写起点压到字段中段（相对偏移 20 不是任何字段边界）。
                var gap = new LocalVariable("gapSlot", new Register(null, "stack_24"));
                instructions[7] = new Instruction(7, OpCode.Move, gap, new Immediate(3)) { MemoryAccessWidthBits = 32 };
            },
            "overlap" => (List<Instruction> instructions, List<LocalVariable> locals) =>
            {
                // 越界宽写越过临时区末尾。
                var overflow = new LocalVariable("overflowSlot", new Register(null, "stack_2C"));
                instructions.Add(new Instruction(12, OpCode.Move, overflow, new Immediate(0)) { MemoryAccessWidthBits = 128 });
                locals.Add(overflow);
            },
            "extra_consumer" => (List<Instruction> instructions, List<LocalVariable> locals) =>
            {
                // 槽写入后被额外读取，无法再证明调用时槽内仍是证明过的值。
                var sink = new LocalVariable("sink", new Register(null, "X9", 1), app.SystemTypes.SystemObjectType);
                instructions.Insert(10, new Instruction(10, OpCode.Move, sink, locals[8]));
                locals.Add(sink);
                Renumber(instructions);
            },
            "no_dominance" => (List<Instruction> instructions, List<LocalVariable> _) =>
            {
                // 写入不支配调用：最后一槽移到调用之后。
                var write = instructions[9];
                instructions.RemoveAt(9);
                instructions.Add(write);
                Renumber(instructions);
            },
            "multiple_definitions" => (List<Instruction> instructions, List<LocalVariable> _) =>
            {
                // 同一槽出现第二条定义，SSA 单一来源证明失效。
                instructions.Insert(9, new Instruction(9, OpCode.Move, instructions[8].Operands[0], new Immediate(9)) { MemoryAccessWidthBits = 32 });
                Renumber(instructions);
            },
            _ => null,
        };
        var (context, stackBase, call, slotWrites, _, _, _) = 构建栈聚合实参形态(
            app, parameterType, app.SystemTypes.SystemInt32Type, spill: true, mutate: mutate);
        var addressCarrier = (LocalVariable)call.Operands[2];
        if (mode is "small_by_value" or "hfa")
        {
            // 小聚合与 HFA 形态使用各自布局的完整槽写入，拒绝只能来自形参门本身。
            (context, stackBase, call, slotWrites, _, _, _) = 构建双槽完整形态(app, parameterType, mode == "hfa");
            addressCarrier = (LocalVariable)call.Operands[2];
        }

        Assert.That(StackAggregateArgumentRecovery.Run(context), Is.False);
        Assert.That(stackBase.Type, Is.Null);
        Assert.That(call.Operands[2], Is.SameAs(addressCarrier));
        foreach (var write in slotWrites)
            Assert.That(write.Operands[0], Is.TypeOf<LocalVariable>());
    }

    /// <summary>两字段同类型值类型（十六字节），用于验证小聚合按值形参不进入本恢复。</summary>
    private static InjectedTypeAnalysisContext 构建双字段值类型(ApplicationAnalysisContext app)
    {
        var duo = new InjectedTypeAnalysisContext(app.Assemblies[0], "StackAggregate", "Duo`1",
            app.GetAssemblyByName("mscorlib")!.GetTypeByFullName("System.ValueType"),
            System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.SequentialLayout);
        var parameter = new GenericParameterTypeAnalysisContext("T", 0, Il2CppTypeEnum.IL2CPP_TYPE_VAR,
            System.Reflection.GenericParameterAttributes.None, duo);
        duo.GenericParameters.Add(parameter);
        foreach (var name in new[] { "P", "Q" })
            duo.InjectFieldContext(name, parameter, System.Reflection.FieldAttributes.Public);
        return duo;
    }

    /// <summary>四字段 Double 同质浮点聚合（HFA，32 字节）。</summary>
    private static InjectedTypeAnalysisContext 构建纯浮点值类型(ApplicationAnalysisContext app)
    {
        var quad = new InjectedTypeAnalysisContext(app.Assemblies[0], "StackAggregate", "Quad",
            app.GetAssemblyByName("mscorlib")!.GetTypeByFullName("System.ValueType"),
            System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.SequentialLayout);
        foreach (var name in new[] { "X", "Y", "Z", "W" })
            quad.InjectFieldContext(name, app.SystemTypes.SystemDoubleType, System.Reflection.FieldAttributes.Public)
                .Offset = 0;
        return quad;
    }

    /// <summary>具体泛型布局字段须还原为基础字段身份后再比较。</summary>
    private static FieldAnalysisContext 基础字段(FieldAnalysisContext field)
        => field is ConcreteGenericFieldAnalysisContext concrete ? concrete.BaseFieldContext : field;

    /// <summary>两槽或四槽完整覆盖的小聚合/HFA 形态：布局本身完整，只有形参门可以拒绝。</summary>
    private static (InjectedMethodAnalysisContext Context, LocalVariable StackBase, Instruction Call,
        List<Instruction> SlotWrites, LocalVariable, LocalVariable, LocalVariable) 构建双槽完整形态(
            ApplicationAnalysisContext app, TypeAnalysisContext parameterType, bool hfa)
    {
        var owner = new InjectedTypeAnalysisContext(app.Assemblies[0], "StackAggregate", "Fixture",
            app.SystemTypes.SystemObjectType, System.Reflection.TypeAttributes.Public);
        var consumer = new InjectedMethodAnalysisContext(owner, "Consume", app.SystemTypes.SystemInt32Type,
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static,
            [parameterType], ["value"]);
        var stackBase = new LocalVariable("base", new Register(null, "stack_10"));
        var addressCarrier = new LocalVariable("address", new Register(null, "X0", 1));
        var result = new LocalVariable("result", new Register(null, "X0", 2), app.SystemTypes.SystemInt32Type);
        var instructions = new List<Instruction>
        {
            new Instruction(0, OpCode.Move, addressCarrier, new AddressOf(stackBase)),
        };
        var slotWrites = new List<Instruction>();
        var slots = hfa ? new[] { "stack_10", "stack_18", "stack_20", "stack_28" } : new[] { "stack_10", "stack_18" };
        foreach (var (slotName, index) in slots.Select((slotName, index) => (slotName, index)))
        {
            var slot = slotName == "stack_10" ? stackBase : new LocalVariable("slot" + index, new Register(null, slotName));
            var carrier = new LocalVariable("carrier" + index, new Register(null, "X1", index + 1), app.SystemTypes.SystemInt64Type);
            instructions.Add(new Instruction(1 + index * 2, OpCode.Move, carrier, new Immediate(index + 1)));
            var write = new Instruction(2 + index * 2, OpCode.Move, slot, carrier) { MemoryAccessWidthBits = 64 };
            instructions.Add(write);
            slotWrites.Add(write);
        }
        var call = new Instruction(9, OpCode.Call, consumer, result, addressCarrier);
        instructions.Add(call);
        instructions.Add(new Instruction(10, OpCode.Return, result));
        var locals = instructions.SelectMany(instruction => instruction.Operands).OfType<LocalVariable>().ToList();
        var context = new InjectedMethodAnalysisContext(owner, "Produce", app.SystemTypes.SystemInt32Type,
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static, [], [])
        {
            ControlFlowGraph = new ISILControlFlowGraph(instructions),
            Locals = locals,
            ParameterLocals = [],
            AnalysisWarnings = []
        };
        return (context, stackBase, call, slotWrites, null!, null!, null!);
    }

    /// <summary>合成嵌套布局：Inner`1<T>{X,Y}、Outer`2<TRef,TNum>{A: TRef, B: Inner`1<TNum>, C: TNum}。</summary>
    private static (InjectedTypeAnalysisContext Inner, InjectedTypeAnalysisContext Outer,
        GenericInstanceTypeAnalysisContext InnerInt, GenericInstanceTypeAnalysisContext Concrete,
        FieldAnalysisContext[] InnerFields, FieldAnalysisContext[] OuterFields) 构建嵌套值类型(
            ApplicationAnalysisContext app, bool privateInnerFields = false)
    {
        var valueType = app.GetAssemblyByName("mscorlib")!.GetTypeByFullName("System.ValueType");
        var inner = new InjectedTypeAnalysisContext(app.Assemblies[0], "StackAggregate", "Inner`1", valueType,
            System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.SequentialLayout);
        var innerParameter = new GenericParameterTypeAnalysisContext("T", 0, Il2CppTypeEnum.IL2CPP_TYPE_VAR,
            System.Reflection.GenericParameterAttributes.None, inner);
        inner.GenericParameters.Add(innerParameter);
        var innerFields = new[] { "X", "Y" }.Select(name => inner.InjectFieldContext(name, innerParameter,
            privateInnerFields ? System.Reflection.FieldAttributes.Private : System.Reflection.FieldAttributes.Public)).ToArray();
        if (privateInnerFields)
        {
            foreach (var field in innerFields)
            {
                var getter = inner.InjectMethodContext(
                    "get_" + field.Name,
                    innerParameter,
                    System.Reflection.MethodAttributes.Public);
                var setter = inner.InjectMethodContext(
                    "set_" + field.Name,
                    app.SystemTypes.SystemVoidType,
                    System.Reflection.MethodAttributes.Public,
                    innerParameter);
                inner.InjectPropertyContext(
                    field.Name,
                    innerParameter,
                    getter,
                    setter,
                    System.Reflection.PropertyAttributes.None);
            }
        }
        var innerInt = inner.MakeGenericInstanceType([app.SystemTypes.SystemInt32Type]);
        var outer = new InjectedTypeAnalysisContext(app.Assemblies[0], "StackAggregate", "Outer`2", valueType,
            System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.SequentialLayout);
        var referenceParameter = new GenericParameterTypeAnalysisContext("TRef", 0, Il2CppTypeEnum.IL2CPP_TYPE_VAR,
            System.Reflection.GenericParameterAttributes.None, outer);
        var numberParameter = new GenericParameterTypeAnalysisContext("TNum", 1, Il2CppTypeEnum.IL2CPP_TYPE_VAR,
            System.Reflection.GenericParameterAttributes.None, outer);
        outer.GenericParameters.Add(referenceParameter);
        outer.GenericParameters.Add(numberParameter);
        var outerFields = new FieldAnalysisContext[3];
        outerFields[0] = outer.InjectFieldContext("A", referenceParameter, System.Reflection.FieldAttributes.Public);
        outerFields[1] = outer.InjectFieldContext("B",
            inner.MakeGenericInstanceType([numberParameter]), System.Reflection.FieldAttributes.Public);
        outerFields[2] = outer.InjectFieldContext("C", numberParameter, System.Reflection.FieldAttributes.Public);
        var concrete = outer.MakeGenericInstanceType([app.SystemTypes.SystemStringType, app.SystemTypes.SystemInt32Type]);
        return (inner, outer, innerInt, concrete, innerFields, outerFields);
    }

    /// <summary>嵌套形态的标准写入：A 引用读取、B.X 零写、B.Y+C 跨聚合边界的六十四位读取写。</summary>
    private static (InjectedMethodAnalysisContext Context, LocalVariable StackBase, Instruction Call,
        Instruction ZeroWrite, Instruction NestedWrite, LocalVariable NestedValue)
        构建嵌套覆盖形态(ApplicationAnalysisContext app, GenericInstanceTypeAnalysisContext concrete, bool simdNestedSource)
    {
        var stringType = app.SystemTypes.SystemStringType;
        var intType = app.SystemTypes.SystemInt32Type;
        var owner = new InjectedTypeAnalysisContext(app.Assemblies[0], "StackAggregate", "Fixture",
            app.SystemTypes.SystemObjectType, System.Reflection.TypeAttributes.Public);
        var f0 = owner.InjectFieldContext("F0", stringType, System.Reflection.FieldAttributes.Public);
        f0.Offset = 16;
        var m = owner.InjectFieldContext("M", intType, System.Reflection.FieldAttributes.Public);
        m.Offset = 24;
        var n = owner.InjectFieldContext("N", intType, System.Reflection.FieldAttributes.Public);
        n.Offset = 28;
        var consumer = new InjectedMethodAnalysisContext(owner, "Consume", intType,
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static,
            [concrete], ["value"]);
        var source = new LocalVariable("source", new Register(null, "X0"), owner);
        var textValue = new LocalVariable("text", new Register(null, "V0", 1), stringType);
        var nestedValue = new LocalVariable("nested", new Register(null, simdNestedSource ? "V1" : "X9", 1), intType);
        var addressCarrier = new LocalVariable("address", new Register(null, "X0", 1));
        var stackBase = new LocalVariable("base", new Register(null, "stack_10"));
        var stackZero = new LocalVariable("zeroSlot", new Register(null, "stack_18"));
        var stackNested = new LocalVariable("nestedSlot", new Register(null, "stack_1C"));
        var result = new LocalVariable("result", new Register(null, "X0", 2), intType);
        var zeroWrite = new Instruction(4, OpCode.Move, stackZero, new Immediate(0)) { MemoryAccessWidthBits = 32 };
        var nestedWrite = new Instruction(5, OpCode.Move, stackNested, nestedValue) { MemoryAccessWidthBits = 64 };
        var instructions = new List<Instruction>
        {
            new Instruction(0, OpCode.Move, textValue, new FieldReference(f0, source, 16)) { MemoryAccessWidthBits = 64 },
            new Instruction(1, OpCode.Move, nestedValue, new FieldReference(m, source, 24)) { MemoryAccessWidthBits = 64 },
            new Instruction(2, OpCode.Move, addressCarrier, new AddressOf(stackBase)),
            new Instruction(3, OpCode.Move, stackBase, textValue) { MemoryAccessWidthBits = 64 },
            zeroWrite,
            nestedWrite,
            new Instruction(6, OpCode.Call, consumer, result, addressCarrier),
            new Instruction(7, OpCode.Return, result),
        };
        var context = new InjectedMethodAnalysisContext(owner, "Produce", intType,
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static, [owner], ["source"])
        {
            ControlFlowGraph = new ISILControlFlowGraph(instructions),
            Locals = [source, textValue, nestedValue, addressCarrier, stackBase, stackZero, stackNested, result],
            ParameterLocals = [source],
            AnalysisWarnings = []
        };
        return (context, stackBase, instructions[6], zeroWrite, nestedWrite, nestedValue);
    }

    [Test]
    [Category("基本功能")]
    [Category("边界值")]
    public void 栈聚合实参恢复叶级覆盖跨嵌套聚合边界并生成可执行CIL()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var stringType = app.SystemTypes.SystemStringType;
        var intType = app.SystemTypes.SystemInt32Type;
        var (inner, outer, innerInt, concrete, innerFields, outerFields) = 构建嵌套值类型(app);
        var (context, stackBase, call, zeroWrite, nestedWrite, nestedValue) =
            构建嵌套覆盖形态(app, concrete, simdNestedSource: true);

        Assert.That(StackAggregateArgumentRecovery.Run(context), Is.True);
        Assert.That(stackBase.Type, Is.SameAs(concrete));
        Assert.That(call.Operands[2], Is.SameAs(stackBase));
        // 零写归约为 Nop；跨聚合边界的六十四位写改写为根字段 B 起点偏移 12 的字段写入。
        Assert.That(zeroWrite.OpCode, Is.EqualTo(OpCode.Nop));
        Assert.That(nestedWrite.Operands[0], Is.TypeOf<FieldReference>());
        var nestedStore = (FieldReference)nestedWrite.Operands[0];
        Assert.That(基础字段(nestedStore.Field), Is.SameAs(outerFields[1]));
        Assert.That(nestedStore.Offset, Is.EqualTo(12));
        Assert.That(nestedWrite.Operands[1], Is.SameAs(nestedValue));

        SsaSimplifier.Run(context);
        DeadCodeEliminator.Run(context);

        var core = new ModuleDefinition("mscorlib.dll");
        var coreAssembly = new AssemblyDefinition("mscorlib", new Version(4, 0, 0, 0));
        coreAssembly.Modules.Add(core);
        绑定AsmResolver系统类型(core, app.SystemTypes.SystemObjectType, "Object", TypeAttributes.Public);
        绑定AsmResolver系统类型(core, stringType, "String", TypeAttributes.Public | TypeAttributes.Sealed);
        绑定AsmResolver系统类型(core, intType, "Int32", TypeAttributes.Public | TypeAttributes.Sealed);
        var name = "StackAggregateNested_" + Guid.NewGuid().ToString("N");
        var module = new ModuleDefinition(name + ".dll", new AssemblyReference(coreAssembly));
        var innerType = new TypeDefinition("StackAggregate", "Inner`1",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout,
            module.CorLibTypeFactory.CorLibScope.CreateTypeReference("System", "ValueType"));
        innerType.GenericParameters.Add(new GenericParameter("T", (GenericParameterAttributes)0));
        module.TopLevelTypes.Add(innerType);
        inner.PutExtraData("AsmResolverType", innerType);
        foreach (var member in innerFields)
        {
            var definition = new FieldDefinition(member.Name, FieldAttributes.Public, member.FieldType.ToTypeSignature(module));
            innerType.Fields.Add(definition);
            member.PutExtraData("AsmResolverField", definition);
        }
        var outerType = new TypeDefinition("StackAggregate", "Outer`2",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout,
            module.CorLibTypeFactory.CorLibScope.CreateTypeReference("System", "ValueType"));
        outerType.GenericParameters.Add(new GenericParameter("TRef", (GenericParameterAttributes)0));
        outerType.GenericParameters.Add(new GenericParameter("TNum", (GenericParameterAttributes)0));
        module.TopLevelTypes.Add(outerType);
        outer.PutExtraData("AsmResolverType", outerType);
        var outerDefinitions = new Dictionary<string, FieldDefinition>();
        foreach (var member in outerFields)
        {
            var definition = new FieldDefinition(member.Name, FieldAttributes.Public, member.FieldType.ToTypeSignature(module));
            outerType.Fields.Add(definition);
            member.PutExtraData("AsmResolverField", definition);
            outerDefinitions[member.Name] = definition;
        }
        var host = new TypeDefinition("StackAggregate", "Fixture", TypeAttributes.Public | TypeAttributes.Class,
            module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(host);
        var ownerContext = (TypeAnalysisContext)context.DeclaringType!;
        ownerContext.PutExtraData("AsmResolverType", host);
        var owner = (InjectedTypeAnalysisContext)ownerContext;
        foreach (var fixtureField in owner.Fields)
        {
            var definition = new FieldDefinition(fixtureField.Name, FieldAttributes.Public,
                fixtureField.FieldType.ToTypeSignature(module));
            host.Fields.Add(definition);
            fixtureField.PutExtraData("AsmResolverField", definition);
        }
        var consumerContext = (MethodAnalysisContext)call.Operands[0];
        var consumer = new MethodDefinition("Consume", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Int32, [concrete.ToTypeSignature(module)]));
        host.Methods.Add(consumer);
        consumerContext.PutExtraData("AsmResolverMethod", consumer);
        consumer.CilMethodBody = new CilMethodBody();
        // return value.B.Y * 100 + value.C
        var closedOuter = concrete.ToTypeSignature(module).ToTypeDefOrRef();
        var closedInner = innerInt.ToTypeSignature(module).ToTypeDefOrRef();
        var body = consumer.CilMethodBody.Instructions;
        // 按值形参先存入局部再取址：ldflda 的接收者必须是托管地址而非值。
        var valueCopy = new CilLocalVariable(concrete.ToTypeSignature(module));
        consumer.CilMethodBody.LocalVariables.Add(valueCopy);
        body.Add(CilOpCodes.Ldarg_0);
        body.Add(CilOpCodes.Stloc, valueCopy);
        body.Add(CilOpCodes.Ldloca, valueCopy);
        body.Add(CilOpCodes.Ldflda, new MemberReference(closedOuter, "B", outerDefinitions["B"].Signature!));
        body.Add(CilOpCodes.Ldfld, new MemberReference(closedInner, "Y",
            innerType.Fields.Single(f => f.Name == "Y").Signature!));
        body.Add(CilOpCodes.Ldc_I4, 100);
        body.Add(CilOpCodes.Mul);
        body.Add(CilOpCodes.Ldarg_0);
        body.Add(CilOpCodes.Ldfld, new MemberReference(closedOuter, "C", outerDefinitions["C"].Signature!));
        body.Add(CilOpCodes.Add);
        body.Add(CilOpCodes.Ret);
        var method = new MethodDefinition("Produce", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Int32, [new TypeDefOrRefSignature(host, false)]));
        method.ParameterDefinitions.Add(new ParameterDefinition(1, "source", (ParameterAttributes)0));
        host.Methods.Add(method);

        IlGenerator.GenerateIl(context, method);
        CilStackValidator.Validate(method.CilMethodBody!, context.FullName);

        var assembly = new AssemblyDefinition(name, new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        using var stream = new System.IO.MemoryStream();
        module.Write(stream);
        var runtime = System.Reflection.Assembly.Load(stream.ToArray()).GetType("StackAggregate.Fixture", true)!;
        var fixture = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(runtime);
        runtime.GetField("F0")!.SetValue(fixture, "a");
        runtime.GetField("M")!.SetValue(fixture, 22);
        runtime.GetField("N")!.SetValue(fixture, 33);
        var actual = runtime.GetMethod("Produce")!.Invoke(null, [fixture]);
        // 六十四位读取先命中 M（写入 B.Y），后命中 N（写入 C）：Y*100+C = 22*100+33。
        Assert.That(actual, Is.EqualTo(22 * 100 + 33));
    }

    [Test]
    [Category("基本功能")]
    [Category("边界值")]
    public void 栈聚合实参恢复接受嵌套私有叶的公开访问器合同()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var (_, _, _, concrete, _, _) = 构建嵌套值类型(app, privateInnerFields: true);
        var (context, stackBase, call, zeroWrite, nestedWrite, _) = 构建嵌套覆盖形态(
            app, concrete, simdNestedSource: true);

        Assert.That(StackAggregateArgumentRecovery.Run(context), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(stackBase.Type, Is.SameAs(concrete));
            Assert.That(call.Operands[2], Is.SameAs(stackBase));
            Assert.That(zeroWrite.OpCode, Is.EqualTo(OpCode.Nop));
            Assert.That(nestedWrite.Operands[0], Is.TypeOf<FieldReference>());
        });
    }

    /// <summary>完整嵌套聚合的纯标量读取必须先展开为叶字段，再复用向量位通道。</summary>
    [Test]
    [Category("基本功能")]
    [Category("边界值")]
    public void 栈聚合实参恢复完整纯标量嵌套聚合跨度()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var intType = app.SystemTypes.SystemInt32Type;
        var (_, _, innerInt, concrete, _, outerFields) = 构建嵌套值类型(app);
        var owner = new InjectedTypeAnalysisContext(app.Assemblies[0], "StackAggregate", "WholeScalarFixture",
            app.SystemTypes.SystemObjectType, System.Reflection.TypeAttributes.Public);
        var consumer = new InjectedMethodAnalysisContext(owner, "Consume", intType,
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static,
            [concrete], ["value"]);
        var source = new LocalVariable("source", new Register(null, "X0"), concrete);
        var nestedValue = new LocalVariable("nested", new Register(null, "V0", 1), innerInt);
        var scalarValue = new LocalVariable("scalar", new Register(null, "X1", 1), intType);
        var addressCarrier = new LocalVariable("address", new Register(null, "X0", 2));
        var stackBase = new LocalVariable("base", new Register(null, "stack_10"));
        var stackNested = new LocalVariable("nestedSlot", new Register(null, "stack_18"));
        var stackScalar = new LocalVariable("scalarSlot", new Register(null, "stack_20"));
        var result = new LocalVariable("result", new Register(null, "X0", 3), intType);
        var zeroWrite = new Instruction(3, OpCode.Move, stackBase, new Immediate(0))
        {
            MemoryAccessWidthBits = 64
        };
        var nestedWrite = new Instruction(4, OpCode.Move, stackNested, nestedValue)
        {
            MemoryAccessWidthBits = 64
        };
        var scalarWrite = new Instruction(5, OpCode.Move, stackScalar, scalarValue)
        {
            MemoryAccessWidthBits = 32
        };
        var instructions = new List<Instruction>
        {
            new Instruction(0, OpCode.Move, nestedValue, new FieldReference(outerFields[1], source, 8))
            {
                MemoryAccessWidthBits = 64
            },
            new Instruction(1, OpCode.Move, scalarValue, new FieldReference(outerFields[2], source, 16))
            {
                MemoryAccessWidthBits = 32
            },
            new Instruction(2, OpCode.Move, addressCarrier, new AddressOf(stackBase)),
            zeroWrite,
            nestedWrite,
            scalarWrite,
            new Instruction(6, OpCode.Call, consumer, result, addressCarrier),
            new Instruction(7, OpCode.Return, result),
        };
        var context = new InjectedMethodAnalysisContext(owner, "ProduceWholeScalar", intType,
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static, [], [])
        {
            ControlFlowGraph = new ISILControlFlowGraph(instructions),
            Locals = [source, nestedValue, scalarValue, addressCarrier, stackBase, stackNested, stackScalar, result],
            ParameterLocals = [],
            AnalysisWarnings = []
        };

        Assert.That(StackAggregateArgumentRecovery.Run(context), Is.True);
        Assert.That(stackBase.Type, Is.SameAs(concrete));
        Assert.That(zeroWrite.OpCode, Is.EqualTo(OpCode.Nop));
        Assert.That(nestedWrite.Operands[0], Is.TypeOf<FieldReference>());
        var nestedStore = (FieldReference)nestedWrite.Operands[0];
        Assert.That(基础字段(nestedStore.Field), Is.SameAs(outerFields[1]));
        Assert.That(nestedStore.Offset, Is.EqualTo(8));
        Assert.That(nestedWrite.Operands[1], Is.SameAs(nestedValue));
        Assert.That(scalarWrite.Operands[0], Is.TypeOf<FieldReference>());
        var scalarStore = (FieldReference)scalarWrite.Operands[0];
        Assert.That(基础字段(scalarStore.Field), Is.SameAs(outerFields[2]));
        Assert.That(scalarStore.Offset, Is.EqualTo(16));
    }

    [TestCase("nested_immediate")]
    [TestCase("channel_unavailable")]
    [TestCase("gap_inside_span")]
    [Category("异常输入")]
    [Category("边界值")]
    public void 栈聚合实参恢复拒绝嵌套立即数无通道及间隙跨度(string mode)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var (_, _, _, concrete, _, _) = 构建嵌套值类型(app);
        if (mode == "gap_inside_span")
        {
            // A@0 String(8)、B@8 Int32(4)、填充[12,16)、C@16 String(8)：一百二十八位写横跨填充间隙。
            var gapOuter = new InjectedTypeAnalysisContext(app.Assemblies[0], "StackAggregate", "Gap`2",
                app.GetAssemblyByName("mscorlib")!.GetTypeByFullName("System.ValueType"),
                System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.SequentialLayout);
            var referenceParameter = new GenericParameterTypeAnalysisContext("TRef", 0, Il2CppTypeEnum.IL2CPP_TYPE_VAR,
                System.Reflection.GenericParameterAttributes.None, gapOuter);
            var numberParameter = new GenericParameterTypeAnalysisContext("TNum", 1, Il2CppTypeEnum.IL2CPP_TYPE_VAR,
                System.Reflection.GenericParameterAttributes.None, gapOuter);
            gapOuter.GenericParameters.Add(referenceParameter);
            gapOuter.GenericParameters.Add(numberParameter);
            gapOuter.InjectFieldContext("A", referenceParameter, System.Reflection.FieldAttributes.Public);
            gapOuter.InjectFieldContext("B", numberParameter, System.Reflection.FieldAttributes.Public);
            gapOuter.InjectFieldContext("C", referenceParameter, System.Reflection.FieldAttributes.Public);
            var gapConcrete = gapOuter.MakeGenericInstanceType(
                [app.SystemTypes.SystemStringType, app.SystemTypes.SystemInt32Type]);
            var (gapContext, _, gapCall, _, _, _) = 构建间隙跨度形态(app, gapConcrete);
            var gapCarrier = (LocalVariable)gapCall.Operands[2];
            Assert.That(StackAggregateArgumentRecovery.Run(gapContext), Is.False);
            Assert.That(gapCall.Operands[2], Is.SameAs(gapCarrier));
            return;
        }
        var (context, stackBase, call, zeroWrite, nestedWrite, _) = 构建嵌套覆盖形态(
            app, concrete, simdNestedSource: mode != "channel_unavailable");
        if (mode == "nested_immediate")
            // 非零立即数写嵌套叶 B.X：普通通道不能寻址嵌套叶，必须拒绝。
            zeroWrite.SetOperands(zeroWrite.Operands[0], new Immediate(11));
        Assert.That(StackAggregateArgumentRecovery.Run(context), Is.False);
        Assert.That(stackBase.Type, Is.Null);
        foreach (var write in new[] { zeroWrite, nestedWrite })
            Assert.That(write.Operands[0], Is.TypeOf<LocalVariable>());
    }

    /// <summary>间隙跨度形态：单个一百二十八位写覆盖 [String, Int32, 填充]。</summary>
    private static (InjectedMethodAnalysisContext Context, LocalVariable StackBase, Instruction Call,
        Instruction, Instruction, LocalVariable) 构建间隙跨度形态(
            ApplicationAnalysisContext app, GenericInstanceTypeAnalysisContext concrete)
    {
        var stringType = app.SystemTypes.SystemStringType;
        var owner = new InjectedTypeAnalysisContext(app.Assemblies[0], "StackAggregate", "Fixture",
            app.SystemTypes.SystemObjectType, System.Reflection.TypeAttributes.Public);
        var g0 = owner.InjectFieldContext("G0", stringType, System.Reflection.FieldAttributes.Public);
        g0.Offset = 16;
        var g1 = owner.InjectFieldContext("G1", stringType, System.Reflection.FieldAttributes.Public);
        g1.Offset = 24;
        var consumer = new InjectedMethodAnalysisContext(owner, "Consume", app.SystemTypes.SystemInt32Type,
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static,
            [concrete], ["value"]);
        var source = new LocalVariable("source", new Register(null, "X0"), owner);
        var wideValue = new LocalVariable("wide", new Register(null, "V0", 1), stringType);
        var tailValue = new LocalVariable("tail", new Register(null, "V1", 1), stringType);
        var addressCarrier = new LocalVariable("address", new Register(null, "X0", 1));
        var stackBase = new LocalVariable("base", new Register(null, "stack_10"));
        var stackTail = new LocalVariable("tailSlot", new Register(null, "stack_20"));
        var result = new LocalVariable("result", new Register(null, "X0", 2), app.SystemTypes.SystemInt32Type);
        var instructions = new List<Instruction>
        {
            new Instruction(0, OpCode.Move, wideValue, new FieldReference(g0, source, 16)) { MemoryAccessWidthBits = 128 },
            new Instruction(1, OpCode.Move, tailValue, new FieldReference(g1, source, 24)) { MemoryAccessWidthBits = 64 },
            new Instruction(2, OpCode.Move, addressCarrier, new AddressOf(stackBase)),
            new Instruction(3, OpCode.Move, stackBase, wideValue) { MemoryAccessWidthBits = 128 },
            new Instruction(4, OpCode.Move, stackTail, tailValue) { MemoryAccessWidthBits = 64 },
            new Instruction(5, OpCode.Call, consumer, result, addressCarrier),
            new Instruction(6, OpCode.Return, result),
        };
        var context = new InjectedMethodAnalysisContext(owner, "Produce", app.SystemTypes.SystemInt32Type,
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static, [owner], ["source"])
        {
            ControlFlowGraph = new ISILControlFlowGraph(instructions),
            Locals = [source, wideValue, tailValue, addressCarrier, stackBase, stackTail, result],
            ParameterLocals = [source],
            AnalysisWarnings = []
        };
        return (context, stackBase, instructions[5], instructions[3], instructions[4], wideValue);
    }

    private static void Renumber(List<Instruction> instructions)
    {
        for (var index = 0; index < instructions.Count; index++)
            instructions[index].Index = index;
    }
}
