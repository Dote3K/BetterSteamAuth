using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using BetterSteamAuth.App.Resources;
using BetterSteamAuth.Core.Accounts;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterSteamAuth.App.ViewModels;

public sealed record ProxyPreviewLine(string Text, bool IsValid, bool IsDuplicate)
{
    public bool IsError => !IsValid && !IsDuplicate;
}

/// <summary>
/// The paste box for proxies, one per line, with a live preview. Used by the Proxies page and the
/// Connection step of import.
/// </summary>
public sealed partial class ProxyPasteViewModel : ObservableObject
{
    private static readonly CompositeFormat UnreadableFormat = CompositeFormat.Parse(Strings.LineUnreadableFormat);
    private static readonly CompositeFormat DuplicateFormat = CompositeFormat.Parse(Strings.LineDuplicateFormat);

    private readonly Func<IEnumerable<ProxySettings>> _existing;
    private List<ProxySettings> _proxies = [];

    /// <param name="existing">Proxies already in the pool; lines that match one are skipped.</param>
    public ProxyPasteViewModel(Func<IEnumerable<ProxySettings>> existing)
    {
        _existing = existing;
    }

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    public ObservableCollection<ProxyPreviewLine> Preview { get; } = [];

    /// <summary>New, readable proxies in the order they were pasted.</summary>
    public IReadOnlyList<ProxySettings> Proxies => _proxies;

    [ObservableProperty]
    public partial int Count { get; private set; }

    [ObservableProperty]
    public partial int ErrorCount { get; private set; }

    public void Clear()
    {
        Text = "";
        Parse();
    }

    partial void OnTextChanged(string value) => Parse();

    private void Parse()
    {
        var existing = _existing().Select(Key).ToHashSet();
        var parsed = new List<ProxySettings>();
        int errors = 0;
        Preview.Clear();
        string[] lines = (Text ?? "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            int lineNumber = i + 1;
            if (!ProxyParser.TryParse(lines[i], out var proxy) || proxy is null)
            {
                errors++;
                Preview.Add(new ProxyPreviewLine(string.Format(CultureInfo.CurrentCulture, UnreadableFormat, lineNumber), false, false));
            }
            else if (!existing.Add(Key(proxy)))
            {
                Preview.Add(new ProxyPreviewLine(string.Format(CultureInfo.CurrentCulture, DuplicateFormat, lineNumber), false, true));
            }
            else
            {
                parsed.Add(proxy);
                Preview.Add(new ProxyPreviewLine(proxy + (proxy.Username is null ? "" : " · " + Strings.WithLogin), true, false));
            }
        }

        _proxies = parsed;
        ErrorCount = errors;
        Count = parsed.Count;
        OnPropertyChanged(nameof(Proxies));
    }

    private static (ProxyScheme, string, int, string?) Key(ProxySettings p) => (p.Scheme, p.Host, p.Port, p.Username);
}
