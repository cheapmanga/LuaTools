namespace LuaTools.Addons;

/// <summary>
/// Where an addon writes. Addons get a log rather than a free hand with the host's logging because
/// every line is tagged with the addon id: when something misbehaves, the user needs to know WHICH
/// addon, and an addon cannot be trusted to say so itself.
/// </summary>
public interface IAddonLog
{
    /// <summary>Routine progress, kept out of the user's way.</summary>
    void Info(string message);

    /// <summary>Something the user may need to know, but that did not stop the addon.</summary>
    void Warn(string message);

    /// <summary>Records a failure. <paramref name="ex"/> is optional so callers can report a refusal
    /// that isn't an exception ("source returned 404").</summary>
    void Error(string message, Exception? ex = null);
}
