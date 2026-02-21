using System;
using System.IO;
using System.Net;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;

namespace NzbDrone.Core.Download.Clients.Seedr
{
    public interface ISeedrProxy
    {
        SeedrFolderContents GetFolderContents(long? folderId, SeedrSettings settings);
        SeedrTransfer AddMagnet(string magnetLink, SeedrSettings settings);
        SeedrTransfer AddTorrentFile(string filename, byte[] fileContent, SeedrSettings settings);
        void DeleteTransfer(long transferId, SeedrSettings settings);
        void DeleteFolder(long folderId, SeedrSettings settings);
        void DeleteFile(long fileId, SeedrSettings settings);
        SeedrUser GetUser(SeedrSettings settings);
        void DownloadFileToPath(long fileId, string filePath, SeedrSettings settings);
    }

    public class SeedrProxy : ISeedrProxy
    {
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public SeedrProxy(IHttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        private HttpRequestBuilder BuildRequest(SeedrSettings settings)
        {
            var requestBuilder = new HttpRequestBuilder("https://www.seedr.cc/rest")
            {
                LogResponseContent = false,
                NetworkCredential = new BasicNetworkCredential(settings.Email, settings.Password)
            };

            requestBuilder.Headers.Accept = "application/json";

            return requestBuilder;
        }

        private HttpResponse HandleRequest(HttpRequest request)
        {
            try
            {
                return _httpClient.Execute(request);
            }
            catch (HttpException ex)
            {
                if (ex.Response?.StatusCode == HttpStatusCode.Forbidden ||
                    ex.Response?.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new DownloadClientAuthenticationException("Failed to authenticate with Seedr.");
                }

                throw new DownloadClientException("Unable to connect to Seedr, please check your settings");
            }
            catch (DownloadClientAuthenticationException)
            {
                throw;
            }
            catch
            {
                throw new DownloadClientException("Unable to connect to Seedr, please check your settings");
            }
        }

        public SeedrFolderContents GetFolderContents(long? folderId, SeedrSettings settings)
        {
            var resource = folderId.HasValue ? $"/folder/{folderId.Value}" : "/folder";
            var request = BuildRequest(settings).Resource(resource).Build();

            return DeserializeResponse<SeedrFolderContents>(HandleRequest(request));
        }

        public SeedrTransfer AddMagnet(string magnetLink, SeedrSettings settings)
        {
            var request = BuildRequest(settings)
                .Resource("/transfer/magnet")
                .Post()
                .AddFormParameter("magnet", magnetLink)
                .Build();

            return DeserializeResponse<SeedrTransfer>(HandleRequest(request));
        }

        public SeedrTransfer AddTorrentFile(string filename, byte[] fileContent, SeedrSettings settings)
        {
            var request = BuildRequest(settings)
                .Resource("/transfer/file")
                .Post()
                .AddFormUpload("file", filename, fileContent, "application/x-bittorrent")
                .Build();

            return DeserializeResponse<SeedrTransfer>(HandleRequest(request));
        }

        public void DeleteTransfer(long transferId, SeedrSettings settings)
        {
            var request = BuildRequest(settings).Resource($"/transfer/{transferId}").Build();
            request.Method = System.Net.Http.HttpMethod.Delete;

            HandleRequest(request);
        }

        public void DeleteFolder(long folderId, SeedrSettings settings)
        {
            var request = BuildRequest(settings).Resource($"/folder/{folderId}").Build();
            request.Method = System.Net.Http.HttpMethod.Delete;

            HandleRequest(request);
        }

        public void DeleteFile(long fileId, SeedrSettings settings)
        {
            var request = BuildRequest(settings).Resource($"/file/{fileId}").Build();
            request.Method = System.Net.Http.HttpMethod.Delete;

            HandleRequest(request);
        }

        public SeedrUser GetUser(SeedrSettings settings)
        {
            var request = BuildRequest(settings).Resource("/user").Build();

            return DeserializeResponse<SeedrUser>(HandleRequest(request));
        }

        private T DeserializeResponse<T>(HttpResponse response)
        {
            if (response.Content.IsNullOrWhiteSpace())
            {
                throw new DownloadClientException("Seedr API returned an empty response");
            }

            return Json.Deserialize<T>(response.Content);
        }

        public void DownloadFileToPath(long fileId, string filePath, SeedrSettings settings)
        {
            var requestBuilder = BuildRequest(settings);
            requestBuilder.AllowAutoRedirect = true;
            var request = requestBuilder.Resource($"/file/{fileId}").Build();
            request.RequestTimeout = TimeSpan.FromMinutes(30);

            var filePartPath = filePath + ".part";

            try
            {
                using (var fileStream = new FileStream(filePartPath, FileMode.Create, FileAccess.Write))
                {
                    request.ResponseStream = fileStream;
                    HandleRequest(request);
                }

                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }

                File.Move(filePartPath, filePath);
            }
            finally
            {
                if (File.Exists(filePartPath))
                {
                    File.Delete(filePartPath);
                }
            }
        }
    }
}
