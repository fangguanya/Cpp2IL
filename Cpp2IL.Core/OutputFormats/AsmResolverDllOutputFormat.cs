// #define VERBOSE_LOGGING

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Builder;
using AsmResolver.PE.Builder;
using AsmResolver.PE.DotNet.Metadata.Tables;
using AssetRipper.CIL;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.Extensions;
using Cpp2IL.Core.Logging;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Cpp2IL.Core.Utils.AsmResolver;
using LibCpp2IL.Metadata;

namespace Cpp2IL.Core.OutputFormats;

public abstract class AsmResolverDllOutputFormat : Cpp2IlOutputFormat
{
    private AssemblyDefinition? MostRecentCorLib { get; set; }
    protected int TotalMethodCount;
    protected int SuccessfulMethodCount;

    private readonly ConcurrentDictionary<ModuleDefinition, object> _stubLocks = new();

