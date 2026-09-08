using System;
using System.Security.Cryptography;

namespace LibCpp2IL;

/// <summary>加载时直接对同一份输入字节计算身份，不在输出阶段重新读取路径。</summary>
internal static class InputIdentityHelper
{
    internal static string ComputeSha256(byte[] bytes)
    {
        if (bytes == null)
            throw new ArgumentNullException(nameof(bytes));
        using var algorithm = SHA256.Create();
        return BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }
}
