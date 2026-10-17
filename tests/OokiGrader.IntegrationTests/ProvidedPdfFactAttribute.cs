namespace OokiGrader.IntegrationTests;

// Private customer files are supplied only at test time, never committed.
internal sealed class ProvidedPdfFactAttribute(string environmentVariable) : FactAttribute
{
    public override string? Skip { get; set; } =
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(environmentVariable))
            ? $"Requires {environmentVariable}."
            : null;
}
