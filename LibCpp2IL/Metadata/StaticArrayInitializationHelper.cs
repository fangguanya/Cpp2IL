using System;
using System.Globalization;
using System.IO;

namespace LibCpp2IL.Metadata;

/// <summary>统一静态初始化长度依据和原始数据区段边界校验。</summary>
public static class StaticArrayInitializationHelper
{
    public static int ResolveLength(string? typeName, int nativeSize)
    {
        const string prefix = "__StaticArrayInitTypeSize=";
        var length = nativeSize;
        // 复用编译器生成的长度声明；不按业务字段名或地址决定内容。
        if (typeName?.StartsWith(prefix, StringComparison.Ordinal) == true)
        {
            if (!int.TryParse(typeName.Substring(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out length))
                throw new InvalidDataException("静态初始化类型的长度声明无效。");
        }
        if (length <= 0)
            throw new InvalidDataException("静态初始化缺少正长度依据。");
        return length;
    }

    public static long ResolvePointer(int sectionOffset, int sectionSize, int dataIndex, int length, long sourceLength)
    {
        // 先提升位宽，避免求和溢出把越界误判为合法区段。
        if (sectionOffset < 0 || sectionSize < 0 || dataIndex < 0 || length <= 0 ||
            (long)dataIndex + length > sectionSize || (long)sectionOffset + sectionSize > sourceLength)
            throw new InvalidDataException("静态初始化读取超出原始默认值数据区段。");
        return (long)sectionOffset + dataIndex;
    }
}
