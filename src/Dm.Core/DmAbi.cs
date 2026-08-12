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
    /// 25: dm_complete_at gains the "IconState" context and the "value" item kind — inside an
    ///     `icon_state = "…"`, the states of the icon that type actually uses. Additive; no export
    ///     changed, and a client that does not know either word treats them as it already treats an
    ///     unknown one.
    /// 24: added dm_icon_states — the icon states in a .dmi, which is M8.
    /// 23: added dm_document_colors — the colours written in a file, with the text to write back.
    /// 22: completion items carry "typeFrom" - which route produced the receiver's type, so a
    ///     client can distinguish an `as` clause the author WROTE from a type we inferred.
    /// 21: completion items carry "type" and "value" — the item's own declared type and its
    ///     initialiser as written. Additive; no export changed.
    /// 20: added dm_workspace_open_standalone, and the .dme tickmark trio dm_dme_is_ticked /
    ///     dm_dme_tick / dm_dme_untick.
    /// 19: added dm_type_definition_at, dm_folding_ranges, dm_document_links and
    ///     dm_file_in_project — the editor-shaped surfaces, and the out-of-project signal.
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
    public const int Minor = 25;

    public static int Packed => (Major << 16) | Minor;
}
