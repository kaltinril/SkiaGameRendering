using Xunit;

namespace Tests.Godot;

/// <summary>
/// Skips the decorated test at runtime unless the <c>GODOT_BIN</c> environment variable points at
/// a Godot 4.7+ .NET ("mono") build's executable. GodotSharp types only function inside a running
/// Godot process, so the Godot adapter cannot be exercised in-process the way the other backends'
/// tests exercise theirs; the only real test is launching the engine against
/// <c>samples/Sample.Godot</c>, and that needs a binary this repo deliberately does not fetch.
/// <para>
/// CI does not set <c>GODOT_BIN</c> today, so <c>Tests.proj</c>'s wildcard discovery runs this
/// project on <c>windows-latest</c> as a visible skip rather than a silent no-op. Wiring it up
/// would need the Godot .NET zip downloaded onto the runner plus a Vulkan or D3D12 device there
/// (<c>windows-latest</c> has no GPU; D3D12 WARP is plausible but unverified) - a decision for the
/// repo owner, not this test.
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

    /// <summary>Skips on macOS, where Godot's opengl3 driver uses a context type this library does not support.</summary>
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
