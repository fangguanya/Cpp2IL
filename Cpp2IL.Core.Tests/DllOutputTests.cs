using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AsmResolver;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Builder;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.Tests;

public class DllOutputTests
{
    [Test]
    [Category("基本功能")]
    public void 单索引器属性恢复唯一默认成员名称()
    {
        var module = new ModuleDefinition("SingleIndexer.dll");
        var property = new PropertyDefinition(
            "Entry",
            PropertyAttributes.None,
            PropertySignature.CreateInstance(
                module.CorLibTypeFactory.String,
                [module.CorLibTypeFactory.Int32]));

        var restored = AsmResolverAssemblyPopulator.TryGetUnambiguousIndexerName(
            [property],
            out var name);

        Assert.Multiple(() =>
        {
            Assert.That(restored, Is.True);
            Assert.That(name, Is.EqualTo("Entry"));
        });
    }

    [Test]
    [Category("边界值")]
    public void 普通无参属性不生成默认成员名称()
    {
        var module = new ModuleDefinition("OrdinaryProperty.dll");
        var property = new PropertyDefinition(
            "Value",
            PropertyAttributes.None,
            PropertySignature.CreateInstance(module.CorLibTypeFactory.String));

        Assert.That(
            AsmResolverAssemblyPopulator.TryGetUnambiguousIndexerName([property], out _),
            Is.False);
    }

    [Test]
    [Category("异常输入")]
    public void 多种索引器属性名称存在歧义时保持关闭()
    {
        var module = new ModuleDefinition("AmbiguousIndexer.dll");
        var first = new PropertyDefinition(
            "First",
            PropertyAttributes.None,
            PropertySignature.CreateInstance(
                module.CorLibTypeFactory.String,
                [module.CorLibTypeFactory.Int32]));
        var second = new PropertyDefinition(
            "Second",
            PropertyAttributes.None,
            PropertySignature.CreateInstance(
                module.CorLibTypeFactory.String,
                [module.CorLibTypeFactory.String]));

        Assert.That(
            AsmResolverAssemblyPopulator.TryGetUnambiguousIndexerName([first, second], out _),
            Is.False);
    }

    [Test]
    [Category("基本功能")]
    public void IL2CPP模块上下文绑定到唯一CLI全局类型()
    {
        var appContext = TestGameLoader.LoadSimple2019Game();
        var moduleContext = appContext.Assemblies
            .SelectMany(assembly => assembly.Types)
            .First(AsmResolverAssemblyPopulator.IsTypeContextModule);
        var managedModule = new ModuleDefinition("ModuleBindingTest.dll");

        var bound = AsmResolverDllOutputFormat.BindModuleTypeContext(moduleContext, managedModule);

        Assert.Multiple(() =>
        {
            Assert.That(bound.Name, Is.EqualTo("<Module>"));
            Assert.That(bound, Is.SameAs(managedModule.TopLevelTypes.Single(type => type.Name == "<Module>")));
            Assert.That(moduleContext.GetExtraData<TypeDefinition>("AsmResolverType"), Is.SameAs(bound));
        });
    }

    [Test]
    [Category("边界值")]
    public void 重复绑定模块上下文不创建第二个全局类型()
    {
        var appContext = TestGameLoader.LoadSimple2019Game();
        var moduleContext = appContext.Assemblies
            .SelectMany(assembly => assembly.Types)
            .First(AsmResolverAssemblyPopulator.IsTypeContextModule);
        var managedModule = new ModuleDefinition("RepeatedModuleBindingTest.dll");

        var first = AsmResolverDllOutputFormat.BindModuleTypeContext(moduleContext, managedModule);
        var second = AsmResolverDllOutputFormat.BindModuleTypeContext(moduleContext, managedModule);

        Assert.Multiple(() =>
        {
            Assert.That(second, Is.SameAs(first));
            Assert.That(managedModule.TopLevelTypes.Count(type => type.Name == "<Module>"), Is.EqualTo(1));
        });
    }

    [Test]
    [Category("异常输入")]
    public void 普通类型不得绑定到CLI全局类型()
    {
        var appContext = TestGameLoader.LoadSimple2019Game();
        var ordinaryType = appContext.Assemblies
            .SelectMany(assembly => assembly.Types)
            .First(type => !AsmResolverAssemblyPopulator.IsTypeContextModule(type));
        var managedModule = new ModuleDefinition("InvalidModuleBindingTest.dll");

        Assert.Throws<ArgumentException>(() =>
            AsmResolverDllOutputFormat.BindModuleTypeContext(ordinaryType, managedModule));
    }

    [Test]
    public void AllAssembliesBuild()
    {
        var appContext = TestGameLoader.LoadSimple2019Game();

        var assemblies = new AsmResolverDllOutputFormatDefault().BuildAssemblies(appContext);

        using (Assert.EnterMultipleScope())
        {
            foreach (var assembly in assemblies)
            {
                Assert.DoesNotThrow(() =>
                {
                    using MemoryStream stream = new();
                    assembly.WriteManifest(stream, new ManagedPEImageBuilder(ThrowErrorListener.Instance));
                });
            }
        }
    }

    [Test]
    public void MscorlibIsItsOwnCorLib()
    {
        var appContext = TestGameLoader.LoadSimple2019Game();

        var assemblies = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(appContext);

        var mscorlib = assemblies.First(a => a.Name == "mscorlib").ManifestModule!;

        Assert.That(SignatureComparer.Default.Equals(mscorlib.CorLibTypeFactory.CorLibScope, mscorlib));
    }

    [Test]
    public void MscorlibHasNoAssemblyReferences()
    {
        var appContext = TestGameLoader.LoadSimple2019Game();

        var assemblies = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(appContext);

        var mscorlib = assemblies.First(a => a.Name == "mscorlib").ManifestModule!;

        Assert.That(mscorlib.AssemblyReferences, Is.Empty);
    }

    [Test]
    public void MscorlibHasNoTypeReferences()
    {
        var appContext = TestGameLoader.LoadSimple2019Game();

        var assemblies = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(appContext);

        var mscorlib = assemblies.First(a => a.Name == "mscorlib").ManifestModule!;

        SearchForTypeReference(mscorlib);
    }

    private static void SearchForTypeReference(ModuleDefinition module)
    {
        if (ContainsTypeReference(module.CustomAttributes))
        {
            Assert.Fail($"Module {module} contains a type reference in its custom attributes");
        }
        if (ContainsTypeReference(module.Assembly?.CustomAttributes))
        {
            Assert.Fail($"Module {module} contains a type reference in its assembly custom attributes");
        }
        foreach (var typeDefinition in module.GetAllTypes())
        {
            // Type
            {
                if (ContainsTypeReference(typeDefinition.BaseType))
                {
                    Assert.Fail($"Type {typeDefinition} contains a type reference in its base type");
                }
                if (ContainsTypeReference(typeDefinition.Interfaces.Select(i => i.Interface)))
                {
                    Assert.Fail($"Type {typeDefinition} contains a type reference in its interfaces");
                }
                if (ContainsTypeReference(typeDefinition.GenericParameters.SelectMany(g => g.Constraints).Select(c => c.Constraint)))
                {
                    Assert.Fail($"Type {typeDefinition} contains a type reference in its generic parameter constraints");
                }
                foreach (var methodOverride in typeDefinition.MethodImplementations)
                {
                    if (methodOverride.Body is not MethodDefinition)
                    {
                        Assert.Fail($"Method override {methodOverride} in type {typeDefinition} does not have a method body");
                    }
                    if (ContainsTypeReference(methodOverride.Declaration?.Signature?.ReturnType))
                    {
                        Assert.Fail($"Method override {methodOverride} in type {typeDefinition} contains a type reference in its return type");
                    }
                    if (ContainsTypeReference(methodOverride.Declaration?.Signature?.ParameterTypes))
                    {
                        Assert.Fail($"Method override {methodOverride} in type {typeDefinition} contains a type reference in its parameter types");
                    }
                    if (methodOverride.Declaration is MethodSpecification methodSpecification && ContainsTypeReference(methodSpecification.Method?.Signature))
                    {
                        Assert.Fail($"Method override {methodOverride} in type {typeDefinition} contains a type reference in its method specification");
                    }
                }
                if (ContainsTypeReference(typeDefinition.CustomAttributes))
                {
                    Assert.Fail($"Type {typeDefinition} contains a type reference in its custom attributes");
                }
            }
            foreach (var field in typeDefinition.Fields)
            {
                if (ContainsTypeReference(field.Signature?.FieldType))
                {
                    Assert.Fail($"Field {field} in type {typeDefinition} contains a type reference in its field type");
                }
                if (ContainsTypeReference(field.CustomAttributes))
                {
                    Assert.Fail($"Field {field} in type {typeDefinition} contains a type reference in its custom attributes");
                }
            }
            foreach (var property in typeDefinition.Properties)
            {
                if (ContainsTypeReference(property.Signature?.ReturnType))
                {
                    Assert.Fail($"Property {property} in type {typeDefinition} contains a type reference in its return type");
                }
                if (ContainsTypeReference(property.Signature?.ParameterTypes))
                {
                    Assert.Fail($"Property {property} in type {typeDefinition} contains a type reference in its parameter types");
                }
                if (ContainsTypeReference(property.CustomAttributes))
                {
                    Assert.Fail($"Property {property} in type {typeDefinition} contains a type reference in its custom attributes");
                }
            }
            foreach (var @event in typeDefinition.Events)
            {
                if (ContainsTypeReference(@event.EventType))
                {
                    Assert.Fail($"Event {@event} in type {typeDefinition} contains a type reference in its event type");
                }
                if (ContainsTypeReference(@event.CustomAttributes))
                {
                    Assert.Fail($"Event {@event} in type {typeDefinition} contains a type reference in its custom attributes");
                }

            }
            foreach (var method in typeDefinition.Methods)
            {
                if (ContainsTypeReference(method.Signature?.ReturnType))
                {
                    Assert.Fail($"Method {method} in type {typeDefinition} contains a type reference in its return type");
                }
                if (ContainsTypeReference(method.Signature?.ParameterTypes))
                {
                    Assert.Fail($"Method {method} in type {typeDefinition} contains a type reference in its parameter types");
                }
                if (ContainsTypeReference(method.GenericParameters.SelectMany(g => g.Constraints).Select(c => c.Constraint)))
                {
                    Assert.Fail($"Method {method} in type {typeDefinition} contains a type reference in its generic parameter constraints");
                }
                if (ContainsTypeReference(method.CilMethodBody?.LocalVariables.Select(v => v.VariableType)))
                {
                    Assert.Fail($"Method {method} in type {typeDefinition} contains a type reference in its local variables");
                }
                if (ContainsTypeReference(method.CilMethodBody?.Instructions.Select(i => i.Operand as ITypeDefOrRef)))
                {
                    Assert.Fail($"Method {method} in type {typeDefinition} contains a type reference in its instructions");
                }
                if (ContainsTypeReference(method.CilMethodBody?.ExceptionHandlers.Select(h => h.ExceptionType)))
                {
                    Assert.Fail($"Method {method} in type {typeDefinition} contains a type reference in its exception handlers");
                }
                if (ContainsTypeReference(method.CustomAttributes))
                {
                    Assert.Fail($"Method {method} in type {typeDefinition} contains a type reference in its custom attributes");
                }
                if (ContainsTypeReference(method.ParameterDefinitions.SelectMany(p => p.CustomAttributes)))
                {
                    Assert.Fail($"Method {method} in type {typeDefinition} contains a type reference in its parameter custom attributes");
                }
            }
        }
    }

    private static bool ContainsTypeReference(IEnumerable<CustomAttribute>? customAttributes)
    {
        return customAttributes is not null && customAttributes.Any(ContainsTypeReference);
    }

    private static bool ContainsTypeReference(MethodSignature? methodSignature)
    {
        return ContainsTypeReference(methodSignature?.ReturnType)
            || ContainsTypeReference(methodSignature?.ParameterTypes);
    }

    private static bool ContainsTypeReference(CustomAttribute customAttribute)
    {
        return ContainsTypeReference(customAttribute.Constructor?.Signature) || ContainsTypeReference(customAttribute.Signature);
    }

    private static bool ContainsTypeReference(CustomAttributeSignature? signature)
    {
        if (signature is null)
            return false;

        return ContainsTypeReference(signature.FixedArguments.Select(a => a.ArgumentType))
            || ContainsTypeReference(signature.NamedArguments.Select(a => a.ArgumentType))
            || ContainsTypeReference(signature.NamedArguments.Select(a => a.Argument.ArgumentType));
    }

    private static bool ContainsTypeReference(TypeSignature? type)
    {
        return type switch
        {
            CorLibTypeSignature corLibTypeSignature => corLibTypeSignature.Scope is not ModuleDefinition,
            TypeDefOrRefSignature typeDefOrRefSignature => typeDefOrRefSignature.Type is not TypeDefinition,
            CustomModifierTypeSignature customModifierTypeSignature => ContainsTypeReference(customModifierTypeSignature.BaseType) || ContainsTypeReference(customModifierTypeSignature.ModifierType),
            TypeSpecificationSignature typeSpecificationSignature => ContainsTypeReference(typeSpecificationSignature.BaseType),
            GenericInstanceTypeSignature genericInstanceTypeSignature => ContainsTypeReference(genericInstanceTypeSignature.GenericType) || ContainsTypeReference(genericInstanceTypeSignature.TypeArguments),
            FunctionPointerTypeSignature functionPointerTypeSignature => ContainsTypeReference(functionPointerTypeSignature.Signature),
            _ => false, // null, GenericParameterSignature, SentinelTypeSignature
        };
    }

    private static bool ContainsTypeReference(IEnumerable<TypeSignature?>? types)
    {
        return types is not null && types.Any(ContainsTypeReference);
    }

    private static bool ContainsTypeReference(ITypeDefOrRef? typeDefOrRef)
    {
        return typeDefOrRef switch
        {
            null => false,
            InvalidTypeDefOrRef => true,
            TypeDefinition => false,
            TypeReference => true,
            TypeSpecification typeSpecification => ContainsTypeReference(typeSpecification.Signature),
            _ => throw new NotSupportedException(),
        };
    }

    private static bool ContainsTypeReference(IEnumerable<ITypeDefOrRef?>? types)
    {
        return types is not null && types.Any(ContainsTypeReference);
    }
}
