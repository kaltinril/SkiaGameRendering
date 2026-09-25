using System.Diagnostics;
using SkiaSharp;
using Xunit;

namespace Tests.Godot.VK;

/// <summary>
/// Launches the real Godot binary (<see cref="GodotBinaryFactAttribute"/>) against
/// <c>samples/Sample.Godot.VK</c> with its <c>--screenshot</c> user argument, then checks the PNG it
/// writes: the shared sample scene is a red circle of radius min(w,h)/2, centered, over a
/// CornflowerBlue clear, so the center pixel must be pure red and every corner pure blue. Solid
/// fills are driver-independent, unlike anti-aliased edges, which is why this probes pixels instead
/// of comparing a golden image (the window size also varies with the host's display scaling).
/// </summary>
public class GodotSampleTests
{
    static readonly SKColor Red = new(255, 0, 0);
    static readonly SKColor CornflowerBlue = new(100, 149, 237);

    [GodotBinaryFact]
    public void GodotSampleRendersSkiaSceneIntoTexture2DRD()
    {
        var godot = Environment.GetEnvironmentVariable(GodotBinaryFactAttribute.EnvironmentVariable)!;
        var sampleDir = FindSampleDirectory();

        // Godot (run outside the editor) loads the project assembly from .godot/mono/temp/bin/Debug;
        // Tests.Godot.VK.csproj's ProjectReference pins the sample build to Debug for that reason.
        // Check anyway, so a missing build fails here with a message instead of as a Godot crash.
        var assembly = Path.Combine(sampleDir, ".godot", "mono", "temp", "bin", "Debug", "Sample.Godot.VK.dll");
        Assert.True(File.Exists(assembly),
            $"Sample assembly not found at {assembly}. Build samples/Sample.Godot.VK first (any configuration of this test project builds it in Debug).");

        var screenshot = Path.Combine(Path.GetTempPath(), $"skiagamerendering-godot-{Guid.NewGuid():N}.png");
        try
        {
            var (exitCode, output) = RunGodot(godot, sampleDir, screenshot);
            Assert.True(exitCode == 0, $"Godot exited with {exitCode}.\n{output}");
            Assert.True(File.Exists(screenshot), $"Godot did not write {screenshot}.\n{output}");

            using var bitmap = SKBitmap.Decode(screenshot);
            Assert.NotNull(bitmap);
            int w = bitmap.Width, h = bitmap.Height;
            Assert.True(w > 100 && h > 100, $"Unexpectedly small screenshot: {w}x{h}");

            AssertPixel(bitmap, w / 2, h / 2, Red, "center (inside the circle)");
            AssertPixel(bitmap, 2, 2, CornflowerBlue, "top-left corner (clear color)");
            AssertPixel(bitmap, w - 3, 2, CornflowerBlue, "top-right corner (clear color)");
            AssertPixel(bitmap, 2, h - 3, CornflowerBlue, "bottom-left corner (clear color)");
            AssertPixel(bitmap, w - 3, h - 3, CornflowerBlue, "bottom-right corner (clear color)");
            // Just inside the circle's leftmost point: catches a vertically flipped or offset copy
            // that a center probe alone would miss.
            int radius = Math.Min(w, h) / 2;
            AssertPixel(bitmap, w / 2 - radius + 6, h / 2, Red, "left edge of the circle");
        }
        finally
        {
            if (File.Exists(screenshot))
                File.Delete(screenshot);
        }
    }

    static (int exitCode, string output) RunGodot(string godot, string sampleDir, string screenshot)
    {
        var startInfo = new ProcessStartInfo(godot)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--path");
        startInfo.ArgumentList.Add(sampleDir);
        startInfo.ArgumentList.Add("--rendering-driver");
        startInfo.ArgumentList.Add("vulkan");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("--screenshot");
        startInfo.ArgumentList.Add(screenshot);

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(120)))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            return (-1, "Timed out after 120s.\n" + stdout.Result + "\n" + stderr.Result);
        }
        return (process.ExitCode, stdout.Result + "\n" + stderr.Result);
    }

    static void AssertPixel(SKBitmap bitmap, int x, int y, SKColor expected, string what)
    {
        var actual = bitmap.GetPixel(x, y);
        const int tolerance = 2;
        bool close =
            Math.Abs(actual.Red - expected.Red) <= tolerance &&
            Math.Abs(actual.Green - expected.Green) <= tolerance &&
            Math.Abs(actual.Blue - expected.Blue) <= tolerance;
        Assert.True(close, $"Pixel at ({x},{y}), {what}: expected {expected}, got {actual}.");
    }

    static string FindSampleDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "samples", "Sample.Godot.VK");
            if (File.Exists(Path.Combine(candidate, "project.godot")))
                return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate samples/Sample.Godot.VK above " + AppContext.BaseDirectory);
    }
}
