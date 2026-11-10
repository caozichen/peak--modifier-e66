using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakWorkshop.Protocol;

namespace PeakWorkshop.Plugin
{
    // Use the JSON library already shipped with PEAK. Unity's runtime serializer
    // omitted the nested FeatureValue[] field from actual in-game responses.
    internal static class WireJson
    {
        private static JsonSerializer CreateSerializer() => JsonSerializer.Create(new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.None,
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            DateParseHandling = DateParseHandling.None,
            FloatParseHandling = FloatParseHandling.Double,
            ObjectCreationHandling = ObjectCreationHandling.Replace,
            Culture = CultureInfo.InvariantCulture,
            MaxDepth = 8,
            CheckAdditionalContent = true
        });

        internal static WireMessage Deserialize(string json)
        {
            if (json == null || json.Length > WireProtocol.MaxMessageBytes ||
                Encoding.UTF8.GetByteCount(json) > WireProtocol.MaxMessageBytes)
                throw new JsonSerializationException("协议消息超过长度上限。");
            using (var text = new StringReader(json))
            using (var reader = new JsonTextReader(text)
            {
                MaxDepth = 8,
                DateParseHandling = DateParseHandling.None,
                FloatParseHandling = FloatParseHandling.Double,
                Culture = CultureInfo.InvariantCulture
            })
            {
                var root = JToken.Load(reader, new JsonLoadSettings
                {
                    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
                }) as JObject;
                if (root == null) throw new JsonSerializationException("协议消息必须是对象。");
                if (reader.Read()) throw new JsonSerializationException("协议消息包含额外内容。");
                ValidateShape(root);
                return root.ToObject<WireMessage>(CreateSerializer()) ?? throw new JsonSerializationException("协议消息为空。");
            }
        }

        internal static string Serialize(WireMessage message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            using (var text = new StringWriter(CultureInfo.InvariantCulture))
            using (var writer = new JsonTextWriter(text))
            {
                CreateSerializer().Serialize(writer, message);
                string json = text.ToString();
                if (Encoding.UTF8.GetByteCount(json) > WireProtocol.MaxMessageBytes)
                    throw new JsonSerializationException("协议响应超过长度上限。");
                return json;
            }
        }

        private static void ValidateShape(JObject root)
        {
            foreach (var property in root.Properties())
            {
                var definition = CanonicalField(typeof(WireMessage), property);
                if (property.Name == "features")
                {
                    if (property.Value.Type == JTokenType.Null) continue;
                    var features = property.Value as JArray;
                    if (features == null || features.Count > FeatureCatalog.All.Length)
                        throw new JsonSerializationException("功能状态数组非法。");
                    foreach (var value in features)
                    {
                        if (value.Type == JTokenType.Null) continue; // FeatureSettings rejects missing entries.
                        var feature = value as JObject;
                        if (feature == null) throw new JsonSerializationException("功能状态必须是对象。");
                        foreach (var field in feature.Properties())
                        {
                            CanonicalField(typeof(FeatureValue), field);
                            if (field.Name == "id" || field.Name == "reason") Require(field, JTokenType.String, true);
                            if (field.Name == "enabled" || field.Name == "available") Require(field, JTokenType.Boolean);
                            if (field.Name == "value") RequireNumber(field);
                        }
                    }
                    continue;
                }
                if (definition == null) continue; // Unknown metadata never selects a runtime type.
                if (definition.FieldType == typeof(string)) Require(property, JTokenType.String, true);
                else if (definition.FieldType == typeof(bool)) Require(property, JTokenType.Boolean);
                else if (definition.FieldType == typeof(int)) Require(property, JTokenType.Integer);
                else if (definition.FieldType == typeof(float)) RequireNumber(property);
            }
        }

        private static FieldInfo CanonicalField(Type owner, JProperty property)
        {
            var field = owner.GetField(property.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (field != null && field.Name != property.Name)
                throw new JsonSerializationException("协议字段大小写非法：" + property.Name);
            return field;
        }

        private static void Require(JProperty property, JTokenType type, bool nullable = false)
        {
            if (property.Value.Type != type && !(nullable && property.Value.Type == JTokenType.Null))
                throw new JsonSerializationException("协议字段类型非法：" + property.Name);
        }

        private static void RequireNumber(JProperty property)
        {
            if (property.Value.Type != JTokenType.Integer && property.Value.Type != JTokenType.Float)
                throw new JsonSerializationException("协议数值类型非法：" + property.Name);
            double number = property.Value.Value<double>();
            if (double.IsNaN(number) || double.IsInfinity(number))
                throw new JsonSerializationException("协议数值必须为有限值：" + property.Name);
        }
    }
}
