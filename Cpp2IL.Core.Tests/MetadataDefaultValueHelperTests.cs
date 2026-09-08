using System;
using System.IO;
using System.Text.Json;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Tests;

public class MetadataDefaultValueHelperTests
{
    private static JsonDocument Project(Func<object?> decode)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            MetadataDefaultValueHelper.Write(writer, decode);
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray());
    }

    [Test]
    [Category("基本功能")]
    public void DecodesExactlyOnceAndPreservesIntegralExtremes()
    {
        object[] values = [true, byte.MaxValue, sbyte.MinValue, short.MinValue, ushort.MaxValue,
            int.MinValue, uint.MaxValue, long.MinValue, ulong.MaxValue, "", "中文\0字符"];
        foreach (var value in values)
        {
            var calls = 0;
            using var document = Project(() => { calls++; return value; });
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(document.RootElement.GetProperty("decodeStatus").GetString(), Is.EqualTo("decoded"));
            var actual = document.RootElement.GetProperty("value");
            if (value is string text)
                Assert.That(actual.GetString(), Is.EqualTo(text));
            else if (value is bool boolean)
                Assert.That(actual.GetBoolean(), Is.EqualTo(boolean));
            else
                Assert.That(actual.GetDecimal(), Is.EqualTo(Convert.ToDecimal(value)));
        }
    }

    [Test]
    [Category("边界值")]
    public void KeepsNullDistinctFromUnsupported()
    {
        using var empty = Project(() => null);
        using var unknown = Project(() => throw new NotSupportedException("原始类型未解码"));
        Assert.That(empty.RootElement.GetProperty("decodeStatus").GetString(), Is.EqualTo("null"));
        Assert.That(empty.RootElement.GetProperty("value").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(unknown.RootElement.GetProperty("decodeStatus").GetString(), Is.EqualTo("unsupported"));
        Assert.That(unknown.RootElement.TryGetProperty("value", out _), Is.False);
    }

    [Test]
    [Category("边界值")]
    public void PreservesFloatingBitsAndLoneSurrogate()
    {
        foreach (var bits in new[] { int.MinValue, 0x7F800000, 0x7FC00001 })
        {
            using var item = Project(() => BitConverter.ToSingle(BitConverter.GetBytes(bits), 0));
            Assert.That(item.RootElement.GetProperty("float32Bits").GetInt32(), Is.EqualTo(bits));
        }
        foreach (var bits in new[] { long.MinValue, 0x7FF0000000000000L, 0x7FF8000000000001L })
        {
            using var item = Project(() => BitConverter.Int64BitsToDouble(bits));
            Assert.That(item.RootElement.GetProperty("float64Bits").GetInt64(), Is.EqualTo(bits));
        }
        using var surrogate = Project(() => '\uD800');
        Assert.That(surrogate.RootElement.GetProperty("utf16CodeUnit").GetInt32(), Is.EqualTo(0xD800));
    }

    [Test]
    [Category("异常输入")]
    public void DoesNotHideCorruptionOrUnknownResultShapes()
    {
        Assert.Throws<InvalidDataException>(() => Project(() => throw new InvalidDataException("数据截断")));
        Assert.Throws<InvalidOperationException>(() => Project(() => new object()));
    }
}
