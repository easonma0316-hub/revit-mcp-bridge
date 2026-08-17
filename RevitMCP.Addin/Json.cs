using System;
using System.Collections.Generic;

#if NET48
using System.Web.Script.Serialization;
#else
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
#endif

namespace RevitMCP.Addin
{
    /// <summary>
    /// JSON (de)serialization behind one tiny API so the rest of the add-in is
    /// runtime-agnostic.
    ///
    ///  - .NET Framework 4.8 (Revit 2024): System.Web.Extensions'
    ///    JavaScriptSerializer - ships with the framework, no NuGet, no clash with
    ///    the Newtonsoft copy Revit already loads.
    ///  - .NET 8 (Revit 2025+): System.Text.Json from the BCL.
    ///
    /// Both sides produce the same CLR shapes for incoming JSON so
    /// <see cref="CommandRouter"/> can stay unchanged: objects become
    /// <c>Dictionary&lt;string, object&gt;</c>, arrays become an
    /// <c>IEnumerable</c> of objects, numbers become long/double, plus
    /// string/bool/null.
    /// </summary>
    internal static class Json
    {
#if NET48
        // Large models can produce big element lists.
        private static readonly JavaScriptSerializer Serializer =
            new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };

        public static string Serialize(object value) => Serializer.Serialize(value);

        public static Dictionary<string, object> DeserializeObject(string json)
            => Serializer.Deserialize<Dictionary<string, object>>(json);
#else
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            // Keep non-ASCII (e.g. Chinese element names) readable instead of \uXXXX.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            // Revit can hand back NaN/Infinity for degenerate geometry; don't throw.
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            WriteIndented = false
        };

        public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);

        /// <summary>Parses a JSON object into plain CLR containers/primitives.</summary>
        public static Dictionary<string, object> DeserializeObject(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            using (var doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    throw new JsonException("Top-level JSON value must be an object.");
                return (Dictionary<string, object>)ToClr(doc.RootElement);
            }
        }

        private static object ToClr(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    var dict = new Dictionary<string, object>();
                    foreach (var prop in el.EnumerateObject())
                        dict[prop.Name] = ToClr(prop.Value);
                    return dict;
                case JsonValueKind.Array:
                    var list = new List<object>();
                    foreach (var item in el.EnumerateArray())
                        list.Add(ToClr(item));
                    return list;
                case JsonValueKind.String:
                    return el.GetString();
                case JsonValueKind.Number:
                    if (el.TryGetInt64(out var l)) return l;
                    return el.GetDouble();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                default:
                    return null;
            }
        }
#endif
    }
}
