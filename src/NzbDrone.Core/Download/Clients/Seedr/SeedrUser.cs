using Newtonsoft.Json;

namespace NzbDrone.Core.Download.Clients.Seedr
{
    public class SeedrUserResponse
    {
        [JsonProperty("account")]
        public SeedrUser Account { get; set; }
    }

    public class SeedrUser
    {
        [JsonProperty("username")]
        public string Username { get; set; }

        [JsonProperty("email")]
        public string Email { get; set; }

        [JsonProperty("space_used")]
        public long SpaceUsed { get; set; }

        [JsonProperty("space_max")]
        public long SpaceMax { get; set; }
    }
}
