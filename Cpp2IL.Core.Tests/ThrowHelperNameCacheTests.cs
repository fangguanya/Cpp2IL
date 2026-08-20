using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cpp2IL.Core.Analysis;

namespace Cpp2IL.Core.Tests;

public class ThrowHelperNameCacheTests
{
    [Test]
    [Category("基本功能")]
    public void ResolveCachesNamedAndNullResultsExactlyOnce()
    {
        var cache = new ThrowHelperNameCache();
        var namedCalls = 0;
        var nullCalls = 0;

        Assert.That(cache.Resolve(0x1000, () =>
        {
            namedCalls++;
            return "NullReferenceException";
        }), Is.EqualTo("NullReferenceException"));
        Assert.That(cache.Resolve(0x1000, () => throw new AssertionException("命中缓存后不应再次解析")), Is.EqualTo("NullReferenceException"));

        Assert.That(cache.Resolve(0x2000, () =>
        {
            nullCalls++;
            return null;
        }), Is.Null);
        Assert.That(cache.Resolve(0x2000, () => throw new AssertionException("空结果同样必须缓存")), Is.Null);

        Assert.Multiple(() =>
        {
            Assert.That(namedCalls, Is.EqualTo(1));
            Assert.That(nullCalls, Is.EqualTo(1));
            Assert.That(cache.Count, Is.EqualTo(2));
            Assert.That(cache.TryGetValue(0x1000, out var cachedName), Is.True);
            Assert.That(cachedName, Is.EqualTo("NullReferenceException"));
            Assert.That(cache.TryGetValue(0x9999, out _), Is.False);
        });
    }

    [Test]
    [Category("边界值")]
    public void ResolveTerminatesMutualRecursionWithoutDuplicatingEntries()
    {
        var cache = new ThrowHelperNameCache();

        var resolved = cache.Resolve(0x1000, () =>
            cache.Resolve(0x2000, () =>
                cache.Resolve(0x1000, () => "不应到达")));

        Assert.Multiple(() =>
        {
            Assert.That(resolved, Is.Null);
            Assert.That(cache.Count, Is.EqualTo(2));
            Assert.That(cache.Resolve(0x1000, () => "不应重新解析"), Is.Null);
            Assert.That(cache.Resolve(0x2000, () => "不应重新解析"), Is.Null);
        });
    }

    [Test]
    [Category("基本功能")]
    public async Task ResolveSerializesConcurrentRequestsForTheSameAddress()
    {
        var cache = new ThrowHelperNameCache();
        using var factoryEntered = new ManualResetEventSlim(false);
        using var releaseFactory = new ManualResetEventSlim(false);
        var factoryCalls = 0;

        var tasks = Enumerable.Range(0, 64)
            .Select(_ => Task.Run(() => cache.Resolve(0x3000, () =>
            {
                Interlocked.Increment(ref factoryCalls);
                factoryEntered.Set();
                if (!releaseFactory.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("并发缓存测试等待释放超时");
                return "ArgumentException";
            })))
            .ToArray();

        Assert.That(factoryEntered.Wait(TimeSpan.FromSeconds(10)), Is.True, "解析工厂未按时启动");
        releaseFactory.Set();
        var results = await Task.WhenAll(tasks);

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.All.EqualTo("ArgumentException"));
            Assert.That(factoryCalls, Is.EqualTo(1));
            Assert.That(cache.Count, Is.EqualTo(1));
        });
    }

    [Test]
    [Category("异常输入")]
    public void ResolveRejectsNullFactoryAndAllowsRetryAfterFactoryFailure()
    {
        var cache = new ThrowHelperNameCache();

        Assert.Throws<ArgumentNullException>(() => cache.Resolve(0x4000, null!));
        Assert.Throws<InvalidOperationException>(() => cache.Resolve(0x4000, () => throw new InvalidOperationException("预期故障")));

        Assert.Multiple(() =>
        {
            Assert.That(cache.Count, Is.Zero);
            Assert.That(cache.Resolve(0x4000, () => "InvalidOperationException"), Is.EqualTo("InvalidOperationException"));
            Assert.That(cache.Count, Is.EqualTo(1));
        });
    }
}
