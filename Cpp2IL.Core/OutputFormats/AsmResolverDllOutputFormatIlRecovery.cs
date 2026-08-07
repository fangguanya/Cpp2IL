using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AssetRipper.CIL;
using Cpp2IL.Core.Extensions;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.Logging;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.OutputFormats;

public class AsmResolverDllOutputFormatIlRecovery : AsmResolverDllOutputFormat
{
    private HashSet<TypeAnalysisContext>? selectedRecoveryTypes;
    private HashSet<MethodAnalysisContext>? selectedRecoveryMethods;

    public override string OutputFormatId => "dll_il_recovery";

    public override string OutputFormatName => "DLL files with IL Recovery";

    public override List<AssemblyDefinition> BuildAssemblies(ApplicationAnalysisContext context)
    {
        var runtimeOptions = Cpp2IlApi.RuntimeOptions;
        var assemblyFilters = runtimeOptions?.IsilDumpAssemblyFilters ?? [];
        var typeFilters = runtimeOptions?.IsilDumpTypeFilters ?? [];
        var methodFilters = runtimeOptions?.IsilDumpMethodFilters ?? [];
        if (assemblyFilters.Count == 0 && typeFilters.Count == 0 && methodFilters.Count == 0)
            return base.BuildAssemblies(context);

        var assemblies = IsilDumpSelectionHelper.SelectExact(
            context.Assemblies,
            assemblyFilters,
            assembly => assembly.Name,
            "IL恢复程序集");
        var types = IsilDumpSelectionHelper.SelectExact(
            assemblies.SelectMany(assembly => assembly.Types),
            typeFilters,
            type => type.Definition?.FullName ?? string.Empty,
            "IL恢复类型");
        if (typeFilters.Count > 0 && types.Any(type => type is InjectedTypeAnalysisContext || type.Methods.Count == 0))
            throw new InvalidOperationException("IL恢复类型筛选命中了注入类型或没有方法的类型。");

        var methods = IsilDumpSelectionHelper.SelectExact(
            types.SelectMany(type => type.Methods)
                .Where(method => method is not InjectedMethodAnalysisContext),
            methodFilters,
            method => method.Definition?.HumanReadableSignature ?? string.Empty,
            "IL恢复方法");

        selectedRecoveryTypes = new HashSet<TypeAnalysisContext>(types);
        selectedRecoveryMethods = new HashSet<MethodAnalysisContext>(methods);
        Logger.InfoNewline(
            $"IL恢复已精确选择 {assemblies.Count} 个程序集、{types.Count} 个类型与 {methods.Count} 个方法；其他成员只保留声明。",
            "DllOutput");
        try
        {
            return base.BuildAssemblies(context);
        }
        finally
        {
            selectedRecoveryTypes = null;
            selectedRecoveryMethods = null;
        }
    }

    protected override bool ShouldFillMethodBody(
        AssemblyAnalysisContext assemblyContext,
        TypeAnalysisContext typeContext)
    {
        return selectedRecoveryTypes == null || selectedRecoveryTypes.Contains(typeContext);
    }

    protected override void FillMethodBody(MethodDefinition methodDefinition, MethodAnalysisContext methodContext)
    {
        var module = methodDefinition.DeclaringModule!;
        var moduleName = module.Name!.ToString();
        var shouldSkip = moduleName.StartsWith("UnityEngine.") || moduleName.StartsWith("Unity.") ||
                         moduleName.StartsWith("System.") || moduleName == "System" ||
                         moduleName.StartsWith("mscorlib");
        var importer = new ReferenceImporter(module);

        if (!methodDefinition.IsManagedMethodWithBody())
            return;

        if (selectedRecoveryMethods != null && !selectedRecoveryMethods.Contains(methodContext))
        {
            methodDefinition.ReplaceMethodBodyWithMinimalImplementation();
            return;
        }

        methodDefinition.CilMethodBody = new();
        var instructions = methodDefinition.CilMethodBody.Instructions;

        if (shouldSkip)
        {
            methodDefinition.ReplaceMethodBodyWithMinimalImplementation();
            return;
        }

        try
        {
            TotalMethodCount++;

            methodContext.Analyze();

            if (methodContext.ConvertedIsil.Count == 0)
                methodDefinition.ReplaceMethodBodyWithMinimalImplementation();
            else
                IlGenerator.GenerateIl(methodContext, methodDefinition);

            //WriteControlFlowGraph(methodContext, Path.Combine(Environment.CurrentDirectory, "Cpp2IL", "bin", "Debug", "net9.0", "cpp2il_out", "cfg"));

            SuccessfulMethodCount++;
        }
        catch (Exception e)
        {
            // Known analysis limitations (DecompilerException) get a one-line warning; anything
            // else is an unexpected bug and keeps its (collapsed) stack trace.
            var detail = e is DecompilerException ? e.Message : e.ToCollapsedString();

            if (e is DecompilerException)
                Logger.WarnNewline($"Skipping {methodContext.FullName}: {e.Message}");
            else
                Logger.ErrorNewline($"Decompiling {methodContext.FullName} failed: {detail}");
            
            methodDefinition.CilMethodBody = new();
            instructions = methodDefinition.CilMethodBody.Instructions;

            var factory = module.CorLibTypeFactory;
            var exceptionCtor = factory.CorLibScope
                .CreateTypeReference("System", "Exception")
                .CreateMemberReference(".ctor", MethodSignature.CreateInstance(factory.Void, [factory.String]))
                .ImportWith(importer);

            instructions.Add(CilOpCodes.Ldstr, detail);
            instructions.Add(CilOpCodes.Newobj, exceptionCtor);
            instructions.Add(CilOpCodes.Throw);
        }

        methodContext.ReleaseAnalysisData();
    }

    public static void WriteControlFlowGraph(MethodAnalysisContext method, string outputPath)
    {
        var graph = method.ControlFlowGraph;

        var sb = new StringBuilder();
        var edges = new List<(int, int)>();

        sb.AppendLine("digraph ControlFlowGraph {");
        sb.AppendLine("    \"label\"=\"Control flow graph\"");

        // no instructions
        graph ??= new ISILControlFlowGraph([]);

        var methodText = $@"{CsFileUtils.GetKeyWordsForMethod(method)} {method.FullNameWithSignature}
parameter locals: {string.Join(", ", method.ParameterLocals)}
parameter operands: {string.Join(", ", method.ParameterOperands)}";

        foreach (var block in graph.Blocks)
        {
            if (block == graph.EntryBlock || block == graph.ExitBlock)
            {
                var isEntry = block == graph.EntryBlock;
                sb.AppendLine($"""
                               	{block.ID} [
                               		"color"="{(isEntry ? "green" : "red")}"
                               		"label"="{(isEntry ? $"Entry ({block.ID})\n{methodText}" : $"Exit ({block.ID})")}"
                               	]
                               """);
            }
            else
            {
                sb.AppendLine($"""
                               	{block.ID} [
                               		"shape"="box"
                               		"label"="{block.ToString().EscapeString().Replace("\\r", "")}"
                               	]
                               """);
            }

            edges.AddRange(block.Successors.Select(b => (block.ID, b.ID)));
        }

        foreach (var edge in edges)
            sb.AppendLine($"    {edge.Item1} -> {edge.Item2}");

        sb.AppendLine("}");

        var type = method.DeclaringType!;
        var assemblyName = MiscUtils.CleanPathElement(type.DeclaringAssembly.CleanAssemblyName);
        var typePath = Path.Combine(type.FullName.Split('.').Select(MiscUtils.CleanPathElement).ToArray());
        var directoryPath = Path.Combine(outputPath, assemblyName, typePath);

        var methodName = MiscUtils.CleanPathElement(method.Name + "_" + string.Join("_",
            method.Parameters.Select(p => MiscUtils.CleanPathElement(p.ParameterType.Name))));
        var path = Path.Combine(directoryPath, methodName) + ".dot";

        if (path.Length > 260)
        {
            path = path[..250];
            path += ".dot";
        }

        var directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, sb.ToString());
    }
}
