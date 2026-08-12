namespace OokiGrader.Host.Services;

/// <summary>
/// States that are safe to dispatch at runtime. A capability-passed profile is
/// used by the one-step Gemini setup and means only that the configured model
/// passed the technical image/JSON probe; it does not claim that a separate
/// accuracy evaluation was performed.
/// </summary>
internal static class AiTaskProfileRuntimePolicy
{
    public static readonly string[] ReadyApprovalStates =
    [
        "capability_passed",
        "pilot_approved",
        "production_approved",
    ];

    public static bool IsReadyApprovalState(string state) =>
        state is "capability_passed"
            or "pilot_approved"
            or "production_approved";
}
