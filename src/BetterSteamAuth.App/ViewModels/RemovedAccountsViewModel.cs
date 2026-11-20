using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using BetterSteamAuth.App.Resources;
using BetterSteamAuth.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterSteamAuth.App.ViewModels;

/// <summary>Settings → Advanced → Removed accounts.</summary>
public sealed partial class RemovedAccountsViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly Vault _vault;

    public RemovedAccountsViewModel(ShellViewModel shell)
    {
        _shell = shell;
        _vault = shell.Vault ?? throw new InvalidOperationException("No vault is open.");
        Load();
    }

    public ObservableCollection<RemovedItemViewModel> Items { get; } = [];

    public bool IsEmpty => Items.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    [RelayCommand]
    private void Back() => _shell.ShowSettings();

    [RelayCommand]
    private void Restore(RemovedItemViewModel item)
    {
        try
        {
            _vault.RestoreRemoved(item.Entry);
            _shell.ShowMain(item.Entry.SteamId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or DuplicateAccountException)
        {
            Error = Strings.RestoreRemovedFailed;
            Load();
        }
    }

    [RelayCommand]
    private void AskDelete(RemovedItemViewModel item)
    {
        foreach (var other in Items)
        {
            other.IsConfirmingDelete = false;
        }

        item.IsConfirmingDelete = true;
    }

    [RelayCommand]
    private void ConfirmDelete(RemovedItemViewModel item)
    {
        try
        {
            _vault.DeleteRemoved(item.Entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = Strings.DeleteRemovedFailed;
        }

        Load();
    }

    private void Load()
    {
        Items.Clear();
        foreach (var entry in _vault.ListRemoved())
        {
            Items.Add(new RemovedItemViewModel(entry, _vault.Contains(entry.SteamId)));
        }

        OnPropertyChanged(nameof(IsEmpty));
    }
}

/// <summary>One removed account: name, why and when, and what can be done with it.</summary>
public sealed partial class RemovedItemViewModel : ObservableObject
{
    private static readonly CompositeFormat DeleteTitleFormat = CompositeFormat.Parse(Strings.DeleteRemovedFormat);

    public RemovedItemViewModel(RemovedEntry entry, bool inApp)
    {
        Entry = entry;
        Name = entry.AccountName ?? entry.SteamId.ToString(CultureInfo.InvariantCulture);
        string reason = entry.Reason switch
        {
            RemovalReason.LinkCancelled => Strings.RemovedReasonLinkCancelled,
            RemovalReason.AuthenticatorRemoved => Strings.RemovedReasonAuthenticator,
            _ => Strings.RemovedReasonRemoved,
        };
        Detail = reason + " · " + ConfirmationItemViewModel.Age(entry.RemovedAt);
        Status = !entry.CanRead ? Strings.RemovedCantRead
            : entry.Reason == RemovalReason.AuthenticatorRemoved ? Strings.RemovedCodesDead
            : inApp ? Strings.RemovedAlreadyInApp
            : null;
        DeleteTitle = string.Format(CultureInfo.CurrentCulture, DeleteTitleFormat, Name);
    }

    public RemovedEntry Entry { get; }

    public string Name { get; }

    public string Detail { get; }

    /// <summary>Why Restore isn't offered; null when it is.</summary>
    public string? Status { get; }

    public bool CanRestore => Status is null;

    public bool HasStatus => Status is not null;

    public string DeleteTitle { get; }

    [ObservableProperty] public partial bool IsConfirmingDelete { get; set; }

    [RelayCommand]
    private void CancelDelete() => IsConfirmingDelete = false;
}
