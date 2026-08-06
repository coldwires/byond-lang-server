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
    /// 18: completion is ranked by scope distance and reports "truncated"; added
    ///     dm_set_completion_limit.
    /// 17: added dm_complete_brief and dm_complete_resolve — lazy completion documentation.
    /// 16: added dm_inlay_hints — inferred-type annotations for untyped locals.
    /// 15: added dm_tree_ready and dm_build_tree, the readiness signal and the warm-at-open call.
    /// 14: dm_query_json gains "references" and "ancestorsOf"; added dm_invalidate.
    /// 13: added dm_diagnostics.
    /// 12: added dm_signature_at.
    /// 11: added dm_query_json, with the objectTree, subtypesOf and members queries.
    /// 10: dm_document_symbols diagnostics carry "severity"; warnings can now appear there.
    /// 9: completion items carry "documentation".
    /// 8: added dm_workspace_symbols.
    /// 7: added dm_hover_at.
    /// 6: added dm_definition_at.
    /// 5: added dm_set_defines.
    /// 4: added dm_complete_at.
    /// 3: added dm_document_symbols.
    /// 2: added dm_set_buffer, dm_close_buffer, and the dm_classify_range family.
    /// 1: workspace open/close/root.
    /// </summary>
    public const int Minor = 18;

    public static int Packed => (Major << 16) | Minor;
}
