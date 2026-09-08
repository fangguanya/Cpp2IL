using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Model.CustomAttributes;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.Utils;

/// <summary>记录既有特性解析器的完整消费状态，不将解析失败变成空特性集。</summary>
internal static class MetadataAttributeDecodeHelper
{
    internal static void Write(Utf8JsonWriter writer, byte[] bytes, ApplicationAnalysisContext application)
    {
        using var stream = new MemoryStream(bytes, false);
        List<AnalyzedCustomAttribute> attributes;
        try
        {
            attributes = V29AttributeUtils.ReadAttributeBlob(stream, application);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            writer.WriteString("decodeStatus", "unresolved");
            writer.WriteNumber("failurePosition", stream.Position);
            writer.WriteString("decodeError", error.ToString());
            return;
        }
        writer.WriteString("decodeStatus", "decoded");
        writer.WriteNumber("consumedBytes", stream.Position);
        writer.WriteStartArray("decodedAttributes");
        foreach (var attribute in attributes)
        {
            writer.WriteStartObject();
            writer.WriteNumber("constructorToken", attribute.Constructor.Token);
            writer.WriteString("constructorAssembly", attribute.Constructor.CustomAttributeAssembly.Name);
            writer.WriteNumber("constructorArguments", attribute.ConstructorParameters.Count);
            writer.WriteNumber("namedFields", attribute.Fields.Count);
            writer.WriteNumber("namedProperties", attribute.Properties.Count);
            writer.WriteBoolean("suitableForEmission", attribute.IsSuitableForEmission);
            writer.WriteStartArray("arguments");
            foreach (var parameter in attribute.ConstructorParameters)
                WriteParameter(writer, parameter);
            writer.WriteEndArray();
            writer.WriteStartArray("fields");
            foreach (var field in attribute.Fields)
            {
                writer.WriteStartObject();
                WriteMember(writer, field.Field);
                writer.WritePropertyName("argument");
                WriteParameter(writer, field.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("properties");
            foreach (var property in attribute.Properties)
            {
                writer.WriteStartObject();
                WriteMember(writer, property.Property);
                writer.WritePropertyName("argument");
                WriteParameter(writer, property.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteMember(Utf8JsonWriter writer, HasCustomAttributes member)
    {
        writer.WriteNumber("memberToken", member.Token);
        writer.WriteString("memberAssembly", member.CustomAttributeAssembly.Name);
    }

    private static void WriteRawType(Utf8JsonWriter writer, Il2CppType? type)
    {
        if (type is null)
            writer.WriteNull("rawType");
        else
        {
            writer.WriteStartObject("rawType");
            writer.WriteNumber("data", type.Datapoint);
            writer.WriteNumber("bits", type.Bits);
            writer.WriteString("kind", type.Type.ToString());
            writer.WriteEndObject();
        }
    }

    internal static void WriteParameter(Utf8JsonWriter writer, BaseCustomAttributeParameter parameter)
    {
        writer.WriteStartObject();
        writer.WriteString("parameterKind", parameter.Kind.ToString());
        writer.WriteNumber("index", parameter.Index);
        switch (parameter)
        {
            case CustomAttributePrimitiveParameter primitive:
                writer.WriteString("shape", "primitive");
                writer.WriteString("type", primitive.PrimitiveType.ToString());
                MetadataDefaultValueHelper.Write(writer, () => primitive.PrimitiveValue);
                break;
            case CustomAttributeEnumParameter enumeration:
                writer.WriteString("shape", "enum");
                WriteRawType(writer, enumeration.EnumType);
                writer.WritePropertyName("underlying");
                WriteParameter(writer, enumeration.UnderlyingPrimitiveParameter);
                break;
            case CustomAttributeArrayParameter array:
                writer.WriteString("shape", "array");
                writer.WriteString("elementType", array.ArrType.ToString());
                writer.WriteBoolean("isNull", array.IsNullArray);
                WriteRawType(writer, array.EnumType);
                writer.WriteStartArray("elements");
                foreach (var element in array.ArrayElements)
                    WriteParameter(writer, element);
                writer.WriteEndArray();
                break;
            case CustomAttributeTypeParameter type:
                writer.WriteString("shape", "type");
                WriteRawType(writer, type.RawType);
                break;
            case CustomAttributeNullParameter:
                writer.WriteString("shape", "null");
                break;
            default:
                throw new InvalidDataException("特性参数缺少已定义的原始事实投影。");
        }
        writer.WriteEndObject();
    }
}
