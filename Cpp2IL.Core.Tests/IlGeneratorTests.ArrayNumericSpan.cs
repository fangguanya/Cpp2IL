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
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorTests
{
    [TestCase("immediate", true)]
    [TestCase("last_complete", true)]
    [TestCase("native_unsigned", true)]
    [TestCase("reference", false)]
    [TestCase("same_name", false)]
    [TestCase("negative", false)]
    [TestCase("overflow", false)]
    [TestCase("index_float", false)]
    [TestCase("index_int64", false)]
    [TestCase("width", false)]
    [TestCase("pointer_size", false)]
    [Category("边界值")]
    [Category("异常输入")]
    public void 数值数组跨度拒绝未知身份和不完整索引合同(string mode, bool expected)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var element = mode == "reference" ? app.SystemTypes.SystemObjectType : app.SystemTypes.SystemInt32Type;
        if (mode == "same_name")
            element = new InjectedTypeAnalysisContext(app.Assemblies[0], "System", "Int32",
                app.SystemTypes.SystemValueTypeType, System.Reflection.TypeAttributes.Public);
        var array = new LocalVariable("array", new Register(null, "array"), element.MakeSzArrayType());
        IOperand index = mode switch
        {
            "negative" => new Immediate(-1),
            "overflow" => new Immediate(int.MaxValue),
            "last_complete" => new Immediate(int.MaxValue - 1),
            "native_unsigned" => new LocalVariable("index", new Register(null, "index"), app.SystemTypes.SystemUIntPtrType),
            "index_float" => new LocalVariable("index", new Register(null, "index"), app.SystemTypes.SystemSingleType),
            "index_int64" => new LocalVariable("index", new Register(null, "index"), app.SystemTypes.SystemInt64Type),
            _ => new Immediate(0)
        };
        var instruction = new Instruction(0, OpCode.Move, new LocalVariable("bits", new Register(null, "bits")),
            new ArrayAccess(array, index)) { MemoryAccessWidthBits = mode == "width" ? 96 : 64 };
        Assert.That(ManagedArraySpanRecoveryHelper.TryDescribe(instruction, mode == "pointer_size" ? 16 : 8, out var span), Is.EqualTo(expected));
        if (expected)
        {
            Assert.That(span.ElementType, Is.SameAs(element));
            Assert.That(span.Count, Is.EqualTo(2));
            Assert.That(span.NativeIndex, Is.EqualTo(mode == "native_unsigned"));
        }
    }

    [TestCase(Il2CppTypeEnum.IL2CPP_TYPE_I1, 64, false)]
    [TestCase(Il2CppTypeEnum.IL2CPP_TYPE_U1, 128, true)]
    [TestCase(Il2CppTypeEnum.IL2CPP_TYPE_I2, 128, false)]
    [TestCase(Il2CppTypeEnum.IL2CPP_TYPE_U2, 64, true)]
    [TestCase(Il2CppTypeEnum.IL2CPP_TYPE_I4, 64, false)]
    [TestCase(Il2CppTypeEnum.IL2CPP_TYPE_I4, 64, true)]
    [TestCase(Il2CppTypeEnum.IL2CPP_TYPE_U4, 128, true)]
    [TestCase(Il2CppTypeEnum.IL2CPP_TYPE_I8, 128, false)]
    [TestCase(Il2CppTypeEnum.IL2CPP_TYPE_U8, 128, true)]
    [TestCase(Il2CppTypeEnum.IL2CPP_TYPE_R4, 64, false)]
    [TestCase(Il2CppTypeEnum.IL2CPP_TYPE_R4, 128, true)]
    [TestCase(Il2CppTypeEnum.IL2CPP_TYPE_R8, 128, true)]
    [Category("基本功能")]
    [Category("边界值")]
    [Category("异常输入")]
    public void 数值数组跨度真实PE保留所有位及宽索引(Il2CppTypeEnum primitive, int width, bool nativeIndex)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var element = app.SystemTypes.GetPrimitive(primitive);
        Assert.That(ManagedFieldSpanRecoveryHelper.TryGetScalarLayout(element, 8, false, out var size, out _), Is.True);
        var count = width / (size * 8);
        var arrayType = element.MakeSzArrayType();
        var indexType = nativeIndex ? app.SystemTypes.SystemIntPtrType : app.SystemTypes.SystemInt32Type;
        var owner = new InjectedTypeAnalysisContext(app.Assemblies[0], "NumericArray", "Host",
            app.SystemTypes.SystemObjectType, System.Reflection.TypeAttributes.Public);
        var fields = Enumerable.Range(0, count).Select(i =>
        {
            var field = owner.InjectFieldContext("Field" + i, element, System.Reflection.FieldAttributes.Public | System.Reflection.FieldAttributes.Static);
            field.Offset = i * size;
            return field;
        }).ToArray();
        var array = new LocalVariable("array", new Register(null, "array"), arrayType);
        var index = new LocalVariable("index", new Register(null, "index"), indexType);
        var value = new LocalVariable("bits", new Register(null, "unclassified_carrier"));
        var receiver = new LocalVariable("unused", new Register(null, "unused"), owner);
        var context = new InjectedMethodAnalysisContext(owner, "Copy", app.SystemTypes.SystemVoidType,
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static,
            [arrayType, indexType], ["array", "index"]);
        context.ParameterLocals = [array, index];
        context.Locals = [array, index, value];
        context.AnalysisWarnings = [];
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, value, new ArrayAccess(array, index)) { MemoryAccessWidthBits = width },
            new Instruction(1, OpCode.Move, new FieldReference(fields[0], receiver, 0), value) { MemoryAccessWidthBits = width },
            new Instruction(2, OpCode.Return)
        ]);
        var core = new ModuleDefinition("mscorlib.dll");
        var coreAssembly = new AssemblyDefinition("mscorlib", new Version(4, 0, 0, 0));
        coreAssembly.Modules.Add(core);
        foreach (var type in new[] { element, indexType }.Distinct())
            绑定AsmResolver系统类型(core, type, type.Name, TypeAttributes.Public | TypeAttributes.Sealed);
        var name = "NumericArray_" + Guid.NewGuid().ToString("N");
        var module = new ModuleDefinition(name + ".dll", new AssemblyReference(coreAssembly));
        var host = new TypeDefinition("NumericArray", "Host", TypeAttributes.Public, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(host);
        owner.PutExtraData("AsmResolverType", host);
        foreach (var field in fields)
        {
            var definition = new FieldDefinition(field.Name, FieldAttributes.Public | FieldAttributes.Static, element.ToTypeSignature(module));
            host.Fields.Add(definition);
            field.PutExtraData("AsmResolverField", definition);
        }
        var method = new MethodDefinition("Copy", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void, [arrayType.ToTypeSignature(module), indexType.ToTypeSignature(module)]));
        method.ParameterDefinitions.Add(new ParameterDefinition(1, "array", (ParameterAttributes)0));
        method.ParameterDefinitions.Add(new ParameterDefinition(2, "index", (ParameterAttributes)0));
        host.Methods.Add(method);
        IlGenerator.GenerateIl(context, method);
        CilStackValidator.Validate(method.CilMethodBody!, context.FullName);
        Assert.That(method.CilMethodBody!.Instructions.Count(i => i.OpCode == CilOpCodes.Ldelem), Is.EqualTo(count));
        var assembly = new AssemblyDefinition(name, new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        using var stream = new System.IO.MemoryStream();
        module.Write(stream);
        var runtime = System.Reflection.Assembly.Load(stream.ToArray()).GetType("NumericArray.Host")!;
        var invoke = runtime.GetMethod("Copy")!;
        var runtimeElement = runtime.GetField("Field0")!.FieldType;
        var source = Array.CreateInstance(runtimeElement, count + 1);
        // 每个字节不同并包含最高位，浮点NaN和负零也必须按位保留而非数值转换。
        var bytes = Enumerable.Range(0, (count + 1) * size).Select(i => unchecked((byte)(0x80 + i * 37))).ToArray();
        Buffer.BlockCopy(bytes, 0, source, 0, bytes.Length);
        foreach (var special in new[] { false, true })
        {
            if (special && primitive == Il2CppTypeEnum.IL2CPP_TYPE_R4)
            {
                source.SetValue(BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)), 1);
                source.SetValue(BitConverter.Int32BitsToSingle(unchecked((int)0x7FC01234)), 2);
            }
            if (special && primitive == Il2CppTypeEnum.IL2CPP_TYPE_R8)
            {
                source.SetValue(BitConverter.Int64BitsToDouble(unchecked((long)0x8000000000000000)), 1);
                source.SetValue(BitConverter.Int64BitsToDouble(0x7FF8000000001234), 2);
            }
            invoke.Invoke(null, [source, nativeIndex ? (object)new IntPtr(1) : 1]);
            var actual = Array.CreateInstance(runtimeElement, count);
            for (var i = 0; i < count; i++) actual.SetValue(runtime.GetField("Field" + i)!.GetValue(null), i);
            var expectedBytes = new byte[count * size];
            var actualBytes = new byte[count * size];
            Buffer.BlockCopy(source, size, expectedBytes, 0, expectedBytes.Length);
            Buffer.BlockCopy(actual, 0, actualBytes, 0, actualBytes.Length);
            Assert.That(actualBytes, Is.EqualTo(expectedBytes));
        }
        // 读取越界必须在任何目标写入之前抛出；64位大索引不得截断后读取低地址。
        foreach (var invalid in new long[] { -1, 2, int.MaxValue, (1L << 32) + 1 })
        {
            if (!nativeIndex && invalid > int.MaxValue) continue;
            var before = fields.Select(f => runtime.GetField(f.Name)!.GetValue(null)).ToArray();
            var error = Assert.Throws<System.Reflection.TargetInvocationException>(() => invoke.Invoke(null,
                [source, nativeIndex ? (object)new IntPtr(invalid) : checked((int)invalid)]));
            Assert.That(error!.InnerException, Is.TypeOf<IndexOutOfRangeException>());
            Assert.That(fields.Select(f => runtime.GetField(f.Name)!.GetValue(null)), Is.EqualTo(before));
        }
        var nullError = Assert.Throws<System.Reflection.TargetInvocationException>(() => invoke.Invoke(null,
            [null, nativeIndex ? (object)new IntPtr(1) : 1]));
        Assert.That(nullError!.InnerException, Is.TypeOf<NullReferenceException>());
    }
}
