using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
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

            if (contents == null)
            {
                return Enumerable.Empty<DownloadClientItem>();
            }

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
                    if (mapping == null && !string.IsNullOrWhiteSpace(transfer.Hash))
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

                    var safeFolderName = Path.GetFileName(folder.Name);
                    var localPath = Path.Combine(Settings.DownloadDirectory, safeFolderName);

                    if (mapping.LocalDownloadComplete || (!mapping.LocalDownloadInProgress && _diskProvider.FolderExists(localPath)))
                    {
                        mapping.LocalDownloadComplete = true;
                        mapping.LocalDownloadFailed = false;
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
                    else if (mapping.LocalDownloadFailed)
                    {
                        items.Add(new DownloadClientItem
                        {
                            DownloadClientInfo = DownloadClientItemClientInfo.FromDownloadClient(this, false),
                            DownloadId = mapping.InfoHash,
                            Title = folder.Name,
                            TotalSize = folder.Size,
                            RemainingSize = folder.Size,
                            Status = DownloadItemStatus.Warning,
                            Message = "Failed to download from Seedr cloud. Remove and re-add to retry.",
                            CanMoveFiles = false,
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

                    var safeFileName = Path.GetFileName(file.Name);
                    var localPath = Path.Combine(Settings.DownloadDirectory, safeFileName);

                    if (mapping.LocalDownloadComplete || (!mapping.LocalDownloadInProgress && _diskProvider.FileExists(localPath)))
                    {
                        mapping.LocalDownloadComplete = true;
                        mapping.LocalDownloadFailed = false;
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
                    else if (mapping.LocalDownloadFailed)
                    {
                        items.Add(new DownloadClientItem
                        {
                            DownloadClientInfo = DownloadClientItemClientInfo.FromDownloadClient(this, false),
                            DownloadId = mapping.InfoHash,
                            Title = file.Name,
                            TotalSize = file.Size,
                            RemainingSize = file.Size,
                            Status = DownloadItemStatus.Warning,
                            Message = "Failed to download from Seedr cloud. Remove and re-add to retry.",
                            CanMoveFiles = false,
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

            try
            {
                if (mapping?.FolderId != null)
                {
                    _proxy.DeleteFolder(mapping.FolderId.Value, Settings);
                }
                else if (mapping?.TransferId != null)
                {
                    _proxy.DeleteTransfer(mapping.TransferId.Value, Settings);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to remove item from Seedr cloud, removing from cache anyway");
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
                IsLocalhost = false,
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
            try
            {
                _proxy.GetUser(Settings);
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
            var infoHash = mapping.InfoHash;

            Task.Run(() =>
            {
                try
                {
                    var folderContents = _proxy.GetFolderContents(folder.Id, settings);
                    var safeFolderName = Path.GetFileName(folder.Name);
                    var localDir = Path.Combine(settings.DownloadDirectory, safeFolderName);

                    _diskProvider.CreateFolder(localDir);

                    if (folderContents.Files != null)
                    {
                        foreach (var file in folderContents.Files)
                        {
                            var safeFileName = Path.GetFileName(file.Name);
                            var filePath = Path.Combine(localDir, safeFileName);

                            _proxy.DownloadFileToPath(file.Id, filePath, settings);
                        }
                    }

                    var currentMapping = _downloadCache.Find(infoHash);

                    if (currentMapping != null)
                    {
                        currentMapping.LocalDownloadComplete = true;
                        currentMapping.LocalDownloadInProgress = false;
                        currentMapping.LocalDownloadFailed = false;
                        _downloadCache.Set(infoHash, currentMapping);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to download folder '{0}' from Seedr cloud", folder.Name);

                    var currentMapping = _downloadCache.Find(infoHash);

                    if (currentMapping != null)
                    {
                        currentMapping.LocalDownloadInProgress = false;
                        currentMapping.LocalDownloadFailed = true;
                        _downloadCache.Set(infoHash, currentMapping);
                    }
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
            var infoHash = mapping.InfoHash;

            Task.Run(() =>
            {
                try
                {
                    var safeFileName = Path.GetFileName(file.Name);
                    var filePath = Path.Combine(settings.DownloadDirectory, safeFileName);

                    _proxy.DownloadFileToPath(file.Id, filePath, settings);

                    var currentMapping = _downloadCache.Find(infoHash);

                    if (currentMapping != null)
                    {
                        currentMapping.LocalDownloadComplete = true;
                        currentMapping.LocalDownloadInProgress = false;
                        currentMapping.LocalDownloadFailed = false;
                        _downloadCache.Set(infoHash, currentMapping);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to download file '{0}' from Seedr cloud", file.Name);

                    var currentMapping = _downloadCache.Find(infoHash);

                    if (currentMapping != null)
                    {
                        currentMapping.LocalDownloadInProgress = false;
                        currentMapping.LocalDownloadFailed = true;
                        _downloadCache.Set(infoHash, currentMapping);
                    }
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
            public bool LocalDownloadFailed { get; set; }
        }
    }
}
