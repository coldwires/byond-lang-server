namespace Dm.Native;

/// <summary>
/// Status codes returned across the C ABI. Mirrored in <c>abi/dm_core.h</c>; keep both in sync.
/// </summary>
internal enum DmStatus
{
    Ok = 0,
    InvalidArgument = 1,
    InvalidHandle = 2,
    NotFound = 3,
    OutOfMemory = 4,
    Internal = 5,
}
