using Avalonia.Threading;
using BetterSteamAuth.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterSteamAuth.App.ViewModels;

/// <summary>
/// Shown instead of unlock when the data was saved by a newer version of the app. Nothing is opened or changed;
/// the only way forward is updating, which works without the data.
/// </summary>
public sealed partial class DataTooNewViewModel : ObservableObject, IDisposable
{
    private readonly ShellViewModel _shell;

    public DataTooNewViewModel(ShellViewModel shell)
    {
        _shell = shell;
        shell.Updater.StatusChanged += OnUpdaterChanged;
        _ = shell.Updater.CheckAsync();
    }

    public string? StatusText => SettingsViewModel.StatusText(_shell.Updater);

    public bool HasStatus => StatusText is not null;

    public bool IsUpdateReady => _shell.Updater.State == UpdateState.Ready;

    public bool CanCheck => !IsUpdateReady && _shell.Updater.State is not (UpdateState.Checking or UpdateState.Downloading);

    public void Dispose() => _shell.Updater.StatusChanged -= OnUpdaterChanged;

    [RelayCommand]
    private Task CheckForUpdatesAsync() => _shell.Updater.CheckAsync();

    [RelayCommand]
    private void RestartToUpdate() => _shell.RestartToUpdate();

    private void OnUpdaterChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(IsUpdateReady));
        OnPropertyChanged(nameof(CanCheck));
    });
}
