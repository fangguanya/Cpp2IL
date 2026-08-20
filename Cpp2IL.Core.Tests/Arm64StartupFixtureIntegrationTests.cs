using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using AssetRipper.Primitives;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using LibCpp2IL;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class Arm64StartupFixtureIntegrationTests
{
    private const string BinaryEnvironmentVariable = "CPP2IL_ARM64_FIXTURE_BINARY";
    private const string MetadataEnvironmentVariable = "CPP2IL_ARM64_FIXTURE_METADATA";
    private const string UnityVersionEnvironmentVariable = "CPP2IL_ARM64_FIXTURE_UNITY_VERSION";

    [Test]
    [Category("fixture集成")]
    public void StartupMethodsUseElfVirtualAddressesAndCompleteBodies()
    {
        var context = LoadFixture();
        Assert.That(context.InstructionSet, Is.TypeOf<NewArmV8InstructionSet>());

        var awake = FindMethod(context, 0x060036C5);
        var loadGameVars = FindMethod(context, 0x0600E2D9);

        AssertMethodIdentity(
            context,
            awake,
            expectedVirtualAddress: 0x2BD01EC,
            expectedRawAddress: 0x2BCC1EC,
            expectedLength: 0x72C,
            expectedInstructionCount: 459,
            expectedSha256: "ce16bd4e7394e09b8d261daa55f5012188e6e40d32e451ec7e1090659aec5623");

        AssertMethodIdentity(
            context,
            loadGameVars,
            expectedVirtualAddress: 0x305B50C,
            expectedRawAddress: 0x305750C,
            expectedLength: 0x4EC8,
            expectedInstructionCount: 5042,
            expectedSha256: "fd0f940ca1517260df3670def0ed0dca7c162cf06315e86db4769a624b43ecbc");

        loadGameVars.Analyze();
        Assert.That(loadGameVars.ConvertedIsil, Is.Not.Null.And.Count.GreaterThanOrEqualTo(5042));
    }

    [Test]
    [Category("fixture集成")]
    public void 字符串元数据GOT槽经二级读取解析为字面量()
    {
        var context = LoadFixture();
        var libContext = context.LibCpp2IlContext;
        var slots = new ulong[] { 0x5A01398, 0x5A01388, 0x59EFE68, 0x59EFE58 };
        var usages = slots.Select(address => new
        {
            Address = address,
            Direct = libContext.GetAnyGlobalByAddress(address),
            Table = libContext.CheckForPost27GlobalTableEntryAt(address, 0),
        }).ToArray();

        foreach (var usage in usages)
        {
            TestContext.Out.WriteLine(
                $"slot=0x{usage.Address:X}; direct={usage.Direct?.Type}; table={usage.Table?.Type}; " +
                $"literal={(usage.Table?.Type == MetadataUsageType.StringLiteral ? usage.Table.AsLiteral() : "<无>")}");
        }

        Assert.That(usages.Select(usage => usage.Table?.Type),
            Is.All.EqualTo(MetadataUsageType.StringLiteral));
        Assert.That(usages.Select(usage => usage.Table!.AsLiteral()), Is.EqualTo(new[]
        {
            "white",
            "yellow",
            "a16_darkCheckboxChecked",
            "a16_darkCheckboxChecked-dm",
        }));
    }

    private static ApplicationAnalysisContext LoadFixture()
    {
        var binaryPath = Environment.GetEnvironmentVariable(BinaryEnvironmentVariable);
        var metadataPath = Environment.GetEnvironmentVariable(MetadataEnvironmentVariable);
        var unityVersionText = Environment.GetEnvironmentVariable(UnityVersionEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(binaryPath) ||
            string.IsNullOrWhiteSpace(metadataPath) ||
            string.IsNullOrWhiteSpace(unityVersionText))
        {
            Assert.Ignore($"需要设置 {BinaryEnvironmentVariable}、{MetadataEnvironmentVariable} 与 {UnityVersionEnvironmentVariable}。");
        }

        Assert.That(File.Exists(binaryPath), Is.True, $"fixture 二进制不存在：{binaryPath}");
        Assert.That(File.Exists(metadataPath), Is.True, $"fixture 元数据不存在：{metadataPath}");

        // 正式路径必须使用默认 Disarm ARM64 实现，不让旧 Capstone 开关污染结果。
        Environment.SetEnvironmentVariable("CPP2IL_LEGACY_ARM64", null);
        Cpp2IlApi.ResetInternalState();
        Cpp2IlApi.RuntimeOptions = new Cpp2IlRuntimeArgs
        {
            MaximumMethodSizeBytes = MethodAnalysisSizePolicy.DefaultMaximumBytes
        };
        EnsureCorePluginInitialized();
        Cpp2IlApi.InitializeLibCpp2Il(binaryPath!, metadataPath!, UnityVersion.Parse(unityVersionText!));
        return Cpp2IlApi.CurrentAppContext!;
    }

    private static void EnsureCorePluginInitialized()
    {
        try
        {
            _ = InstructionSetRegistry.GetInstructionSet(DefaultInstructionSets.ARM_V8);
        }
        catch (KeyNotFoundException)
        {
            Cpp2IlApi.Init();
        }
    }

    private static MethodAnalysisContext FindMethod(ApplicationAnalysisContext context, uint token)
    {
        return context.Assemblies
            .SelectMany(assembly => assembly.Types)
            .SelectMany(type => type.Methods)
            .Single(method => method.Definition?.token == token);
    }

    private static void AssertMethodIdentity(
        ApplicationAnalysisContext context,
        MethodAnalysisContext method,
        ulong expectedVirtualAddress,
        long expectedRawAddress,
        int expectedLength,
        int expectedInstructionCount,
        string expectedSha256)
    {
        var instructions = NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(
            context.Binary,
            method.UnderlyingPointer);
        var actualSha256 = Convert.ToHexString(SHA256.HashData(method.RawBytes.AsSpan())).ToLowerInvariant();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(method.UnderlyingPointer, Is.EqualTo(expectedVirtualAddress));
            Assert.That(context.Binary.MapVirtualAddressToRaw(method.UnderlyingPointer), Is.EqualTo(expectedRawAddress));
            Assert.That(method.RawBytes.Length, Is.EqualTo(expectedLength));
            Assert.That(actualSha256, Is.EqualTo(expectedSha256));
            Assert.That(instructions, Has.Count.EqualTo(expectedInstructionCount));
            Assert.That(instructions[0].Address, Is.EqualTo(expectedVirtualAddress));
        }
    }
}
