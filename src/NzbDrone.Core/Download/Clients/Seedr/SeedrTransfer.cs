using Newtonsoft.Json;

namespace NzbDrone.Core.Download.Clients.Seedr
{
    public class SeedrTransfer
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("progress")]
        public int Progress { get; set; }

        [JsonProperty("size")]
        public long Size { get; set; }

        [JsonProperty("hash")]
        public string Hash { get; set; }
    }
}
