using System.Windows;
using System.Windows.Media;
using ModularRuntime.Desktop.Services;

namespace ModularRuntime.Desktop.Tests;

/// <summary>Runs the real <see cref="ThemeService"/> against a WPF <see cref="Application"/> on an STA thread.</summary>
public sealed class ThemeServiceTests
{
    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("STA test body failed.", failure);
        }
    }

    private static Color BackgroundColor(ResourceDictionary resources) => ((SolidColorBrush)resources["WindowBackgroundBrush"]).Color;

    [Fact]
    public void Apply_ReplacesThePaletteRegardlessOfHowItWasDeclared_AndKeepsExactlyOne()
    {
        RunOnSta(() =>
        {
            // One Application per process: this is the only test that creates it.
            var app = Application.Current ?? new Application();
            var merged = app.Resources.MergedDictionaries;
            merged.Clear();

            // The palette as App.xaml declares it: a relative Source the v0.3.0 lookup never matched.
            // Here it has no Source at all; only the ThemeName key identifies it.
            var initial = new ResourceDictionary
            {
                ["ThemeName"] = "Dark",
                ["WindowBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(0x16, 0x17, 0x1B)),
            };
            var controls = new ResourceDictionary { ["UnrelatedKey"] = 1 };
            merged.Add(initial);
            merged.Add(controls);
            var service = new ThemeService();

            service.Apply(AppTheme.Light);

            Assert.Single(merged, d => d.Contains("ThemeName"));
            Assert.Equal("Light", app.Resources["ThemeName"]);
            Assert.Equal(Color.FromRgb(0xF4, 0xF5, 0xF8), BackgroundColor(app.Resources));
            Assert.Same(controls, merged[1]); // the palette stays before the control styles

            service.Apply(AppTheme.Dark);

            Assert.Single(merged, d => d.Contains("ThemeName"));
            Assert.Equal("Dark", app.Resources["ThemeName"]);
            Assert.Equal(Color.FromRgb(0x16, 0x17, 0x1B), BackgroundColor(app.Resources));

            // Applying the active theme again is a no-op.
            var current = merged[0];
            service.Apply(AppTheme.Dark);
            Assert.Same(current, merged[0]);
        });
    }
}
