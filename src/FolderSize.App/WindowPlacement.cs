using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using FolderSize.Core;

namespace FolderSize.App;

public sealed record WindowPlacement
{
    public required int Left { get; init; }
    public required int Top { get; init; }
    public required int Right { get; init; }
    public required int Bottom { get; init; }
    public bool Maximized { get; init; }

    public void Validate()
    {
        if ((long)Right - Left is <= 0 or > int.MaxValue ||
            (long)Bottom - Top is <= 0 or > int.MaxValue)
            throw new InvalidDataException("The saved window bounds are not supported.");
    }

    public static void Track(Window window, ThemeSettingsStore store, WindowPlacement? saved)
    {
        window.SourceInitialized += (_, _) =>
        {
            if (saved is null) return;
            try { saved.Restore(window); }
            catch (Exception ex) when (ex is Win32Exception or InvalidDataException)
            {
                MessageBox.Show(window, $"Could not restore the window position. Default bounds will be used.\n{ex.Message}",
                    "Window position could not be restored", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        window.Closing += (_, e) =>
        {
            if (e.Cancel || new WindowInteropHelper(window).Handle == 0) return;
            try { store.Save(store.LoadSettings() with { WindowPlacement = Capture(window) }); }
            catch (Exception ex) when (FileErrors.IsExpected(ex) || ex is JsonException or Win32Exception)
            {
                MessageBox.Show(window, $"Could not save the window size and position. Your previous settings are unchanged.\n{ex.Message}",
                    "Window position could not be saved", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
    }

    public static WindowPlacement Capture(Window window)
    {
        var placement = new NativePlacement { Length = Marshal.SizeOf<NativePlacement>() };
        if (!GetWindowPlacement(new WindowInteropHelper(window).Handle, ref placement))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return new WindowPlacement
        {
            Left = placement.NormalPosition.Left,
            Top = placement.NormalPosition.Top,
            Right = placement.NormalPosition.Right,
            Bottom = placement.NormalPosition.Bottom,
            Maximized = placement.ShowCommand == 3 || (placement.ShowCommand == 2 && (placement.Flags & 2) != 0)
        };
    }

    private void Restore(Window window)
    {
        Validate();
        // Keep Windows' workspace coordinates paired with its placement API; it also recovers off-screen windows.
        var placement = new NativePlacement
        {
            Length = Marshal.SizeOf<NativePlacement>(),
            ShowCommand = Maximized ? 3 : 1,
            NormalPosition = new NativeRect { Left = Left, Top = Top, Right = Right, Bottom = Bottom }
        };
        if (!SetWindowPlacement(new WindowInteropHelper(window).Handle, ref placement))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(nint window, ref NativePlacement placement);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(nint window, ref NativePlacement placement);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePlacement
    {
        public int Length;
        public int Flags;
        public int ShowCommand;
        public NativePoint MinPosition;
        public NativePoint MaxPosition;
        public NativeRect NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
