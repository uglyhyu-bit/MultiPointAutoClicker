using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

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
    private CancellationTokenSource? _ocrCancellation;
    private bool _isRunning;
    private bool _ocrTriggered;
    private MathTask? _pendingMathTask;
    private bool _resumeClickerAfterAnswer;
    private int _lastSelectedAnswer;
    private int _nextId = 1;
    private string _telegramToken = string.Empty;
    private string _telegramChatId = string.Empty;
    private string _alertImagePath = string.Empty;
    private CancellationTokenSource? _telegramPollingCancellation;
    private long _telegramUpdateOffset;
    private static readonly string TelegramSettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultiPointAutoClicker", "telegram.json");

    public ObservableCollection<ClickPoint> Points { get; } = new();

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Union; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int Dx, Dy; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }

    private const uint InputMouse = 0, MouseLeftDown = 0x0002, MouseLeftUp = 0x0004;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        LoadTelegramSettings();
        StartTelegramPolling();
        _hook = WindowHook;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var source = HwndSource.FromHwnd(handle);
            source?.AddHook(_hook);
            RegisterHotKey(handle, HotkeyF6, ModNoRepeat, 0x75);
            RegisterHotKey(handle, HotkeyF8, ModNoRepeat, 0x77);
            RegisterHotKey(handle, HotkeyF9, ModNoRepeat, 0x78);
        };
        Closing += (_, _) =>
        {
            StopClicking();
            _ocrCancellation?.Cancel();
            _telegramPollingCancellation?.Cancel();
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
        if (_isRunning) { e.Cancel = true; SetStatus("Останови автокликер перед редактированием."); }
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
            { SetStatus($"Проверь интервал и удержание точки №{point.Id}."); return; }
        }

        var snapshot = Points.Select(p => new ClickPoint
        {
            Id = p.Id, X = p.X, Y = p.Y, IntervalSeconds = p.IntervalSeconds, HoldSeconds = p.HoldSeconds
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
            StatusText.Text = _ocrTriggered ? "Обнаружено задание — автокликер остановлен." : "Автокликер остановлен.";
        }
    }

    private void StopClicking()
    {
        if (!_isRunning) return;
        _cancellation?.Cancel();
        SendMouse(MouseLeftUp);
        SetStatus("Остановка…");
    }

    private void LoadTelegramSettings()
    {
        try
        {
            if (!File.Exists(TelegramSettingsPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(TelegramSettingsPath));
            var root = doc.RootElement;
            _telegramChatId = root.TryGetProperty("chatId", out var chat) ? chat.GetString() ?? "" : "";
            _alertImagePath = root.TryGetProperty("alertImagePath", out var image) ? image.GetString() ?? "" : "";
            var encrypted = root.TryGetProperty("token", out var token) ? token.GetString() : null;
            if (!string.IsNullOrWhiteSpace(encrypted))
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(encrypted), null, DataProtectionScope.CurrentUser);
                _telegramToken = Encoding.UTF8.GetString(bytes);
                TelegramTokenBox.Password = _telegramToken;
            }
            TelegramChatIdBox.Text = _telegramChatId;
            UpdateAlertImagePreview();
            TelegramStatusText.Text = string.IsNullOrWhiteSpace(_telegramToken) ? "Не подключён · введи токен бота" : "Настройки загружены · токен защищён Windows";
        }
        catch { TelegramStatusText.Text = "Не удалось прочитать настройки · введи токен заново"; }
    }
    private void SaveTelegramSettings()
    {
        var entered = TelegramTokenBox.Password.Trim();
        if (!string.IsNullOrWhiteSpace(entered)) _telegramToken = entered;
        _telegramChatId = TelegramChatIdBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(_telegramToken)) throw new InvalidOperationException("Сначала введи токен Telegram-бота.");
        Directory.CreateDirectory(Path.GetDirectoryName(TelegramSettingsPath)!);
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(_telegramToken), null, DataProtectionScope.CurrentUser);
        File.WriteAllText(TelegramSettingsPath, JsonSerializer.Serialize(new { token = Convert.ToBase64String(data), chatId = _telegramChatId, alertImagePath = _alertImagePath }));
        StartTelegramPolling();
        TelegramStatusText.Text = "Настройки сохранены локально и защищены Windows.";
    }
    private void ChooseAlertImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выбери картинку для уведомления",
            Filter = "Изображения|*.png;*.jpg;*.jpeg;*.webp;*.bmp|Все файлы|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        _alertImagePath = dialog.FileName;
        UpdateAlertImagePreview();
        try { SaveTelegramSettings(); TelegramStatusText.Text = "Картинка выбрана и путь сохранён."; }
        catch (Exception ex) { TelegramStatusText.Text = "Картинка выбрана, но настройки не сохранены: " + ex.Message; }
    }

    private void UpdateAlertImagePreview()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_alertImagePath) || !File.Exists(_alertImagePath))
            {
                AlertImagePreview.Source = null;
                AlertImageName.Text = "Картинка не выбрана";
                return;
            }
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(_alertImagePath);
            bitmap.DecodePixelWidth = 180;
            bitmap.EndInit();
            AlertImagePreview.Source = bitmap;
            AlertImageName.Text = Path.GetFileName(_alertImagePath);
        }
        catch
        {
            AlertImagePreview.Source = null;
            AlertImageName.Text = "Не удалось открыть изображение";
        }
    }

    private void StartTelegramPolling()
    {
        _telegramPollingCancellation?.Cancel();
        if (string.IsNullOrWhiteSpace(_telegramToken) || string.IsNullOrWhiteSpace(_telegramChatId)) return;
        _telegramPollingCancellation = new CancellationTokenSource();
        _ = TelegramPollingLoopAsync(_telegramPollingCancellation.Token);
    }

    private async Task TelegramPollingLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var updates = await TelegramService.GetUpdatesAsync(_telegramToken, _telegramUpdateOffset, cancellationToken);
                foreach (var update in updates)
                {
                    _telegramUpdateOffset = Math.Max(_telegramUpdateOffset, update.UpdateId + 1);
                    // Принимаем команды только из личного чата, чей ID сохранён в настройках,
                    // и только от пользователя с тем же Telegram ID.
                    if (update.ChatId != _telegramChatId || update.FromId != _telegramChatId) continue;
                    await HandleTelegramCommandAsync(update, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() => TelegramStatusText.Text = "Связь с Telegram: " + ex.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task HandleTelegramCommandAsync(TelegramIncomingUpdate update, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(update.CallbackQueryId))
        {
            if (update.CallbackData is not null && update.CallbackData.StartsWith("answer:", StringComparison.Ordinal) &&
                int.TryParse(update.CallbackData["answer:".Length..], out var selectedIndex))
            {
                var clickTask = await Dispatcher.InvokeAsync(() =>
                {
                    if (_pendingMathTask is null || selectedIndex < 0 || selectedIndex >= _pendingMathTask.Options.Length)
                        return Task.FromResult(false);
                    var pending = _pendingMathTask;
                    _pendingMathTask = null;
                    return ClickMathAnswerAsync(pending, selectedIndex);
                });
                var clicked = await clickTask;
                await TelegramService.AnswerCallbackQueryAsync(_telegramToken, update.CallbackQueryId,
                    clicked ? "Выбор принят. Отправляю ответ в игру." : "Задание уже не ожидает выбора.");
                await TelegramService.SendMessageAsync(_telegramToken, _telegramChatId,
                    clicked ? $"Выбран вариант: {await Dispatcher.InvokeAsync(() => _lastSelectedAnswer.ToString())}. Нажимаю «Ответить»." : "Сейчас нет ожидающего задания.");
            }
            else
            {
                await TelegramService.AnswerCallbackQueryAsync(_telegramToken, update.CallbackQueryId, "Неизвестная команда.");
            }
            return;
        }

        var command = update.Text;
        if (string.IsNullOrWhiteSpace(command)) return;
        var normalized = command.Trim().ToLowerInvariant();
        if (normalized is "да" or "подтверждаю" or "подтвердить ответ" or "ответить")
        {
            await TelegramService.SendMessageAsync(_telegramToken, _telegramChatId,
                _pendingMathTask is null
                    ? "Сейчас нет ожидающего задания."
                    : "Выбери конкретный вариант ответа кнопкой под изображением примера.");
        }
        else if (normalized is "нет" or "отмена" or "отменить")
        {
            var cancelled = await Dispatcher.InvokeAsync(() => { var hadTask = _pendingMathTask is not null; _pendingMathTask = null; _resumeClickerAfterAnswer = false; return hadTask; });
            await TelegramService.SendMessageAsync(_telegramToken, _telegramChatId, cancelled ? "Ответ отменён. Ничего не нажимал." : "Нет задания для отмены.");
        }
        else if (normalized is "▶ запустить" or "/start_clicker" or "запустить")
        {
            var canStart = await Dispatcher.InvokeAsync(() =>
            {
                if (Points.Count == 0) return false;
                _ = StartClickingAsync();
                return true;
            });
            await TelegramService.SendMessageAsync(_telegramToken, _telegramChatId,
                canStart ? "Команда запуска принята." : "Не могу запустить: сначала добавь точки в приложении на ПК.");
        }
        else if (normalized is "■ остановить" or "/stop_clicker" or "остановить")
        {
            await Dispatcher.InvokeAsync(() => StopClicking());
            await TelegramService.SendMessageAsync(_telegramToken, _telegramChatId, "Команда остановки принята.");
        }
        else if (normalized is "ℹ статус" or "/status" or "статус")
        {
            var running = await Dispatcher.InvokeAsync(() => _isRunning);
            await TelegramService.SendMessageAsync(_telegramToken, _telegramChatId,
                running ? "Статус: 🟢 автокликер работает." : "Статус: ⚪ автокликер остановлен.");
        }
    }

    private void TelegramSave_Click(object sender, RoutedEventArgs e)
    { try { SaveTelegramSettings(); } catch (Exception ex) { TelegramStatusText.Text = ex.Message; } }
    private async void TelegramGetChatId_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveTelegramSettings();
            _telegramPollingCancellation?.Cancel();
            TelegramStatusText.Text = "Ищем сообщение от тебя…";
            var id = await TelegramService.GetLatestChatIdAsync(_telegramToken);
            if (string.IsNullOrWhiteSpace(id)) { TelegramStatusText.Text = "Сообщение не найдено · отправь боту /start и повтори"; return; }
            TelegramChatIdBox.Text = id;
            SaveTelegramSettings();
            TelegramStatusText.Text = "Chat ID получен · теперь проверь тестовое сообщение.";
        }
        catch (Exception ex) { TelegramStatusText.Text = "Telegram: " + ex.Message; }
    }
    private async void TelegramTest_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveTelegramSettings();
            if (string.IsNullOrWhiteSpace(_telegramChatId)) throw new InvalidOperationException("Сначала нажми «Получить Chat ID».");
            TelegramStatusText.Text = "Отправляем тест…";
            await TelegramService.SendMessageAsync(_telegramToken, _telegramChatId, "MultiPoint AutoClicker подключён! OCR ищет математическую плашку в центре экрана. После распознавания пришлёт пример и будет ждать твоего подтверждения.");
            await TelegramService.SendControlPanelAsync(_telegramToken, _telegramChatId);
            StartTelegramPolling();
            TelegramStatusText.Text = "Тест и пульт управления отправлены · проверь Telegram на телефоне.";
        }
        catch (Exception ex) { TelegramStatusText.Text = "Не удалось отправить: " + ex.Message; }
    }
    private async Task SendTelegramMathTaskAsync(MathTask task)
    {
        if (string.IsNullOrWhiteSpace(_telegramToken) || string.IsNullOrWhiteSpace(_telegramChatId))
        {
            await Dispatcher.InvokeAsync(() => TelegramStatusText.Text = "Задание распознано, но Telegram ещё не настроен.");
            return;
        }
        try
        {
            var options = string.Join("  |  ", task.Options);
            var message = "🧮 Найден математический пример\n\n" + task.Expression + "\n" +
                $"Варианты: {options}\n\n" +
                "Выбери один из вариантов кнопкой ниже. Программа нажмёт выбранный вариант и кнопку «Ответить».";
            await TelegramService.SendMathTaskAsync(_telegramToken, _telegramChatId, task.ScreenshotPng, message, task.Options);
            await Dispatcher.InvokeAsync(() => TelegramStatusText.Text = "Пример и варианты отправлены · ожидается твой выбор в Telegram.");
        }
        catch (Exception ex) { await Dispatcher.InvokeAsync(() => TelegramStatusText.Text = "Ошибка Telegram: " + ex.Message); }
    }

    private async Task<bool> ClickMathAnswerAsync(MathTask task, int selectedIndex)
    {
        if (selectedIndex < 0 || selectedIndex >= task.Options.Length) return false;
        _lastSelectedAnswer = task.Options[selectedIndex];
        var screenLeft = GetSystemMetrics(76);
        var screenTop = GetSystemMetrics(77);
        var scale = task.ScreenHeight / 800.0;
        var rowOffsets = new[] { -61.0, -27.0, 6.0, 39.0, 72.0 };
        if (selectedIndex >= rowOffsets.Length) return false;
        var centerX = screenLeft + task.ScreenWidth / 2;
        var answerY = screenTop + task.ScreenHeight / 2 + (int)Math.Round(rowOffsets[selectedIndex] * scale);
        var buttonY = screenTop + task.ScreenHeight / 2 + (int)Math.Round(114.0 * scale);
        SetCursorPos(centerX, answerY);
        SendMouse(MouseLeftDown);
        SendMouse(MouseLeftUp);
        await Task.Delay(180);
        SetCursorPos(centerX, buttonY);
        SendMouse(MouseLeftDown);
        SendMouse(MouseLeftUp);
        OcrStatusText.Text = $"Выбран вариант {task.Options[selectedIndex]} · нажата кнопка «Ответить»";
        if (_resumeClickerAfterAnswer)
        {
            _resumeClickerAfterAnswer = false;
            await Task.Delay(500);
            await StartClickingAsync();
        }
        return true;
    }

    private async void OcrStart_Click(object sender, RoutedEventArgs e)
    {
        if (_ocrCancellation is not null) return;
        _ocrTriggered = false;
        _pendingMathTask = null;
        _ocrCancellation = new CancellationTokenSource();
        OcrStartButton.IsEnabled = false;
        OcrStopButton.IsEnabled = true;
        OcrStatusText.Text = "Активно · поиск математической плашки в центре экрана";
        var token = _ocrCancellation.Token;
        try
        {
            await Task.Run(() => OcrService.WatchForExampleAsync(async task =>
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    _ocrTriggered = true;
                    _pendingMathTask = task;
                    _resumeClickerAfterAnswer = _isRunning;
                    StopClicking();
                    OcrStatusText.Text = $"НАЙДЕНО: {task.Expression} = {task.CorrectAnswer} · ожидается Telegram";
                    OcrStartButton.IsEnabled = true;
                    OcrStopButton.IsEnabled = false;
                }).Task;
                await SendTelegramMathTaskAsync(task);
            }, token), token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                OcrStatusText.Text = "Ошибка OCR: " + ex.Message;
                MessageBox.Show(this, ex.Message, "Ошибка распознавания", MessageBoxButton.OK, MessageBoxImage.Error);
            });
        }
        finally
        {
            if (_ocrCancellation is not null)
            {
                _ocrCancellation.Dispose();
                _ocrCancellation = null;
                OcrStartButton.IsEnabled = true;
                OcrStopButton.IsEnabled = false;
                if (!_ocrTriggered) OcrStatusText.Text = "Остановлено · поиск математической плашки";
            }
        }
    }

    private void OcrStop_Click(object sender, RoutedEventArgs e)
    {
        _ocrCancellation?.Cancel();
        OcrStatusText.Text = "Останавливаем распознавание…";
    }

    private static void SendMouse(uint flags)
    {
        var input = new Input { Type = InputMouse, Union = new InputUnion { Mouse = new MouseInput { Flags = flags } } };
        SendInput(1, new[] { input }, Marshal.SizeOf<Input>());
    }

    private void SetStatus(string message) => StatusText.Text = message;
}
