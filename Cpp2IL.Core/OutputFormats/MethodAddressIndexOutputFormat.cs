using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
        var assemblies = IsilDumpSelectionHelper.SelectExact(
            context.Assemblies,
            assemblyFilters,
            assembly => assembly.Name,
            "方法地址索引程序集");

        var indexRoot = Path.Combine(outputRoot, "MethodAddressIndex");
        Directory.CreateDirectory(indexRoot);
        foreach (var assembly in assemblies.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            var rows = BuildRows(assembly);
            var outputPath = Path.Combine(indexRoot, assembly.CleanAssemblyName + ".methods.tsv");
            File.WriteAllText(outputPath, Render(rows), new UTF8Encoding(false));
        }
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
