using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using GitSona.App.ViewModels;
using GitSona.App.Views;

namespace GitSona.App.Services;

/// <summary>Small modal helpers. Dialogs are built in code to keep them out of the way of the page views.</summary>
internal static class Dialogs
{
    public static Window? Owner =>
        (Application.Current as App)?.MainWindowInstance;

    public static async Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", bool destructive = false)
    {
        var result = false;
        var confirm = new Button { Content = confirmText, MinWidth = 90 };
        if (destructive) confirm.Classes.Add("destructive");
        else confirm.Classes.Add("accent");
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };

        var window = Build(title, message, confirm, cancel);
        confirm.Click += (_, _) => { result = true; window.Close(); };
        cancel.Click += (_, _) => window.Close();
        await ShowAsync(window);
        return result;
    }

    public static async Task MessageAsync(string title, string message)
    {
        var ok = new Button { Content = "OK", MinWidth = 90 };
        ok.Classes.Add("accent");
        var window = Build(title, message, ok);
        ok.Click += (_, _) => window.Close();
        await ShowAsync(window);
    }

    public static async Task<string?> PickFolderAsync(string title)
    {
        if (Owner is not { } owner) return null;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public static async Task<bool> ShowAccountEditorAsync(AccountEditorViewModel viewModel)
    {
        var window = new AccountEditorWindow { DataContext = viewModel };
        var saved = false;
        viewModel.CloseRequested += ok => { saved = ok; window.Close(); };
        await ShowAsync(window);
        viewModel.ClearSecrets();
        return saved;
    }

    public static async Task OpenUriAsync(string uri)
    {
        if (Owner is { } owner) await owner.Launcher.LaunchUriAsync(new Uri(uri));
    }

    public static async Task OpenFolderAsync(string path)
    {
        if (Owner is { } owner) await owner.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
    }

    static Window Build(string title, string message, params Button[] buttons)
    {
        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var b in buttons) buttonRow.Children.Add(b);

        return new Window
        {
            Title = title,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 18,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    buttonRow,
                },
            },
        };
    }

    static async Task ShowAsync(Window window)
    {
        if (Owner is { IsVisible: true } owner)
        {
            await window.ShowDialog(owner);
            return;
        }

        // The main window is hidden in the tray; show the dialog on its own.
        var closed = new TaskCompletionSource();
        window.Closed += (_, _) => closed.TrySetResult();
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.Show();
        await closed.Task;
    }
}
