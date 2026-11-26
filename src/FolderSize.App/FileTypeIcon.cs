using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FolderSize.App;

public sealed class FileTypeIcon : Image
{
    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(nameof(Row), typeof(UsageRow),
        typeof(FileTypeIcon), new PropertyMetadata(null, RowChanged));
    private long request;
    public UsageRow? Row { get => (UsageRow?)GetValue(RowProperty); set => SetValue(RowProperty, value); }

    public FileTypeIcon()
    {
        Width = Height = 20;
        Stretch = Stretch.Uniform;
        Loaded += async (_, _) => await RefreshAsync();
        Unloaded += (_, _) => { request++; Source = null; };
    }

    private static async void RowChanged(DependencyObject target, DependencyPropertyChangedEventArgs args) =>
        await ((FileTypeIcon)target).RefreshAsync();

    private async Task RefreshAsync()
    {
        var id = ++request;
        Source = null;
        ToolTip = null;
        if (!IsLoaded || Row is not { } row) return;
        try
        {
            var icon = await ShellIcons.GetAsync(row.Name, row.Node.IsDirectory);
            if (id == request && IsLoaded) Source = icon;
        }
        catch (Exception ex) when (ex is IOException or COMException or ArgumentException)
        {
            Trace.TraceWarning("Associated icon could not be loaded for {0}: {1}", row.FullPath, ex.Message);
            if (id == request && IsLoaded) ToolTip = $"Icon unavailable: {ex.Message}";
        }
    }
}
