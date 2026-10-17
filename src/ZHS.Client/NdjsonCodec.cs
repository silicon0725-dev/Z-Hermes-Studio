using System.Text.Json;

namespace ZHS.Client;

/// <summary>NDJSON 帧编解码。策略与 ZCAC frames.ts 一致：按行分割、空行跳过、非 JSON 污染行跳过不中断流。</summary>
public static class NdjsonCodec
{
    /// <summary>编码一条请求帧（不含换行，由调用方追加）。</summary>
    public static string EncodeRequest(long id, string method, object? parameters)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            writer.WriteString("method", method);
            if (parameters is not null)
            {
                writer.WritePropertyName("params");
                JsonSerializer.Serialize(writer, parameters, ZHS.Protocol.Wire.ProtocolJson.Options);
            }
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>编码一条响应帧（应答服务端反向请求；id 原样回显，数字/字符串都支持）。</summary>
    public static string EncodeResponse(JsonElement id, object? result, (int Code, string Message)? error)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("id");
            id.WriteTo(writer);
            if (error is { } failure)
            {
                writer.WritePropertyName("error");
                writer.WriteStartObject();
                writer.WriteNumber("code", failure.Code);
                writer.WriteString("message", failure.Message);
                writer.WriteEndObject();
            }
            else
            {
                writer.WritePropertyName("result");
                if (result is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    JsonSerializer.Serialize(writer, result, ZHS.Protocol.Wire.ProtocolJson.Options);
                }
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>从流缓冲解析全部完整帧；不完整的尾行保留在 Rest 中等下一次拼接。</summary>
    public static NdjsonDecodeResult DecodeFrames(string buffer)
    {
        var frames = new List<JsonDocument>();
        var rest = buffer;
        int newlineIndex;
        while ((newlineIndex = rest.IndexOf('\n')) != -1)
        {
            var line = rest.AsSpan(0, newlineIndex).Trim();
            rest = rest[(newlineIndex + 1)..];
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                frames.Add(JsonDocument.Parse(line.ToString()));
            }
            catch (JsonException)
            {
                // 子进程日志污染行：跳过，不中断流
            }
        }

        return new NdjsonDecodeResult(frames, rest);
    }
}

public readonly record struct NdjsonDecodeResult(IReadOnlyList<JsonDocument> Frames, string Rest);
