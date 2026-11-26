using System;
using System.Windows.Forms;

namespace MultiPointAutoClicker;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
