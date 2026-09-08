using System;
using System.Text.Json;

namespace Cpp2IL.Core.Utils;

/// <summary>投影唯一解码器的结果，保留真实空值、不支持状态和浮点原始位。</summary>
internal static class MetadataDefaultValueHelper
{
    internal static void Write(Utf8JsonWriter writer, Func<object?> decode)
    {
        object? value;
        try
        {
            value = decode();
        }
        catch (NotSupportedException error)
        {
            writer.WriteString("decodeStatus", "unsupported");
            writer.WriteString("decodeError", error.Message);
            return;
        }

        writer.WriteString("decodeStatus", value is null ? "null" : "decoded");
        switch (value)
        {
            case null: writer.WriteNull("value"); break;
            case bool item: writer.WriteBoolean("value", item); break;
            case byte item: writer.WriteNumber("value", item); break;
            case sbyte item: writer.WriteNumber("value", item); break;
            case short item: writer.WriteNumber("value", item); break;
            case ushort item: writer.WriteNumber("value", item); break;
            case int item: writer.WriteNumber("value", item); break;
            case uint item: writer.WriteNumber("value", item); break;
            case long item: writer.WriteNumber("value", item); break;
            case ulong item: writer.WriteNumber("value", item); break;
            // 使用 UTF-16 码元数值，保留独立代理码元；字符串由既有解码器提供。
            case char item: writer.WriteNumber("utf16CodeUnit", item); break;
            case string item: writer.WriteString("value", item); break;
            // JSON 数字不表示 NaN/无穷且可能丢失负零；始终保存 IEEE 原始位。
            case float item: writer.WriteNumber("float32Bits", BitConverter.ToInt32(BitConverter.GetBytes(item), 0)); break;
            case double item: writer.WriteNumber("float64Bits", BitConverter.DoubleToInt64Bits(item)); break;
            default: throw new InvalidOperationException("默认值解码结果未定义无损投影。");
        }
    }
}
