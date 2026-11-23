using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using BetterSteamAuth.App.Resources;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterSteamAuth.App.ViewModels;

/// <summary>A day's entries in the full history.</summary>
public sealed class HistoryDay(string title, IEnumerable<HistoryItemViewModel> items)
{
    public string Title { get; } = title;

    public ObservableCollection<HistoryItemViewModel> Items { get; } = [.. items];
}

/// <summary>One account's full confirmation history, by day, newest first, loaded a page at a time.</summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private const int PageSize = 50;
    private static readonly CompositeFormat TitleFormat = CompositeFormat.Parse(Strings.HistoryTitleFormat);

    private readonly ShellViewModel _shell;
    private readonly ulong _steamId;
    private readonly Action _back;
    private Storage.HistoryEntry? _oldest;

    public HistoryViewModel(ShellViewModel shell, ulong steamId, string accountName, Action back)
    {
        _shell = shell;
        _steamId = steamId;
        _back = back;
        Title = string.Format(CultureInfo.CurrentCulture, TitleFormat, accountName);
        LoadPage();
        IsEmpty = Days.Count == 0;
    }

    public string Title { get; }

    public ObservableCollection<HistoryDay> Days { get; } = [];

    public bool IsEmpty { get; }

    [ObservableProperty]
    public partial bool HasMore { get; private set; }

    [RelayCommand]
    private void ShowMore() => LoadPage();

    [RelayCommand]
    private void Back() => _back();

    private void LoadPage()
    {
        if (_shell.Vault is not { } vault)
        {
            return;
        }

        var page = vault.ListHistory(_steamId, PageSize + 1, _oldest);
        HasMore = page.Count > PageSize;
        var items = page.Take(PageSize).Select(e => new HistoryItemViewModel(e, withDay: false)).ToList();
        if (items.Count == 0)
        {
            return;
        }

        _oldest = page[items.Count - 1];
        foreach (var group in items.GroupBy(i => i.LocalDate))
        {
            if (Days.Count > 0 && Days[^1].Items[0].LocalDate == group.Key)
            {
                foreach (var item in group)
                {
                    Days[^1].Items.Add(item);
                }
            }
            else
            {
                Days.Add(new HistoryDay(DayTitle(group.Key), group));
            }
        }
    }

    private static string DayTitle(DateTime day)
        => day == DateTime.Today ? Strings.Today
            : day == DateTime.Today.AddDays(-1) ? Strings.Yesterday
            : day.ToString("D", CultureInfo.CurrentCulture);
}
