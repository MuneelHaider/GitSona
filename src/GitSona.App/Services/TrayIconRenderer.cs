using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using GitSona.Core;

namespace GitSona.App.Services;

/// <summary>Draws the tray icon: the active account's initials on a provider-coloured disc.</summary>
internal static class TrayIconRenderer
{
    public static WindowIcon Render(Account? account)
    {
        const int size = 64;
        var (background, text) = account is null
            ? (Color.Parse("#6B7280"), "?")
            : (ProviderColor(account.Provider), Initials(account.Label));

        var disc = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2.0),
            Background = new SolidColorBrush(background),
            Child = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontSize = text.Length > 1 ? 26 : 32,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        disc.Measure(new Size(size, size));
        disc.Arrange(new Rect(0, 0, size, size));

        var bitmap = new RenderTargetBitmap(new PixelSize(size, size));
        bitmap.Render(disc);
        return new WindowIcon(bitmap);
    }

    public static string Initials(string label)
    {
        var words = label.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries);
        return words.Length switch
        {
            0 => "?",
            1 => words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant(),
            _ => string.Concat(words[0][0], words[1][0]).ToUpperInvariant(),
        };
    }

    static Color ProviderColor(Provider provider) => provider switch
    {
        Provider.GitHub => Color.Parse("#24292F"),
        Provider.GitLab => Color.Parse("#E24329"),
        _ => Color.Parse("#2563EB"),
    };
}
