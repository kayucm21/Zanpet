using ZapretUI.Mvvm;

namespace ZapretUI.Models;

public enum IpRuleKind
{
    /// <summary>Do not desync — games / direct servers.</summary>
    Exclude,
    /// <summary>Actively desync traffic to this IP (from domain DNS).</summary>
    Bypass,
}

public sealed class CustomIpRule : ObservableObject
{
    public required string Cidr { get; init; }
    public IpRuleKind Kind { get; init; }
    public string? Label { get; init; }

    private long _pingMs;
    public long PingMs
    {
        get => _pingMs;
        set { if (SetField(ref _pingMs, value)) OnPropertyChanged(nameof(Subtitle)); }
    }

    public string Subtitle => Kind switch
    {
        IpRuleKind.Exclude => $"исключение · ping {PingMs} ms",
        _ => $"обход · ping {PingMs} ms",
    };
}
