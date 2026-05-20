

#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property PublishTrimmed=false


// 创建 options 实例
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Helper;

public static partial class Helpers
{

    private static readonly JsonSerializerOptions _jsonSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,


    };

    static Helpers()
    {
        _jsonSerializerOptions.Converters.Add(new DateTimeJsonConverter());
        _jsonSerializerOptions.Converters.Add(new DateTimeOffsetJsonConverter());
    }

    /// <summary>
    /// 序列化
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="obj"></param>
    /// <returns></returns>
    public static string JsonSerialize<T>(T obj)
    {

        return JsonSerializer.Serialize(obj, typeof(T), _jsonSerializerOptions);
    }

    /// <summary>
    /// 反序列化
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="json"></param>
    /// <returns></returns>
    public static T? JsonDeserialize<T>(string json)
    {

        return JsonSerializer.Deserialize<T>(json, _jsonSerializerOptions);
    }

    /// <summary>
    /// 反序列化
    /// </summary>
    /// <param name="json">json文本</param>
    /// <param name="type">类型</param>
    /// <returns></returns>
    public static object? JsonDeserialize(string json, Type type)
    {

        return JsonSerializer.Deserialize(json, type, _jsonSerializerOptions);
    }

    public static JsonSerializerOptions JsonOptions => _jsonSerializerOptions;
}

/// <summary>
/// DateTime 序列化转换器 yyyy-MM-dd HH:mm:ss
/// </summary>
public class DateTimeJsonConverter : JsonConverter<DateTime>
{
    private readonly string _format = "yyyy-MM-dd HH:mm:ss";
    /// <summary>
    /// 默认使用 yyyy-MM-dd HH:mm:ss
    /// </summary>
    public DateTimeJsonConverter()
    {
    }
    /// <summary>
    /// 使用 format 进行格式化
    /// </summary>
    /// <param name="format">格式化参数</param>
    public DateTimeJsonConverter(string format)
    {
        _format = format;
    }
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return DateTime.Parse(s: reader.GetString());
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString(_format));
    }
}

/// <summary>
/// DateTimeOffset 序列化对象
/// </summary>
public class DateTimeOffsetJsonConverter : JsonConverter<DateTimeOffset>
{
    private readonly string _format = "yyyy-MM-dd HH:mm:ss";
    /// <summary>
    /// 默认使用 yyyy-MM-dd HH:mm:ss序列化
    /// </summary>
    public DateTimeOffsetJsonConverter()
    {

    }
    /// <summary>
    /// 使用 format 序列化时间
    /// </summary>
    /// <param name="format">格式化参数</param>

    public DateTimeOffsetJsonConverter(string format)
    {
        _format = format;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset date, JsonSerializerOptions options)
    {
        writer.WriteStringValue(date.ToString(_format));
    }

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();

        if (string.IsNullOrEmpty(value))
            return DateTimeOffset.MinValue;

        if (DateTimeOffset.TryParse(value, out DateTimeOffset res))
            return res;

        if (DateTimeOffset.TryParse(value, null, DateTimeStyles.AssumeUniversal, out res))
            return res;

        if (DateTimeOffset.TryParse(value, null, DateTimeStyles.RoundtripKind, out res))
            return res;

        return DateTimeOffset.ParseExact(value, _format, null);
    }
}
