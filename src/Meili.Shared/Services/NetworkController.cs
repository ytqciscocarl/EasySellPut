using Meili.Shared.ViewModels;
using Meili.Core.Settings;
using Meili.Data;
using Meili.Data.Http;

namespace Meili.Shared.Services;

/// <summary>Applies the proxy settings to the data layer and describes the effective network path in the current language.</summary>
public sealed class NetworkController : INetworkController
{
    private readonly DataServices _data;
    private readonly SettingsStore _store;
    private readonly LocalizationService _loc;
    private readonly ShellViewModel _shell;

    public NetworkController(DataServices data, SettingsStore store, LocalizationService loc, ShellViewModel shell)
    {
        _data = data; _store = store; _loc = loc; _shell = shell;
        _loc.LanguageChanged += (_, _) => Publish();
    }

    public string EffectiveDescription => Describe(_data.Gateway.Proxy);

    public bool NoProxyDetected => _data.Gateway.Proxy is { Mode: ProxyMode.AutoDetect, ProxyUri: null };

    public async Task<string> ReconfigureAsync()
    {
        await _data.Gateway.ReconfigureAsync(_store.Current.Network).ConfigureAwait(true);
        _data.Health.Reset();
        _data.Router.ForgetMisses();
        _data.Cache.Clear();
        Publish();
        return EffectiveDescription;
    }

    private void Publish()
    {
        var text = EffectiveDescription;
        Ui.Platform.Run(() => _shell.NetworkDescription = text);
    }

    private string Describe(ProxyResolution p) => p.Mode switch
    {
        ProxyMode.Direct => _loc["Net_Direct"],
        ProxyMode.Manual when p.ProxyUri is not null => $"{_loc["Net_Manual"]} {p.ProxyUri.Authority}",
        ProxyMode.Manual => _loc["Net_ManualInvalid"],
        ProxyMode.System => _loc["Net_System"],
        _ when p.ProxyUri is not null => $"{_loc["Net_Auto"]} {p.ProxyUri.Authority} ({Origin(p.Origin)})",
        _ => _loc["Net_AutoNone"],
    };

    private string Origin(string origin) => origin switch
    {
        "Windows proxy setting" => _loc["Net_FromWindows"],
        "macOS proxy setting" => _loc["Net_FromMac"],
        _ => origin,
    };
}