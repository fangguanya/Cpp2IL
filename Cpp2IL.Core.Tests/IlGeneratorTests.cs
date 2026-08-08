using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;
using ReflectionMethodAttributes = System.Reflection.MethodAttributes;

namespace Cpp2IL.Core.Tests;

public class IlGeneratorTests
{
    private static void 绑定AsmResolver系统类型(
        ModuleDefinition module,
        TypeAnalysisContext context,
        string name,
        TypeAttributes attributes)
    {
        var definition = new TypeDefinition("System", name, attributes);
        module.TopLevelTypes.Add(definition);
        context.PutExtraData("AsmResolverType", definition);
    }

    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    public void Void方法尾部为普通调用时需要返回终结点()
    {
        var finalInstruction = new CilInstruction(CilOpCodes.Call, null);

        Assert.That(IlGenerator.RequiresTerminalReturn(true, finalInstruction), Is.True);
    }

    [Test]
    [Category("基本功能")]
    public void Box指令生成值类型装箱而非原生地址传递()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var systemObject = app.SystemTypes.SystemObjectType;
        var systemBoolean = app.SystemTypes.SystemBooleanType;
        var systemVoid = app.SystemTypes.SystemVoidType;
        var context = new InjectedMethodAnalysisContext(
            systemObject,
            "BoxBoolean",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);
        var value = new LocalVariable("value", new Register(null, "value"), systemBoolean);
        var result = new LocalVariable("result", new Register(null, "result"), systemObject);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, value, new Immediate(1)),
            new Instruction(1, OpCode.Box, result, value, systemBoolean),
            new Instruction(2, OpCode.Return),
        ]);
        context.Locals = [value, result];
        context.ParameterLocals = [];
        context.AnalysisWarnings = [];

        var module = new ModuleDefinition(
            "BoxTest.dll",
            new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        绑定AsmResolver系统类型(module, systemObject, "Object", TypeAttributes.Class | TypeAttributes.Public);
        绑定AsmResolver系统类型(
            module,
            systemBoolean,
            "Boolean",
            TypeAttributes.Public | TypeAttributes.Sealed);
        var type = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "BoxTestType",
            TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(type);
        var method = new MethodDefinition(
            "BoxBoolean",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        type.Methods.Add(method);

        IlGenerator.GenerateIl(context, method);

        var box = method.CilMethodBody!.Instructions.Single(instruction => instruction.OpCode == CilOpCodes.Box);
        Assert.That(box.Operand!.ToString(), Does.Contain("System.Boolean"));
    }

    [TestCaseSource(nameof(不可顺序落出的终结指令))]
    public void Void方法已有控制流终结点时不重复补返回(CilOpCode opCode)
    {
        var finalInstruction = new CilInstruction(opCode);

        Assert.That(IlGenerator.RequiresTerminalReturn(true, finalInstruction), Is.False);
    }

    [Test]
    public void 非Void方法尾部为普通调用时不补无值返回()
    {
        var finalInstruction = new CilInstruction(CilOpCodes.Call, null);

        Assert.That(IlGenerator.RequiresTerminalReturn(false, finalInstruction), Is.False);
    }

    private static IEnumerable<CilOpCode> 不可顺序落出的终结指令()
    {
        yield return CilOpCodes.Ret;
        yield return CilOpCodes.Throw;
        yield return CilOpCodes.Rethrow;
        yield return CilOpCodes.Br;
        yield return CilOpCodes.Br_S;
        yield return CilOpCodes.Leave;
        yield return CilOpCodes.Leave_S;
        yield return CilOpCodes.Jmp;
        yield return CilOpCodes.Endfinally;
        yield return CilOpCodes.Endfilter;
    }

    [Test]
    public void StaticCall_DoesNotLoadMethodInfoOperand()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var systemVoid = appContext.SystemTypes.SystemVoidType;
        var systemInt = appContext.SystemTypes.SystemInt32Type;

        var callerContext = new InjectedMethodAnalysisContext(
            systemObject,
            "Caller",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);

        var targetContext = new InjectedMethodAnalysisContext(
            systemObject,
            "TargetStatic",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            [systemInt, systemInt]);

        var x = new LocalVariable("x", new Register(null, "x"));
        var y = new LocalVariable("y", new Register(null, "y"));

        var instructions = new List<Instruction>
        {
            
            new(0, OpCode.Move, x, Imm(5)),
            new(1, OpCode.Move, y, Imm(10)),
            // Operand layout: target, arg0, arg1, trailing-non-parameter.
            new(2, OpCode.CallVoid, targetContext, x, y, Imm(999)),
            new(3, OpCode.Return),
        };

        callerContext.ControlFlowGraph = new ISILControlFlowGraph(instructions);
        callerContext.Locals = [x, y];
        callerContext.ParameterLocals = [];
        callerContext.AnalysisWarnings = [];

        var module = new ModuleDefinition("Test.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        var typeDef = new TypeDefinition("Cpp2IL.Core.Tests", "IlGeneratorTestType", TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDef);

        var callerMethodDef = new MethodDefinition("Caller", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        typeDef.Methods.Add(callerMethodDef);

        var targetMethodDef = new MethodDefinition("TargetStatic", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void,
                [module.CorLibTypeFactory.Int32, module.CorLibTypeFactory.Int32]));
        typeDef.Methods.Add(targetMethodDef);
        targetContext.PutExtraData("AsmResolverMethod", targetMethodDef);

        IlGenerator.GenerateIl(callerContext, callerMethodDef);

        var il = callerMethodDef.CilMethodBody!.Instructions;

        Assert.That(il.Any(i => i.OpCode == CilOpCodes.Call), Is.True, "expected generated method to contain a call");
        Assert.That(il.Any(i => i.Operand is int intOperand && intOperand == 999), Is.False,
            "trailing non-parameter operand must not be emitted as a call argument");
        Assert.That(il.Count(i => i.OpCode == CilOpCodes.Ldloc), Is.EqualTo(2),
            "expected exactly two Ldloc instructions for the two parameters of the target method");
    }

    [Test]
    public void 非This局部变量的构造调用生成Newobj并写回()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var systemVoid = appContext.SystemTypes.SystemVoidType;
        var constructedObject = new LocalVariable("constructedObject", new Register(null, "constructedObject"), systemObject);
        var constructorContext = new InjectedMethodAnalysisContext(
            systemObject,
            ".ctor",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.SpecialName |
            ReflectionMethodAttributes.RTSpecialName,
            []);
        var callerContext = new InjectedMethodAnalysisContext(
            systemObject,
            "Caller",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);
        callerContext.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.CallVoid, constructorContext, constructedObject),
            new Instruction(1, OpCode.Return),
        ]);
        callerContext.Locals = [constructedObject];
        callerContext.ParameterLocals = [];
        callerContext.AnalysisWarnings = [];

        var module = new ModuleDefinition("Test.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        var typeDef = new TypeDefinition("Cpp2IL.Core.Tests", "ConstructedType", TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDef);
        systemObject.PutExtraData("AsmResolverType", typeDef);
        var constructorDefinition = new MethodDefinition(
            ".ctor",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RuntimeSpecialName,
            MethodSignature.CreateInstance(module.CorLibTypeFactory.Void));
        typeDef.Methods.Add(constructorDefinition);
        constructorContext.PutExtraData("AsmResolverMethod", constructorDefinition);
        var callerDefinition = new MethodDefinition(
            "Caller",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        typeDef.Methods.Add(callerDefinition);

        IlGenerator.GenerateIl(callerContext, callerDefinition);

        var il = callerDefinition.CilMethodBody!.Instructions;
        Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Newobj), Is.EqualTo(1));
        Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Stloc), Is.EqualTo(1));
        Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Call), Is.False);
    }

    [Test]
    public void This上的构造调用保持普通Call()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var systemVoid = appContext.SystemTypes.SystemVoidType;
        var thisObject = new LocalVariable("this", new Register(null, "this"), systemObject) { IsThis = true };
        var constructorContext = new InjectedMethodAnalysisContext(
            systemObject,
            ".ctor",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.SpecialName |
            ReflectionMethodAttributes.RTSpecialName,
            []);
        var callerContext = new InjectedMethodAnalysisContext(
            systemObject,
            "Caller",
            systemVoid,
            ReflectionMethodAttributes.Public,
            []);
        callerContext.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.CallVoid, constructorContext, thisObject),
            new Instruction(1, OpCode.Return),
        ]);
        callerContext.Locals = [];
        callerContext.ParameterLocals = [thisObject];
        callerContext.AnalysisWarnings = [];

        var module = new ModuleDefinition("Test.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        var typeDef = new TypeDefinition("Cpp2IL.Core.Tests", "ConstructedType", TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDef);
        systemObject.PutExtraData("AsmResolverType", typeDef);
        var constructorDefinition = new MethodDefinition(
            ".ctor",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RuntimeSpecialName,
            MethodSignature.CreateInstance(module.CorLibTypeFactory.Void));
        typeDef.Methods.Add(constructorDefinition);
        constructorContext.PutExtraData("AsmResolverMethod", constructorDefinition);
        var callerDefinition = new MethodDefinition(
            "Caller",
            MethodAttributes.Public,
            MethodSignature.CreateInstance(module.CorLibTypeFactory.Void));
        typeDef.Methods.Add(callerDefinition);

        IlGenerator.GenerateIl(callerContext, callerDefinition);

        var il = callerDefinition.CilMethodBody!.Instructions;
        Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Call), Is.EqualTo(1));
        Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Newobj), Is.False);
        Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Stloc), Is.False);
    }

    [Test]
    [Category("基本功能")]
    public void Hfa返回值按原始浮点位重组为值类型()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var valueType = appContext.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.ValueType")!;
        var aggregate = appContext.InjectTypeIntoAllAssemblies(
                "Cpp2IL.Core.Tests",
                "RecoveredVector2",
                valueType,
                System.Reflection.TypeAttributes.Public
                | System.Reflection.TypeAttributes.Sealed
                | System.Reflection.TypeAttributes.SequentialLayout)
            .InjectedTypes[0];
        var xField = aggregate.InjectFieldContext(
            "x",
            appContext.SystemTypes.SystemSingleType,
            System.Reflection.FieldAttributes.Public);
        var yField = aggregate.InjectFieldContext(
            "y",
            appContext.SystemTypes.SystemSingleType,
            System.Reflection.FieldAttributes.Public);
        var context = aggregate.InjectMethodContext(
            "GetActualSize",
            aggregate,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);

        var xBits = new LocalVariable("xBits", new Register(null, "X8"), appContext.SystemTypes.SystemInt32Type);
        var yBits = new LocalVariable("yBits", new Register(null, "X9"), appContext.SystemTypes.SystemInt32Type);
        var x = new LocalVariable("x", new Register(null, "V0"), appContext.SystemTypes.SystemSingleType);
        var y = new LocalVariable("y", new Register(null, "V1"), appContext.SystemTypes.SystemSingleType);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, xBits, Imm(0x44480000)),
            new Instruction(1, OpCode.Move, yBits, Imm(0x43870000)),
            new Instruction(2, OpCode.ReinterpretIntegerBitsAsFloat, x, xBits, Imm(32)),
            new Instruction(3, OpCode.ReinterpretIntegerBitsAsFloat, y, yBits, Imm(32)),
            new Instruction(4, OpCode.Return, x, y),
        ]);
        context.Locals = [xBits, yBits, x, y];
        context.ParameterLocals = [];
        context.AnalysisWarnings = [];

        var corlibAssembly = new AssemblyDefinition("mscorlib", new Version(4, 0, 0, 0));
        var corlibModule = new ModuleDefinition("mscorlib.dll");
        corlibAssembly.Modules.Add(corlibModule);
        var valueTypeDefinition = new TypeDefinition(
            "System",
            "ValueType",
            TypeAttributes.Public | TypeAttributes.Abstract);
        var int32Definition = new TypeDefinition(
            "System",
            "Int32",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout)
        {
            BaseType = valueTypeDefinition
        };
        var singleDefinition = new TypeDefinition(
            "System",
            "Single",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout)
        {
            BaseType = valueTypeDefinition
        };
        corlibModule.TopLevelTypes.Add(valueTypeDefinition);
        corlibModule.TopLevelTypes.Add(int32Definition);
        corlibModule.TopLevelTypes.Add(singleDefinition);
        appContext.SystemTypes.SystemInt32Type.PutExtraData("AsmResolverType", int32Definition);
        appContext.SystemTypes.SystemSingleType.PutExtraData("AsmResolverType", singleDefinition);

        var module = new ModuleDefinition("Test.dll", new AssemblyReference(corlibAssembly));
        var aggregateDefinition = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "RecoveredVector2",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout)
        {
            BaseType = module.CorLibTypeFactory.CorLibScope.CreateTypeReference("System", "ValueType")
        };
        module.TopLevelTypes.Add(aggregateDefinition);
        aggregate.PutExtraData("AsmResolverType", aggregateDefinition);

        var xDefinition = new FieldDefinition("x", FieldAttributes.Public, module.CorLibTypeFactory.Single);
        var yDefinition = new FieldDefinition("y", FieldAttributes.Public, module.CorLibTypeFactory.Single);
        aggregateDefinition.Fields.Add(xDefinition);
        aggregateDefinition.Fields.Add(yDefinition);
        xField.PutExtraData("AsmResolverField", xDefinition);
        yField.PutExtraData("AsmResolverField", yDefinition);

        var definition = new MethodDefinition(
            "GetActualSize",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(aggregate.ToTypeSignature(module)));
        aggregateDefinition.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);

        var il = definition.CilMethodBody!.Instructions;
        var bitConversionCalls = il
            .Where(instruction => instruction.OpCode == CilOpCodes.Call)
            .Select(instruction => instruction.Operand)
            .OfType<IMethodDescriptor>()
            .Where(method => method.Name == "Int32BitsToSingle")
            .ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bitConversionCalls, Has.Length.EqualTo(2));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Initobj), Is.EqualTo(1));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Stfld), Is.EqualTo(2));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Ret), Is.EqualTo(1));
            Assert.That(
                il.Any(instruction => instruction.Operand is int value && value == 0x44480000),
                Is.True);
            Assert.That(
                il.Any(instruction => instruction.Operand is int value && value == 0x43870000),
                Is.True);
        }
    }

    [Test]
    [Category("基本功能")]
    public void Ref浮点内存读写生成Ldobj和Stobj()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var systemVoid = appContext.SystemTypes.SystemVoidType;
        var systemSingle = appContext.SystemTypes.SystemSingleType;
        var byRefSingle = systemSingle.MakeByReferenceType();
        var runningHeight = new LocalVariable("runningHeight", new Register(null, "X5"), byRefSingle);
        var increment = new LocalVariable("increment", new Register(null, "V1"), systemSingle);
        var sum = new LocalVariable("sum", new Register(null, "V0"), systemSingle);
        var runningHeightMemory = new MemoryOperand(runningHeight);
        var context = new InjectedMethodAnalysisContext(
            systemObject,
            "AddHeight",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            [byRefSingle]);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, increment, new FloatLiteral(32f)),
            new Instruction(1, OpCode.Add, sum, runningHeightMemory, increment),
            new Instruction(2, OpCode.Move, runningHeightMemory, sum),
            new Instruction(3, OpCode.Return),
        ]);
        context.Locals = [increment, sum];
        context.ParameterLocals = [runningHeight];
        context.AnalysisWarnings = [];

        var corlibAssembly = new AssemblyDefinition("mscorlib", new Version(4, 0, 0, 0));
        var corlibModule = new ModuleDefinition("mscorlib.dll");
        corlibAssembly.Modules.Add(corlibModule);
        var valueTypeDefinition = new TypeDefinition(
            "System",
            "ValueType",
            TypeAttributes.Public | TypeAttributes.Abstract);
        var singleDefinition = new TypeDefinition(
            "System",
            "Single",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout)
        {
            BaseType = valueTypeDefinition
        };
        corlibModule.TopLevelTypes.Add(valueTypeDefinition);
        corlibModule.TopLevelTypes.Add(singleDefinition);
        systemSingle.PutExtraData("AsmResolverType", singleDefinition);

        var module = new ModuleDefinition("Test.dll", new AssemblyReference(corlibAssembly));
        var typeDefinition = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "ByRefMemoryType",
            TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDefinition);
        var definition = new MethodDefinition(
            "AddHeight",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(
                module.CorLibTypeFactory.Void,
                [module.CorLibTypeFactory.Single.MakeByReferenceType()]));
        definition.ParameterDefinitions.Add(new ParameterDefinition(
            1,
            "runningHeight",
            (ParameterAttributes)0));
        typeDefinition.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);

        var il = definition.CilMethodBody!.Instructions;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Ldobj), Is.EqualTo(1));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Stobj), Is.EqualTo(1));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Add), Is.EqualTo(1));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Ldarg), Is.EqualTo(2));
        }
    }

    [Test]
    [Category("基本功能")]
    public void 无构造器类型操作数写入IntPtr生成原生零值()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var systemVoid = appContext.SystemTypes.SystemVoidType;
        var systemIntPtr = appContext.SystemTypes.SystemIntPtrType;
        var pointer = new LocalVariable("pointer", new Register(null, "X8"), systemIntPtr);
        var context = new InjectedMethodAnalysisContext(
            systemObject,
            "LoadStaticTypeAddress",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, pointer, systemVoid),
            new Instruction(1, OpCode.Return),
        ]);
        context.Locals = [pointer];
        context.ParameterLocals = [];
        context.AnalysisWarnings = [];

        var module = new ModuleDefinition("Test.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        绑定AsmResolver系统类型(
            module,
            systemIntPtr,
            "IntPtr",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout);
        var typeDefinition = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "NativeDefaultType",
            TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDefinition);
        var definition = new MethodDefinition(
            "LoadStaticTypeAddress",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        typeDefinition.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);

        var il = definition.CilMethodBody!.Instructions;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Ldnull), Is.False);
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Ldc_I4_0), Is.EqualTo(1));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Conv_I), Is.EqualTo(1));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Stloc), Is.EqualTo(1));
        }
    }

    [Test]
    [Category("基本功能")]
    public void 运行时类型句柄默认值按IntPtr生成原生零值()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var systemVoid = appContext.SystemTypes.SystemVoidType;
        var runtimeClass = new RuntimeClassTypeAnalysisContext(systemObject, systemObject.DeclaringAssembly);
        var pointer = new LocalVariable("runtimeClass", new Register(null, "X8"), runtimeClass);
        var context = new InjectedMethodAnalysisContext(
            systemObject,
            "LoadRuntimeClassAddress",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, pointer, systemVoid),
            new Instruction(1, OpCode.Return),
        ]);
        context.Locals = [pointer];
        context.ParameterLocals = [];
        context.AnalysisWarnings = [];

        var module = new ModuleDefinition("Test.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        var typeDefinition = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "RuntimeClassDefaultType",
            TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDefinition);
        var definition = new MethodDefinition(
            "LoadRuntimeClassAddress",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        typeDefinition.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);

        var il = definition.CilMethodBody!.Instructions;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Ldnull), Is.False);
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Ldc_I4_0), Is.EqualTo(1));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Conv_I), Is.EqualTo(1));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Stloc), Is.EqualTo(1));
        }
    }

    [Test]
    [Category("基本功能")]
    public void 托管引用上的运行时类布局探针生成原生零值()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var systemVoid = appContext.SystemTypes.SystemVoidType;
        var runtimeClass = new RuntimeClassTypeAnalysisContext(
            systemObject,
            systemObject.DeclaringAssembly);
        var receiver = new LocalVariable("receiver", new Register(null, "X0"), systemObject);
        var pointer = new LocalVariable("runtimeClass", new Register(null, "X8"), runtimeClass);
        var context = new InjectedMethodAnalysisContext(
            systemObject,
            "ProbeRuntimeClass",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, pointer, new MemoryOperand(receiver)),
            new Instruction(1, OpCode.Return),
        ]);
        context.Locals = [receiver, pointer];
        context.ParameterLocals = [];
        context.AnalysisWarnings = [];

        var module = new ModuleDefinition("Test.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        绑定AsmResolver系统类型(module, systemObject, "Object", TypeAttributes.Class | TypeAttributes.Public);
        var typeDefinition = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "RuntimeClassProbeType",
            TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDefinition);
        var definition = new MethodDefinition(
            "ProbeRuntimeClass",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        typeDefinition.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);

        var il = definition.CilMethodBody!.Instructions;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Ldc_I4_0), Is.True);
            Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Conv_I), Is.True);
            Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Ldloc), Is.False);
        }
    }

    [Test]
    [Category("边界值")]
    public void 托管引用取址写入托管槽时恢复原引用()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var systemVoid = appContext.SystemTypes.SystemVoidType;
        var source = new LocalVariable("source", new Register(null, "X0"), systemObject);
        var destination = new LocalVariable("destination", new Register(null, "X1"), systemObject);
        var context = new InjectedMethodAnalysisContext(
            systemObject,
            "RecoverManagedReference",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, destination, new AddressOf(source)),
            new Instruction(1, OpCode.Return),
        ]);
        context.Locals = [source, destination];
        context.ParameterLocals = [];
        context.AnalysisWarnings = [];

        var module = new ModuleDefinition("Test.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        绑定AsmResolver系统类型(module, systemObject, "Object", TypeAttributes.Class | TypeAttributes.Public);
        var typeDefinition = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "ManagedReferenceProbeType",
            TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDefinition);
        var definition = new MethodDefinition(
            "RecoverManagedReference",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        typeDefinition.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);

        var il = definition.CilMethodBody!.Instructions;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Ldloc), Is.True);
            Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Ldloca), Is.False);
        }
    }

    [Test]
    [Category("基本功能")]
    public void 原生内存值与无构造器类型比较时生成同型原生零值()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var systemVoid = appContext.SystemTypes.SystemVoidType;
        var systemBoolean = appContext.SystemTypes.SystemBooleanType;
        var systemIntPtr = appContext.SystemTypes.SystemIntPtrType;
        var pointer = new LocalVariable("runtimeClass", new Register(null, "X8"), systemIntPtr);
        var memoryValue = new MemoryOperand(pointer);
        var equal = new LocalVariable("equal", new Register(null, "W9"), systemBoolean);
        var context = new InjectedMethodAnalysisContext(
            systemObject,
            "CompareRuntimeClassAddress",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.CheckEqual, equal, memoryValue, systemVoid),
            new Instruction(1, OpCode.Return),
        ]);
        context.Locals = [pointer, equal];
        context.ParameterLocals = [];
        context.AnalysisWarnings = [];

        var module = new ModuleDefinition("Test.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        绑定AsmResolver系统类型(
            module,
            systemIntPtr,
            "IntPtr",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout);
        绑定AsmResolver系统类型(
            module,
            systemBoolean,
            "Boolean",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout);
        var typeDefinition = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "RuntimeClassComparisonType",
            TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDefinition);
        var definition = new MethodDefinition(
            "CompareRuntimeClassAddress",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        typeDefinition.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);

        var il = definition.CilMethodBody!.Instructions;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Ldnull), Is.False);
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Conv_I), Is.EqualTo(1));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Ceq), Is.EqualTo(1));
        }
    }

    [Test]
    [Category("边界值")]
    public void 托管引用与整数零比较时按引用空值生成()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var systemVoid = appContext.SystemTypes.SystemVoidType;
        var systemBoolean = appContext.SystemTypes.SystemBooleanType;
        var target = new LocalVariable("target", new Register(null, "X8"), systemObject);
        var equal = new LocalVariable("equal", new Register(null, "W9"), systemBoolean);
        var context = new InjectedMethodAnalysisContext(
            systemObject,
            "CompareReferenceWithZero",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.CheckEqual, equal, target, Imm(0)),
            new Instruction(1, OpCode.Return),
        ]);
        context.Locals = [target, equal];
        context.ParameterLocals = [];
        context.AnalysisWarnings = [];

        var module = new ModuleDefinition("Test.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        绑定AsmResolver系统类型(
            module,
            systemObject,
            "Object",
            TypeAttributes.Public | TypeAttributes.Class);
        绑定AsmResolver系统类型(
            module,
            systemBoolean,
            "Boolean",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout);
        var typeDefinition = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "ReferenceComparisonType",
            TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDefinition);
        var definition = new MethodDefinition(
            "CompareReferenceWithZero",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        typeDefinition.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);

        var il = definition.CilMethodBody!.Instructions;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Ldnull), Is.EqualTo(1));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Ceq), Is.EqualTo(1));
            Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Conv_I), Is.False);
        }
    }

    [Test]
    [Category("异常输入")]
    public void 数值与整数零比较不得改写为引用空值()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var systemVoid = appContext.SystemTypes.SystemVoidType;
        var systemBoolean = appContext.SystemTypes.SystemBooleanType;
        var systemInt32 = appContext.SystemTypes.SystemInt32Type;
        var value = new LocalVariable("value", new Register(null, "W8"), systemInt32);
        var equal = new LocalVariable("equal", new Register(null, "W9"), systemBoolean);
        var context = new InjectedMethodAnalysisContext(
            systemObject,
            "CompareIntegerWithZero",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.CheckEqual, equal, value, Imm(0)),
            new Instruction(1, OpCode.Return),
        ]);
        context.Locals = [value, equal];
        context.ParameterLocals = [];
        context.AnalysisWarnings = [];

        var module = new ModuleDefinition("Test.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        绑定AsmResolver系统类型(
            module,
            systemInt32,
            "Int32",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout);
        绑定AsmResolver系统类型(
            module,
            systemBoolean,
            "Boolean",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout);
        var typeDefinition = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "IntegerComparisonType",
            TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDefinition);
        var definition = new MethodDefinition(
            "CompareIntegerWithZero",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        typeDefinition.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);

        var il = definition.CilMethodBody!.Instructions;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Ldnull), Is.False);
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Ldc_I4), Is.EqualTo(1));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Ceq), Is.EqualTo(1));
        }
    }

    [Test]
    [Category("边界值")]
    public void 未配对Newobj写入UIntPtr生成无符号原生零值()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var systemVoid = appContext.SystemTypes.SystemVoidType;
        var systemUIntPtr = appContext.SystemTypes.SystemUIntPtrType;
        var pointer = new LocalVariable("pointer", new Register(null, "X9"), systemUIntPtr);
        var context = new InjectedMethodAnalysisContext(
            systemObject,
            "AllocateMetadataAddress",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Newobj, pointer),
            new Instruction(1, OpCode.Return),
        ]);
        context.Locals = [pointer];
        context.ParameterLocals = [];
        context.AnalysisWarnings = [];

        var module = new ModuleDefinition("Test.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        绑定AsmResolver系统类型(
            module,
            systemUIntPtr,
            "UIntPtr",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout);
        var typeDefinition = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "UnsignedNativeDefaultType",
            TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDefinition);
        var definition = new MethodDefinition(
            "AllocateMetadataAddress",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        typeDefinition.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);

        var il = definition.CilMethodBody!.Instructions;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Ldnull), Is.False);
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Conv_U), Is.EqualTo(1));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Stloc), Is.EqualTo(1));
        }
    }

    [Test]
    [Category("异常输入")]
    public void 无构造器类型操作数写入引用仍生成Null()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var systemVoid = appContext.SystemTypes.SystemVoidType;
        var target = new LocalVariable("target", new Register(null, "X10"), systemObject);
        var context = new InjectedMethodAnalysisContext(
            systemObject,
            "LoadMissingReference",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, target, systemVoid),
            new Instruction(1, OpCode.Return),
        ]);
        context.Locals = [target];
        context.ParameterLocals = [];
        context.AnalysisWarnings = [];

        var module = new ModuleDefinition("Test.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        绑定AsmResolver系统类型(
            module,
            systemObject,
            "Object",
            TypeAttributes.Public | TypeAttributes.Class);
        var typeDefinition = new TypeDefinition(
            "Cpp2IL.Core.Tests",
            "ReferenceDefaultType",
            TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDefinition);
        var definition = new MethodDefinition(
            "LoadMissingReference",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        typeDefinition.Methods.Add(definition);

        IlGenerator.GenerateIl(context, definition);

        var il = definition.CilMethodBody!.Instructions;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Ldnull), Is.EqualTo(1));
            Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Conv_I), Is.False);
            Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Conv_U), Is.False);
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Stloc), Is.EqualTo(1));
        }
    }
}
