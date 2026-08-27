using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace McpVs2010.Bridge.Protocol
{
    internal static class JsonWire
    {
        public static string Serialize<T>(T value)
        {
            return SerializeCore(value, typeof(T));
        }

        public static string SerializeObject(object value)
        {
            return SerializeCore(value, value.GetType());
        }

        public static T Deserialize<T>(string json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            using (MemoryStream stream = new MemoryStream(bytes))
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T));
                return (T)serializer.ReadObject(stream);
            }
        }

        private static string SerializeCore(object value, System.Type type)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(type);
                serializer.WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }
}

