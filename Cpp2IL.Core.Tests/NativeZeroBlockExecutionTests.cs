using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Cloning;
using AsmResolver.DotNet.Signatures;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils;
using LibCpp2IL.BinaryStructures;
using ManagedTypeAttributes = AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes;
using ManagedMethodAttributes = AsmResolver.PE.DotNet.Metadata.Tables.MethodAttributes;
using ManagedFieldAttributes = AsmResolver.PE.DotNet.Metadata.Tables.FieldAttributes;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class NativeZeroBlockExecutionTests
{
    [Test]
    [Category("fixture集成")]
    public void 原始完整方法通过生产发射并保留两处零块写()
    {
        var app = Arm64StartupFixtureIntegrationTests.LoadFixture();
        var original = app.Assemblies.Single(assembly => assembly.Name == "UnityEngine.CoreModule").Types
            .SelectMany(type => type.Methods).Single(method => method.Definition?.token == 0x06000BB4u);
        // 只按原始身份选择回归范围，生产入口负责全部声明绑定和完整原生方法分析。
        var options = Cpp2IlApi.RuntimeOptions ?? throw new InvalidOperationException("原始输入加载器未初始化运行选项。");
        options.IsilDumpAssemblyFilters = [original.DeclaringType!.DeclaringAssembly.Name];
        options.IsilDumpTypeFilters = [original.DeclaringType.FullName];
        new AsmResolverDllOutputFormatIlRecovery().BuildAssemblies(app);
        var definition = original.GetExtraData<MethodDefinition>("AsmResolverMethod");
        Assert.That(definition, Is.Not.Null);
        Assert.That(definition!.CilMethodBody, Is.Not.Null);
        var body = definition.CilMethodBody!;
        CilStackValidator.Validate(body, original.FullName);
        var instructions = body.Instructions.Select(instruction => instruction.ToString()).ToArray();
        Assert.That(body.Instructions.Count(instruction => instruction.OpCode.Code ==
            AsmResolver.PE.DotNet.Cil.CilCode.Initblk), Is.EqualTo(2));
        Assert.That(definition.Signature!.ReturnType.FullName, Is.EqualTo("System.Boolean"));
        Assert.That(definition.Signature.ParameterTypes.Select(type => type.FullName), Is.EqualTo(
            new[] { "System.String", "UnityEngine.Bindings.ManagedSpanWrapper&" }));
        Assert.That(original.AnalysisWarnings, Is.Empty);
        // 完整原始方法中的零指针存储与宽整数调用必须保持真实求值栈种类。
        Assert.That(body.Instructions.Any(instruction => instruction.OpCode.Code ==
            AsmResolver.PE.DotNet.Cil.CilCode.Conv_U), Is.True);
        for (int index = 1; index < body.Instructions.Count; index++)
        {
            var instruction = body.Instructions[index];
            if (instruction.OpCode.Code == AsmResolver.PE.DotNet.Cil.CilCode.Stfld)
                Assert.That(body.Instructions[index - 1].OpCode.Code,
                    Is.Not.EqualTo(AsmResolver.PE.DotNet.Cil.CilCode.Ldnull));
            if (instruction.Operand is IMethodDescriptor called
                && called.Signature?.ParameterTypes.Count == 1
                && called.Signature.ParameterTypes[0].FullName == "System.UInt64")
                Assert.That(body.Instructions[index - 1].OpCode.Code,
                    Is.EqualTo(AsmResolver.PE.DotNet.Cil.CilCode.Ldc_I8));
        }
        var root = Environment.GetEnvironmentVariable("CPP2IL_LEDGER_EVIDENCE_ROOT");
        Assert.That(root, Is.Not.Null.And.Not.Empty);
        Directory.CreateDirectory(root!);
        using var receipt = new FileStream(Path.Combine(root!, "native-full-method-cil.json"), FileMode.CreateNew);
        JsonSerializer.Serialize(receipt, new
        {
            schema = "NativeFullMethodCilRegression/v1", method = original.FullName,
            address = original.UnderlyingPointer,
            binarySha256 = app.LibCpp2IlContext.InputBinarySha256,
            metadataSha256 = app.LibCpp2IlContext.InputMetadataSha256,
            instructions, cilStackValidated = true, originalFullGraphUsed = true,
            fullMethodRuntimeProved = false, sourceRoundTripProved = false, fullRecoveryProved = false
        });
        ExecuteFullMethod(original, definition, root!);
    }

    private static void ExecuteFullMethod(MethodAnalysisContext original, MethodDefinition definition, string root)
    {
        var element = ((ByRefTypeAnalysisContext)original.Parameters[1].ParameterType).ElementType;
        var structure = element.GetExtraData<TypeDefinition>("AsmResolverType")!;
        var corlib = definition.DeclaringModule!.CorLibTypeFactory.CorLibScope as AssemblyReference
            ?? throw new InvalidDataException("原始核心库身份不是程序集引用。");
        var module = new ModuleDefinition("NativeFullMethod.dll", corlib);
        // 使用库的元数据深复制保留原始完整 CIL；不修改指令、调用目标签名或结构布局。
        var cloner = new MemberCloner(module);
        cloner.Include(definition.DeclaringType!, false);
        cloner.Include(definition);
        cloner.Include(structure, false);
        foreach (var field in structure.Fields) cloner.Include(field);
        var result = cloner.Clone();
        foreach (var type in result.ClonedTopLevelTypes) module.TopLevelTypes.Add(type);
        var cloned = result.GetClonedMember(definition);
        CilStackValidator.Validate(cloned.CilMethodBody!, original.FullName);
        Assert.That(cloned.CilMethodBody!.Instructions.Select(instruction => instruction.ToString()),
            Is.EqualTo(definition.CilMethodBody!.Instructions.Select(instruction => instruction.ToString())));
        var assembly = new AssemblyDefinition("NativeFullMethod", new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        using var stream = new MemoryStream();
        module.Write(stream);
        byte[] pe = stream.ToArray();
        string directory = Path.Combine(root, "native-full-method-runtime");
        Directory.CreateDirectory(directory);
        using (var output = new FileStream(Path.Combine(directory, "NativeFullMethod.dll"), FileMode.CreateNew)) output.Write(pe);
        var loaded = Assembly.Load(pe);
        var runtimeType = loaded.GetType(element.FullName, true)!;
        Assert.That(System.Runtime.InteropServices.Marshal.SizeOf(runtimeType), Is.EqualTo(16));
        var method = loaded.GetType(definition.DeclaringType!.FullName, true)!
            .GetMethod(definition.Name!, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        var rows = new System.Collections.Generic.List<object>();
        foreach (string? input in new string?[] { null, "", "x", "\0", "字符串" })
        {
            byte[] bytes = Enumerable.Repeat((byte)0xA5, 32).ToArray();
            bool actual = (bool)typeof(NativeZeroBlockExecutionTests)
                .GetMethod(nameof(InvokeFullTyped), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(runtimeType).Invoke(null, [method, input, bytes])!;
            bool expected = input == null || input.Length == 0;
            Assert.That(actual, Is.EqualTo(expected));
            for (int index = 0; index < bytes.Length; index++)
            {
                byte value = 0xA5;
                if (expected && index >= 8 && index < 24)
                    value = input != null && index == 8 ? (byte)1 : (byte)0;
                Assert.That(bytes[index], Is.EqualTo(value), $"完整方法输入 {input ?? "<null>"} 的字节 {index} 不一致。");
            }
            rows.Add(new { input, actual, bytes = bytes.Select(value => (int)value).ToArray() });
        }
        using var receipt = new FileStream(Path.Combine(directory, "runtime-paths.json"), FileMode.CreateNew);
        JsonSerializer.Serialize(receipt, new
        {
            schema = "NativeFullMethodRuntime/v1", method = original.FullName,
            address = original.UnderlyingPointer, rows, fullCilInstructionsUnchanged = true,
            assemblySha256 = Convert.ToHexString(SHA256.HashData(pe)).ToLowerInvariant(),
            runtimeAssembly = typeof(string).Assembly.FullName, clrPathsExecuted = true,
            originalPlayerComparisonProved = false, sourceRoundTripProved = false, fullRecoveryProved = false
        });
    }

    private delegate bool FullMethod<T>(string? input, ref T value) where T : struct;
    private static bool InvokeFullTyped<T>(MethodInfo method, string? input, byte[] bytes) where T : struct
    {
        var invoke = method.CreateDelegate<FullMethod<T>>();
        ref T value = ref Unsafe.As<byte, T>(ref bytes[8]);
        return invoke(input, ref value);
    }

    [Test]
    [Category("fixture集成")]
    public void 原始带填充值类型的零块写保持完整范围与外部哨兵()
    {
        var app = Arm64StartupFixtureIntegrationTests.LoadFixture();
        // 身份只固定回归输入；所有布局和写入范围均从原始输入与真实分析图取得。
        var original = app.Assemblies.Single(assembly => assembly.Name == "UnityEngine.CoreModule").Types
            .SelectMany(type => type.Methods).Single(method => method.Definition?.token == 0x06000BB4u);
        original.Analyze();
        var writes = original.ControlFlowGraph!.Instructions.Where(instruction =>
            ByRefZeroBlockRecovery.TryDescribe(instruction, app.Binary.PointerSizeBytes, out _)).ToArray();
        Assert.That(writes, Has.Length.EqualTo(2));
        var root = Environment.GetEnvironmentVariable("CPP2IL_LEDGER_EVIDENCE_ROOT");
        Assert.That(root, Is.Not.Null.And.Not.Empty);
        for (int ordinal = 0; ordinal < writes.Length; ordinal++)
            ExecuteOriginalWrite(app, original, writes[ordinal], ordinal, root!);
    }

    private static void ExecuteOriginalWrite(ApplicationAnalysisContext app, MethodAnalysisContext original,
        Instruction source, int ordinal, string root)
    {
        Assert.That(ByRefZeroBlockRecovery.TryDescribe(source, app.Binary.PointerSizeBytes, out var range), Is.True);
        var element = ((ByRefTypeAnalysisContext)range.Receiver.Type!).ElementType;
        long size = TypeSizes.UnboxedSize(element, app.Binary.PointerSizeBytes);
        var module = new ModuleDefinition("NativeZeroBlock.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        var structure = new TypeDefinition(element.Namespace, element.Name,
            ManagedTypeAttributes.Public | ManagedTypeAttributes.Sealed | ManagedTypeAttributes.ExplicitLayout,
            module.CorLibTypeFactory.CorLibScope.CreateTypeReference("System", "ValueType"))
            { ClassLayout = new(0, checked((uint)size)) };
        module.TopLevelTypes.Add(structure);
        foreach (var field in element.Fields.Where(field => !field.IsStatic))
            structure.Fields.Add(new FieldDefinition(field.Name, ManagedFieldAttributes.Public, FieldSignature(field.FieldType, module))
                { FieldOffset = field.Offset });
        element.PutExtraData("AsmResolverType", structure);
        var host = new TypeDefinition("Fixture", "NativeZeroBlockHost", ManagedTypeAttributes.Public, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(host);
        var definition = new MethodDefinition("WriteRange", ManagedMethodAttributes.Public | ManagedMethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void, [new TypeDefOrRefSignature(structure, true).MakeByReferenceType()]));
        definition.ParameterDefinitions.Add(new ParameterDefinition(1, range.Receiver.Name,
            (AsmResolver.PE.DotNet.Metadata.Tables.ParameterAttributes)0));
        host.Methods.Add(definition);
        // 探针仅承载原图中的一次实际写入，不宣称恢复了原始完整方法的控制流。
        var probe = new InjectedMethodAnalysisContext(original.DeclaringType!, "NativeWriteProbe", app.SystemTypes.SystemVoidType,
            MethodAttributes.Public | MethodAttributes.Static, [range.Receiver.Type!]);
        probe.ParameterLocals = [range.Receiver];
        probe.Locals = [];
        probe.AnalysisWarnings = [];
        probe.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, source.OpCode, source.Operands.ToList()) { MemoryAccessWidthBits = source.MemoryAccessWidthBits },
            new Instruction(1, OpCode.Return)]);
        IlGenerator.GenerateIl(probe, definition);
        CilStackValidator.Validate(definition.CilMethodBody!, "Fixture.NativeZeroBlockHost::WriteRange");
        var assembly = new AssemblyDefinition("NativeZeroBlock", new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        using var stream = new MemoryStream();
        module.Write(stream);
        byte[] pe = stream.ToArray();
        var loaded = Assembly.Load(pe);
        var actualType = loaded.GetType(element.FullName, true)!;
        var method = loaded.GetType("Fixture.NativeZeroBlockHost", true)!.GetMethod("WriteRange")!;
        byte[] bytes = Enumerable.Repeat((byte)0xA5, checked((int)size + 16)).ToArray();
        typeof(NativeZeroBlockExecutionTests).GetMethod(nameof(InvokeTyped), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(actualType).Invoke(null, [method, bytes]);
        for (int index = 0; index < bytes.Length; index++)
            Assert.That(bytes[index], Is.EqualTo(index >= 8 + range.Offset && index < 8 + range.Offset + range.ByteCount ? (byte)0 : (byte)0xA5),
                $"原始布局字节 {index} 与写入边界不一致。");
        string directory = Path.Combine(root, $"native-zero-block-{ordinal}");
        Directory.CreateDirectory(directory);
        using (var output = new FileStream(Path.Combine(directory, "NativeZeroBlock.dll"), FileMode.CreateNew)) output.Write(pe);
        using var receipt = new FileStream(Path.Combine(directory, "runtime-bytes.json"), FileMode.CreateNew);
        JsonSerializer.Serialize(receipt, new
        {
            schema = "NativeLayoutZeroBlockRuntime/v1", originalMethod = original.FullName,
            originalAddress = original.UnderlyingPointer, instructionIndex = source.Index,
            binarySha256 = app.LibCpp2IlContext.InputBinarySha256, metadataSha256 = app.LibCpp2IlContext.InputMetadataSha256,
            type = element.FullName, size, offset = range.Offset, byteCount = range.ByteCount,
            actualBytes = bytes.Select(value => (int)value).ToArray(),
            assemblySha256 = Convert.ToHexString(SHA256.HashData(pe)).ToLowerInvariant(),
            originalLayoutByteExecutionProved = true, fullOriginalMethodProved = false, sourceRoundTripProved = false
        });
    }

    private delegate void RefValue<T>(ref T value) where T : struct;
    private static void InvokeTyped<T>(MethodInfo method, byte[] bytes) where T : struct
    {
        var invoke = method.CreateDelegate<RefValue<T>>();
        ref T value = ref Unsafe.As<byte, T>(ref bytes[8]);
        invoke(ref value);
    }

    private static TypeSignature FieldSignature(TypeAnalysisContext type, ModuleDefinition module)
    {
        if (type is PointerTypeAnalysisContext pointer)
            return FieldSignature(pointer.ElementType, module).MakePointerType();
        var factory = module.CorLibTypeFactory;
        return type.Type switch
        {
            Il2CppTypeEnum.IL2CPP_TYPE_VOID => factory.Void,
            Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN => factory.Boolean,
            Il2CppTypeEnum.IL2CPP_TYPE_CHAR => factory.Char,
            Il2CppTypeEnum.IL2CPP_TYPE_I1 => factory.SByte,
            Il2CppTypeEnum.IL2CPP_TYPE_U1 => factory.Byte,
            Il2CppTypeEnum.IL2CPP_TYPE_I2 => factory.Int16,
            Il2CppTypeEnum.IL2CPP_TYPE_U2 => factory.UInt16,
            Il2CppTypeEnum.IL2CPP_TYPE_I4 => factory.Int32,
            Il2CppTypeEnum.IL2CPP_TYPE_U4 => factory.UInt32,
            Il2CppTypeEnum.IL2CPP_TYPE_I8 => factory.Int64,
            Il2CppTypeEnum.IL2CPP_TYPE_U8 => factory.UInt64,
            Il2CppTypeEnum.IL2CPP_TYPE_R4 => factory.Single,
            Il2CppTypeEnum.IL2CPP_TYPE_R8 => factory.Double,
            Il2CppTypeEnum.IL2CPP_TYPE_I => factory.IntPtr,
            Il2CppTypeEnum.IL2CPP_TYPE_U => factory.UIntPtr,
            _ => throw new InvalidDataException("原始执行夹具尚未支持该字段类型：" + type.FullName)
        };
    }
}
