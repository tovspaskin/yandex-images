using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using YandexImages.Core;

namespace YandexImages;

public partial class MainWindow : Window
{
    private readonly YandexDiskClient _client = new();
    private CancellationTokenSource? _operation;
    private IReadOnlyList<RemoteImage> _images = Array.Empty<RemoteImage>();
    private DiskSource? _scannedSource;
    private bool _scannedRecursive;
    private bool _closeAfterStop;

    public MainWindow()
    {
        InitializeComponent();
        if (Screens.Primary is { } screen)
        {
            var availableHeight = Math.Max(240, screen.WorkingArea.Height / screen.Scaling - 40);
            var availableWidth = Math.Max(360, screen.WorkingArea.Width / screen.Scaling - 40);
            MinHeight = Math.Min(MinHeight, availableHeight);
            MinWidth = Math.Min(MinWidth, availableWidth);
            Height = Math.Min(850, availableHeight);
            Width = Math.Min(1040, availableWidth);
        }
        DestinationBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Яндекс Фото");
    }

    private async void Check_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        await ScanAsync(DiskSource.Parse(SourceBox.Text ?? ""), RecursiveBox.IsChecked == true, token);
    });

    private async void Download_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        var source = DiskSource.Parse(SourceBox.Text ?? "");
        if (string.IsNullOrWhiteSpace(DestinationBox.Text))
            throw new ArgumentException("Выберите папку, в которую нужно сохранить изображения.");
        var destination = Path.GetFullPath(Environment.ExpandEnvironmentVariables(DestinationBox.Text!.Trim()));
        var recursive = RecursiveBox.IsChecked == true;
        if (_scannedSource != source || recursive != _scannedRecursive)
            await ScanAsync(source, recursive, token);
        if (_images.Count == 0) return;

        var parallelism = ParallelBox.SelectedItem is ComboBoxItem selected && int.TryParse(selected.Content?.ToString(), out var count) ? count : 4;
        StatusText.Text = "Скачивание оригиналов…";
        DownloadProgress.IsIndeterminate = false;
        DownloadProgress.Value = 0;
        var acceptingProgress = true;
        var progress = new Progress<DownloadUpdate>(update =>
        {
            if (!acceptingProgress) return;
            DownloadProgress.Value = update.Total == 0 ? 0 : 100d * update.Completed / update.Total;
            StatusText.Text = $"Обработано {update.Completed} из {update.Total}";
            DetailText.Text = $"Скачано: {update.Downloaded} · Уже есть: {update.Skipped} · Ошибок: {update.Failed} · {FormatSize(update.BytesReceived)} получено";
            ToolTip.SetTip(StatusText, update.CurrentFile);
        });
        DownloadResult result;
        try { result = await new ImageDownloader(_client).DownloadAsync(source, _images, destination, parallelism, progress, token); }
        finally { acceptingProgress = false; }
        DownloadProgress.Value = 100;
        StatusText.Text = result.Failed == 0 ? "Готово — изображения сохранены" : "Загрузка завершена с ошибками";
        DetailText.Text = $"Скачано: {result.Downloaded} · Уже есть: {result.Skipped} · Ошибок: {result.Failed}. {destination}";
        if (result.Errors.Count > 0) ShowErrors(string.Join(Environment.NewLine, result.Errors));
    });

    private async Task ScanAsync(DiskSource source, bool recursive, CancellationToken token)
    {
        _scannedSource = null;
        _images = Array.Empty<RemoteImage>();
        FilesList.ItemsSource = null;
        EmptyText.IsVisible = true;
        EmptyText.Text = "Проверяем папку на Яндекс Диске…";
        SummaryText.Text = "Поиск изображений…";
        StatusText.Text = "Получение списка изображений…";
        DetailText.Text = recursive ? "Проверяем папку и все вложенные папки." : "Проверяем только выбранную папку.";
        DownloadProgress.IsIndeterminate = true;
        var images = await _client.ListImagesAsync(source, recursive, token);
        _images = images;
        _scannedSource = source;
        _scannedRecursive = recursive;
        FilesList.ItemsSource = images.Select(image => new FileRow(image.Name, FolderOf(image.RelativePath), FormatSize(image.Size))).ToArray();
        SummaryText.Text = $"Файлов: {images.Count} · {FormatSize(images.Sum(image => image.Size))}";
        EmptyText.Text = "В этой папке не найдено изображений.";
        EmptyText.IsVisible = images.Count == 0;
        DownloadProgress.IsIndeterminate = false;
        DownloadProgress.Value = 0;
        StatusText.Text = images.Count == 0 ? "Изображений нет" : $"Найдено изображений: {images.Count}";
        DetailText.Text = "Готовые файлы пропускаются. Файлы с другим содержимым сохраняются рядом.";
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_operation is not null) return;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        SetBusy(true);
        ErrorBox.IsVisible = false;
        ErrorBox.Text = "";
        try
        {
            await action(operation.Token);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            StatusText.Text = "Остановлено";
            DetailText.Text = "Готовые файлы сохранены. Операцию можно запустить снова.";
            if (_scannedSource is null) EmptyText.Text = "Проверка остановлена. Можно запустить её снова.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось завершить операцию";
            ShowErrors(ex.Message);
            if (_scannedSource is null)
            {
                SummaryText.Text = "Не удалось проверить папку";
                EmptyText.Text = "Проверьте ссылку и подключение к интернету, затем попробуйте ещё раз.";
            }
        }
        finally
        {
            _operation = null;
            DownloadProgress.IsIndeterminate = false;
            SetBusy(false);
            if (_closeAfterStop) Close();
        }
    }

    private void SetBusy(bool busy)
    {
        SourceBox.IsEnabled = DestinationBox.IsEnabled = PasteButton.IsEnabled = BrowseButton.IsEnabled = !busy;
        RecursiveBox.IsEnabled = ParallelBox.IsEnabled = CheckButton.IsEnabled = DownloadButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
    }

    private void ShowErrors(string text)
    {
        ErrorBox.Text = text;
        ErrorBox.IsVisible = true;
    }

    private async void Paste_Click(object? sender, RoutedEventArgs e)
    {
        try { if (Clipboard is { } clipboard && await clipboard.TryGetTextAsync() is { } text) SourceBox.Text = text.Trim(); }
        catch { ShowErrors($"Буфер обмена занят. Попробуйте ещё раз или вставьте ссылку с помощью {(OperatingSystem.IsMacOS() ? "⌘V" : "Ctrl+V")}."); }
    }

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!StorageProvider.CanPickFolder)
            {
                ShowErrors("Выбор папки в этой среде недоступен. Введите путь вручную.");
                return;
            }
            var initial = Directory.Exists(DestinationBox.Text)
                ? await StorageProvider.TryGetFolderFromPathAsync(new Uri(Path.GetFullPath(DestinationBox.Text!))) : null;
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            { Title = "Куда сохранить изображения", AllowMultiple = false, SuggestedStartLocation = initial });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } local) DestinationBox.Text = local;
        }
        catch (Exception ex) { ShowErrors(ex.Message); }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(DestinationBox.Text))
            {
                ShowErrors("Выберите папку сохранения.");
                return;
            }
            var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(DestinationBox.Text!.Trim()));
            if (!Directory.Exists(path)) { ShowErrors("Папка ещё не создана. Она появится при скачивании изображений."); return; }
            var command = OperatingSystem.IsWindows() ? "explorer.exe" : OperatingSystem.IsMacOS() ? "open" : "xdg-open";
            var info = new ProcessStartInfo(command) { UseShellExecute = false };
            info.ArgumentList.Add(path);
            Process.Start(info);
        }
        catch (Exception ex) { ShowErrors(ex.Message); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _operation?.Cancel();
        CancelButton.IsEnabled = false;
        StatusText.Text = "Останавливаем операцию…";
    }

    private void Window_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (_operation is not null)
        {
            e.Cancel = true;
            _closeAfterStop = true;
            _operation.Cancel();
        }
        else _client.Dispose();
    }

    private static string FolderOf(string path)
    {
        var separator = path.LastIndexOfAny(['/', '\\']);
        return separator < 0 ? "—" : path[..separator];
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double size = Math.Max(0, bytes);
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.#} {units[unit]}";
    }

    private sealed record FileRow(string Name, string Folder, string Size);
}
