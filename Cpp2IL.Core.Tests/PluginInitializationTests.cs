using System;
using System.Threading.Tasks;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.Attributes;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class PluginInitializationTests
{
    [Test]
    [Category("基本功能")]
    public void 插件重复入口复用同一类型的已完成初始化()
    {
        Cpp2IlPluginManager.InitializeCandidates([new RegisterCpp2IlPluginAttribute(typeof(正常插件)), new RegisterCpp2IlPluginAttribute(typeof(正常插件))]);
        Cpp2IlPluginManager.InitializeCandidates([new RegisterCpp2IlPluginAttribute(typeof(正常插件))]);
        Assert.That(正常插件.Loads, Is.EqualTo(1));
    }

    [Test]
    [Category("异常输入")]
    public void 插件构造及加载失败不破坏遍历也不自动重复副作用()
    {
        Assert.DoesNotThrow(() => Cpp2IlPluginManager.InitializeCandidates([new RegisterCpp2IlPluginAttribute(typeof(构造失败插件)), new RegisterCpp2IlPluginAttribute(typeof(加载失败插件)), new RegisterCpp2IlPluginAttribute(typeof(后继插件))]));
        Assert.DoesNotThrow(() => Cpp2IlPluginManager.InitializeCandidates([new RegisterCpp2IlPluginAttribute(typeof(构造失败插件)), new RegisterCpp2IlPluginAttribute(typeof(加载失败插件)), new RegisterCpp2IlPluginAttribute(typeof(后继插件))]));
        Assert.That(构造失败插件.Attempts, Is.EqualTo(1));
        Assert.That(加载失败插件.Loads, Is.EqualTo(1));
        Assert.That(后继插件.Loads, Is.EqualTo(1));
    }

    [Test]
    [Category("边界值")]
    public void 插件回调重入不再次构造同一插件()
    {
        Cpp2IlPluginManager.InitializeCandidates([new RegisterCpp2IlPluginAttribute(typeof(重入插件))]);
        Assert.That(重入插件.Loads, Is.EqualTo(1));
        Assert.That(依赖插件.Loads, Is.EqualTo(1));
    }

    [Test]
    [Category("边界值")]
    public void 插件并发初始化共享一次完成状态()
    {
        Parallel.For(0, 4, _ => Cpp2IlPluginManager.InitializeCandidates([new RegisterCpp2IlPluginAttribute(typeof(并发插件))]));
        Assert.That(并发插件.Loads, Is.EqualTo(1));
    }

    [Test]
    [Category("基本功能")]
    public void 原始及小型输入加载器共用已初始化核心插件()
    {
        TestGameLoader.LoadSimple2019Game();
        Assert.DoesNotThrow(() => Cpp2IlApi.Init());
        Assert.DoesNotThrow(() => Cpp2IlApi.Init());
    }

    public abstract class 测试插件 : Cpp2IlPlugin
    {
        public override string Name => GetType().Name;
        public override string Description => "插件生命周期测试";
    }
    public sealed class 正常插件 : 测试插件 { public static int Loads; public override void OnLoad() => Loads++; }
    public sealed class 后继插件 : 测试插件 { public static int Loads; public override void OnLoad() => Loads++; }
    public sealed class 依赖插件 : 测试插件 { public static int Loads; public override void OnLoad() => Loads++; }
    public sealed class 并发插件 : 测试插件 { public static int Loads; public override void OnLoad() => Loads++; }
    public sealed class 构造失败插件 : 测试插件
    {
        public static int Attempts;
        public 构造失败插件() { Attempts++; throw new InvalidOperationException("预期的构造失败"); }
        public override void OnLoad() => throw new InvalidOperationException("失败构造之后不应回调");
    }
    public sealed class 加载失败插件 : 测试插件
    {
        public static int Loads;
        public override void OnLoad() { Loads++; throw new InvalidOperationException("预期的加载失败"); }
    }
    public sealed class 重入插件 : 测试插件
    {
        public static int Loads;
        public override void OnLoad()
        {
            Loads++;
            Cpp2IlPluginManager.InitializeCandidates([new RegisterCpp2IlPluginAttribute(typeof(重入插件)), new RegisterCpp2IlPluginAttribute(typeof(依赖插件))]);
        }
    }
}
