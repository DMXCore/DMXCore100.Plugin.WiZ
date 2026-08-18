using System.Text;
using System.Text.Json;

namespace DMXCore100.WiZ;

/// <summary>
/// The WiZ JSON request datagrams the plugin sends besides setPilot, and the
/// parsing of the replies (every reply is <c>{"method":..., "env":"pro",
/// "result":{...}}</c>, or <c>"error":{...}</c>).
/// </summary>
internal static class WizMessages
{
    /// <summary>
    /// Discovery probe: a registration with <c>register:false</c> makes every
    /// light answer <c>{"method":"registration","result":{"mac":...}}</c>
    /// without actually subscribing us to its push updates. Same message the
    /// WiZ app and pywizlight broadcast.
    /// </summary>
    public static readonly byte[] Registration = Encoding.UTF8.GetBytes(
        """{"method":"registration","params":{"phoneMac":"AAAAAAAAAAAA","register":false,"phoneIp":"1.2.3.4","id":"1"}}""");

    public static readonly byte[] GetSystemConfig = Encoding.UTF8.GetBytes(
        """{"method":"getSystemConfig","params":{}}""");

    public static readonly byte[] GetPilot = Encoding.UTF8.GetBytes(
        """{"method":"getPilot","params":{}}""");

    /// <summary>
    /// Parse one reply. Returns false when the datagram is not a WiZ reply
    /// with a method and a result object.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> datagram, out string method, out JsonElement result)
    {
        method = "";
        result = default;
        try
        {
            using var document = JsonDocument.Parse(datagram.ToArray());
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("method", out JsonElement methodElement)
                || methodElement.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("result", out JsonElement resultElement)
                || resultElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            method = methodElement.GetString() ?? "";
            result = resultElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string? GetString(JsonElement result, string name) =>
        result.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// The MAC from a registration / config reply, normalized to 12 lowercase
    /// hex digits; null when absent or malformed.
    /// </summary>
    public static string? GetMac(JsonElement result)
    {
        string? mac = GetString(result, "mac");
        if (mac == null)
        {
            return null;
        }

        string normalized = mac.Replace(":", "").Replace("-", "").Trim().ToLowerInvariant();
        return normalized.Length == 12 && normalized.All(Uri.IsHexDigit) ? normalized : null;
    }
}
