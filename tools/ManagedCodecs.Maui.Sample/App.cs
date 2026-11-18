namespace ManagedCodecs.MauiSample;

/// <summary>One window, one page: <see cref="LivePage"/>.</summary>
public sealed class App : Application
{
    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new LivePage(MauiProgram.Options)) { Title = "mclive-maui" };
}
