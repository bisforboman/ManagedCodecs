using Android.App;
using Android.Content.PM;
using Android.OS;

namespace ManagedCodecs.MauiSample;

/// <summary>The app's one activity, whose intent's extras are the options (<see cref="LiveOptions"/>).</summary>
/// <remarks>
/// The extras are <c>url</c>, <c>frames</c> (an int) and <c>certificate-hash</c>, as
/// <c>adb shell am start -n io.github.managedcodecs.mclive/.MainActivity -e url http://10.0.2.2:8090/video.mjpg --ei frames 100</c>
/// gives them.
/// </remarks>
[Activity(
    Theme = "@style/Maui.MainTheme.NoActionBar",
    MainLauncher = true,
    Exported = true,
    Name = "io.github.managedcodecs.mclive.MainActivity",
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public sealed class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        if (Intent?.GetStringExtra("url") is { } url)
        {
            var args = new List<string> { url };
            int frames = Intent.GetIntExtra("frames", 0);
            if (frames > 0)
            {
                args.AddRange(["--frames", frames.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            }

            if (Intent.GetStringExtra("certificate-hash") is { } hash)
            {
                args.AddRange(["--certificate-hash", hash]);
            }

            MauiProgram.Options = LiveOptions.Parse(args) ?? LiveOptions.None;
        }

        base.OnCreate(savedInstanceState);
    }
}
