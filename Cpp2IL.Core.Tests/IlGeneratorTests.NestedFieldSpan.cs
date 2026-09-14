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
    [Test]
    [Category("基本功能")]
    [Category("边界值")]
    public void 嵌套值字段公开访问器恢复真实PE读写()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var coreAssembly = app.GetAssemblyByName("mscorlib")!;
        var inner = new InjectedTypeAnalysisContext(
            app.Assemblies[0],
            "NestedAccessor",
            "Inner",
            coreAssembly.GetTypeByFullName("System.ValueType")!,
            System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.SequentialLayout
                | System.Reflection.TypeAttributes.Sealed);
        var parameter = new GenericParameterTypeAnalysisContext(
            "T", 0, Il2CppTypeEnum.IL2CPP_TYPE_VAR,
            System.Reflection.GenericParameterAttributes.NotNullableValueTypeConstraint, inner);
        inner.GenericParameters.Add(parameter);
        var innerConcrete = inner.MakeGenericInstanceType([app.SystemTypes.SystemInt32Type]);
        var first = inner.InjectFieldContext(
            "<First>k__BackingField",
            app.SystemTypes.SystemInt32Type,
            System.Reflection.FieldAttributes.Private);
        var second = inner.InjectFieldContext(
            "<Second>k__BackingField",
            app.SystemTypes.SystemInt32Type,
            System.Reflection.FieldAttributes.Private);
        var third = inner.InjectFieldContext(
            "<Third>k__BackingField",
            app.SystemTypes.SystemInt32Type,
            System.Reflection.FieldAttributes.Private);
        first.Offset = 0;
        second.Offset = 4;
        third.Offset = 8;
        var getterFirst = inner.InjectMethodContext(
            "get_First",
            app.SystemTypes.SystemInt32Type,
            System.Reflection.MethodAttributes.Public);
        var setterFirst = inner.InjectMethodContext(
            "set_First",
            app.SystemTypes.SystemVoidType,
            System.Reflection.MethodAttributes.Public,
            app.SystemTypes.SystemInt32Type);
        var getterSecond = inner.InjectMethodContext(
            "get_Second",
            app.SystemTypes.SystemInt32Type,
            System.Reflection.MethodAttributes.Public);
        var setterSecond = inner.InjectMethodContext(
            "set_Second",
            app.SystemTypes.SystemVoidType,
            System.Reflection.MethodAttributes.Public,
            app.SystemTypes.SystemInt32Type);
        inner.InjectPropertyContext("First", app.SystemTypes.SystemInt32Type, getterFirst, setterFirst,
            System.Reflection.PropertyAttributes.None);
        inner.InjectPropertyContext("Second", app.SystemTypes.SystemInt32Type, getterSecond, setterSecond,
            System.Reflection.PropertyAttributes.None);

        var owner = new InjectedTypeAnalysisContext(
            app.Assemblies[0], "NestedAccessor", "Owner", app.SystemTypes.SystemObjectType,
            System.Reflection.TypeAttributes.Public);
        var caller = new InjectedTypeAnalysisContext(
            app.Assemblies[0], "NestedAccessor", "Caller", app.SystemTypes.SystemObjectType,
            System.Reflection.TypeAttributes.Public);
        var source = owner.InjectFieldContext("Source", innerConcrete, System.Reflection.FieldAttributes.Public);
        var target = owner.InjectFieldContext("Target", innerConcrete, System.Reflection.FieldAttributes.Public);
        source.Offset = 16;
        target.Offset = 32;
        var receiver = new LocalVariable("owner", new Register(null, "owner"), owner);
        var word = new LocalVariable("word", new Register(null, "opaque"), app.SystemTypes.SystemInt64Type);
        var sourceReference = new FieldReference(source, receiver, source.Offset);
        var targetReference = new FieldReference(target, receiver, target.Offset);
        var context = new InjectedMethodAnalysisContext(
            caller,
            "Copy",
            app.SystemTypes.SystemVoidType,
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static,
            [owner],
            ["owner"])
        {
            ParameterLocals = [receiver],
            Locals = [receiver, word],
            AnalysisWarnings = [],
            ControlFlowGraph = new ISILControlFlowGraph([
                new Instruction(0, OpCode.Move, word, new NativeBitPatternLiteral(0, 0, 64))
                    { MemoryAccessWidthBits = 64 },
                new Instruction(1, OpCode.Move, word, sourceReference) { MemoryAccessWidthBits = 64 },
                new Instruction(2, OpCode.Move, targetReference, word) { MemoryAccessWidthBits = 64 },
                new Instruction(3, OpCode.Return),
            ])
        };

        var core = new ModuleDefinition("mscorlib.dll");
        var runtimeCoreAssembly = new AssemblyDefinition("mscorlib", new Version(4, 0, 0, 0));
        runtimeCoreAssembly.Modules.Add(core);
        绑定AsmResolver系统类型(core, app.SystemTypes.SystemObjectType, "Object", TypeAttributes.Public);
        绑定AsmResolver系统类型(core, app.SystemTypes.SystemValueTypeType, "ValueType", TypeAttributes.Public);
        绑定AsmResolver系统类型(core, app.SystemTypes.SystemInt32Type, "Int32", TypeAttributes.Public | TypeAttributes.Sealed);
        绑定AsmResolver系统类型(core, app.SystemTypes.SystemInt64Type, "Int64", TypeAttributes.Public | TypeAttributes.Sealed);
        绑定AsmResolver系统类型(core, app.SystemTypes.SystemVoidType, "Void", TypeAttributes.Public | TypeAttributes.Sealed);
        var module = new ModuleDefinition("NestedAccessor.dll", new AssemblyReference(runtimeCoreAssembly));
        var valueTypeReference = module.CorLibTypeFactory.CorLibScope.CreateTypeReference("System", "ValueType");
        var innerType = new TypeDefinition(
            "NestedAccessor",
            "Inner`1",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout,
            valueTypeReference);
        innerType.GenericParameters.Add(new GenericParameter("T", GenericParameterAttributes.NotNullableValueTypeConstraint));
        var ownerType = new TypeDefinition("NestedAccessor", "Owner", TypeAttributes.Public,
            module.CorLibTypeFactory.Object.Type);
        var callerType = new TypeDefinition("NestedAccessor", "Caller", TypeAttributes.Public,
            module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(innerType);
        module.TopLevelTypes.Add(ownerType);
        module.TopLevelTypes.Add(callerType);
        inner.PutExtraData("AsmResolverType", innerType);
        owner.PutExtraData("AsmResolverType", ownerType);
        caller.PutExtraData("AsmResolverType", callerType);
        var intSignature = module.CorLibTypeFactory.Int32;
        var innerSignature = innerConcrete.ToTypeSignature(module);

        var innerFields = new[] { (Context: first, Name: "<First>k__BackingField"),
            (Context: second, Name: "<Second>k__BackingField"),
            (Context: third, Name: "<Third>k__BackingField") };
        foreach (var item in innerFields)
        {
            var definition = new FieldDefinition(item.Name, FieldAttributes.Private, intSignature);
            innerType.Fields.Add(definition);
            item.Context.PutExtraData("AsmResolverField", definition);
        }

        var accessors = new[]
        {
            (Field: first, GetterContext: getterFirst, SetterContext: setterFirst,
                GetterName: "get_First", SetterName: "set_First"),
            (Field: second, GetterContext: getterSecond, SetterContext: setterSecond,
                GetterName: "get_Second", SetterName: "set_Second"),
        };
        foreach (var item in accessors)
        {
            var fieldDefinition = item.Field.GetExtraData<FieldDefinition>("AsmResolverField")!;
            var getterDefinition = new MethodDefinition(
                item.GetterName,
                MethodAttributes.Public,
                MethodSignature.CreateInstance(intSignature));
            var setterDefinition = new MethodDefinition(
                item.SetterName,
                MethodAttributes.Public,
                MethodSignature.CreateInstance(module.CorLibTypeFactory.Void, [intSignature]));
            innerType.Methods.Add(getterDefinition);
            innerType.Methods.Add(setterDefinition);
            item.GetterContext.PutExtraData("AsmResolverMethod", getterDefinition);
            item.SetterContext.PutExtraData("AsmResolverMethod", setterDefinition);
            getterDefinition.CilMethodBody = new CilMethodBody();
            getterDefinition.CilMethodBody.Instructions.Add(CilOpCodes.Ldarg_0);
            getterDefinition.CilMethodBody.Instructions.Add(CilOpCodes.Ldfld, fieldDefinition);
            getterDefinition.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
            setterDefinition.CilMethodBody = new CilMethodBody();
            setterDefinition.CilMethodBody.Instructions.Add(CilOpCodes.Ldarg_0);
            setterDefinition.CilMethodBody.Instructions.Add(CilOpCodes.Ldarg_1);
            setterDefinition.CilMethodBody.Instructions.Add(CilOpCodes.Stfld, fieldDefinition);
            setterDefinition.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
        }

        foreach (var item in new[] { (Context: source, Name: "Source"), (Context: target, Name: "Target") })
        {
            var definition = new FieldDefinition(item.Name, FieldAttributes.Public, innerSignature);
            ownerType.Fields.Add(definition);
            item.Context.PutExtraData("AsmResolverField", definition);
        }
        var method = new MethodDefinition(
            "Copy",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void, [new TypeDefOrRefSignature(ownerType, false)]));
        method.ParameterDefinitions.Add(new ParameterDefinition(1, "owner", (ParameterAttributes)0));
        callerType.Methods.Add(method);

        Assert.That(GenericInstanceFieldLayout.GetSizeAndAlignment(innerConcrete, app.Binary.PointerSizeBytes),
            Is.EqualTo((12L, 4L)));
        Assert.That(ManagedFieldSpanRecoveryHelper.TryDescribe(sourceReference, app.Binary.PointerSizeBytes, 8,
                out var span, out var spanFailure), Is.True, spanFailure);
        Assert.That(span.Segments.Select(segment => segment.Field.Name),
            Is.EqualTo(new[] { "<First>k__BackingField", "<Second>k__BackingField" }));
        IlGenerator.GenerateIl(context, method);
        CilStackValidator.Validate(method.CilMethodBody!, context.FullName);
        var accessorCalls = method.CilMethodBody!.Instructions
            .Where(instruction => instruction.OpCode == CilOpCodes.Call)
            .Select(instruction => ((IMethodDescriptor)instruction.Operand!).Name!.ToString())
            .Where(name => name is "get_First" or "get_Second" or "set_First" or "set_Second")
            .ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(accessorCalls, Is.EqualTo(new[] { "get_First", "get_Second", "set_First", "set_Second" }));
            Assert.That(method.CilMethodBody.Instructions.Any(instruction => instruction.OpCode == CilOpCodes.Ldind_I8
                || instruction.OpCode == CilOpCodes.Stind_I8), Is.False);
        });
        var assembly = new AssemblyDefinition("NestedAccessor", new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        using var stream = new System.IO.MemoryStream();
        module.Write(stream);
        var runtime = System.Reflection.Assembly.Load(stream.ToArray());
        var runtimeOwner = runtime.GetType("NestedAccessor.Owner")!;
        var runtimeInner = runtime.GetType("NestedAccessor.Inner`1")!.MakeGenericType(typeof(int));
        var instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(runtimeOwner);
        var sourceValue = Activator.CreateInstance(runtimeInner)!;
        var targetValue = Activator.CreateInstance(runtimeInner)!;
        runtimeInner.GetField("<First>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(sourceValue, 0x12345678);
        runtimeInner.GetField("<Second>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(sourceValue, unchecked((int)0x89ABCDEF));
        runtimeInner.GetField("<Third>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(sourceValue, 99);
        runtimeInner.GetField("<First>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(targetValue, 7);
        runtimeInner.GetField("<Second>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(targetValue, 11);
        runtimeInner.GetField("<Third>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(targetValue, 77);
        runtimeOwner.GetField("Source")!.SetValue(instance, sourceValue);
        runtimeOwner.GetField("Target")!.SetValue(instance, targetValue);
        runtime.GetType("NestedAccessor.Caller")!.GetMethod("Copy")!.Invoke(null, [instance]);
        var actual = runtimeOwner.GetField("Target")!.GetValue(instance)!;
        Assert.Multiple(() =>
        {
            Assert.That(runtimeInner.GetField("<First>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(actual),
                Is.EqualTo(0x12345678));
            Assert.That(runtimeInner.GetField("<Second>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(actual),
                Is.EqualTo(unchecked((int)0x89ABCDEF)));
            Assert.That(runtimeInner.GetField("<Third>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(actual),
                Is.EqualTo(77));
        });
    }

    [Test]
    [Category("基本功能")]
    [Category("边界值")]
    public void 完整非托管聚合私有嵌套字段使用根字段位模式()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var coreAssembly = app.GetAssemblyByName("mscorlib")!;
        var pairDefinition = new InjectedTypeAnalysisContext(
            app.Assemblies[0],
            "NestedSpan",
            "Pair`1",
            coreAssembly.GetTypeByFullName("System.ValueType")!,
            System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.SequentialLayout);
        var parameter = new GenericParameterTypeAnalysisContext(
            "T", 0, Il2CppTypeEnum.IL2CPP_TYPE_VAR,
            System.Reflection.GenericParameterAttributes.None, pairDefinition);
        pairDefinition.GenericParameters.Add(parameter);
        var pair = pairDefinition.MakeGenericInstanceType([app.SystemTypes.SystemSingleType]);
        var left = pairDefinition.InjectFieldContext("left", parameter, System.Reflection.FieldAttributes.Private);
        var right = pairDefinition.InjectFieldContext("right", parameter, System.Reflection.FieldAttributes.Private);
        left.Offset = 0;
        right.Offset = 4;

        var owner = new InjectedTypeAnalysisContext(
            app.Assemblies[0], "NestedSpan", "Owner", app.SystemTypes.SystemObjectType, System.Reflection.TypeAttributes.Public);
        var caller = new InjectedTypeAnalysisContext(
            app.Assemblies[0], "NestedSpan", "Caller", app.SystemTypes.SystemObjectType, System.Reflection.TypeAttributes.Public);
        var source = owner.InjectFieldContext("Source", pair, System.Reflection.FieldAttributes.Public);
        var target = owner.InjectFieldContext("Target", pair, System.Reflection.FieldAttributes.Public);
        source.Offset = 16;
        target.Offset = 24;
        var receiver = new LocalVariable("owner", new Register(null, "owner"), owner);
        var word = new LocalVariable("word", new Register(null, "opaque"), app.SystemTypes.SystemInt64Type);
        var sourceReference = new FieldReference(source, receiver, source.Offset);
        var targetReference = new FieldReference(target, receiver, target.Offset);
        var context = new InjectedMethodAnalysisContext(
            caller,
            "Copy",
            app.SystemTypes.SystemVoidType,
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static,
            [owner],
            ["owner"])
        {
            ParameterLocals = [receiver],
            Locals = [receiver, word],
            AnalysisWarnings = [],
            ControlFlowGraph = new ISILControlFlowGraph([
                new Instruction(0, OpCode.Move, word,
                    new NativeBitPatternLiteral(0x7FC01234U, 0, 64)) { MemoryAccessWidthBits = 64 },
                new Instruction(1, OpCode.Move, word, sourceReference) { MemoryAccessWidthBits = 64 },
                new Instruction(2, OpCode.Move, targetReference, word) { MemoryAccessWidthBits = 64 },
                new Instruction(3, OpCode.Return),
            ])
        };

        var runtimeCore = new ModuleDefinition("mscorlib.dll");
        var runtimeCoreAssembly = new AssemblyDefinition("mscorlib", new Version(4, 0, 0, 0));
        runtimeCoreAssembly.Modules.Add(runtimeCore);
        绑定AsmResolver系统类型(runtimeCore, app.SystemTypes.SystemObjectType, "Object", TypeAttributes.Public);
        绑定AsmResolver系统类型(runtimeCore, app.SystemTypes.SystemSingleType, "Single", TypeAttributes.Public | TypeAttributes.Sealed);
        绑定AsmResolver系统类型(runtimeCore, app.SystemTypes.SystemInt64Type, "Int64", TypeAttributes.Public | TypeAttributes.Sealed);
        var module = new ModuleDefinition("NestedSpanPair.dll", new AssemblyReference(runtimeCoreAssembly));
        var pairType = new TypeDefinition(
            "NestedSpan",
            "Pair`1",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout,
            module.CorLibTypeFactory.CorLibScope.CreateTypeReference("System", "ValueType"));
        pairType.GenericParameters.Add(new GenericParameter("T", (GenericParameterAttributes)0));
        module.TopLevelTypes.Add(pairType);
        pairDefinition.PutExtraData("AsmResolverType", pairType);
        foreach (var fieldContext in new[] { left, right })
        {
            var field = new FieldDefinition(
                fieldContext.Name,
                (FieldAttributes)fieldContext.Attributes,
                parameter.ToTypeSignature(module));
            pairType.Fields.Add(field);
            fieldContext.PutExtraData("AsmResolverField", field);
        }

        var ownerType = new TypeDefinition("NestedSpan", "Owner", TypeAttributes.Public, module.CorLibTypeFactory.Object.Type);
        var callerType = new TypeDefinition("NestedSpan", "Caller", TypeAttributes.Public, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(ownerType);
        module.TopLevelTypes.Add(callerType);
        owner.PutExtraData("AsmResolverType", ownerType);
        caller.PutExtraData("AsmResolverType", callerType);
        foreach (var fieldContext in new[] { source, target })
        {
            var field = new FieldDefinition(
                fieldContext.Name,
                (FieldAttributes)fieldContext.Attributes,
                pair.ToTypeSignature(module));
            ownerType.Fields.Add(field);
            fieldContext.PutExtraData("AsmResolverField", field);
        }

        var method = new MethodDefinition(
            "Copy",
            MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void, [owner.ToTypeSignature(module)]));
        callerType.Methods.Add(method);
        method.ParameterDefinitions.Add(new ParameterDefinition(1, "owner", (ParameterAttributes)0));

        IlGenerator.GenerateIl(context, method);
        CilStackValidator.Validate(method.CilMethodBody!, context.FullName);
        Assert.Multiple(() =>
        {
            Assert.That(method.CilMethodBody!.Instructions.Any(instruction => instruction.OpCode == CilOpCodes.Ldind_I8), Is.True);
            Assert.That(method.CilMethodBody.Instructions.Any(instruction => instruction.OpCode == CilOpCodes.Ldfld), Is.False);
            Assert.That(method.CilMethodBody.Instructions.Any(instruction => instruction.OpCode == CilOpCodes.Stfld), Is.False);
        });

        var assembly = new AssemblyDefinition("NestedSpanPair", new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        using var stream = new System.IO.MemoryStream();
        module.Write(stream);
        var runtime = System.Reflection.Assembly.Load(stream.ToArray());
        var runtimeOwner = runtime.GetType("NestedSpan.Owner")!;
        var runtimePair = runtime.GetType("NestedSpan.Pair`1")!.MakeGenericType(typeof(float));
        var instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(runtimeOwner);
        var sourceValue = Activator.CreateInstance(runtimePair)!;
        var targetValue = Activator.CreateInstance(runtimePair)!;
        var sourceBits = new[] { 0x7FC01234U, 0x80000000U };
        var targetBits = new[] { 1U, 2U };
        var pairFields = new[] { "left", "right" };
        for (var index = 0; index < pairFields.Length; index++)
        {
            runtimePair.GetField(pairFields[index], System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(
                sourceValue, BitConverter.Int32BitsToSingle(unchecked((int)sourceBits[index])));
            runtimePair.GetField(pairFields[index], System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(
                targetValue, BitConverter.Int32BitsToSingle(unchecked((int)targetBits[index])));
        }
        runtimeOwner.GetField("Source")!.SetValue(instance, sourceValue);
        runtimeOwner.GetField("Target")!.SetValue(instance, targetValue);
        runtime.GetType("NestedSpan.Caller")!.GetMethod("Copy")!.Invoke(null, [instance]);
        var actual = runtimeOwner.GetField("Target")!.GetValue(instance)!;
        for (var index = 0; index < pairFields.Length; index++)
        {
            var value = (float)runtimePair.GetField(pairFields[index], System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(actual)!;
            Assert.That(unchecked((uint)BitConverter.SingleToInt32Bits(value)), Is.EqualTo(sourceBits[index]));
        }
    }

    [TestCase(false, false, 0, "正常")]
    [TestCase(false, false, 1, "正常")]
    [TestCase(true, false, 0, "正常")]
    [TestCase(true, false, 1, "正常")]
    [TestCase(false, true, 0, "正常")]
    [TestCase(false, true, 1, "正常")]
    [TestCase(true, true, 0, "正常")]
    [TestCase(true, true, 1, "正常")]
    [TestCase(false, false, 0, "私有父字段")]
    [TestCase(true, false, 0, "私有父字段")]
    [TestCase(false, false, 0, "只读父字段")]
    [TestCase(true, true, 0, "只读父字段")]
    [TestCase(false, false, 0, "只读叶字段")]
    [TestCase(true, true, 1, "只读叶字段")]
    [Category("基本功能")]
    [Category("边界值")]
    [Category("异常输入")]
    public void 嵌套值字段部分跨度真实PE保留位模式及未访问分量(bool isStatic, bool useDouble, int firstComponent, string boundary)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var scalar = useDouble ? app.SystemTypes.SystemDoubleType : app.SystemTypes.SystemSingleType;
        var scalarBytes = useDouble ? 8 : 4;
        var shape = new InjectedTypeAnalysisContext(app.Assemblies[0], "NestedSpan", "Triple`1",
            app.GetAssemblyByName("mscorlib")!.GetTypeByFullName("System.ValueType"),
            System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.SequentialLayout);
        var parameter = new GenericParameterTypeAnalysisContext("T", 0, Il2CppTypeEnum.IL2CPP_TYPE_VAR,
            System.Reflection.GenericParameterAttributes.None, shape);
        shape.GenericParameters.Add(parameter);
        var members = new[] { "A", "B", "C" }.Select(name => shape.InjectFieldContext(name, parameter,
            System.Reflection.FieldAttributes.Public | (boundary == "只读叶字段" ? System.Reflection.FieldAttributes.InitOnly : 0))).ToArray();
        var concrete = shape.MakeGenericInstanceType([scalar]);
        var owner = new InjectedTypeAnalysisContext(app.Assemblies[0], "NestedSpan", "Owner", app.SystemTypes.SystemObjectType, System.Reflection.TypeAttributes.Public);
        var caller = new InjectedTypeAnalysisContext(app.Assemblies[0], "NestedSpan", "Caller", app.SystemTypes.SystemObjectType, System.Reflection.TypeAttributes.Public);
        var attributes = (boundary == "私有父字段" ? System.Reflection.FieldAttributes.Private : System.Reflection.FieldAttributes.Public)
            | (isStatic ? System.Reflection.FieldAttributes.Static : 0);
        var source = owner.InjectFieldContext("Source", concrete, attributes);
        var target = owner.InjectFieldContext("Target", concrete, attributes | (boundary == "只读父字段" ? System.Reflection.FieldAttributes.InitOnly : 0));
        source.Offset = isStatic ? 0 : 16;
        target.Offset = source.Offset + 4 * scalarBytes;
        var receiver = new LocalVariable("owner", new Register(null, "owner"), owner);
        var value = new LocalVariable("word", new Register(null, "opaque"), app.SystemTypes.SystemInt64Type);
        var sourceReference = new FieldReference(source, receiver, source.Offset + firstComponent * scalarBytes);
        var targetReference = new FieldReference(target, receiver, target.Offset + firstComponent * scalarBytes);
        Assert.That(ManagedFieldSpanRecoveryHelper.TryDescribe(sourceReference, 8, 8, out var layout, out var failure), Is.True, failure);
        Assert.That(layout.Segments.Count, Is.EqualTo(8 / scalarBytes));
        Assert.That(layout.Segments.All(segment => segment.ParentFields is { Count: 1 } && ReferenceEquals(segment.RootField, source)), Is.True);
        var context = new InjectedMethodAnalysisContext(caller, "Copy", app.SystemTypes.SystemVoidType,
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static, [owner], ["owner"])
        {
            ParameterLocals = [receiver], Locals = [receiver, value], AnalysisWarnings = [],
            ControlFlowGraph = new ISILControlFlowGraph([
                new Instruction(0, OpCode.Move, value, new NativeBitPatternLiteral(0, 0, 64)) { MemoryAccessWidthBits = 64 },
                new Instruction(1, OpCode.Move, value, sourceReference) { MemoryAccessWidthBits = 64 },
                new Instruction(2, OpCode.Move, targetReference, value) { MemoryAccessWidthBits = 64 },
                new Instruction(3, OpCode.Return)])
        };
        Assert.That(PropertyBackingFieldRecovery.Run(context), Is.Zero, "部分结构字段不改成整个属性调用。");
        var core = new ModuleDefinition("mscorlib.dll");
        var coreAssembly = new AssemblyDefinition("mscorlib", new Version(4, 0, 0, 0));
        coreAssembly.Modules.Add(core);
        绑定AsmResolver系统类型(core, app.SystemTypes.SystemObjectType, "Object", TypeAttributes.Public);
        绑定AsmResolver系统类型(core, scalar, useDouble ? "Double" : "Single", TypeAttributes.Public | TypeAttributes.Sealed);
        绑定AsmResolver系统类型(core, app.SystemTypes.SystemInt64Type, "Int64", TypeAttributes.Public | TypeAttributes.Sealed);
        var name = "NestedSpan_" + Guid.NewGuid().ToString("N");
        var module = new ModuleDefinition(name + ".dll", new AssemblyReference(coreAssembly));
        var shapeType = new TypeDefinition("NestedSpan", "Triple`1", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout,
            module.CorLibTypeFactory.CorLibScope.CreateTypeReference("System", "ValueType"));
        shapeType.GenericParameters.Add(new GenericParameter("T", (GenericParameterAttributes)0));
        module.TopLevelTypes.Add(shapeType);
        shape.PutExtraData("AsmResolverType", shapeType);
        foreach (var member in members)
        {
            var field = new FieldDefinition(member.Name, (FieldAttributes)member.Attributes, parameter.ToTypeSignature(module));
            shapeType.Fields.Add(field);
            member.PutExtraData("AsmResolverField", field);
        }
        var ownerType = new TypeDefinition("NestedSpan", "Owner", TypeAttributes.Public, module.CorLibTypeFactory.Object.Type);
        var callerType = new TypeDefinition("NestedSpan", "Caller", TypeAttributes.Public, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(ownerType); module.TopLevelTypes.Add(callerType);
        owner.PutExtraData("AsmResolverType", ownerType); caller.PutExtraData("AsmResolverType", callerType);
        foreach (var field in new[] { source, target })
        {
            var definition = new FieldDefinition(field.Name, (FieldAttributes)field.Attributes, concrete.ToTypeSignature(module));
            ownerType.Fields.Add(definition); field.PutExtraData("AsmResolverField", definition);
        }
        var method = new MethodDefinition("Copy", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void, [owner.ToTypeSignature(module)]));
        callerType.Methods.Add(method);
        // 声明参数身份与实际生产的元数据一致，避免把接收者误当未赋值局部。
        method.ParameterDefinitions.Add(new ParameterDefinition(1, "owner", (ParameterAttributes)0));
        if (boundary != "正常")
        {
            var error = Assert.Throws<UnresolvedCilSemanticException>(() => IlGenerator.GenerateIl(context, method));
            Assert.That(error!.Message, Does.Contain(boundary.StartsWith("只读", StringComparison.Ordinal) ? "READONLY_NATIVE_STORE" : "FIELD_SPAN_ACCESSIBILITY"));
            return;
        }
        IlGenerator.GenerateIl(context, method);
        CilStackValidator.Validate(method.CilMethodBody!, context.FullName);
        Assert.That(method.CilMethodBody!.Instructions.Any(i => i.OpCode == CilOpCodes.Ldind_I8 || i.OpCode == CilOpCodes.Stind_I8), Is.False);
        var assembly = new AssemblyDefinition(name, new Version(1, 0, 0, 0)); assembly.Modules.Add(module);
        using var stream = new System.IO.MemoryStream(); module.Write(stream);
        var runtime = System.Reflection.Assembly.Load(stream.ToArray());
        var runtimeOwner = runtime.GetType("NestedSpan.Owner")!;
        var runtimeShape = runtime.GetType("NestedSpan.Triple`1")!.MakeGenericType(useDouble ? typeof(double) : typeof(float));
        var instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(runtimeOwner);
        var beforeSource = new ulong[] { useDouble ? 0x8000000000000000UL : 0x80000000UL, useDouble ? 0x7FF8000000001234UL : 0x7FC01234UL, useDouble ? 0xFFF0000000000000UL : 0xFF800000UL };
        var beforeTarget = new ulong[] { 1, 2, 3 };
        object MakeValue(ulong[] bits)
        {
            var box = Activator.CreateInstance(runtimeShape)!;
            for (var index = 0; index < 3; index++)
            {
                object scalarValue = useDouble ? (object)BitConverter.Int64BitsToDouble(unchecked((long)bits[index])) : BitConverter.Int32BitsToSingle(unchecked((int)bits[index]));
                runtimeShape.GetField(new[] { "A", "B", "C" }[index])!.SetValue(box, scalarValue);
            }
            return box;
        }
        runtimeOwner.GetField("Source")!.SetValue(instance, MakeValue(beforeSource));
        runtimeOwner.GetField("Target")!.SetValue(instance, MakeValue(beforeTarget));
        runtime.GetType("NestedSpan.Caller")!.GetMethod("Copy")!.Invoke(null, [instance]);
        foreach (var side in new[] { "Source", "Target" })
        {
            var box = runtimeOwner.GetField(side)!.GetValue(instance)!;
            for (var index = 0; index < 3; index++)
            {
                var actual = runtimeShape.GetField(new[] { "A", "B", "C" }[index])!.GetValue(box)!;
                var bits = useDouble ? unchecked((ulong)BitConverter.DoubleToInt64Bits((double)actual)) : unchecked((uint)BitConverter.SingleToInt32Bits((float)actual));
                var expected = side == "Source" || index >= firstComponent && index < firstComponent + 8 / scalarBytes ? beforeSource[index] : beforeTarget[index];
                Assert.That(bits, Is.EqualTo(expected), $"{side}[{index}] 的按位值或未访问哨兵发生变化。");
            }
        }
    }
}
