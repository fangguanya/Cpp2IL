using System.Collections.Generic;
using AssetRipper.Primitives;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core;

public class Cpp2IlRuntimeArgs
{
    //To determine easily if this struct is the default one or not.
    public bool Valid;

    //Core variables
    public UnityVersion UnityVersion;
    public string PathToAssembly = null!;
    public string PathToMetadata = null!;

    public string? WasmFrameworkJsFile;

    public List<Cpp2IlProcessingLayer> ProcessingLayersToRun = [];
    public readonly Dictionary<string, string> ProcessingLayerConfigurationOptions = new();

    public IEnumerable<Cpp2IlOutputFormat>? OutputFormats;
    public string OutputRootDirectory = null!;

    public bool LowMemoryMode;

    // 该上限只控制分析资源预算；ARM64 方法体实际长度由相邻虚拟地址动态确定。
    public int MaximumMethodSizeBytes = MethodAnalysisSizePolicy.DefaultMaximumBytes;

    // ISIL 精确输出范围使用完整名称匹配；空集合保持原有全量输出语义。
    public IReadOnlyList<string> IsilDumpAssemblyFilters = [];
    public IReadOnlyList<string> IsilDumpTypeFilters = [];
    public IReadOnlyList<string> IsilDumpMethodFilters = [];
}
