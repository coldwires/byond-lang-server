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

    /// <summary>
    /// 2: added dm_set_buffer, dm_close_buffer, and the dm_classify_range family.
    /// 1: workspace open/close/root.
    /// </summary>
    public const int Minor = 2;

    public static int Packed => (Major << 16) | Minor;
}
