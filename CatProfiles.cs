using System.Text.Json.Serialization;

namespace DigiRigControlCenter;

public sealed class CatProfilesFile
{
    [JsonPropertyName("brands")] public List<CatBrandProfile> Brands { get; set; } = new();
}
public sealed class CatBrandProfile
{
    [JsonPropertyName("brand")] public string Brand { get; set; } = "";
    [JsonPropertyName("models")] public List<CatModelProfile> Models { get; set; } = new();
}
public sealed class CatModelProfile
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("CATprotocol")] public string CATprotocol { get; set; } = "none";
    [JsonPropertyName("civAddress")] public string CivAddress { get; set; } = "";
    [JsonPropertyName("pttMethod")] public string PttMethod { get; set; } = "rts";
    [JsonPropertyName("baud")] public int Baud { get; set; } = 9600;
    [JsonPropertyName("dataBits")] public int DataBits { get; set; } = 8;
    [JsonPropertyName("stopBits")] public int StopBits { get; set; } = 1;
    [JsonPropertyName("testCommand")] public string TestCommand { get; set; } = "ID;";
}
