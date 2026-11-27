using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Tesseract;

namespace MultiPointAutoClicker;

public sealed record MathTask(string Expression, int CorrectAnswer, int[] Options, int CorrectOptionIndex, int ScreenWidth, int ScreenHeight, string RawText, byte[] ScreenshotPng);

public static class OcrService
{
    private const int SmXVirtualScreen = 76, SmYVirtualScreen = 77, SmCxVirtualScreen = 78, SmCyVirtualScreen = 79;
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    private static readonly Dictionary<string, int> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ноль"] = 0, ["один"] = 1, ["одна"] = 1, ["два"] = 2, ["две"] = 2, ["три"] = 3,
        ["четыре"] = 4, ["пять"] = 5, ["шесть"] = 6, ["семь"] = 7, ["восемь"] = 8,
        ["девять"] = 9, ["десять"] = 10, ["одиннадцать"] = 11, ["двенадцать"] = 12,
        ["тринадцать"] = 13, ["четырнадцать"] = 14, ["пятнадцать"] = 15,
        ["шестнадцать"] = 16, ["семнадцать"] = 17, ["восемнадцать"] = 18,
        ["девятнадцать"] = 19, ["двадцать"] = 20
    };

    public static async Task WatchForExampleAsync(Func<MathTask, Task> onDetected, CancellationToken token)
    {
        var dataPath = Path.Combine(AppContext.BaseDirectory, "tessdata");
        if (!File.Exists(Path.Combine(dataPath, "rus.traineddata")) || !File.Exists(Path.Combine(dataPath, "eng.traineddata")))
            throw new DirectoryNotFoundException("Не найдены языковые файлы OCR. Переустанови последнюю сборку программы.");

        using var engine = new TesseractEngine(dataPath, "rus+eng", EngineMode.Default);
        engine.DefaultPageSegMode = PageSegMode.SparseText;
        while (!token.IsCancellationRequested)
        {
            token.ThrowIfCancellationRequested();
            using var capture = CaptureCenter();
            using var memory = new MemoryStream();
            capture.Bitmap.Save(memory, System.Drawing.Imaging.ImageFormat.Png);
            var screenshotPng = memory.ToArray();
            using var pix = Pix.LoadFromMemory(screenshotPng);
            using var page = engine.Process(pix);
            var text = page.GetText() ?? string.Empty;
            // Главный триггер остановки — именно слово «Ответить», а не появление математической плашки.
            if (ContainsAnswerButton(text))
            {
                if (!TryParseMathTask(text, capture.ScreenWidth, capture.ScreenHeight, screenshotPng, out var task))
                {
                    task = new MathTask("Пример не удалось распознать", 0, Array.Empty<int>(), -1,
                        capture.ScreenWidth, capture.ScreenHeight, text, screenshotPng);
                }
                await onDetected(task!);
                return;
            }
            await Task.Delay(700, token);
        }
    }

    private sealed record ScreenCapture(Bitmap Bitmap, int ScreenWidth, int ScreenHeight) : IDisposable
    {
        public void Dispose() => Bitmap.Dispose();
    }

    private static ScreenCapture CaptureCenter()
    {
        var left = GetSystemMetrics(SmXVirtualScreen); var top = GetSystemMetrics(SmYVirtualScreen);
        var width = GetSystemMetrics(SmCxVirtualScreen); var height = GetSystemMetrics(SmCyVirtualScreen);
        if (width <= 0 || height <= 0) throw new InvalidOperationException("Не удалось определить размер экрана.");
        // Игровая кнопка «Ответить» и область задания находятся в центральной части экрана.
        var cropWidth = Math.Min(width, (int)(width * 0.34));
        var cropHeight = Math.Min(height, (int)(height * 0.62));
        var x = left + (width - cropWidth) / 2; var y = top + (height - cropHeight) / 2;
        var bitmap = new Bitmap(cropWidth, cropHeight, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(x, y, 0, 0, new Size(cropWidth, cropHeight), CopyPixelOperation.SourceCopy);
            return new(bitmap, width, height);
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static bool ContainsAnswerButton(string text)
    {
        var normalized = (text ?? string.Empty).ToLowerInvariant().Replace('ё', 'е');
        return Regex.IsMatch(normalized, @"(?<!\p{L})ответить(?!\p{L})");
    }

    private static bool TryParseMathTask(string text, int screenWidth, int screenHeight, byte[] screenshotPng, out MathTask? task)
    {
        task = null;
        var normalized = text.ToLowerInvariant().Replace('ё', 'е');
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"(?<=\d)\s*[хx]\s*(?=\d)", " * ");
        var match = System.Text.RegularExpressions.Regex.Match(normalized, @"(ноль|один|одна|два|две|три|четыре|пять|шесть|семь|восемь|девять|десять|одиннадцать|двенадцать|тринадцать|четырнадцать|пятнадцать|шестнадцать|семнадцать|восемнадцать|девятнадцать|двадцать|\d{1,2})\s*(\+|\-|−|\*|×|х|x|÷|/)\s*(ноль|один|одна|два|две|три|четыре|пять|шесть|семь|восемь|девять|десять|одиннадцать|двенадцать|тринадцать|четырнадцать|пятнадцать|шестнадцать|семнадцать|восемнадцать|девятнадцать|двадцать|\d{1,2})");
        if (!match.Success) return false;
        if (!TryNumber(match.Groups[1].Value, out var a) || !TryNumber(match.Groups[3].Value, out var b)) return false;
        int answer = match.Groups[2].Value switch
        {
            "+" => a + b, "-" or "−" => a - b, "*" or "×" or "х" or "x" => a * b,
            "÷" or "/" when b != 0 && a % b == 0 => a / b, _ => int.MinValue
        };
        if (answer == int.MinValue || answer < 0 || answer > 400) return false;

        var tail = normalized[(match.Index + match.Length)..];
        var values = System.Text.RegularExpressions.Regex.Matches(tail, @"(?<![\p{L}\d])\d{1,3}(?![\p{L}\d])").Select(m => int.Parse(m.Value)).ToList();
        // В плашке сначала может быть строка цены; берём последние пять чисел как варианты ответа.
        if (values.Count < 5) return false;
        var options = values.TakeLast(5).ToArray();
        var index = Array.IndexOf(options, answer);
        if (index < 0) return false;
        var expression = $"{match.Groups[1].Value} {match.Groups[2].Value} {match.Groups[3].Value} = ?";
        task = new MathTask(expression, answer, options, index, screenWidth, screenHeight, text, screenshotPng);
        return true;
    }

    private static bool TryNumber(string value, out int number)
    {
        if (int.TryParse(value, out number)) return true;
        return NumberWords.TryGetValue(value.Replace('ё', 'е'), out number);
    }
}
