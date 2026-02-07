using System.Collections.Generic;
using Newtonsoft.Json;

namespace NzbDrone.Core.Download.Clients.Seedr
{
    public class SeedrFolderContents
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("folders")]
        public List<SeedrSubFolder> Folders { get; set; }

        [JsonProperty("files")]
        public List<SeedrFile> Files { get; set; }

        [JsonProperty("transfers")]
        public List<SeedrTransfer> Transfers { get; set; }

        [JsonProperty("space_used")]
        public long SpaceUsed { get; set; }

        [JsonProperty("space_max")]
        public long SpaceMax { get; set; }
    }

    public class SeedrSubFolder
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("size")]
        public long Size { get; set; }
    }

    public class SeedrFile
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("size")]
        public long Size { get; set; }

        [JsonProperty("folder_id")]
        public long FolderId { get; set; }
    }
}
