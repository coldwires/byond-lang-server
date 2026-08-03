using System.Collections.Generic;

namespace Dm.Core.Preprocessing;

/// <summary>
/// Tracks nested <c>#if</c> / <c>#elif</c> / <c>#else</c> / <c>#endif</c> regions.
/// </summary>
/// <remarks>
/// Three pieces of state per level, and all three are needed:
/// <list type="bullet">
/// <item><description><c>Active</c> — whether this branch's content is compiled.</description></item>
/// <item><description><c>AnyTaken</c> — whether an earlier branch at this level already won, so
/// later <c>#elif</c>s are skipped even when their conditions are true.</description></item>
/// <item><description><c>ParentActive</c> — whether the enclosing region was active. A branch inside
/// a skipped region stays skipped no matter what its own condition says, and conditions in a
/// skipped region must not be evaluated at all: they routinely reference macros that only exist in
/// the other branch.</description></item>
/// </list>
/// </remarks>
internal sealed class ConditionalStack
{
    private readonly struct Level
    {
        public Level(bool active, bool anyTaken, bool parentActive)
        {
            Active = active;
            AnyTaken = anyTaken;
            ParentActive = parentActive;
        }

        public bool Active { get; }

        public bool AnyTaken { get; }

        public bool ParentActive { get; }
    }

    private readonly List<Level> _levels = new();

    /// <summary>True when the current position is inside compiled code.</summary>
    public bool IsActive => _levels.Count == 0 || _levels[^1].Active;

    public int Depth => _levels.Count;

    /// <summary>
    /// Opens a region. <paramref name="evaluate"/> is only invoked when the enclosing region is
    /// active, so conditions inside skipped code are never evaluated.
    /// </summary>
    public void PushIf(System.Func<bool> evaluate)
    {
        bool parentActive = IsActive;
        bool taken = parentActive && evaluate();

        _levels.Add(new Level(taken, taken, parentActive));
    }

    /// <summary>Returns false if there is no open region to attach to.</summary>
    public bool Elif(System.Func<bool> evaluate)
    {
        if (_levels.Count == 0)
            return false;

        Level level = _levels[^1];

        bool taken = level.ParentActive && !level.AnyTaken && evaluate();
        _levels[^1] = new Level(taken, level.AnyTaken || taken, level.ParentActive);
        return true;
    }

    public bool Else()
    {
        if (_levels.Count == 0)
            return false;

        Level level = _levels[^1];

        bool taken = level.ParentActive && !level.AnyTaken;
        _levels[^1] = new Level(taken, true, level.ParentActive);
        return true;
    }

    public bool Endif()
    {
        if (_levels.Count == 0)
            return false;

        _levels.RemoveAt(_levels.Count - 1);
        return true;
    }
}
