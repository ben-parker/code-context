using System.Text.Json.Serialization;

namespace CodeContext.Core.Instances
{
    [JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(InstanceRegistryDocument))]
    [JsonSerializable(typeof(InstanceRecord))]
    [JsonSerializable(typeof(List<InstanceRecord>))]
    public partial class InstanceRegistryJsonContext : JsonSerializerContext
    {
    }
}
