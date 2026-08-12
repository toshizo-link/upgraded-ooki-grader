namespace OokiGrader.Host.Services;

public static class TemplateVersionUsePolicy
{
    public static bool IsImmutablePublishedSnapshot(string state) =>
        state is "published" or "superseded" or "retired";
}
