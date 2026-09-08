using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.OutputFormats;

/// <summary>
/// 输出程序集内全部托管方法的原生入口索引，不执行逐方法反汇编。
/// 该索引用于证明过滤式 ISIL 波次中的直接调用是否仍指向未采集的托管方法。
/// </summary>
public sealed class MethodAddressIndexOutputFormat : Cpp2IlOutputFormat
{
    public override string OutputFormatId => "method-address-index";

    public override string OutputFormatName => "Managed Method Address Index";

    public override void DoOutput(ApplicationAnalysisContext context, string outputRoot)
    {
        var runtimeOptions = Cpp2IlApi.RuntimeOptions;
        var assemblyFilters = runtimeOptions?.IsilDumpAssemblyFilters ?? [];
        // 类型和方法筛选不属于完整地址索引合同，禁止接收后静默忽略。
        if ((runtimeOptions?.IsilDumpTypeFilters.Count ?? 0) != 0 ||
            (runtimeOptions?.IsilDumpMethodFilters.Count ?? 0) != 0)
            throw new InvalidOperationException("方法地址索引只接受程序集筛选；类型或方法筛选必须移除。");
        var assemblies = IsilDumpSelectionHelper.SelectExact(
            context.Assemblies,
            assemblyFilters,
            assembly => assembly.Name,
            "方法地址索引程序集");

        var indexRoot = Path.Combine(outputRoot, "MethodAddressIndex");
        // 拒绝旧索引混入本轮范围，失败时不留下可被误读为本轮成功的历史 manifest。
        if (Directory.Exists(indexRoot) && Directory.EnumerateFileSystemEntries(indexRoot).Any())
            throw new InvalidOperationException("方法地址索引输出目录必须为空。");
        Directory.CreateDirectory(indexRoot);
        // 一次生成行集合，原始 metadata 独立提供分母，禁止从已输出行反推完整性。
        var indexed = assemblies.OrderBy(item => item.Name, StringComparer.Ordinal)
            .Select(assembly => (Assembly: assembly, Rows: BuildRows(assembly))).ToArray();
        var expected = assemblies.Where(assembly => assembly.Definition != null)
            .SelectMany(assembly => assembly.Definition!.Image.Types.SelectMany(type => type.Methods ?? [])
                .Select(method => new MethodIndexCompletenessHelper.Identity(assembly.Name, method.token)));
        var summary = MethodIndexCompletenessHelper.Validate(expected,
            indexed.SelectMany(item => item.Rows).Select(row => new MethodIndexCompletenessHelper.Entry(
                new MethodIndexCompletenessHelper.Identity(row.AssemblyIdentity, row.MethodToken), row.VirtualAddress)));
        var fullScope = assemblyFilters.Count == 0;
        if (fullScope && (summary.ExpectedMethods != context.Metadata.MethodDefinitionCount ||
            assemblies.Count(assembly => assembly.Definition != null) != context.Metadata.AssemblyDefinitions.Length))
            throw new InvalidOperationException("方法索引上下文未覆盖原始 metadata 的全部程序集或方法。");

        var artifacts = new List<(string Assembly, string File, int Methods, string Sha256)>();
        foreach (var (assembly, rows) in indexed)
        {
            var outputPath = Path.Combine(indexRoot, assembly.CleanAssemblyName + ".methods.tsv");
            File.WriteAllText(outputPath, Render(rows), new UTF8Encoding(false));
            artifacts.Add((assembly.Name, Path.GetFileName(outputPath), rows.Count, FileDigest(outputPath)));
        }

        // 该回执仅证明原始方法盘点；零地址仍在分母中，不自动宣称外部边界或源码恢复成功。
        using var stream = File.Create(Path.Combine(indexRoot, "scope-manifest.json"));
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("schema", "Cpp2IL.MethodAddressIndex.Scope/v1");
        writer.WriteString("scope", fullScope ? "ALL_METADATA_ASSEMBLIES" : "DIAGNOSTIC_ASSEMBLY_SELECTION");
        writer.WriteBoolean("sourceRecoveryAccepted", false);
        writer.WriteBoolean("scopeContractApproved", false);
        writer.WriteString("unityVersion", context.UnityVersion.ToString());
        writer.WriteNumber("metadataVersion", context.MetadataVersion);
        writer.WriteStartObject("input");
        writer.WriteString("binarySha256", FileDigest(runtimeOptions!.PathToAssembly));
        writer.WriteString("metadataSha256", FileDigest(runtimeOptions.PathToMetadata));
        writer.WriteEndObject();
        // 二进制摘要由宿主冻结；库内不假设单文件/AOT 宿主存在独立 DLL 路径。
        writer.WriteString("producerVersion", typeof(MethodAddressIndexOutputFormat).Assembly.GetName().Version?.ToString());
        writer.WriteNumber("rawAssemblies", context.Metadata.AssemblyDefinitions.Length);
        writer.WriteNumber("rawTypes", context.Metadata.TypeDefinitionCount);
        writer.WriteNumber("rawMethods", context.Metadata.MethodDefinitionCount);
        writer.WriteStartObject("summary");
        writer.WriteNumber("expectedMethods", summary.ExpectedMethods);
        writer.WriteNumber("indexedMethods", summary.IndexedMethods);
        writer.WriteNumber("zeroAddressMethods", summary.ZeroAddressMethods);
        writer.WriteNumber("sharedAddressGroups", summary.SharedAddressGroups);
        writer.WriteNumber("sharedAddressMethods", summary.SharedAddressMethods);
        writer.WriteEndObject();
        writer.WriteStartArray("assemblies");
        foreach (var artifact in artifacts)
        {
            writer.WriteStartObject();
            writer.WriteString("assembly", artifact.Assembly);
            writer.WriteString("file", artifact.File);
            writer.WriteNumber("methods", artifact.Methods);
            writer.WriteString("sha256", artifact.Sha256);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("excludedAssemblies");
        foreach (var assembly in context.Assemblies.Except(assemblies))
            writer.WriteStringValue(assembly.Name);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static string FileDigest(string path)
    {
        using var stream = File.OpenRead(path);
        using var algorithm = SHA256.Create();
        return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    private static IReadOnlyList<MethodAddressRow> BuildRows(AssemblyAnalysisContext assembly)
    {
        var rows = new List<MethodAddressRow>();
        foreach (var type in assembly.Types.OrderBy(
                     item => item.Definition?.FullName ?? string.Empty,
                     StringComparer.Ordinal))
        {
            if (type is InjectedTypeAnalysisContext || type.Definition == null)
                continue;

            foreach (var method in type.Methods
                         .Where(item => item is not InjectedMethodAnalysisContext && item.Definition != null)
                         .OrderBy(item => item.Token)
                         .ThenBy(item => item.Definition!.HumanReadableSignature, StringComparer.Ordinal))
            {
                rows.Add(new MethodAddressRow(
                    assembly.Name,
                    type.Definition.FullName!,
                    method.Token,
                    method.Definition!.Name!,
                    method.Definition.HumanReadableSignature!,
                    method.UnderlyingPointer));
            }
        }

        return rows;
    }

    private static string Render(IEnumerable<MethodAddressRow> rows)
    {
        var output = new StringBuilder();
        output.AppendLine("assemblyIdentity\ttypeFullName\tmethodToken\tmethodName\tsignature\tvirtualAddress");
        foreach (var row in rows)
        {
            output.Append(Escape(row.AssemblyIdentity)).Append('\t')
                .Append(Escape(row.TypeFullName)).Append('\t')
                .Append("0x").Append(row.MethodToken.ToString("X8")).Append('\t')
                .Append(Escape(row.MethodName)).Append('\t')
                .Append(Escape(row.Signature)).Append('\t')
                .Append("0x").Append(row.VirtualAddress.ToString("X"))
                .AppendLine();
        }

        return output.ToString();
    }

    private static string Escape(string value)
    {
        return value.Replace("\\", "\\\\")
            .Replace("\t", "\\t")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n");
    }

    private sealed record MethodAddressRow(
        string AssemblyIdentity,
        string TypeFullName,
        uint MethodToken,
        string MethodName,
        string Signature,
        ulong VirtualAddress);
}
