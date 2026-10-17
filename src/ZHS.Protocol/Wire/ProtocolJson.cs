using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZHS.Protocol.Wire;

/// <summary>
/// 线协议统一 JSON 选项：camelCase、大小写不敏感绑定（上游字段全部 camelCase）、
/// null 字段省略（实测 v3.15.3 的 Zod 校验拒绝显式 null：toolAllowlist expected array, received null）。
/// </summary>
public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
