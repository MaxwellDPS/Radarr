using System;
using System.IO;
using System.Net;
using System.Threading;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;

namespace NzbDrone.Core.Download.Clients.Seedr
{
    public interface ISeedrProxy
    {
        SeedrFolderContents GetFolderContents(long? folderId, SeedrSettings settings);
        SeedrAddTransferResponse AddMagnet(string magnetLink, SeedrSettings settings);
        SeedrAddTransferResponse AddTorrentFile(string filename, byte[] fileContent, SeedrSettings settings);
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

        private HttpResponse HandleRequest(HttpRequest request, int maxRetries = 0)
        {
            for (var attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    return _httpClient.Execute(request);
                }
                catch (HttpException ex)
                {
                    var statusCode = (int)(ex.Response?.StatusCode ?? 0);
                    var isTransient = statusCode == 429 || statusCode >= 500 || ex.Response == null;

                    if (!isTransient || attempt == maxRetries)
                    {
                        if (ex.Response != null)
                        {
                            if (ex.Response.StatusCode == HttpStatusCode.Forbidden ||
                                ex.Response.StatusCode == HttpStatusCode.Unauthorized)
                            {
                                throw new DownloadClientAuthenticationException("Failed to authenticate with Seedr.");
                            }

                            if (statusCode == 429)
                            {
                                throw new DownloadClientException("Seedr API rate limit exceeded. Please try again later.");
                            }

                            if (ex.Response.StatusCode == HttpStatusCode.NotFound)
                            {
                                throw new DownloadClientException("Seedr API resource not found (404).");
                            }

                            if (statusCode >= 500)
                            {
                                throw new DownloadClientException($"Seedr API server error ({statusCode}). Please try again later.");
                            }

                            throw new DownloadClientException($"Seedr API request failed with status {statusCode}.");
                        }

                        throw new DownloadClientException("Unable to connect to Seedr, please check your settings");
                    }

                    var delay = (int)Math.Min(30000, 1000 * Math.Pow(2, attempt));
                    _logger.Debug("Transient error ({0}), retrying in {1}ms (attempt {2}/{3})", statusCode, delay, attempt + 1, maxRetries);
                    Thread.Sleep(delay);
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

            throw new DownloadClientException("Seedr API request failed after all retry attempts");
        }

        public SeedrFolderContents GetFolderContents(long? folderId, SeedrSettings settings)
        {
            var resource = folderId.HasValue ? $"/folder/{folderId.Value}" : "/folder";
            var request = BuildRequest(settings).Resource(resource).Build();

            var contents = DeserializeResponse<SeedrFolderContents>(HandleRequest(request));

            if (!contents.IsSuccess)
            {
                throw new DownloadClientException($"Seedr API returned error for folder {folderId}: result={contents.Result}, code={contents.Code}");
            }

            return contents;
        }

        public SeedrAddTransferResponse AddMagnet(string magnetLink, SeedrSettings settings)
        {
            var request = BuildRequest(settings)
                .Resource("/transfer/magnet")
                .Post()
                .AddFormParameter("magnet", magnetLink)
                .Build();

            return DeserializeResponse<SeedrAddTransferResponse>(HandleRequest(request));
        }

        public SeedrAddTransferResponse AddTorrentFile(string filename, byte[] fileContent, SeedrSettings settings)
        {
            var request = BuildRequest(settings)
                .Resource("/transfer/file")
                .Post()
                .AddFormUpload("file", filename, fileContent, "application/x-bittorrent")
                .Build();

            return DeserializeResponse<SeedrAddTransferResponse>(HandleRequest(request));
        }

        public void DeleteTransfer(long transferId, SeedrSettings settings)
        {
            var request = BuildRequest(settings).Resource($"/torrent/{transferId}").Build();
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
            var response = HandleRequest(request);

            var userResponse = DeserializeResponse<SeedrUserResponse>(response);

            return userResponse.Account
                   ?? throw new DownloadClientException("Seedr API returned a user response with no account data");
        }

        private T DeserializeResponse<T>(HttpResponse response)
            where T : new()
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
            requestBuilder.Headers.Accept = "*/*";
            var request = requestBuilder.Resource($"/file/{fileId}").Build();
            request.RequestTimeout = TimeSpan.FromMinutes(30);

            var filePartPath = filePath + ".part";

            try
            {
                using (var fileStream = new FileStream(filePartPath, FileMode.Create, FileAccess.Write))
                {
                    request.ResponseStream = fileStream;
                    HandleRequest(request, maxRetries: 2);
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
