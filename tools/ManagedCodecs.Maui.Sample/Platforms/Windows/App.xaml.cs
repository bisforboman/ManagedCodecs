namespace ManagedCodecs.MauiSample.WinUI;

/// <summary>
/// The WinUI application around MAUI's. The command line (without the program's name) gives the options
/// (<see cref="LiveOptions"/>); one that doesn't parse writes the usage line and exits with 1.
/// </summary>
public partial class App : MauiWinUIApplication
{
    public App()
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (LiveOptions.Parse(args) is not { } options)
        {
            Console.Error.WriteLine("usage: " + LiveOptions.Usage);
            Environment.Exit(1);
            return;
        }

        MauiProgram.Options = options;
        InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
