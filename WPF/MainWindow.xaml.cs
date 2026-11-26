using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MultiPointAutoClicker;

public sealed class ClickPoint : INotifyPropertyChanged
{
    private double _intervalSeconds = 2;
    private double _holdSeconds = 1;

    public int Id { get; init; }
    public int X { get; set; }
    public int Y { get; set; }

    public double IntervalSeconds
    {
        get => _intervalSeconds;
        set { _intervalSeconds = value; PropertyChanged?.Invoke(this, new(nameof(IntervalSeconds))); }
    }

    public double HoldSeconds
    {
        get => _holdSeconds;
        set { _holdSeconds = value; PropertyChanged?.Invoke(this, new(nameof(HoldSeconds))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    private const int HotkeyF6 = 106, HotkeyF8 = 108, HotkeyF9 = 109;
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private readonly HwndSourceHook _hook;
    private CancellationTokenSource? _cancellation;
    private bool _isRunning;
    private int _nextId = 1;

    public ObservableCollection<ClickPoint> Points { get; } = new();

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx, Dy;
        public uint MouseData, Flags, Time;
        public IntPtr ExtraInfo;
    }

    private const uint InputMouse = 0, MouseLeftDown = 0x0002, MouseLeftUp = 0x0004;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _hook = WindowHook;
        SourceInitialized += (_, _) =>
        {
            var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source?.AddHook(_hook);
            RegisterHotKey(new WindowInteropHelper(this).Handle, HotkeyF6, ModNoRepeat, 0x75);
            RegisterHotKey(new WindowInteropHelper(this).Handle, HotkeyF8, ModNoRepeat, 0x77);
            RegisterHotKey(new WindowInteropHelper(this).Handle, HotkeyF9, ModNoRepeat, 0x78);
        };
        Closing += (_, _) =>
        {
            StopClicking();
            var handle = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(handle, HotkeyF6);
            UnregisterHotKey(handle, HotkeyF8);
            UnregisterHotKey(handle, HotkeyF9);
            HwndSource.FromHwnd(handle)?.RemoveHook(_hook);
        };
    }

    private IntPtr WindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmHotkey) return IntPtr.Zero;
        handled = true;
        switch (wParam.ToInt32())
        {
            case HotkeyF6: AddPoint(); break;
            case HotkeyF8: _ = StartClickingAsync(); break;
            case HotkeyF9: StopClicking(); break;
        }
        return IntPtr.Zero;
    }

    private void AddPoint_Click(object sender, RoutedEventArgs e) => AddPoint();

    private void AddPoint()
    {
        if (_isRunning) { SetStatus("Сначала останови автокликер, чтобы менять точки."); return; }
        if (!GetCursorPos(out var cursor)) { SetStatus("Не удалось получить координаты курсора."); return; }
        var point = new ClickPoint { Id = _nextId++, X = cursor.X, Y = cursor.Y };
        Points.Add(point);
        PointsGrid.SelectedItem = point;
        PointCount.Text = $"  /  {Points.Count}";
        SetStatus($"Добавлена точка №{point.Id}  ·  X={point.X}, Y={point.Y}");
    }

    private void DeletePoint_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) { SetStatus("Останови автокликер перед изменением списка."); return; }
        if (PointsGrid.SelectedItem is not ClickPoint point) return;
        Points.Remove(point);
        PointCount.Text = $"  /  {Points.Count}";
        SetStatus($"Точка №{point.Id} удалена.");
    }

    private void UpdatePoint_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) { SetStatus("Останови автокликер перед изменением списка."); return; }
        if (PointsGrid.SelectedItem is not ClickPoint point) return;
        if (!GetCursorPos(out var cursor)) return;
        point.X = cursor.X;
        point.Y = cursor.Y;
        PointsGrid.Items.Refresh();
        SetStatus($"Координаты точки №{point.Id} обновлены.");
    }

    private void PointsGrid_BeginningEdit(object? sender, System.Windows.Controls.DataGridBeginningEditEventArgs e)
    {
        if (_isRunning)
        {
            e.Cancel = true;
            SetStatus("Останови автокликер перед редактированием.");
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e) => await StartClickingAsync();
    private void Stop_Click(object sender, RoutedEventArgs e) => StopClicking();

    private async Task StartClickingAsync()
    {
        if (_isRunning) return;
        PointsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
        PointsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
        if (Points.Count == 0) { SetStatus("Сначала добавь хотя бы одну точку клавишей F6."); return; }

        foreach (var point in Points)
        {
            if (!double.IsFinite(point.IntervalSeconds) || !double.IsFinite(point.HoldSeconds) ||
                point.IntervalSeconds <= 0 || point.IntervalSeconds > 86400 ||
                point.HoldSeconds <= 0 || point.HoldSeconds > 3600)
            {
                SetStatus($"Проверь интервал и удержание точки №{point.Id}.");
                return;
            }
        }

        var snapshot = Points.Select(p => new ClickPoint
        {
            Id = p.Id, X = p.X, Y = p.Y,
            IntervalSeconds = p.IntervalSeconds, HoldSeconds = p.HoldSeconds
        }).ToList();

        _isRunning = true;
        _cancellation = new CancellationTokenSource();
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        StatusDot.Fill = Brushes.White;
        StatusLabel.Text = "РАБОТАЕТ";
        StatusText.Text = "Точки выполняются последовательно.";
        var token = _cancellation.Token;

        try
        {
            await Task.Run(async () =>
            {
                var due = snapshot.ToDictionary(p => p.Id, _ => Stopwatch.GetTimestamp());
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var now = Stopwatch.GetTimestamp();
                    var point = snapshot.OrderBy(p => due[p.Id]).First();
                    if (due[point.Id] > now)
                    {
                        var wait = TimeSpan.FromSeconds((double)(due[point.Id] - now) / Stopwatch.Frequency);
                        await Task.Delay(wait < TimeSpan.FromMilliseconds(2) ? TimeSpan.FromMilliseconds(2) : wait, token);
                        continue;
                    }

                    token.ThrowIfCancellationRequested();
                    SetCursorPos(point.X, point.Y);
                    SendMouse(MouseLeftDown);
                    try { await Task.Delay(TimeSpan.FromSeconds(point.HoldSeconds), token); }
                    finally { SendMouse(MouseLeftUp); }
                    due[point.Id] = Stopwatch.GetTimestamp() + (long)(point.IntervalSeconds * Stopwatch.Frequency);
                }
            }, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ошибка автокликера", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SendMouse(MouseLeftUp);
            _isRunning = false;
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(119, 119, 119));
            StatusLabel.Text = "ОСТАНОВЛЕН";
            StatusText.Text = "Автокликер остановлен.";
        }
    }

    private void StopClicking()
    {
        if (!_isRunning) return;
        _cancellation?.Cancel();
        SendMouse(MouseLeftUp);
        SetStatus("Остановка…");
    }

    private static void SendMouse(uint flags)
    {
        var input = new Input
        {
            Type = InputMouse,
            Union = new InputUnion { Mouse = new MouseInput { Flags = flags } }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<Input>());
    }

    private void SetStatus(string message) => StatusText.Text = message;
}
