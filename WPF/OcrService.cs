using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Tesseract;

namespace MultiPointAutoClicker;

public static class OcrService
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    public static async Task WatchForExampleAsync(Func<string, Task> onDetected, CancellationToken token)
    {
        var dataPath = Path.Combine(AppContext.BaseDirectory, "tessdata");
        if (!File.Exists(Path.Combine(dataPath, "rus.traineddata")) ||
            !File.Exists(Path.Combine(dataPath, "eng.traineddata")))
        {
            throw new DirectoryNotFoundException("Не найдены языковые файлы OCR. Переустанови последнюю сборку программы.");
        }

        using var engine = new TesseractEngine(dataPath, "rus+eng", EngineMode.Default);
        engine.DefaultPageSegMode = PageSegMode.Auto;

        while (!token.IsCancellationRequested)
        {
            token.ThrowIfCancellationRequested();
            using var bitmap = CaptureScreen();
            using var memory = new MemoryStream();
            bitmap.Save(memory, ImageFormat.Png);
            using var pix = Pix.LoadFromMemory(memory.ToArray());
            using var page = engine.Process(pix);
            var recognizedText = page.GetText() ?? string.Empty;

            if (ContainsTargetPhrase(recognizedText))
            {
                await onDetected(recognizedText);
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(900), token);
        }
    }

    private static Bitmap CaptureScreen()
    {
        var left = GetSystemMetrics(SmXVirtualScreen);
        var top = GetSystemMetrics(SmYVirtualScreen);
        var width = GetSystemMetrics(SmCxVirtualScreen);
        var height = GetSystemMetrics(SmCyVirtualScreen);
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("Не удалось определить размер экрана.");

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(left, top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static bool ContainsTargetPhrase(string text)
    {
        var normalized = Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ");
        return normalized.Contains("решите") && normalized.Contains("пример");
    }
}