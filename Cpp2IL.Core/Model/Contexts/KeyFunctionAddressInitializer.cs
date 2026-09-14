using System;
using System.Threading;
using Cpp2IL.Core.Il2CppApiFunctions;

namespace Cpp2IL.Core.Model.Contexts;

/// <summary>
/// 以事务方式初始化并发布关键函数地址。构造、扫描与发布属于同一次操作，失败候选永不进入共享状态。
/// </summary>
internal sealed class KeyFunctionAddressInitializer
{
    private readonly object gate = new();
    private BaseKeyFunctionAddresses? value;
    private int initializingThreadId = -1;

    internal BaseKeyFunctionAddresses GetOrInitialize(
        Func<BaseKeyFunctionAddresses> create,
        Action<BaseKeyFunctionAddresses> initialize)
    {
        if (create == null)
            throw new ArgumentNullException(nameof(create));
        if (initialize == null)
            throw new ArgumentNullException(nameof(initialize));

        lock (gate)
        {
            if (value != null)
                return value;

            var currentThreadId = Thread.CurrentThread.ManagedThreadId;
            if (initializingThreadId == currentThreadId)
                throw new InvalidOperationException("关键函数地址初始化发生同线程重入，候选尚未完成，禁止提前发布。");

            initializingThreadId = currentThreadId;
            try
            {
                var candidate = create()
                    ?? throw new InvalidOperationException("指令集返回了空的关键函数地址候选。");
                initialize(candidate);
                value = candidate;
                return candidate;
            }
            finally
            {
                initializingThreadId = -1;
            }
        }
    }
}
