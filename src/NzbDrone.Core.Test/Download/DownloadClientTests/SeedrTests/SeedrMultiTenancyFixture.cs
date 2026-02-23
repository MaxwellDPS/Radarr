using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.Seedr;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Localization;
using NzbDrone.Core.MediaFiles.TorrentInfo;
using NzbDrone.Core.Validation;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.Download.DownloadClientTests.SeedrTests
{
    [TestFixture]
    public class SeedrMultiTenancyFixture : DownloadClientFixtureBase<Seedr>
    {
        private const string HASH = "CBC2F069FE8BB2F544EAE707D75BCD3DE9DCF951";
        private SeedrFolderContents _folderContents;

        [SetUp]
        public void Setup()
        {
            Subject.Definition = new DownloadClientDefinition();
            Subject.Definition.Settings = new SeedrSettings
            {
                Email = "test@test.com",
                Password = "pass",
                DownloadDirectory = @"/downloads".AsOsAgnostic(),
                DeleteFromCloud = true,
                SharedAccount = true,
                InstanceTag = "radarr-4k",
                RedisConnectionString = "redis:6379"
            };

            _folderContents = new SeedrFolderContents
            {
                Transfers = new List<SeedrTransfer>(),
                Folders = new List<SeedrSubFolder>(),
                Files = new List<SeedrFile>()
            };

            Mocker.SetConstant<ICacheManager>(Mocker.Resolve<CacheManager>());

            Mocker.GetMock<ITorrentFileInfoReader>()
                  .Setup(s => s.GetHashFromTorrentFile(It.IsAny<byte[]>()))
                  .Returns(HASH);

            Mocker.GetMock<IHttpClient>()
                  .Setup(s => s.Get(It.IsAny<HttpRequest>()))
                  .Returns<HttpRequest>(r => new HttpResponse(r, new HttpHeader(), Array.Empty<byte>()));

            Mocker.GetMock<ISeedrProxy>()
                  .Setup(s => s.GetFolderContents(null, It.IsAny<SeedrSettings>()))
                  .Returns(() => _folderContents);

            Mocker.GetMock<ISeedrProxy>()
                  .Setup(s => s.AddMagnet(It.IsAny<string>(), It.IsAny<SeedrSettings>()))
                  .Returns(new SeedrAddTransferResponse { Id = 1, Name = _title, Hash = HASH });

            Mocker.GetMock<ISeedrProxy>()
                  .Setup(s => s.AddTorrentFile(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<SeedrSettings>()))
                  .Returns(new SeedrAddTransferResponse { Id = 1, Name = _title, Hash = HASH });

            Mocker.GetMock<ILocalizationService>()
                  .Setup(s => s.GetLocalizedString(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>()))
                  .Returns<string, Dictionary<string, object>>((key, args) => key);
        }

        private void GivenSharedAccountDisabled()
        {
            Subject.Definition.Settings.As<SeedrSettings>().SharedAccount = false;
        }

        private void GivenNoRedis()
        {
            Subject.Definition.Settings.As<SeedrSettings>().RedisConnectionString = null;
        }

        private void GivenFolder(long id, string name, long size)
        {
            _folderContents.Folders.Add(new SeedrSubFolder { Id = id, Name = name, Size = size });
        }

        private void GivenFile(long id, string name, long size)
        {
            _folderContents.Files.Add(new SeedrFile { Id = id, Name = name, Size = size });
        }

        private void GivenTransfer(long id, string name, double progress, long size, string hash = null)
        {
            _folderContents.Transfers.Add(new SeedrTransfer { Id = id, Name = name, RawProgress = progress, Size = size, Hash = hash });
        }

        private void GivenLocalFolderExists(string name)
        {
            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FolderExists(It.Is<string>(p => p.EndsWith(name))))
                  .Returns(true);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.GetFiles(It.Is<string>(p => p.EndsWith(name)), true))
                  .Returns(new[] { "/downloads/" + name + "/movie.mkv" });
        }

        private void GivenLocalFileExists(string name)
        {
            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FileExists(It.Is<string>(p => p.EndsWith(name))))
                  .Returns(true);
        }

        // === AddFromMagnetLink / AddFromTorrentFile: ClaimOwnership ===

        [Test]
        public void AddFromMagnetLink_should_claim_ownership()
        {
            var remoteMovie = CreateRemoteMovie();
            remoteMovie.Release.DownloadUrl = $"magnet:?xt=urn:btih:{HASH}&tr=udp";

            Subject.Download(remoteMovie, CreateIndexer()).Wait();

            Mocker.GetMock<ISeedrOwnershipService>()
                  .Verify(v => v.ClaimOwnership(HASH, It.IsAny<SeedrSettings>()), Times.Once());
        }

        [Test]
        public void AddFromTorrentFile_should_claim_ownership()
        {
            var remoteMovie = CreateRemoteMovie();

            Subject.Download(remoteMovie, CreateIndexer()).Wait();

            Mocker.GetMock<ISeedrOwnershipService>()
                  .Verify(v => v.ClaimOwnership(HASH, It.IsAny<SeedrSettings>()), Times.Once());
        }

        [Test]
        public void AddFromMagnetLink_should_claim_ownership_even_when_shared_disabled()
        {
            // ClaimOwnership is always called; the service itself decides to no-op
            GivenSharedAccountDisabled();

            var remoteMovie = CreateRemoteMovie();
            remoteMovie.Release.DownloadUrl = $"magnet:?xt=urn:btih:{HASH}&tr=udp";

            Subject.Download(remoteMovie, CreateIndexer()).Wait();

            Mocker.GetMock<ISeedrOwnershipService>()
                  .Verify(v => v.ClaimOwnership(HASH, It.IsAny<SeedrSettings>()), Times.Once());
        }

        // === GetItems: Transfer ownership filtering ===

        [Test]
        public void GetItems_should_skip_transfers_not_owned_by_me()
        {
            GivenTransfer(1, _title, 50, 1000, HASH);

            Mocker.GetMock<ISeedrOwnershipService>()
                  .Setup(s => s.IsOwnedByMe(HASH, It.IsAny<SeedrSettings>()))
                  .Returns(false);

            var items = Subject.GetItems().ToList();

            items.Should().BeEmpty();
        }

        [Test]
        public void GetItems_should_include_transfers_owned_by_me()
        {
            GivenTransfer(1, _title, 50, 1000, HASH);

            Mocker.GetMock<ISeedrOwnershipService>()
                  .Setup(s => s.IsOwnedByMe(HASH, It.IsAny<SeedrSettings>()))
                  .Returns(true);

            var items = Subject.GetItems().ToList();

            items.Should().HaveCount(1);
        }

        [Test]
        public void GetItems_should_include_transfers_when_redis_unavailable()
        {
            GivenTransfer(1, _title, 50, 1000, HASH);

            Mocker.GetMock<ISeedrOwnershipService>()
                  .Setup(s => s.IsOwnedByMe(HASH, It.IsAny<SeedrSettings>()))
                  .Returns((bool?)null);

            var items = Subject.GetItems().ToList();

            items.Should().HaveCount(1);
        }

        [Test]
        public void GetItems_should_not_check_ownership_when_shared_disabled()
        {
            GivenSharedAccountDisabled();

            GivenTransfer(1, _title, 50, 1000, HASH);

            var items = Subject.GetItems().ToList();

            items.Should().HaveCount(1);

            Mocker.GetMock<ISeedrOwnershipService>()
                  .Verify(v => v.IsOwnedByMe(It.IsAny<string>(), It.IsAny<SeedrSettings>()), Times.Never());
        }

        // === GetItems: Folders — no fuzzy matching in shared mode ===

        [Test]
        public void GetItems_should_skip_unmapped_folders_in_shared_mode()
        {
            GivenFolder(100, "Unknown.Folder", 1000);

            var items = Subject.GetItems().ToList();

            items.Should().BeEmpty();
        }

        [Test]
        public void GetItems_should_fuzzy_match_unmapped_folders_when_shared_disabled()
        {
            GivenSharedAccountDisabled();

            GivenFolder(100, "Unknown.Folder", 1000);

            var items = Subject.GetItems().ToList();

            items.Should().BeEmpty();

            ExceptionVerification.ExpectedWarns(1);
        }

        // === GetItems: Files — no fuzzy matching in shared mode ===

        [Test]
        public void GetItems_should_skip_unmapped_files_in_shared_mode()
        {
            GivenFile(200, "Unknown.File.mkv", 1000);

            var items = Subject.GetItems().ToList();

            items.Should().BeEmpty();
        }

        [Test]
        public void GetItems_should_fuzzy_match_unmapped_files_when_shared_disabled()
        {
            GivenSharedAccountDisabled();

            GivenFile(200, "Unknown.File.mkv", 1000);

            var items = Subject.GetItems().ToList();

            items.Should().BeEmpty();

            ExceptionVerification.ExpectedWarns(1);
        }

        // === RemoveItem: Ownership-aware cloud deletion ===

        [Test]
        public void RemoveItem_should_delete_from_cloud_when_last_owner()
        {
            var remoteMovie = CreateRemoteMovie();
            remoteMovie.Release.DownloadUrl = $"magnet:?xt=urn:btih:{HASH}&tr=udp";
            Subject.Download(remoteMovie, CreateIndexer()).Wait();

            _folderContents.Transfers.Clear();
            GivenFolder(100, _title, 1000);
            GivenLocalFolderExists(_title);

            Mocker.GetMock<ISeedrOwnershipService>()
                  .Setup(s => s.ReleaseOwnership(HASH, It.IsAny<SeedrSettings>()))
                  .Returns(true);

            var item = Subject.GetItems().Single();
            Subject.RemoveItem(item, false);

            Mocker.GetMock<ISeedrProxy>()
                  .Verify(v => v.DeleteFolder(100, It.IsAny<SeedrSettings>()), Times.Once());
        }

        [Test]
        public void RemoveItem_should_skip_cloud_deletion_when_others_own()
        {
            var remoteMovie = CreateRemoteMovie();
            remoteMovie.Release.DownloadUrl = $"magnet:?xt=urn:btih:{HASH}&tr=udp";
            Subject.Download(remoteMovie, CreateIndexer()).Wait();

            _folderContents.Transfers.Clear();
            GivenFolder(100, _title, 1000);
            GivenLocalFolderExists(_title);

            Mocker.GetMock<ISeedrOwnershipService>()
                  .Setup(s => s.ReleaseOwnership(HASH, It.IsAny<SeedrSettings>()))
                  .Returns(false);

            var item = Subject.GetItems().Single();
            Subject.RemoveItem(item, false);

            Mocker.GetMock<ISeedrProxy>()
                  .Verify(v => v.DeleteFolder(It.IsAny<long>(), It.IsAny<SeedrSettings>()), Times.Never());
        }

        [Test]
        public void RemoveItem_should_skip_cloud_deletion_when_redis_unavailable()
        {
            var remoteMovie = CreateRemoteMovie();
            remoteMovie.Release.DownloadUrl = $"magnet:?xt=urn:btih:{HASH}&tr=udp";
            Subject.Download(remoteMovie, CreateIndexer()).Wait();

            _folderContents.Transfers.Clear();
            GivenFolder(100, _title, 1000);
            GivenLocalFolderExists(_title);

            Mocker.GetMock<ISeedrOwnershipService>()
                  .Setup(s => s.ReleaseOwnership(HASH, It.IsAny<SeedrSettings>()))
                  .Returns((bool?)null);

            var item = Subject.GetItems().Single();
            Subject.RemoveItem(item, false);

            Mocker.GetMock<ISeedrProxy>()
                  .Verify(v => v.DeleteFolder(It.IsAny<long>(), It.IsAny<SeedrSettings>()), Times.Never());

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void RemoveItem_should_always_delete_from_cloud_when_shared_disabled()
        {
            GivenSharedAccountDisabled();

            var remoteMovie = CreateRemoteMovie();
            remoteMovie.Release.DownloadUrl = $"magnet:?xt=urn:btih:{HASH}&tr=udp";
            Subject.Download(remoteMovie, CreateIndexer()).Wait();

            _folderContents.Transfers.Clear();
            GivenFolder(100, _title, 1000);
            GivenLocalFolderExists(_title);

            var item = Subject.GetItems().Single();
            Subject.RemoveItem(item, false);

            Mocker.GetMock<ISeedrProxy>()
                  .Verify(v => v.DeleteFolder(100, It.IsAny<SeedrSettings>()), Times.Once());

            Mocker.GetMock<ISeedrOwnershipService>()
                  .Verify(v => v.ReleaseOwnership(It.IsAny<string>(), It.IsAny<SeedrSettings>()), Times.Never());
        }

        // === MarkItemAsImported: Ownership-aware cloud deletion ===

        [Test]
        public void MarkItemAsImported_should_delete_from_cloud_when_last_owner()
        {
            var remoteMovie = CreateRemoteMovie();
            remoteMovie.Release.DownloadUrl = $"magnet:?xt=urn:btih:{HASH}&tr=udp";
            Subject.Download(remoteMovie, CreateIndexer()).Wait();

            _folderContents.Transfers.Clear();
            GivenFolder(100, _title, 1000);
            GivenLocalFolderExists(_title);

            Mocker.GetMock<ISeedrOwnershipService>()
                  .Setup(s => s.ReleaseOwnership(HASH, It.IsAny<SeedrSettings>()))
                  .Returns(true);

            var item = Subject.GetItems().Single();
            Subject.MarkItemAsImported(item);

            Mocker.GetMock<ISeedrProxy>()
                  .Verify(v => v.DeleteFolder(100, It.IsAny<SeedrSettings>()), Times.Once());
        }

        [Test]
        public void MarkItemAsImported_should_skip_cloud_deletion_when_others_own()
        {
            var remoteMovie = CreateRemoteMovie();
            remoteMovie.Release.DownloadUrl = $"magnet:?xt=urn:btih:{HASH}&tr=udp";
            Subject.Download(remoteMovie, CreateIndexer()).Wait();

            _folderContents.Transfers.Clear();
            GivenFolder(100, _title, 1000);
            GivenLocalFolderExists(_title);

            Mocker.GetMock<ISeedrOwnershipService>()
                  .Setup(s => s.ReleaseOwnership(HASH, It.IsAny<SeedrSettings>()))
                  .Returns(false);

            var item = Subject.GetItems().Single();
            Subject.MarkItemAsImported(item);

            Mocker.GetMock<ISeedrProxy>()
                  .Verify(v => v.DeleteFolder(It.IsAny<long>(), It.IsAny<SeedrSettings>()), Times.Never());
        }

        [Test]
        public void MarkItemAsImported_should_skip_cloud_deletion_when_redis_unavailable()
        {
            var remoteMovie = CreateRemoteMovie();
            remoteMovie.Release.DownloadUrl = $"magnet:?xt=urn:btih:{HASH}&tr=udp";
            Subject.Download(remoteMovie, CreateIndexer()).Wait();

            _folderContents.Transfers.Clear();
            GivenFolder(100, _title, 1000);
            GivenLocalFolderExists(_title);

            Mocker.GetMock<ISeedrOwnershipService>()
                  .Setup(s => s.ReleaseOwnership(HASH, It.IsAny<SeedrSettings>()))
                  .Returns((bool?)null);

            var item = Subject.GetItems().Single();
            Subject.MarkItemAsImported(item);

            Mocker.GetMock<ISeedrProxy>()
                  .Verify(v => v.DeleteFolder(It.IsAny<long>(), It.IsAny<SeedrSettings>()), Times.Never());

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void MarkItemAsImported_should_always_delete_when_shared_disabled()
        {
            GivenSharedAccountDisabled();

            var remoteMovie = CreateRemoteMovie();
            remoteMovie.Release.DownloadUrl = $"magnet:?xt=urn:btih:{HASH}&tr=udp";
            Subject.Download(remoteMovie, CreateIndexer()).Wait();

            _folderContents.Transfers.Clear();
            GivenFolder(100, _title, 1000);
            GivenLocalFolderExists(_title);

            var item = Subject.GetItems().Single();
            Subject.MarkItemAsImported(item);

            Mocker.GetMock<ISeedrProxy>()
                  .Verify(v => v.DeleteFolder(100, It.IsAny<SeedrSettings>()), Times.Once());

            Mocker.GetMock<ISeedrOwnershipService>()
                  .Verify(v => v.ReleaseOwnership(It.IsAny<string>(), It.IsAny<SeedrSettings>()), Times.Never());
        }

        // === Test(): Redis connection validation ===

        [Test]
        public void Test_should_warn_when_shared_account_without_redis()
        {
            GivenNoRedis();

            Mocker.GetMock<ISeedrProxy>()
                  .Setup(s => s.GetUser(It.IsAny<SeedrSettings>()))
                  .Returns(new SeedrUser { Email = "test@test.com", SpaceUsed = 100, SpaceMax = 1000 });

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FolderExists(It.IsAny<string>()))
                  .Returns(true);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FolderWritable(It.IsAny<string>()))
                  .Returns(true);

            var result = Subject.Test();

            result.Errors.Should().Contain(e =>
                e.GetType() == typeof(NzbDroneValidationFailure) &&
                ((NzbDroneValidationFailure)e).IsWarning &&
                e.PropertyName == "RedisConnectionString");
        }

        [Test]
        public void Test_should_fail_when_redis_connection_fails()
        {
            Mocker.GetMock<ISeedrProxy>()
                  .Setup(s => s.GetUser(It.IsAny<SeedrSettings>()))
                  .Returns(new SeedrUser { Email = "test@test.com", SpaceUsed = 100, SpaceMax = 1000 });

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FolderExists(It.IsAny<string>()))
                  .Returns(true);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FolderWritable(It.IsAny<string>()))
                  .Returns(true);

            Mocker.GetMock<ISeedrOwnershipService>()
                  .Setup(s => s.TestConnection(It.IsAny<SeedrSettings>()))
                  .Returns("Connection refused");

            var result = Subject.Test();

            result.Errors.Should().Contain(e => e.PropertyName == "RedisConnectionString");
        }

        [Test]
        public void Test_should_pass_when_redis_connection_succeeds()
        {
            Mocker.GetMock<ISeedrProxy>()
                  .Setup(s => s.GetUser(It.IsAny<SeedrSettings>()))
                  .Returns(new SeedrUser { Email = "test@test.com", SpaceUsed = 100, SpaceMax = 1000 });

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FolderExists(It.IsAny<string>()))
                  .Returns(true);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FolderWritable(It.IsAny<string>()))
                  .Returns(true);

            Mocker.GetMock<ISeedrOwnershipService>()
                  .Setup(s => s.TestConnection(It.IsAny<SeedrSettings>()))
                  .Returns((string)null);

            var result = Subject.Test();

            result.IsValid.Should().BeTrue();
        }

        [Test]
        public void Test_should_not_check_redis_when_shared_disabled()
        {
            GivenSharedAccountDisabled();

            Mocker.GetMock<ISeedrProxy>()
                  .Setup(s => s.GetUser(It.IsAny<SeedrSettings>()))
                  .Returns(new SeedrUser { Email = "test@test.com", SpaceUsed = 100, SpaceMax = 1000 });

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FolderExists(It.IsAny<string>()))
                  .Returns(true);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FolderWritable(It.IsAny<string>()))
                  .Returns(true);

            var result = Subject.Test();

            result.IsValid.Should().BeTrue();

            Mocker.GetMock<ISeedrOwnershipService>()
                  .Verify(v => v.TestConnection(It.IsAny<SeedrSettings>()), Times.Never());
        }

        // === RecoverCacheFromHistory: Claim ownership ===

        [Test]
        public void RecoverCacheFromHistory_should_claim_ownership_for_recovered_items()
        {
            Mocker.GetMock<IDownloadHistoryService>()
                  .Setup(s => s.GetGrabbedItemsByDownloadClient(It.IsAny<int>()))
                  .Returns(new List<DownloadHistory>
                  {
                      new DownloadHistory
                      {
                          DownloadId = HASH,
                          SourceTitle = _title,
                          Data = new Dictionary<string, string>
                          {
                              { "SeedrName", _title },
                              { "SeedrTransferId", "1" }
                          }
                      }
                  });

            Mocker.GetMock<IDownloadHistoryService>()
                  .Setup(s => s.DownloadAlreadyImported(It.IsAny<string>()))
                  .Returns(false);

            // Trigger GetItems which calls RecoverCacheFromHistory
            Subject.GetItems().ToList();

            Mocker.GetMock<ISeedrOwnershipService>()
                  .Verify(v => v.ClaimOwnership(HASH, It.IsAny<SeedrSettings>()), Times.Once());
        }

        [Test]
        public void RecoverCacheFromHistory_should_not_claim_when_shared_disabled()
        {
            GivenSharedAccountDisabled();

            Mocker.GetMock<IDownloadHistoryService>()
                  .Setup(s => s.GetGrabbedItemsByDownloadClient(It.IsAny<int>()))
                  .Returns(new List<DownloadHistory>
                  {
                      new DownloadHistory
                      {
                          DownloadId = HASH,
                          SourceTitle = _title,
                          Data = new Dictionary<string, string>
                          {
                              { "SeedrName", _title },
                              { "SeedrTransferId", "1" }
                          }
                      }
                  });

            Mocker.GetMock<IDownloadHistoryService>()
                  .Setup(s => s.DownloadAlreadyImported(It.IsAny<string>()))
                  .Returns(false);

            Subject.GetItems().ToList();

            Mocker.GetMock<ISeedrOwnershipService>()
                  .Verify(v => v.ClaimOwnership(It.IsAny<string>(), It.IsAny<SeedrSettings>()), Times.Never());
        }

        // === Settings validation ===

        [Test]
        public void Validation_should_fail_when_shared_account_without_instance_tag()
        {
            var settings = new SeedrSettings
            {
                Email = "test@test.com",
                Password = "pass",
                DownloadDirectory = @"/downloads".AsOsAgnostic(),
                SharedAccount = true,
                InstanceTag = ""
            };

            var result = settings.Validate();

            result.IsValid.Should().BeFalse();
            result.Errors.Should().Contain(e => e.PropertyName == "InstanceTag");
        }

        [Test]
        public void Validation_should_fail_when_instance_tag_has_invalid_chars()
        {
            var settings = new SeedrSettings
            {
                Email = "test@test.com",
                Password = "pass",
                DownloadDirectory = @"/downloads".AsOsAgnostic(),
                SharedAccount = true,
                InstanceTag = "bad tag"
            };

            var result = settings.Validate();

            result.IsValid.Should().BeFalse();
            result.Errors.Should().Contain(e => e.PropertyName == "InstanceTag");
        }

        [Test]
        public void Validation_should_pass_with_valid_instance_tag()
        {
            var settings = new SeedrSettings
            {
                Email = "test@test.com",
                Password = "pass",
                DownloadDirectory = @"/downloads".AsOsAgnostic(),
                SharedAccount = true,
                InstanceTag = "radarr-4k"
            };

            var result = settings.Validate();

            result.IsValid.Should().BeTrue();
        }

        [Test]
        public void Validation_should_not_require_instance_tag_when_shared_disabled()
        {
            var settings = new SeedrSettings
            {
                Email = "test@test.com",
                Password = "pass",
                DownloadDirectory = @"/downloads".AsOsAgnostic(),
                SharedAccount = false,
                InstanceTag = ""
            };

            var result = settings.Validate();

            result.IsValid.Should().BeTrue();
        }
    }
}
