using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AvaloniaYuvPlayer;

public partial class MainWindow : Window
{
    string? _file;

    public MainWindow()
    {
        InitializeComponent();
    }

    async void OnOpenFile(object? sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog();
        dlg.Filters.Add(new FileDialogFilter { Name = "YUV", Extensions = { "yuv" } });
        var res = await dlg.ShowAsync(this);

        if (res?.Length > 0)
            _file = res[0];
    }

    void OnPlay(object? sender, RoutedEventArgs e)
    {
        if (_file == null) return;

        GlView.Start(
            _file,
            int.Parse(WidthBox.Text!),
            int.Parse(HeightBox.Text!),
            int.Parse(FpsBox.Text!)
        );
    }

    void OnStop(object? sender, RoutedEventArgs e)
    {
        GlView.Stop();
    }
}
