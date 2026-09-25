using Xunit;

namespace Tests.Godot;

/// <summary>
/// Skips the decorated test at runtime unless the <c>GODOT_BIN</c> environment variable points at
/// a Godot 4.7+ .NET ("mono") build's executable. GodotSharp types only function inside a running
/// Godot process, so the Godot adapter cannot be exercised in-process the way the other backends'
/// tests exercise theirs; the only real test is launching the engine against
/// <c>samples/Sample.Godot</c>, and that needs a binary this repo deliberately does not fetch.
/// <para>
/// CI downloads the Godot .NET build and sets <c>GODOT_BIN</c> in <c>master.yml</c>'s Debug
/// <c>desktop-and-core</c> leg (lavapipe, llvmpipe and WARP) and in the <c>godot-linux</c> job.
/// </para>
/// </summary>
public sealed class GodotBinaryTheoryAttribute : TheoryAttribute
{
    public const string EnvironmentVariable = "GODOT_BIN";

    public GodotBinaryTheoryAttribute()
    {
        var path = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(path))
            Skip = $"{EnvironmentVariable} is not set - point it at a Godot 4.7+ .NET build's executable to run the Godot sample end to end.";
        else if (!File.Exists(path))
            Skip = $"{EnvironmentVariable} is set to '{path}', which does not exist.";
    }

    /// <summary>Skips everywhere but Windows (Godot offers d3d12 only there).</summary>
    public bool WindowsOnly
    {
        get => false;
        set
        {
            if (value && Skip == null && !OperatingSystem.IsWindows())
                Skip = "Windows only.";
        }
    }

    /// <summary>Skips on macOS, which the Godot adapter does not support on any driver.</summary>
    public bool SkipOnMacOS
    {
        get => false;
        set
        {
            if (value && Skip == null && OperatingSystem.IsMacOS())
                Skip = "Not supported on macOS.";
        }
    }
}
