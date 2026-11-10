using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HmaMcp;

static class Json {
   static readonly JsonSerializerOptions options = new() {
      WriteIndented = true,
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
      Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
   };

   public static string Of(object value) => JsonSerializer.Serialize(value, options);
}
