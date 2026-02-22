using System.Globalization;
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
        public object RawProgress { get; set; }

        [JsonIgnore]
        public double Progress
        {
            get
            {
                if (RawProgress == null)
                {
                    return 0;
                }

                if (RawProgress is double d)
                {
                    return d;
                }

                if (RawProgress is long l)
                {
                    return l;
                }

                if (double.TryParse(RawProgress.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }

                return 0;
            }
        }

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
