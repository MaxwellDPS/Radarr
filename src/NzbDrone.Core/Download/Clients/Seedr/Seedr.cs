using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Localization;
using NzbDrone.Core.MediaFiles.TorrentInfo;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;

namespace NzbDrone.Core.Download.Clients.Seedr
{
    public class Seedr : TorrentClientBase<SeedrSettings>
    {
        private readonly ISeedrProxy _proxy;
        private readonly ICached<SeedrDownloadMapping> _downloadCache;

        public Seedr(ISeedrProxy proxy,
                     ICacheManager cacheManager,
                     ITorrentFileInfoReader torrentFileInfoReader,
                     IHttpClient httpClient,
                     IConfigService configService,
                     IDiskProvider diskProvider,
                     IRemotePathMappingService remotePathMappingService,
                     ILocalizationService localizationService,
                     IBlocklistService blocklistService,
                     Logger logger)
            : base(torrentFileInfoReader, httpClient, configService, diskProvider, remotePathMappingService, localizationService, blocklistService, logger)
        {
            _proxy = proxy;
            _downloadCache = cacheManager.GetCache<SeedrDownloadMapping>(GetType());
        }

        public override string Name => "Seedr";

        protected override string AddFromMagnetLink(RemoteMovie remoteMovie, string hash, string magnetLink)
        {
            var transfer = _proxy.AddMagnet(magnetLink, Settings);

            _downloadCache.Set(hash.ToUpper(), new SeedrDownloadMapping
            {
                InfoHash = hash.ToUpper(),
                TransferId = transfer.Id,
                Name = transfer.Name
            });

            return hash;
        }

        protected override string AddFromTorrentFile(RemoteMovie remoteMovie, string hash, string filename, byte[] fileContent)
        {
            var transfer = _proxy.AddTorrentFile(filename, fileContent, Settings);

            _downloadCache.Set(hash.ToUpper(), new SeedrDownloadMapping
            {
                InfoHash = hash.ToUpper(),
                TransferId = transfer.Id,
                Name = transfer.Name
            });

            return hash;
        }

        public override IEnumerable<DownloadClientItem> GetItems()
        {
            var contents = _proxy.GetFolderContents(null, Settings);
            var items = new List<DownloadClientItem>();
            var cachedMappings = _downloadCache.Values.ToList();

            // Active transfers
            if (contents.Transfers != null)
            {
                foreach (var transfer in contents.Transfers)
                {
                    var mapping = cachedMappings.FirstOrDefault(m => m.TransferId == transfer.Id) ??
                                  cachedMappings.FirstOrDefault(m => m.Name == transfer.Name);

                    var infoHash = mapping?.InfoHash ?? transfer.Hash?.ToUpper() ?? $"seedr-{transfer.Id}";

                    // Update cache with transfer info if we have a hash from the transfer
                    if (mapping == null && transfer.Hash.IsNotNullOrWhiteSpace())
                    {
                        mapping = new SeedrDownloadMapping
                        {
                            InfoHash = infoHash,
                            TransferId = transfer.Id,
                            Name = transfer.Name
                        };

                        _downloadCache.Set(infoHash, mapping);
                    }

                    var item = new DownloadClientItem
                    {
                        DownloadClientInfo = DownloadClientItemClientInfo.FromDownloadClient(this, false),
                        DownloadId = infoHash,
                        Title = transfer.Name,
                        TotalSize = transfer.Size,
                        RemainingSize = transfer.Size - (long)(transfer.Size * (transfer.Progress / 100.0)),
                        Status = DownloadItemStatus.Downloading,
                        CanMoveFiles = false,
                        CanBeRemoved = false
                    };

                    items.Add(item);
                }
            }

            // Completed folders
            if (contents.Folders != null)
            {
                foreach (var folder in contents.Folders)
                {
                    var mapping = cachedMappings.FirstOrDefault(m => m.Name == folder.Name) ??
                                  cachedMappings.FirstOrDefault(m => m.FolderId == folder.Id);

                    if (mapping == null)
                    {
                        continue;
                    }

                    // Update cache with folder ID
                    mapping.FolderId = folder.Id;
                    _downloadCache.Set(mapping.InfoHash, mapping);

                    var localPath = Path.Combine(Settings.DownloadDirectory, folder.Name);

                    if (mapping.LocalDownloadComplete || _diskProvider.FolderExists(localPath))
                    {
                        mapping.LocalDownloadComplete = true;
                        _downloadCache.Set(mapping.InfoHash, mapping);

                        items.Add(new DownloadClientItem
                        {
                            DownloadClientInfo = DownloadClientItemClientInfo.FromDownloadClient(this, false),
                            DownloadId = mapping.InfoHash,
                            Title = folder.Name,
                            TotalSize = folder.Size,
                            RemainingSize = 0,
                            Status = DownloadItemStatus.Completed,
                            OutputPath = new OsPath(localPath),
                            CanMoveFiles = true,
                            CanBeRemoved = true
                        });
                    }
                    else
                    {
                        DownloadFolderFromCloud(folder, mapping);

                        items.Add(new DownloadClientItem
                        {
                            DownloadClientInfo = DownloadClientItemClientInfo.FromDownloadClient(this, false),
                            DownloadId = mapping.InfoHash,
                            Title = folder.Name,
                            TotalSize = folder.Size,
                            RemainingSize = folder.Size,
                            Status = DownloadItemStatus.Downloading,
                            Message = "Downloading from Seedr cloud",
                            CanMoveFiles = false,
                            CanBeRemoved = false
                        });
                    }
                }
            }

            // Completed single files in root folder
            if (contents.Files != null)
            {
                foreach (var file in contents.Files)
                {
                    var mapping = cachedMappings.FirstOrDefault(m => m.Name == file.Name);

                    if (mapping == null)
                    {
                        continue;
                    }

                    var localPath = Path.Combine(Settings.DownloadDirectory, file.Name);

                    if (mapping.LocalDownloadComplete || _diskProvider.FileExists(localPath))
                    {
                        mapping.LocalDownloadComplete = true;
                        _downloadCache.Set(mapping.InfoHash, mapping);

                        items.Add(new DownloadClientItem
                        {
                            DownloadClientInfo = DownloadClientItemClientInfo.FromDownloadClient(this, false),
                            DownloadId = mapping.InfoHash,
                            Title = file.Name,
                            TotalSize = file.Size,
                            RemainingSize = 0,
                            Status = DownloadItemStatus.Completed,
                            OutputPath = new OsPath(localPath),
                            CanMoveFiles = true,
                            CanBeRemoved = true
                        });
                    }
                    else
                    {
                        DownloadFileFromCloud(file, mapping);

                        items.Add(new DownloadClientItem
                        {
                            DownloadClientInfo = DownloadClientItemClientInfo.FromDownloadClient(this, false),
                            DownloadId = mapping.InfoHash,
                            Title = file.Name,
                            TotalSize = file.Size,
                            RemainingSize = file.Size,
                            Status = DownloadItemStatus.Downloading,
                            Message = "Downloading from Seedr cloud",
                            CanMoveFiles = false,
                            CanBeRemoved = false
                        });
                    }
                }
            }

            return items;
        }

        public override void RemoveItem(DownloadClientItem item, bool deleteData)
        {
            var mapping = _downloadCache.Find(item.DownloadId);

            if (mapping?.FolderId != null)
            {
                _proxy.DeleteFolder(mapping.FolderId.Value, Settings);
            }
            else if (mapping?.TransferId != null)
            {
                _proxy.DeleteTransfer(mapping.TransferId.Value, Settings);
            }

            if (deleteData)
            {
                DeleteItemData(item);
            }

            _downloadCache.Remove(item.DownloadId);
        }

        public override DownloadClientInfo GetStatus()
        {
            return new DownloadClientInfo
            {
                IsLocalhost = true,
                OutputRootFolders = new List<OsPath> { new OsPath(Settings.DownloadDirectory) }
            };
        }

        public override void MarkItemAsImported(DownloadClientItem downloadClientItem)
        {
            if (Settings.DeleteFromCloud)
            {
                var mapping = _downloadCache.Find(downloadClientItem.DownloadId);

                if (mapping?.FolderId != null)
                {
                    _proxy.DeleteFolder(mapping.FolderId.Value, Settings);
                }
            }

            _downloadCache.Remove(downloadClientItem.DownloadId);
        }

        protected override void Test(List<ValidationFailure> failures)
        {
            SeedrUser user = null;

            try
            {
                user = _proxy.GetUser(Settings);
            }
            catch (DownloadClientAuthenticationException ex)
            {
                failures.Add(new ValidationFailure("Email", ex.Message));
                return;
            }
            catch (Exception ex)
            {
                failures.Add(new ValidationFailure("Email", ex.Message));
                return;
            }

            if (!user.IsPremium)
            {
                failures.Add(new ValidationFailure("Email", _localizationService.GetLocalizedString("DownloadClientSeedrValidationPremiumRequired")));
            }

            var folderFailure = TestFolder(Settings.DownloadDirectory, "DownloadDirectory");

            if (folderFailure != null)
            {
                failures.Add(folderFailure);
            }
        }

        private void DownloadFolderFromCloud(SeedrSubFolder folder, SeedrDownloadMapping mapping)
        {
            if (mapping.LocalDownloadInProgress)
            {
                return;
            }

            mapping.LocalDownloadInProgress = true;
            _downloadCache.Set(mapping.InfoHash, mapping);

            var settings = Settings;

            Task.Run(() =>
            {
                try
                {
                    var folderContents = _proxy.GetFolderContents(folder.Id, settings);
                    var localDir = Path.Combine(settings.DownloadDirectory, folder.Name);

                    _diskProvider.CreateFolder(localDir);

                    foreach (var file in folderContents.Files)
                    {
                        var response = _proxy.DownloadFile(file.Id, settings);
                        var filePath = Path.Combine(localDir, file.Name);

                        File.WriteAllBytes(filePath, response.ResponseData);
                    }

                    mapping.LocalDownloadComplete = true;
                    mapping.LocalDownloadInProgress = false;
                    _downloadCache.Set(mapping.InfoHash, mapping);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to download folder '{0}' from Seedr cloud", folder.Name);
                    mapping.LocalDownloadInProgress = false;
                    _downloadCache.Set(mapping.InfoHash, mapping);
                }
            });
        }

        private void DownloadFileFromCloud(SeedrFile file, SeedrDownloadMapping mapping)
        {
            if (mapping.LocalDownloadInProgress)
            {
                return;
            }

            mapping.LocalDownloadInProgress = true;
            _downloadCache.Set(mapping.InfoHash, mapping);

            var settings = Settings;

            Task.Run(() =>
            {
                try
                {
                    var response = _proxy.DownloadFile(file.Id, settings);
                    var filePath = Path.Combine(settings.DownloadDirectory, file.Name);

                    File.WriteAllBytes(filePath, response.ResponseData);

                    mapping.LocalDownloadComplete = true;
                    mapping.LocalDownloadInProgress = false;
                    _downloadCache.Set(mapping.InfoHash, mapping);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to download file '{0}' from Seedr cloud", file.Name);
                    mapping.LocalDownloadInProgress = false;
                    _downloadCache.Set(mapping.InfoHash, mapping);
                }
            });
        }

        private class SeedrDownloadMapping
        {
            public string InfoHash { get; set; }
            public long? TransferId { get; set; }
            public long? FolderId { get; set; }
            public string Name { get; set; }
            public bool LocalDownloadComplete { get; set; }
            public bool LocalDownloadInProgress { get; set; }
        }
    }
}
