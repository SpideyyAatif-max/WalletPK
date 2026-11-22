using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WalletPK.Contracts.Tests;

/// <summary>
/// The recorded contracts (Snapshots/*.json, reviewed like code). To record a deliberate change run the tests
/// once with WALLETPK_UPDATE_CONTRACTS=1; a breaking change is never recorded unless the version is raised.
/// </summary>
public static class Snapshots
{
    public const string UpdateVariable = "WALLETPK_UPDATE_CONTRACTS";

    public static bool Updating => Environment.GetEnvironmentVariable(UpdateVariable) == "1";

    public static T? Read<T>(string name)
        where T : class
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Snapshots", name);
        return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), SchemaDescriber.SnapshotOptions) : null;
    }

    public static void Write<T>(string name, T value, [CallerFilePath] string sourceFile = "")
    {
        var path = Path.Combine(Path.GetDirectoryName(sourceFile)!, "Snapshots", name);
        File.WriteAllText(path, Normalized(value) + "\n");
    }

    /// <summary>
    /// The same JSON text for equal content, whatever the file's line endings and however a dictionary read
    /// back orders its keys (objects are sorted ordinally, independent of the machine's culture).
    /// </summary>
    public static string Normalized<T>(T value) =>
        Sorted(JsonSerializer.SerializeToNode(value, SchemaDescriber.SnapshotOptions))?.ToJsonString(SchemaDescriber.SnapshotOptions) ?? "null";

    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(property => property.Key, StringComparer.Ordinal)
            .Select(property => KeyValuePair.Create(property.Key, Sorted(property.Value?.DeepClone())))),
        JsonArray array => new JsonArray([.. array.Select(item => Sorted(item?.DeepClone()))]),
        _ => node?.DeepClone(),
    };
}
