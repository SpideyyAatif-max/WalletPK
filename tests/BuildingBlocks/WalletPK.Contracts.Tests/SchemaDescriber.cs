using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Google.Protobuf.Reflection;
using WalletPK.Messaging.Events;
using WalletPK.Messaging.Serialization;

namespace WalletPK.Contracts.Tests;

/// <summary>One integration event as it travels on Kafka: its name, version, topic and JSON fields.</summary>
public sealed record EventSchema(string Name, int Version, string Topic, string ClrType, SortedDictionary<string, string> Fields);

/// <summary>One protobuf message field: number → "name type".</summary>
public sealed record ProtoMessage(string Name, SortedDictionary<int, string> Fields);

public sealed record ProtoFile(string Name, string Package, SortedDictionary<string, string> Methods, SortedDictionary<string, ProtoMessage> Messages);

/// <summary>Describes the contracts in WalletPK.Contracts the way consumers see them.</summary>
public static class SchemaDescriber
{
    public static Assembly Contracts { get; } = typeof(Ledger.WalletEntryTitles).Assembly;

    /// <summary>The serializer's settings, with its default resolver (so names and converters match the wire).</summary>
    private static readonly JsonSerializerOptions Wire = new(JsonEventSerializer.Options) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    public static IReadOnlyList<Type> EventTypes() =>
        [.. Contracts.GetTypes()
            .Where(type => typeof(IntegrationEvent).IsAssignableFrom(type) && type is { IsAbstract: false, IsGenericTypeDefinition: false })
            .OrderBy(type => type.FullName, StringComparer.Ordinal)];

    public static SortedDictionary<string, EventSchema> Events()
    {
        var schemas = new SortedDictionary<string, EventSchema>(StringComparer.Ordinal);
        foreach (var type in EventTypes())
        {
            var attribute = type.GetCustomAttribute<IntegrationEventAttribute>()
                ?? throw new InvalidOperationException($"{type.FullName} has no [IntegrationEvent] attribute.");
            schemas.Add(attribute.Name, new EventSchema(attribute.Name, attribute.Version, attribute.Topic, type.FullName!, FieldsOf(type, [])));
        }

        return schemas;
    }

    /// <summary>JSON property name → JSON shape, from the serializer's own contract (so names match the wire).</summary>
    private static SortedDictionary<string, string> FieldsOf(Type type, HashSet<Type> seen)
    {
        var info = Wire.GetTypeInfo(type);
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var nullability = new NullabilityInfoContext();
        foreach (var property in info.Properties)
        {
            var nullable = Nullable.GetUnderlyingType(property.PropertyType) is not null
                || (!property.PropertyType.IsValueType && property.AttributeProvider is PropertyInfo clr && nullability.Create(clr).ReadState == NullabilityState.Nullable);
            fields.Add(property.Name, ShapeOf(property.PropertyType, seen) + (nullable ? "?" : ""));
        }

        return fields;
    }

    private static string ShapeOf(Type type, HashSet<Type> seen)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        var simple = type switch
        {
            _ when type == typeof(string) => "string",
            _ when type == typeof(Guid) => "uuid",
            _ when type == typeof(bool) => "boolean",
            _ when type == typeof(DateTimeOffset) || type == typeof(DateTime) => "date-time",
            _ when type == typeof(DateOnly) => "date",
            _ when type == typeof(decimal) || type == typeof(double) || type == typeof(float) => "number",
            { IsPrimitive: true } => "integer",
            { IsEnum: true } => $"enum({string.Join('|', Enum.GetNames(type))})",
            _ when type.IsGenericType && type.IsAssignableTo(typeof(IDictionary)) => $"map<{ShapeOf(type.GetGenericArguments()[1], seen)}>",
            _ => null,
        };
        if (simple is not null)
        {
            return simple;
        }

        if (type != typeof(string) && type.IsAssignableTo(typeof(IEnumerable)))
        {
            var element = type.IsArray ? type.GetElementType()! : type.GetGenericArguments().Single();
            return $"array<{ShapeOf(element, seen)}>";
        }

        if (!seen.Add(type))
        {
            return type.Name;
        }

        var fields = FieldsOf(type, seen);
        seen.Remove(type);
        return "{" + string.Join(", ", fields.Select(field => $"{field.Key}: {field.Value}")) + "}";
    }

    public static SortedDictionary<string, ProtoFile> Protos()
    {
        var files = new SortedDictionary<string, ProtoFile>(StringComparer.Ordinal);
        var descriptors = Contracts.GetTypes()
            .Select(type => type.GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static))
            .Where(property => property?.PropertyType == typeof(FileDescriptor))
            .Select(property => (FileDescriptor)property!.GetValue(null)!);
        foreach (var file in descriptors)
        {
            var methods = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var service in file.Services)
            {
                foreach (var method in service.Methods)
                {
                    methods.Add($"{service.Name}.{method.Name}", $"{method.InputType.FullName} -> {method.OutputType.FullName}");
                }
            }

            var messages = new SortedDictionary<string, ProtoMessage>(StringComparer.Ordinal);
            foreach (var message in file.MessageTypes.SelectMany(Flatten))
            {
                var fields = new SortedDictionary<int, string>();
                foreach (var field in message.Fields.InFieldNumberOrder())
                {
                    var kind = field.FieldType switch
                    {
                        FieldType.Message => field.MessageType.FullName,
                        FieldType.Enum => field.EnumType.FullName,
                        _ => field.FieldType.ToString().ToLowerInvariant(),
                    };
                    fields.Add(field.FieldNumber, $"{field.Name} {(field.IsRepeated ? "repeated " : "")}{kind}");
                }

                messages.Add(message.FullName, new ProtoMessage(message.FullName, fields));
            }

            files.Add(file.Name, new ProtoFile(file.Name, file.Package, methods, messages));
        }

        return files;
    }

    private static IEnumerable<MessageDescriptor> Flatten(MessageDescriptor message) => [message, .. message.NestedTypes.SelectMany(Flatten)];

    public static JsonSerializerOptions SnapshotOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };
}
