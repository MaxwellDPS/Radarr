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
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Localization;
using NzbDrone.Core.MediaFiles.TorrentInfo;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Download.Clients.Seedr
{
    public class Seedr : TorrentClientBase<SeedrSettings>, IProvideGrabMetadata
    {
        private readonly ISeedrProxy _proxy;
        private readonly IDownloadHistoryService _downloadHistoryService;
        private readonly ISeedrOwnershipService _ownershipService;
        private readonly ICached<SeedrDownloadMapping> _downloadCache;
        private bool _cacheRecovered;

        public Seedr(ISeedrProxy proxy,
                     IDownloadHistoryService downloadHistoryService,
                     ISeedrOwnershipService ownershipService,
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
            _downloadHistoryService = downloadHistoryService;
            _ownershipService = ownershipService;
            _downloadCache = cacheManager.GetCache<SeedrDownloadMapping>(GetType());
        }

        public override string Name => "Seedr";

        public Dictionary<string, string> GetGrabMetadata(string downloadId)
        {
            var mapping = _downloadCache.Find(downloadId);

            if (mapping == null)
            {
                return null;
            }

            var metadata = new Dictionary<string, string>
            {
                { "SeedrName", mapping.Name }
            };

            if (mapping.TransferId.HasValue)
            {
                metadata["SeedrTransferId"] = mapping.TransferId.Value.ToString();
            }

            return metadata;
        }

        protected override string AddFromMagnetLink(RemoteMovie remoteMovie, string hash, string magnetLink)
        {
            var transfer = _proxy.AddMagnet(magnetLink, Settings);

            _downloadCache.Set(hash.ToUpper(), new SeedrDownloadMapping
            {
                InfoHash = hash.ToUpper(),
                TransferId = transfer.Id,
                Name = transfer.Name
            });

            _ownershipService.ClaimOwnership(hash.ToUpper(), Settings);

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

            _ownershipService.ClaimOwnership(hash.ToUpper(), Settings);

            return hash;
        }

        public override IEnumerable<DownloadClientItem> GetItems()
        {
            if (!_cacheRecovered && !_downloadCache.Values.Any())
            {
                RecoverCacheFromHistory();
            }

            var contents = _proxy.GetFolderContents(null, Settings);

            // R4: Add warning log when API returns null
            if (contents == null)
            {
                _logger.Warn("Seedr API returned null folder contents");
                return Array.Empty<DownloadClientItem>();
            }

            var items = new List<DownloadClientItem>();
            var cachedMappings = _downloadCache.Values.ToList();

            _logger.Info("Seedr folder contents: {0} transfers, {1} folders, {2} files, {3} cached mappings",
                contents.Transfers?.Count ?? 0,
                contents.Folders?.Count ?? 0,
                contents.Files?.Count ?? 0,
                cachedMappings.Count);

            // Active transfers
            if (contents.Transfers != null)
            {
                foreach (var transfer in contents.Transfers)
                {
                    var mapping = cachedMappings.FirstOrDefault(m => m.TransferId == transfer.Id) ??
                                  cachedMappings.FirstOrDefault(m => m.Name == transfer.Name);

                    var infoHash = mapping?.InfoHash ?? transfer.Hash?.ToUpper() ?? $"seedr-{transfer.Id}";

                    // In shared account mode, filter items by ownership
                    if (Settings.SharedAccount)
                    {
                        var isOwned = _ownershipService.IsOwnedByMe(infoHash, Settings);

                        if (isOwned == false)
                        {
                            continue;
                        }

                        // If null (Redis down), fall through to existing cache-based logic
                    }

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

                    // Estimate RemainingTime from progress rate
                    TimeSpan? remainingTime = null;
                    var now = DateTime.UtcNow;

                    if (mapping != null && transfer.Progress > 0 && transfer.Progress < 100)
                    {
                        if (mapping.LastProgress > 0 && mapping.LastProgressTime.HasValue && transfer.Progress > mapping.LastProgress)
                        {
                            var elapsed = (now - mapping.LastProgressTime.Value).TotalSeconds;

                            if (elapsed > 0)
                            {
                                var progressDelta = transfer.Progress - mapping.LastProgress;
                                var progressPerSecond = progressDelta / elapsed;
                                var remainingProgress = 100 - transfer.Progress;
                                var estimatedSeconds = remainingProgress / progressPerSecond;

                                if (estimatedSeconds > 0 && estimatedSeconds < 86400)
                                {
                                    remainingTime = TimeSpan.FromSeconds(estimatedSeconds);
                                }
                            }
                        }

                        // Update tracking snapshot when progress changes
                        if (mapping.LastProgress != transfer.Progress)
                        {
                            mapping.LastProgress = transfer.Progress;
                            mapping.LastProgressTime = now;
                            _downloadCache.Set(infoHash, mapping);
                        }
                    }

                    var item = new DownloadClientItem
                    {
                        DownloadClientInfo = DownloadClientItemClientInfo.FromDownloadClient(this, false),
                        DownloadId = infoHash,
                        Title = transfer.Name,
                        TotalSize = transfer.Size,
                        RemainingSize = transfer.Size - (long)(transfer.Size * (transfer.Progress / 100.0)),
                        RemainingTime = remainingTime,
                        Status = DownloadItemStatus.Downloading,
                        Message = $"Downloading to Seedr cloud ({transfer.Progress:F1}%)",
                        CanMoveFiles = false,
                        CanBeRemoved = false
                    };

                    items.Add(item);
                }
            }

            // Completed folders (3.4: check FolderId first, then Name)
            if (contents.Folders != null)
            {
                foreach (var folder in contents.Folders)
                {
                    var mapping = cachedMappings.FirstOrDefault(m => m.FolderId == folder.Id) ??
                                  cachedMappings.FirstOrDefault(m => m.Name == folder.Name);

                    if (mapping == null)
                    {
                        if (Settings.SharedAccount)
                        {
                            _logger.Debug("Seedr folder '{0}' has no cached mapping. Skipping (shared account mode).", folder.Name);
                            continue;
                        }

                        mapping = TryMatchOrphanedItem(folder.Name);

                        if (mapping == null)
                        {
                            _logger.Warn("Seedr folder '{0}' (ID: {1}) has no cached mapping and could not be matched to history. Skipping.", folder.Name, folder.Id);
                            continue;
                        }

                        _logger.Info("Seedr folder '{0}' matched to history entry with hash {1}", folder.Name, mapping.InfoHash);
                    }

                    // Update cache with folder ID
                    mapping.FolderId = folder.Id;
                    _downloadCache.Set(mapping.InfoHash, mapping);

                    var localPath = Path.Combine(Settings.DownloadDirectory, SanitizeFileName(folder.Name));

                    // 3.6: Verify folder contains non-.part files before marking complete
                    // Don't falsely mark as complete if a previous download failed partway through
                    if (mapping.LocalDownloadComplete || (!mapping.LocalDownloadInProgress && !mapping.LocalDownloadFailed && FolderExistsWithCompletedFiles(localPath)))
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
                            RemainingTime = TimeSpan.Zero,
                            Status = DownloadItemStatus.Completed,
                            OutputPath = new OsPath(localPath),
                            CanMoveFiles = true,
                            CanBeRemoved = true
                        });
                    }
                    else
                    {
                        // Reset failed state to allow automatic retry on next poll
                        if (mapping.LocalDownloadFailed)
                        {
                            _logger.Info("Retrying previously failed cloud-to-local download for Seedr folder '{0}'", folder.Name);
                            mapping.LocalDownloadFailed = false;
                            _downloadCache.Set(mapping.InfoHash, mapping);
                        }

                        DownloadFolderFromCloud(folder, mapping);

                        var localDir = Path.Combine(Settings.DownloadDirectory, SanitizeFileName(folder.Name));
                        var bytesOnDisk = GetBytesOnDisk(localDir);
                        var remainingBytes = Math.Max(0, folder.Size - bytesOnDisk);
                        var localRemainingTime = EstimateLocalDownloadTime(mapping, bytesOnDisk, folder.Size);

                        items.Add(new DownloadClientItem
                        {
                            DownloadClientInfo = DownloadClientItemClientInfo.FromDownloadClient(this, false),
                            DownloadId = mapping.InfoHash,
                            Title = folder.Name,
                            TotalSize = folder.Size,
                            RemainingSize = remainingBytes,
                            RemainingTime = localRemainingTime,
                            Status = DownloadItemStatus.Downloading,
                            Message = "Downloading from Seedr cloud to local",
                            CanMoveFiles = false,
                            CanBeRemoved = false
                        });
                    }
                }
            }

            // Completed single files in root folder (3.1: add FileId matching)
            if (contents.Files != null)
            {
                foreach (var file in contents.Files)
                {
                    var mapping = cachedMappings.FirstOrDefault(m => m.FileId == file.Id) ??
                                  cachedMappings.FirstOrDefault(m => m.Name == file.Name);

                    if (mapping == null)
                    {
                        if (Settings.SharedAccount)
                        {
                            _logger.Debug("Seedr file '{0}' has no cached mapping. Skipping (shared account mode).", file.Name);
                            continue;
                        }

                        mapping = TryMatchOrphanedItem(file.Name);

                        if (mapping == null)
                        {
                            _logger.Warn("Seedr file '{0}' (ID: {1}) has no cached mapping and could not be matched to history. Skipping.", file.Name, file.Id);
                            continue;
                        }

                        _logger.Info("Seedr file '{0}' matched to history entry with hash {1}", file.Name, mapping.InfoHash);
                    }

                    // 3.1: Update cache with file ID
                    mapping.FileId = file.Id;
                    _downloadCache.Set(mapping.InfoHash, mapping);

                    var localPath = Path.Combine(Settings.DownloadDirectory, SanitizeFileName(file.Name));

                    // 3.6: Check file exists and is not a .part file
                    if (mapping.LocalDownloadComplete || (!mapping.LocalDownloadInProgress && !mapping.LocalDownloadFailed && FileExistsCompleted(localPath)))
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
                            RemainingTime = TimeSpan.Zero,
                            Status = DownloadItemStatus.Completed,
                            OutputPath = new OsPath(localPath),
                            CanMoveFiles = true,
                            CanBeRemoved = true
                        });
                    }
                    else
                    {
                        // Reset failed state to allow automatic retry on next poll
                        if (mapping.LocalDownloadFailed)
                        {
                            _logger.Info("Retrying previously failed cloud-to-local download for Seedr file '{0}'", file.Name);
                            mapping.LocalDownloadFailed = false;
                            _downloadCache.Set(mapping.InfoHash, mapping);
                        }

                        DownloadFileFromCloud(file, mapping);

                        var bytesOnDisk = GetFileBytesOnDisk(localPath);
                        var remainingBytes = Math.Max(0, file.Size - bytesOnDisk);
                        var localRemainingTime = EstimateLocalDownloadTime(mapping, bytesOnDisk, file.Size);

                        items.Add(new DownloadClientItem
                        {
                            DownloadClientInfo = DownloadClientItemClientInfo.FromDownloadClient(this, false),
                            DownloadId = mapping.InfoHash,
                            Title = file.Name,
                            TotalSize = file.Size,
                            RemainingSize = remainingBytes,
                            RemainingTime = localRemainingTime,
                            Status = DownloadItemStatus.Downloading,
                            Message = "Downloading from Seedr cloud to local",
                            CanMoveFiles = false,
                            CanBeRemoved = false
                        });
                    }
                }
            }

            return items;
        }

        // R2: Narrow catch to DownloadClientException. 3.1: Add FileId branch.
        public override void RemoveItem(DownloadClientItem item, bool deleteData)
        {
            var mapping = _downloadCache.Find(item.DownloadId);

            if (Settings.SharedAccount)
            {
                var isLastOwner = _ownershipService.ReleaseOwnership(item.DownloadId, Settings);

                if (isLastOwner == true)
                {
                    DeleteFromCloud(mapping, item.DownloadId);
                }
                else if (isLastOwner == false)
                {
                    _logger.Info("Skipping cloud deletion for {0} — other instances still own this item.", item.DownloadId);
                }
                else
                {
                    _logger.Warn("Redis unavailable. Skipping cloud deletion for {0} to prevent conflicts.", item.DownloadId);
                }
            }
            else
            {
                DeleteFromCloud(mapping, item.DownloadId);
            }

            if (deleteData)
            {
                DeleteItemData(item);
            }

            _downloadCache.Remove(item.DownloadId);
        }

        private void DeleteFromCloud(SeedrDownloadMapping mapping, string downloadId)
        {
            try
            {
                if (mapping?.FolderId != null)
                {
                    _proxy.DeleteFolder(mapping.FolderId.Value, Settings);
                }
                else if (mapping?.FileId != null)
                {
                    _proxy.DeleteFile(mapping.FileId.Value, Settings);
                }
                else if (mapping?.TransferId != null)
                {
                    _proxy.DeleteTransfer(mapping.TransferId.Value, Settings);
                }
            }
            catch (DownloadClientException ex)
            {
                _logger.Warn(ex, "Failed to delete item from Seedr cloud for {0}", downloadId);
            }
        }

        public override DownloadClientInfo GetStatus()
        {
            return new DownloadClientInfo
            {
                IsLocalhost = false,
                OutputRootFolders = new List<OsPath> { new OsPath(Settings.DownloadDirectory) }
            };
        }

        // R3: Add FileId and TransferId fallbacks. 3.1: Full branch coverage.
        public override void MarkItemAsImported(DownloadClientItem downloadClientItem)
        {
            if (Settings.DeleteFromCloud)
            {
                var mapping = _downloadCache.Find(downloadClientItem.DownloadId);

                if (Settings.SharedAccount)
                {
                    var isLastOwner = _ownershipService.ReleaseOwnership(downloadClientItem.DownloadId, Settings);

                    if (isLastOwner == true)
                    {
                        DeleteFromCloud(mapping, downloadClientItem.DownloadId);
                    }
                    else if (isLastOwner == false)
                    {
                        _logger.Info("Skipping cloud deletion for {0} — other instances still own this item.", downloadClientItem.DownloadId);
                    }
                    else
                    {
                        _logger.Warn("Redis unavailable. Skipping cloud deletion for {0} to prevent conflicts.", downloadClientItem.DownloadId);
                    }
                }
                else
                {
                    DeleteFromCloud(mapping, downloadClientItem.DownloadId);
                }
            }

            _downloadCache.Remove(downloadClientItem.DownloadId);
        }

        // 3.3: Add storage warning to Test()
        protected override void Test(List<ValidationFailure> failures)
        {
            SeedrUser user;

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

            if (user.SpaceMax > 0)
            {
                var usedPercent = (int)(user.SpaceUsed * 100 / user.SpaceMax);

                if (usedPercent >= 90)
                {
                    failures.Add(new NzbDroneValidationFailure("Email",
                        _localizationService.GetLocalizedString("DownloadClientSeedrValidationStorageWarning",
                            new Dictionary<string, object> { { "usedPercent", usedPercent } }))
                    {
                        IsWarning = true
                    });
                }
            }

            var folderFailure = TestFolder(Settings.DownloadDirectory, "DownloadDirectory");

            if (folderFailure != null)
            {
                failures.Add(folderFailure);
            }

            if (Settings.SharedAccount)
            {
                if (Settings.RedisConnectionString.IsNotNullOrWhiteSpace())
                {
                    var redisError = _ownershipService.TestConnection(Settings);

                    if (redisError != null)
                    {
                        failures.Add(new ValidationFailure("RedisConnectionString",
                            _localizationService.GetLocalizedString("DownloadClientSeedrValidationRedisConnectionFailed",
                                new Dictionary<string, object> { { "errorMessage", redisError } })));
                    }
                }
                else
                {
                    failures.Add(new NzbDroneValidationFailure("RedisConnectionString",
                        _localizationService.GetLocalizedString("DownloadClientSeedrValidationNoRedisWarning"))
                    {
                        IsWarning = true
                    });
                }
            }
        }

        private void RecoverCacheFromHistory()
        {
            _cacheRecovered = true;

            try
            {
                var grabbedHistory = _downloadHistoryService.GetGrabbedItemsByDownloadClient(Definition.Id);

                if (grabbedHistory == null || !grabbedHistory.Any())
                {
                    _logger.Info("Seedr cache recovery: no grab history found for this client instance");
                    return;
                }

                var recovered = 0;

                foreach (var historyItem in grabbedHistory)
                {
                    if (_downloadCache.Find(historyItem.DownloadId) != null)
                    {
                        continue;
                    }

                    if (_downloadHistoryService.DownloadAlreadyImported(historyItem.DownloadId))
                    {
                        continue;
                    }

                    var mapping = new SeedrDownloadMapping
                    {
                        InfoHash = historyItem.DownloadId,
                        Name = historyItem.Data.ContainsKey("SeedrName") ? historyItem.Data["SeedrName"] : historyItem.SourceTitle
                    };

                    if (historyItem.Data.ContainsKey("SeedrTransferId") && long.TryParse(historyItem.Data["SeedrTransferId"], out var transferId))
                    {
                        mapping.TransferId = transferId;
                    }

                    _downloadCache.Set(mapping.InfoHash, mapping);

                    if (Settings.SharedAccount)
                    {
                        _ownershipService.ClaimOwnership(mapping.InfoHash, Settings);
                    }

                    recovered++;
                }

                _logger.Info("Seedr cache recovery: restored {0} mappings from download history", recovered);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Seedr cache recovery failed, items may not appear in Activity until re-grabbed");
            }
        }

        private SeedrDownloadMapping TryMatchOrphanedItem(string itemName)
        {
            try
            {
                var grabbedHistory = _downloadHistoryService.GetGrabbedItemsByDownloadClient(Definition.Id);

                if (grabbedHistory == null)
                {
                    return null;
                }

                foreach (var historyItem in grabbedHistory)
                {
                    if (_downloadHistoryService.DownloadAlreadyImported(historyItem.DownloadId))
                    {
                        continue;
                    }

                    var seedrName = historyItem.Data.ContainsKey("SeedrName") ? historyItem.Data["SeedrName"] : historyItem.SourceTitle;

                    if (itemName.Contains(seedrName, StringComparison.OrdinalIgnoreCase) ||
                        seedrName.Contains(itemName, StringComparison.OrdinalIgnoreCase))
                    {
                        var mapping = new SeedrDownloadMapping
                        {
                            InfoHash = historyItem.DownloadId,
                            Name = seedrName
                        };

                        if (historyItem.Data.ContainsKey("SeedrTransferId") && long.TryParse(historyItem.Data["SeedrTransferId"], out var transferId))
                        {
                            mapping.TransferId = transferId;
                        }

                        _downloadCache.Set(mapping.InfoHash, mapping);
                        return mapping;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to match orphaned Seedr item '{0}' against history", itemName);
            }

            return null;
        }

        // 3.2: Recurse subfolders. R1: Use SanitizeFileName helper.
        private void DownloadFolderFromCloud(SeedrSubFolder folder, SeedrDownloadMapping mapping)
        {
            if (mapping.LocalDownloadInProgress)
            {
                return;
            }

            mapping.LocalDownloadInProgress = true;
            mapping.LocalDownloadStartTime = DateTime.UtcNow;
            mapping.LocalTotalBytes = folder.Size;
            _downloadCache.Set(mapping.InfoHash, mapping);

            var settings = Settings;
            var infoHash = mapping.InfoHash;

            _logger.Info("Starting cloud-to-local download for Seedr folder '{0}' (hash: {1})", folder.Name, infoHash);

            Task.Run(() =>
            {
                try
                {
                    var localDir = Path.Combine(settings.DownloadDirectory, SanitizeFileName(folder.Name));
                    _diskProvider.CreateFolder(localDir);

                    var (filesDownloaded, filesFailed) = DownloadFolderContentsRecursive(folder.Id, localDir, settings);

                    if (filesDownloaded == 0 && filesFailed == 0)
                    {
                        throw new DownloadClientException($"Seedr folder '{folder.Name}' returned no files from API. The folder may still be processing on Seedr's servers.");
                    }

                    var currentMapping = _downloadCache.Find(infoHash);

                    if (currentMapping != null)
                    {
                        if (filesFailed > 0)
                        {
                            currentMapping.LocalDownloadInProgress = false;
                            currentMapping.LocalDownloadFailed = true;
                            _downloadCache.Set(infoHash, currentMapping);
                            _logger.Warn("Partially downloaded Seedr folder '{0}': {1} files succeeded, {2} files failed. Will retry on next poll.", folder.Name, filesDownloaded, filesFailed);
                        }
                        else
                        {
                            currentMapping.LocalDownloadComplete = true;
                            currentMapping.LocalDownloadInProgress = false;
                            currentMapping.LocalDownloadFailed = false;
                            _downloadCache.Set(infoHash, currentMapping);
                            _logger.Info("Completed cloud-to-local download for Seedr folder '{0}' ({1} files)", folder.Name, filesDownloaded);
                        }
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

        // R1: Use SanitizeFileName helper.
        private void DownloadFileFromCloud(SeedrFile file, SeedrDownloadMapping mapping)
        {
            if (mapping.LocalDownloadInProgress)
            {
                return;
            }

            mapping.LocalDownloadInProgress = true;
            mapping.LocalDownloadStartTime = DateTime.UtcNow;
            mapping.LocalTotalBytes = file.Size;
            _downloadCache.Set(mapping.InfoHash, mapping);

            var settings = Settings;
            var infoHash = mapping.InfoHash;

            _logger.Info("Starting cloud-to-local download for Seedr file '{0}' (hash: {1})", file.Name, infoHash);

            Task.Run(() =>
            {
                try
                {
                    var filePath = Path.Combine(settings.DownloadDirectory, SanitizeFileName(file.Name));

                    _proxy.DownloadFileToPath(file.Id, filePath, settings);

                    var currentMapping = _downloadCache.Find(infoHash);

                    if (currentMapping != null)
                    {
                        currentMapping.LocalDownloadComplete = true;
                        currentMapping.LocalDownloadInProgress = false;
                        currentMapping.LocalDownloadFailed = false;
                        _downloadCache.Set(infoHash, currentMapping);
                    }

                    _logger.Info("Completed cloud-to-local download for Seedr file '{0}'", file.Name);
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

        // 3.2: Recursive helper for nested folder downloads. Returns (downloaded, failed) counts.
        private (int Downloaded, int Failed) DownloadFolderContentsRecursive(long folderId, string localDir, SeedrSettings settings)
        {
            var folderContents = _proxy.GetFolderContents(folderId, settings);
            var downloaded = 0;
            var failed = 0;

            if (folderContents?.Files != null)
            {
                foreach (var file in folderContents.Files)
                {
                    try
                    {
                        var filePath = Path.Combine(localDir, SanitizeFileName(file.Name));
                        _proxy.DownloadFileToPath(file.Id, filePath, settings);
                        downloaded++;
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "Failed to download file '{0}' (ID: {1}) from Seedr cloud, continuing with remaining files", file.Name, file.Id);
                        failed++;
                    }
                }
            }

            if (folderContents?.Folders != null)
            {
                foreach (var subFolder in folderContents.Folders)
                {
                    var subDir = Path.Combine(localDir, SanitizeFileName(subFolder.Name));
                    _diskProvider.CreateFolder(subDir);
                    var (subDownloaded, subFailed) = DownloadFolderContentsRecursive(subFolder.Id, subDir, settings);
                    downloaded += subDownloaded;
                    failed += subFailed;
                }
            }

            return (downloaded, failed);
        }

        // 3.6: Verify folder has at least one non-.part file
        private bool FolderExistsWithCompletedFiles(string localPath)
        {
            if (!_diskProvider.FolderExists(localPath))
            {
                return false;
            }

            var files = _diskProvider.GetFiles(localPath, true);

            return files.Any(f => !f.EndsWith(".part"));
        }

        // 3.6: Verify file exists and is not a .part file
        private bool FileExistsCompleted(string localPath)
        {
            return _diskProvider.FileExists(localPath) && !localPath.EndsWith(".part");
        }

        private long GetBytesOnDisk(string directoryPath)
        {
            try
            {
                if (!_diskProvider.FolderExists(directoryPath))
                {
                    return 0;
                }

                return _diskProvider.GetFiles(directoryPath, true)
                    .Sum(f => _diskProvider.GetFileSize(f));
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to calculate bytes on disk for '{0}'", directoryPath);
                return 0;
            }
        }

        private long GetFileBytesOnDisk(string filePath)
        {
            try
            {
                // Check .part file first (in-progress download), then completed file
                var partPath = filePath + ".part";

                if (_diskProvider.FileExists(partPath))
                {
                    return _diskProvider.GetFileSize(partPath);
                }

                if (_diskProvider.FileExists(filePath))
                {
                    return _diskProvider.GetFileSize(filePath);
                }

                return 0;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to get file size for '{0}'", filePath);
                return 0;
            }
        }

        private TimeSpan? EstimateLocalDownloadTime(SeedrDownloadMapping mapping, long bytesOnDisk, long totalBytes)
        {
            if (totalBytes <= 0 || !mapping.LocalDownloadStartTime.HasValue)
            {
                return null;
            }

            if (bytesOnDisk <= 0)
            {
                return null;
            }

            var elapsed = (DateTime.UtcNow - mapping.LocalDownloadStartTime.Value).TotalSeconds;

            if (elapsed <= 0)
            {
                return null;
            }

            var bytesPerSecond = bytesOnDisk / elapsed;

            if (bytesPerSecond <= 0)
            {
                return null;
            }

            var remainingBytes = totalBytes - bytesOnDisk;
            var estimatedSeconds = remainingBytes / bytesPerSecond;

            if (estimatedSeconds > 0 && estimatedSeconds < 86400)
            {
                return TimeSpan.FromSeconds(estimatedSeconds);
            }

            return null;
        }

        // R1: Centralized SanitizeFileName helper (ported from Sonarr)
        private static string SanitizeFileName(string name)
        {
            var safeName = Path.GetFileName(name);

            if (safeName.IsNullOrWhiteSpace())
            {
                throw new DownloadClientException($"Invalid file/folder name from Seedr API: '{name}'");
            }

            return safeName;
        }

        // 3.1: Added FileId.
        private class SeedrDownloadMapping
        {
            public string InfoHash { get; set; }
            public long? TransferId { get; set; }
            public long? FolderId { get; set; }
            public long? FileId { get; set; }
            public string Name { get; set; }
            public bool LocalDownloadComplete { get; set; }
            public bool LocalDownloadInProgress { get; set; }
            public bool LocalDownloadFailed { get; set; }

            // Progress tracking for ETA estimation
            public double LastProgress { get; set; }
            public DateTime? LastProgressTime { get; set; }
            public DateTime? LocalDownloadStartTime { get; set; }
            public long LocalTotalBytes { get; set; }
        }
    }
}
