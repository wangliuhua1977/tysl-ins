namespace Tysl.Inspection.Desktop.Contracts.Configuration;

public sealed class DatabaseOptions
{
    public string Path { get; set; } = System.IO.Path.Combine("data", "inspection.db");
}
