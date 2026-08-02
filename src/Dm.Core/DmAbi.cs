namespace Dm.Core;

/// <summary>
/// Version of the native ABI exposed by Dm.Native.
/// </summary>
/// <remarks>
/// Packed as <c>(major &lt;&lt; 16) | minor</c> so a client can check compatibility with one call.
/// Additive changes bump minor. Breaking changes bump major and require downstream work, so the
/// bump is deliberate.
/// </remarks>
public static class DmAbi
{
    public const int Major = 0;
    public const int Minor = 1;

    public static int Packed => (Major << 16) | Minor;
}
