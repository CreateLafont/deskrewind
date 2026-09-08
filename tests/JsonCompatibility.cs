#if POWERSHELL_TEST
// Test host only: the shipping Framework build keeps System.Web.Extensions.
namespace System.Web.Script.Serialization
{
    public sealed class JavaScriptSerializer
    {
        private static readonly System.Text.Json.JsonSerializerOptions Options = new System.Text.Json.JsonSerializerOptions { IncludeFields = true };
        public string Serialize(object value) { return System.Text.Json.JsonSerializer.Serialize(value, Options); }
        public T Deserialize<T>(string text) { return System.Text.Json.JsonSerializer.Deserialize<T>(text, Options); }
    }
}
#endif
