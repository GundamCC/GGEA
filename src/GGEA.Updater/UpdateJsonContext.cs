using System.Text.Json.Serialization;

namespace GGEA.Updater;

/// <summary>独立进程持久化协议的编译期元数据，保持旧版驼峰字段与缩进格式，避免裁剪反射依赖。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(UpdateRequest))]
[JsonSerializable(typeof(UpdateJournal))]
[JsonSerializable(typeof(int))]
internal partial class UpdateJsonContext : JsonSerializerContext;
