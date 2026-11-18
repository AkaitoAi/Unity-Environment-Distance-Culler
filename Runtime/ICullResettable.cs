namespace EnvironmentCulling
{
    /// <summary>
    /// Optional interface for stateful or destructible props managed by
    /// <see cref="EnvironmentDistanceCuller"/>. When an object is culled out of range,
    /// <see cref="ResetOnCull"/> is invoked if <see cref="IsBroken"/> is true.
    /// </summary>
    public interface ICullResettable
    {
        bool IsBroken { get; }
        void ResetOnCull();
    }
}
