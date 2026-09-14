using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Cpp2IL.Core.Il2CppApiFunctions;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public class KeyFunctionAddressInitializerTests
{
    [Test]
    [Category("基本功能")]
    public void 初始化完成后所有调用复用同一已发布实例()
    {
        var initializer = new KeyFunctionAddressInitializer();
        var creates = 0;
        var scans = 0;

        var first = initializer.GetOrInitialize(
            () => { creates++; return new 测试地址表(); },
            _ => scans++);
        var second = initializer.GetOrInitialize(
            () => { creates++; return new 测试地址表(); },
            _ => scans++);

        Assert.That(second, Is.SameAs(first));
        Assert.That(creates, Is.EqualTo(1));
        Assert.That(scans, Is.EqualTo(1));
    }

    [Test]
    [Category("异常输入")]
    public void 扫描失败的候选不发布且后续可重新完整初始化()
    {
        var initializer = new KeyFunctionAddressInitializer();
        var failed = new 测试地址表();
        var completed = new 测试地址表();

        Assert.Throws<InvalidOperationException>(() => initializer.GetOrInitialize(
            () => failed,
            _ => throw new InvalidOperationException("预期扫描失败")));
        var result = initializer.GetOrInitialize(() => completed, _ => { });

        Assert.That(result, Is.SameAs(completed));
        Assert.That(result, Is.Not.SameAs(failed));
    }

    [Test]
    [Category("边界值")]
    public void 并发调用只完成一次扫描和发布()
    {
        var initializer = new KeyFunctionAddressInitializer();
        var creates = 0;
        var scans = 0;
        var results = new BaseKeyFunctionAddresses[8];

        Parallel.For(0, results.Length, index =>
        {
            results[index] = initializer.GetOrInitialize(
                () => { System.Threading.Interlocked.Increment(ref creates); return new 测试地址表(); },
                _ => System.Threading.Interlocked.Increment(ref scans));
        });

        Assert.That(results, Has.All.SameAs(results[0]));
        Assert.That(creates, Is.EqualTo(1));
        Assert.That(scans, Is.EqualTo(1));
    }

    private sealed class 测试地址表 : BaseKeyFunctionAddresses
    {
        protected override ulong GetObjectIsInstFromSystemType() => 0;
        protected override IEnumerable<ulong> FindAllThunkFunctions(
            ulong addr,
            uint maxBytesBack = 0,
            params ulong[] addressesToIgnore) => [];
        protected override ulong FindFunctionThisIsAThunkOf(ulong thunkPtr, bool prioritiseCall = false) => 0;
        protected override int GetCallerCount(ulong toWhere) => 0;
    }
}
