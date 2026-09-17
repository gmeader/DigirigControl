namespace DigiRigControlCenter.Models;

public sealed class AudioEndpointInfo
{
    public string Name { get; init; } = "";
    public string Id { get; init; } = "";
    public bool IsInput { get; init; }
    public bool IsDigiRigCandidate { get; init; }
    public bool IsDefault { get; init; }
    public string RoleText { get; init; } = "";
    public string SampleFormat { get; set; } = "Unknown";
    public float Volume { get; set; }
    public bool Muted { get; set; }
    public bool EnhancementsDisabled { get; set; }
    public bool AgcKnown { get; set; }
    public bool AgcDisabled { get; set; }
}
