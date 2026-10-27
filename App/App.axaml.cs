using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace YandexImages;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            if (desktop.Args?.Contains("--smoke-test", StringComparer.Ordinal) == true)
            {
                window.ShowActivated = false;
                window.Opened += (_, _) => DispatcherTimer.RunOnce(() => window.Close(), TimeSpan.FromMilliseconds(750));
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
