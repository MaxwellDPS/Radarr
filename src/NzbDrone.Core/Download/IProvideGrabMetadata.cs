using System.Collections.Generic;

namespace NzbDrone.Core.Download
{
    public interface IProvideGrabMetadata
    {
        Dictionary<string, string> GetGrabMetadata(string downloadId);
    }
}
