using System.Runtime.Versioning;
using System.Security;
using Avalonia.Threading;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace BetterSteamAuth.App.Services;

/// <summary>
/// Windows toast notifications for an unpackaged app. Windows shows them only for an AppUserModelID it knows: the
/// installed build's Start menu shortcut carries Velopack's, the portable build has none, so the ID is registered
/// under the current user's classes with a name and icon (no admin needed). Clicks are handled while the app runs.
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class WindowsToasts
{
    /// <summary>The ID Velopack gives the installed app's shortcut and process.</summary>
    public const string AppId = "velopack.BetterSteamAuth";

    private const string RegistryPath = @"Software\Classes\AppUserModelId\" + AppId;

    private readonly ToastNotifier _notifier;

    // Shown toasts stay referenced so their Activated handlers aren't collected.
    private readonly List<ToastNotification> _shown = [];

    public WindowsToasts(string displayName)
    {
        Register(displayName);
        _notifier = ToastNotificationManager.CreateToastNotifier(AppId);
    }

    public void Show(string title, string text, Action? onClick)
    {
        var xml = new XmlDocument();
        var toast = xml.CreateElement("toast");
        xml.AppendChild(toast);
        var visual = xml.CreateElement("visual");
        toast.AppendChild(visual);
        var binding = xml.CreateElement("binding");
        binding.SetAttribute("template", "ToastGeneric");
        visual.AppendChild(binding);
        foreach (string line in new[] { title, text })
        {
            var element = xml.CreateElement("text");
            element.InnerText = line;
            binding.AppendChild(element);
        }

        var notification = new ToastNotification(xml);
        if (onClick is not null)
        {
            notification.Activated += (_, _) => Dispatcher.UIThread.Post(onClick);
        }

        lock (_shown)
        {
            _shown.Add(notification);
            if (_shown.Count > 20)
            {
                _shown.RemoveAt(0);
            }
        }

        _notifier.Show(notification);
    }

    /// <summary>Removes the registration (uninstall).</summary>
    public static void Unregister()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(RegistryPath, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            // Nothing left to do on uninstall.
        }
    }

    private static void Register(string displayName)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            key.SetValue("DisplayName", displayName);
            string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(icon))
            {
                key.SetValue("IconUri", icon);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            // Without it, the installed build still shows toasts through its shortcut.
        }
    }
}
