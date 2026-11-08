using System.Text.Json;
using System.Text.Json.Serialization;

namespace WalletPK.Common.Primitives;

/// <summary>
/// Writes money as exact minor units plus currency, for example
/// <c>{"minorUnits":125050,"currency":"PKR"}</c> for Rs. 1,250.50. Never as a floating-point number.
/// </summary>
public sealed class MoneyJsonConverter : JsonConverter<Money>
{
    private const string MinorUnitsProperty = "minorUnits";
    private const string CurrencyProperty = "currency";

    public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Money must be a JSON object like {\"minorUnits\":125050,\"currency\":\"PKR\"}.");
        }

        long? minorUnits = null;
        string? currencyCode = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Unexpected token while reading Money.");
            }

            var propertyName = reader.GetString();
            reader.Read();

            if (string.Equals(propertyName, MinorUnitsProperty, StringComparison.OrdinalIgnoreCase))
            {
                if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out var value))
                {
                    throw new JsonException($"'{MinorUnitsProperty}' must be a whole number of minor units (paisa).");
                }

                minorUnits = value;
            }
            else if (string.Equals(propertyName, CurrencyProperty, StringComparison.OrdinalIgnoreCase))
            {
                currencyCode = reader.GetString();
            }
            else
            {
                reader.Skip();
            }
        }

        if (minorUnits is null)
        {
            throw new JsonException($"Money requires a '{MinorUnitsProperty}' property.");
        }

        if (currencyCode is not null && !Currency.TryFromCode(currencyCode, out _))
        {
            throw new JsonException($"Unsupported currency '{currencyCode}'.");
        }

        var currency = currencyCode is null ? Currency.Pkr : Currency.FromCode(currencyCode);
        return Money.FromMinorUnits(minorUnits.Value, currency);
    }

    public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteNumber(MinorUnitsProperty, value.MinorUnits);
        writer.WriteString(CurrencyProperty, value.Currency.Code);
        writer.WriteEndObject();
    }
}
