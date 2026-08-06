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
using ReflectionMethodAttributes = System.Reflection.MethodAttributes;

namespace Cpp2IL.Core.Tests;

public class IlGeneratorTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
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
}
