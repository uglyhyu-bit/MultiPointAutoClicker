using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MultiPointAutoClicker;

public sealed class MainForm : Form
{
    private readonly List<ClickPoint> _points = new();
    private readonly DataGridView _grid = new();
    private readonly Label _status = new();
    private Button _start = new();
    private Button _stop = new();
    private readonly System.Windows.Forms.Timer _refresh = new();
    private CancellationTokenSource? _cts;
    private bool _running;
    private int _nextId = 1;

    private const int HOTKEY_ID_F6 = 106;
    private const int HOTKEY_ID_F8 = 108;
    private const int HOTKEY_ID_F9 = 109;
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    private sealed class ClickPoint
    {
        public int Id { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public double Interval { get; set; } = 2;
        public double Hold { get; set; } = 1;
    }

    public MainForm()
    {
        Text = "MultiPoint AutoClicker";
        MinimumSize = new Size(850, 600);
        Size = new Size(980, 700);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(17, 19, 26);
        ForeColor = Color.FromArgb(235, 238, 245);
        Font = new Font("Segoe UI", 9.5f);
        BuildUi();

        _refresh.Interval = 250;
        _refresh.Tick += (_, _) => UpdateStatus();
        _refresh.Start();

        Shown += (_, _) =>
        {
            RegisterHotKey(Handle, HOTKEY_ID_F6, MOD_NOREPEAT, (uint)Keys.F6);
            RegisterHotKey(Handle, HOTKEY_ID_F8, MOD_NOREPEAT, (uint)Keys.F8);
            RegisterHotKey(Handle, HOTKEY_ID_F9, MOD_NOREPEAT, (uint)Keys.F9);
        };
        FormClosing += (_, _) =>
        {
            StopClicking();
            UnregisterHotKey(Handle, HOTKEY_ID_F6);
            UnregisterHotKey(Handle, HOTKEY_ID_F8);
            UnregisterHotKey(Handle, HOTKEY_ID_F9);
        };
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            switch (m.WParam.ToInt32())
            {
                case HOTKEY_ID_F6: AddPoint(); break;
                case HOTKEY_ID_F8: StartClicking(); break;
                case HOTKEY_ID_F9: StopClicking(); break;
            }
        }
        base.WndProc(ref m);
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5,
            Padding = new Padding(22), BackColor = BackColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        Controls.Add(root);

        var header = new Panel { Dock = DockStyle.Fill };
        var title = new Label
        {
            Text = "MULTIPOINT  /  AUTOCLICKER", AutoSize = true,
            Font = new Font("Segoe UI Semibold", 19, FontStyle.Bold),
            ForeColor = Color.White, Location = new Point(0, 0)
        };
        var subtitle = new Label
        {
            Text = "Несколько координат • индивидуальные интервалы • удержание ЛКМ",
            AutoSize = true, ForeColor = Color.FromArgb(151, 160, 177),
            Location = new Point(2, 39)
        };
        header.Controls.Add(title); header.Controls.Add(subtitle);
        root.Controls.Add(header, 0, 0);

        var tablePanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(25, 28, 38), Padding = new Padding(12) };
        _grid.Dock = DockStyle.Fill;
        _grid.BackgroundColor = Color.FromArgb(25, 28, 38);
        _grid.BorderStyle = BorderStyle.None;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(35, 40, 53);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(210, 218, 231);
        _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 9.5f);
        _grid.DefaultCellStyle.BackColor = Color.FromArgb(25, 28, 38);
        _grid.DefaultCellStyle.ForeColor = Color.White;
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(48, 66, 94);
        _grid.DefaultCellStyle.SelectionForeColor = Color.White;
        _grid.GridColor = Color.FromArgb(45, 50, 64);
        _grid.RowHeadersVisible = false;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Id", HeaderText = "№", ReadOnly = true, FillWeight = 35 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "X", HeaderText = "Координата X", ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Y", HeaderText = "Координата Y", ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Interval", HeaderText = "Интервал после клика (сек.)" });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Hold", HeaderText = "Зажатие ЛКМ (сек.)" });
        _grid.CellEndEdit += (_, e) => SaveEditedCell(e.RowIndex);
        tablePanel.Controls.Add(_grid);
        root.Controls.Add(tablePanel, 0, 1);

        var actionPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, Padding = new Padding(0, 17, 0, 0)
        };
        actionPanel.Controls.Add(MakeButton("＋  Добавить точку  [F6]", Color.FromArgb(43, 93, 153), AddPoint, 210));
        actionPanel.Controls.Add(MakeButton("Удалить выбранную", Color.FromArgb(65, 70, 83), DeleteSelected, 180));
        actionPanel.Controls.Add(MakeButton("Обновить координаты", Color.FromArgb(65, 70, 83), UpdateSelectedCoordinates, 190));
        root.Controls.Add(actionPanel, 0, 2);

        var runPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, Padding = new Padding(0, 12, 0, 0)
        };
        _start = MakeButton("▶  ЗАПУСК  [F8]", Color.FromArgb(35, 135, 91), StartClicking, 220);
        _stop = MakeButton("■  СТОП  [F9]", Color.FromArgb(157, 61, 68), StopClicking, 220);
        runPanel.Controls.Add(_start); runPanel.Controls.Add(_stop);
        root.Controls.Add(runPanel, 0, 3);

        _status.Text = "●  Остановлен  |  F6 — добавить точку   F8 — запуск   F9 — стоп";
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.ForeColor = Color.FromArgb(158, 169, 188);
        root.Controls.Add(_status, 0, 4);
    }

    private Button MakeButton(string text, Color color, Action action, int width)
    {
        var b = new Button
        {
            Text = text, Width = width, Height = 43, FlatStyle = FlatStyle.Flat,
            BackColor = color, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 9.5f),
            Margin = new Padding(0, 0, 10, 0), Cursor = Cursors.Hand
        };
        b.FlatAppearance.BorderSize = 0;
        b.Click += (_, _) => action();
        return b;
    }

    private void AddPoint()
    {
        if (_running) { SetStatus("Сначала останови автокликер, чтобы менять точки."); return; }
        var p = Cursor.Position;
        var item = new ClickPoint { Id = _nextId++, X = p.X, Y = p.Y };
        _points.Add(item);
        AddRow(item);
        SetStatus($"Добавлена точка №{item.Id}  •  X={item.X}, Y={item.Y}");
    }

    private void AddRow(ClickPoint p)
    {
        _grid.Rows.Add(p.Id, p.X, p.Y, p.Interval.ToString("0.###"), p.Hold.ToString("0.###"));
        _grid.Rows[_grid.Rows.Count - 1].Tag = p.Id;
    }

    private void DeleteSelected()
    {
        if (_running) { SetStatus("Останови автокликер перед изменением списка."); return; }
        if (_grid.CurrentRow?.Tag is not int id) return;
        _points.RemoveAll(p => p.Id == id);
        _grid.Rows.Remove(_grid.CurrentRow);
        SetStatus($"Точка №{id} удалена.");
    }

    private void UpdateSelectedCoordinates()
    {
        if (_running) { SetStatus("Останови автокликер перед изменением списка."); return; }
        if (_grid.CurrentRow?.Tag is not int id) return;
        var p = _points.FirstOrDefault(x => x.Id == id);
        if (p == null) return;
        p.X = Cursor.Position.X; p.Y = Cursor.Position.Y;
        var row = _grid.CurrentRow;
        row.Cells["X"].Value = p.X; row.Cells["Y"].Value = p.Y;
        SetStatus($"Координаты точки №{id} обновлены.");
    }

    private void SaveEditedCell(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _grid.Rows.Count) return;
        if (_running) { SetStatus("Останови автокликер перед редактированием."); return; }
        var row = _grid.Rows[rowIndex];
        if (row.Tag is not int id) return;
        var p = _points.FirstOrDefault(x => x.Id == id);
        if (p == null) return;
        try
        {
            double interval = Convert.ToDouble(row.Cells["Interval"].Value?.ToString()?.Replace(',', '.'),
                System.Globalization.CultureInfo.InvariantCulture);
            double hold = Convert.ToDouble(row.Cells["Hold"].Value?.ToString()?.Replace(',', '.'),
                System.Globalization.CultureInfo.InvariantCulture);
            if (interval <= 0 || hold <= 0 || interval > 86400 || hold > 3600) throw new FormatException();
            p.Interval = interval; p.Hold = hold;
            row.Cells["Interval"].Value = interval.ToString("0.###");
            row.Cells["Hold"].Value = hold.ToString("0.###");
            SetStatus($"Настройки точки №{id} сохранены.");
        }
        catch
        {
            MessageBox.Show("Укажи положительные числа. Интервал: до 86400 сек., зажатие: до 3600 сек.",
                "Некорректное значение", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            row.Cells["Interval"].Value = p.Interval.ToString("0.###");
            row.Cells["Hold"].Value = p.Hold.ToString("0.###");
        }
    }

    private async void StartClicking()
    {
        if (_running) return;
        if (_points.Count == 0)
        {
            MessageBox.Show("Добавь хотя бы одну точку клавишей F6 или кнопкой.",
                "Нет точек", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _grid.EndEdit();
        _running = true;
        _cts = new CancellationTokenSource();
        _start.Enabled = false;
        SetStatus("●  Работает  |  точки выполняются по очереди");
        var token = _cts.Token;
        var snapshot = _points.Select(p => new ClickPoint
        {
            Id = p.Id, X = p.X, Y = p.Y, Interval = p.Interval, Hold = p.Hold
        }).ToList();

        try
        {
            await Task.Run(async () =>
            {
                var due = snapshot.ToDictionary(p => p.Id, _ => Stopwatch.GetTimestamp());
                while (!token.IsCancellationRequested)
                {
                    var now = Stopwatch.GetTimestamp();
                    var p = snapshot.OrderBy(x => due[x.Id]).First();
                    if (due[p.Id] > now)
                    {
                        var wait = TimeSpan.FromSeconds((double)(due[p.Id] - now) / Stopwatch.Frequency);
                        await Task.Delay(wait < TimeSpan.FromMilliseconds(2) ? TimeSpan.FromMilliseconds(2) : wait, token);
                        continue;
                    }

                    token.ThrowIfCancellationRequested();
                    SetCursorPos(p.X, p.Y);
                    MouseDown();
                    try { await Task.Delay(TimeSpan.FromSeconds(p.Hold), token); }
                    finally { MouseUp(); }

                    due[p.Id] = Stopwatch.GetTimestamp() +
                        (long)(p.Interval * Stopwatch.Frequency);
                }
            }, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Ошибка автокликера", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            MouseUp();
            _running = false;
            _start.Enabled = true;
            SetStatus("●  Остановлен");
        }
    }

    private void StopClicking()
    {
        if (!_running) return;
        _cts?.Cancel();
        MouseUp();
        SetStatus("Остановка…");
    }

    private static void MouseDown() => SendMouse(MOUSEEVENTF_LEFTDOWN);
    private static void MouseUp() => SendMouse(MOUSEEVENTF_LEFTUP);
    private static void SendMouse(uint flags)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags } }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private void SetStatus(string text)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => SetStatus(text))); return; }
        _status.Text = text;
    }

    private void UpdateStatus() { }
}
