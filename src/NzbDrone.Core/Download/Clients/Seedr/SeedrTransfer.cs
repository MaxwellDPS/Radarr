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

    // Transfer creation responses use different field names than folder listing transfers
    public class SeedrAddTransferResponse
    {
        [JsonProperty("user_torrent_id")]
        public long Id { get; set; }

        [JsonProperty("title")]
        public string Name { get; set; }

        [JsonProperty("torrent_hash")]
        public string Hash { get; set; }
    }
}
