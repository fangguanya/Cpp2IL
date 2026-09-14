using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.Attributes;
using Cpp2IL.Core.Logging;
using LibCpp2IL;

namespace Cpp2IL.Core;

public static class Cpp2IlPluginManager
{
    private static readonly List<Cpp2IlPlugin> _loadedPlugins = [];
    private static readonly HashSet<Type> _attemptedPluginTypes = [];
    private static readonly object InitializationGate = new();
    
    internal static List<LibCpp2IlMain.MetadataFixupFunc>? MetadataFixupFuncs;

    [RequiresUnreferencedCode("Plugins are loaded dynamically.")]
    internal static void LoadFromDirectory(string pluginsDir)
    {
        Logger.InfoNewline($"Loading plugins from {pluginsDir}...", "Plugins");

        if (!Directory.Exists(pluginsDir))
            return;

        foreach (var file in Directory.EnumerateFiles(pluginsDir))
        {
            if (Path.GetExtension(file) == ".dll")
            {
                Logger.VerboseNewline($"\tLoading {Path.GetFileName(file)}...", "Plugins");
                Assembly.LoadFrom(file);
            }
        }
    }

    internal static void InitAll()
    {
        var registrations = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(assembly => assembly.GetCustomAttributes<RegisterCpp2IlPluginAttribute>()).ToArray();
        InitializeCandidates(registrations);
    }

    /// <summary>
    /// 以运行时类型身份保证每个插件只尝试初始化一次。失败插件可能已经注册部分全局状态，
    /// 因此不自动重试，也不把失败实例放入事件列表；重复入口和同线程重入均复用既有状态。
    /// </summary>
    internal static void InitializeCandidates(IEnumerable<RegisterCpp2IlPluginAttribute> registrations)
    {
        lock (InitializationGate)
        {
            foreach (var registration in registrations.ToArray())
            {
                // 保留注册属性原有的构造函数裁剪注解，不经过丢失类型保留信息的 Type 集合。
                var pluginType = registration.PluginType;
                if (!_attemptedPluginTypes.Add(pluginType)) continue;
                try
                {
                    var plugin = (Cpp2IlPlugin)Activator.CreateInstance(pluginType)!;
                    plugin.OnLoad();
                    Logger.InfoNewline($"Using Plugin: {plugin.Name}", "Plugins");
                    _loadedPlugins.Add(plugin);
                }
                catch (Exception error)
                {
                    Logger.ErrorNewline($"插件 {pluginType.FullName} 初始化失败，不加入事件列表且不重复执行：{error}", "Plugins");
                }
            }
        }
    }

    private static Cpp2IlPlugin[] LoadedPluginSnapshot()
    {
        lock (InitializationGate) return _loadedPlugins.ToArray();
    }

    /// <summary>
    /// Attempts to handle the given game path and populate the runtime arguments by passing them to plugins.
    /// </summary>
    /// <param name="gamePath">The path provided by the user for their game.</param>
    /// <param name="args">The arguments to populate with the result, if the game can be handled</param>
    /// <returns>True if the path was handled, and the game can be loaded based on the arguments, otherwise false.</returns>
    public static bool TryProcessGamePath(string gamePath, ref Cpp2IlRuntimeArgs args)
    {
        foreach (var cpp2IlPlugin in LoadedPluginSnapshot())
        {
            if (cpp2IlPlugin.HandleGamePath(gamePath, ref args))
                return true;
        }

        return false;
    }

    public static void CallOnFinish()
    {
        foreach (var cpp2IlPlugin in LoadedPluginSnapshot())
        {
            cpp2IlPlugin.CallOnFinish();
        }
    }
}
