using System;
using System.IO;
using System.Windows.Forms;

namespace CSharpViewer;

/// <summary>
/// WinForms application entry point.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Initializes WinForms and opens the viewer, optionally with an image path from the command line.
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        string? initialFile = args.Length > 0 && File.Exists(args[0]) ? args[0] : null;
        Application.Run(new MainForm(initialFile));
    }
}
