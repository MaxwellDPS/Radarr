using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Clients.Seedr;
using NzbDrone.Core.Localization;
using NzbDrone.Core.MediaFiles.TorrentInfo;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.Download.DownloadClientTests.SeedrTests
{
    [TestFixture]
    public class SeedrFixture : DownloadClientFixtureBase<Seedr>
    {
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
                DeleteFromCloud = true
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
                  .Returns("CBC2F069FE8BB2F544EAE707D75BCD3DE9DCF951");

            Mocker.GetMock<IHttpClient>()
                  .Setup(s => s.Get(It.IsAny<HttpRequest>()))
                  .Returns<HttpRequest>(r => new HttpResponse(r, new HttpHeader(), Array.Empty<byte>()));

            Mocker.GetMock<ISeedrProxy>()
                  .Setup(s => s.GetFolderContents(null, It.IsAny<SeedrSettings>()))
                  .Returns(() => _folderContents);

            Mocker.GetMock<ILocalizationService>()
                  .Setup(s => s.GetLocalizedString(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>()))
                  .Returns<string, Dictionary<string, object>>((key, args) => key);
        }

        protected void GivenTransfer(long id, string name, double progress, long size, string hash = null)
        {
            _folderContents.Transfers.Add(new SeedrTransfer
            {
                Id = id,
                Name = name,
                RawProgress = progress,
                Size = size,
                Hash = hash
            });
        }

        protected void GivenFolder(long id, string name, long size)
        {
            _folderContents.Folders.Add(new SeedrSubFolder
            {
                Id = id,
                Name = name,
                Size = size
            });
        }

        protected void GivenFile(long id, string name, long size, long folderId = 0)
        {
            _folderContents.Files.Add(new SeedrFile
            {
                Id = id,
                Name = name,
                Size = size,
                FolderId = folderId
            });
        }

        protected void GivenSuccessfulMagnetDownload()
        {
            Mocker.GetMock<ISeedrProxy>()
                  .Setup(s => s.AddMagnet(It.IsAny<string>(), It.IsAny<SeedrSettings>()))
                  .Returns(new SeedrAddTransferResponse
                  {
                      Id = 1,
                      Name = _title,
                      Hash = "CBC2F069FE8BB2F544EAE707D75BCD3DE9DCF951"
                  });
        }

        protected void GivenSuccessfulTorrentDownload()
        {
            Mocker.GetMock<ISeedrProxy>()
                  .Setup(s => s.AddTorrentFile(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<SeedrSettings>()))
                  .Returns(new SeedrAddTransferResponse
                  {
                      Id = 1,
                      Name = _title,
                      Hash = "CBC2F069FE8BB2F544EAE707D75BCD3DE9DCF951"
                  });
        }

        protected void GivenLocalFolderExists(string name)
        {
            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FolderExists(It.Is<string>(p => p.EndsWith(name))))
                  .Returns(true);

            // FolderExistsWithCompletedFiles requires at least one non-.part file
            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.GetFiles(It.Is<string>(p => p.EndsWith(name)), true))
                  .Returns(new[] { "/downloads/" + name + "/movie.mkv" });
        }

        protected void GivenLocalFileExists(string name)
        {
            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FileExists(It.Is<string>(p => p.EndsWith(name))))
                  .Returns(true);
        }

        [Test]
        public void GetItems_should_return_no_items_when_empty()
        {
            var items = Subject.GetItems().ToList();

            items.Should().BeEmpty();
        }

        [Test]
        public void active_transfer_should_have_required_properties()
        {
            GivenTransfer(1, _title, 50, 1000, "CBC2F069FE8BB2F544EAE707D75BCD3DE9DCF951");

            var item = Subject.GetItems().Single();

            VerifyDownloading(item);
            item.Title.Should().Be(_title);
            item.TotalSize.Should().Be(1000);
            item.RemainingSize.Should().Be(500);
            item.CanMoveFiles.Should().BeFalse();
            item.CanBeRemoved.Should().BeFalse();
        }

        [Test]
        public void active_transfer_at_zero_progress_should_have_full_remaining_size()
        {
            GivenTransfer(1, _title, 0, 1000, "HASH123");

            var item = Subject.GetItems().Single();

            VerifyDownloading(item);
            item.RemainingSize.Should().Be(1000);
        }

        [Test]
        public void completed_folder_with_local_download_should_be_completed()
        {
            GivenSuccessfulMagnetDownload();

            var remoteMovie = CreateRemoteMovie();
            remoteMovie.Release.DownloadUrl = "magnet:?xt=urn:btih:CBC2F069FE8BB2F544EAE707D75BCD3DE9DCF951&tr=udp";

            Subject.Download(remoteMovie, CreateIndexer()).Wait();

            _folderContents.Transfers.Clear();
            GivenFolder(100, _title, 1000);
            GivenLocalFolderExists(_title);

            var item = Subject.GetItems().Single();

            VerifyIdentifiable(item);
            item.Status.Should().Be(DownloadItemStatus.Completed);
            item.RemainingSize.Should().Be(0);
            item.CanMoveFiles.Should().BeTrue();
            item.CanBeRemoved.Should().BeTrue();
        }

        [Test]
        public void completed_folder_without_local_download_should_be_downloading()
        {
            GivenSuccessfulMagnetDownload();

            var remoteMovie = CreateRemoteMovie();
            remoteMovie.Release.DownloadUrl = "magnet:?xt=urn:btih:CBC2F069FE8BB2F544EAE707D75BCD3DE9DCF951&tr=udp";

            Subject.Download(remoteMovie, CreateIndexer()).Wait();

            _folderContents.Transfers.Clear();
            GivenFolder(100, _title, 1000);

            // Set up folder contents for cloud download
            Mocker.GetMock<ISeedrProxy>()
                  .Setup(s => s.GetFolderContents(100, It.IsAny<SeedrSettings>()))
                  .Returns(new SeedrFolderContents
                  {
                      Files = new List<SeedrFile>
                      {
                          new SeedrFile { Id = 200, Name = "movie.mkv", Size = 1000 }
                      }
                  });

            var item = Subject.GetItems().Single();

            item.Status.Should().Be(DownloadItemStatus.Downloading);
            item.Message.Should().Be("Downloading from Seedr cloud to local");
            item.CanMoveFiles.Should().BeFalse();
            item.CanBeRemoved.Should().BeFalse();
        }

        [Test]
        public void completed_file_with_local_download_should_be_completed()
        {
            GivenSuccessfulTorrentDownload();

            var remoteMovie = CreateRemoteMovie();

            Subject.Download(remoteMovie, CreateIndexer()).Wait();

            _folderContents.Transfers.Clear();
            GivenFile(200, _title, 1000);
            GivenLocalFileExists(_title);

            var item = Subject.GetItems().Single();

            VerifyIdentifiable(item);
            item.Status.Should().Be(DownloadItemStatus.Completed);
            item.RemainingSize.Should().Be(0);
            item.CanMoveFiles.Should().BeTrue();
            item.CanBeRemoved.Should().BeTrue();
        }

        [Test]
        public void completed_file_without_local_download_should_be_downloading()
        {
            GivenSuccessfulTorrentDownload();

            var remoteMovie = CreateRemoteMovie();

            Subject.Download(remoteMovie, CreateIndexer()).Wait();

            _folderContents.Transfers.Clear();
            GivenFile(200, _title, 1000);

            var item = Subject.GetItems().Single();

            item.Status.Should().Be(DownloadItemStatus.Downloading);
            item.Message.Should().Be("Downloading from Seedr cloud to local");
        }

        [Test]
        public void folders_without_cache_mapping_should_be_skipped()
        {
            GivenFolder(100, "Unknown.Folder", 1000);

            var items = Subject.GetItems().ToList();

            items.Should().BeEmpty();

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void files_without_cache_mapping_should_be_skipped()
        {
            GivenFile(200, "Unknown.File.mkv", 1000);

            var items = Subject.GetItems().ToList();

            items.Should().BeEmpty();

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public async Task Download_with_magnet_should_return_hash()
        {
            GivenSuccessfulMagnetDownload();

            var remoteMovie = CreateRemoteMovie();
            remoteMovie.Release.DownloadUrl = "magnet:?xt=urn:btih:CBC2F069FE8BB2F544EAE707D75BCD3DE9DCF951&tr=udp";

            var id = await Subject.Download(remoteMovie, CreateIndexer());

            id.Should().Be("CBC2F069FE8BB2F544EAE707D75BCD3DE9DCF951");
        }

        [Test]
        public async Task Download_with_magnet_should_call_proxy()
        {
            GivenSuccessfulMagnetDownload();

            var remoteMovie = CreateRemoteMovie();
            remoteMovie.Release.DownloadUrl = "magnet:?xt=urn:btih:CBC2F069FE8BB2F544EAE707D75BCD3DE9DCF951&tr=udp";

            await Subject.Download(remoteMovie, CreateIndexer());

            Mocker.GetMock<ISeedrProxy>()
                  .Verify(v => v.AddMagnet(It.IsAny<string>(), It.IsAny<SeedrSettings>()), Times.Once());
        }

        [Test]
        public async Task Download_with_torrent_file_should_return_hash()
        {
            GivenSuccessfulTorrentDownload();

            var remoteMovie = CreateRemoteMovie();

            var id = await Subject.Download(remoteMovie, CreateIndexer());

            id.Should().NotBeNullOrEmpty();
        }

        [Test]
        public async Task Download_with_torrent_file_should_call_proxy()
        {
            GivenSuccessfulTorrentDownload();

            var remoteMovie = CreateRemoteMovie();

            await Subject.Download(remoteMovie, CreateIndexer());

            Mocker.GetMock<ISeedrProxy>()
                  .Verify(v => v.AddTorrentFile(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<SeedrSettings>()), Times.Once());
        }

        [Test]
        public void RemoveItem_should_delete_folder_when_folder_id_exists()
        {
            GivenSuccessfulMagnetDownload();

            var remoteMovie = CreateRemoteMovie();
            remoteMovie.Release.DownloadUrl = "magnet:?xt=urn:btih:CBC2F069FE8BB2F544EAE707D75BCD3DE9DCF951&tr=udp";

            Subject.Download(remoteMovie, CreateIndexer()).Wait();

            _folderContents.Transfers.Clear();
            GivenFolder(100, _title, 1000);
            GivenLocalFolderExists(_title);

            var item = Subject.GetItems().Single();

            Subject.RemoveItem(item, false);

            Mocker.GetMock<ISeedrProxy>()
                  .Verify(v => v.DeleteFolder(100, It.IsAny<SeedrSettings>()), Times.Once());
        }

        [Test]
        public void RemoveItem_should_delete_transfer_when_only_transfer_id_exists()
        {
            GivenSuccessfulMagnetDownload();

            var remoteMovie = CreateRemoteMovie();
            remoteMovie.Release.DownloadUrl = "magnet:?xt=urn:btih:CBC2F069FE8BB2F544EAE707D75BCD3DE9DCF951&tr=udp";

            Subject.Download(remoteMovie, CreateIndexer()).Wait();

            GivenTransfer(1, _title, 50, 1000, "CBC2F069FE8BB2F544EAE707D75BCD3DE9DCF951");

            var item = Subject.GetItems().Single();

            Subject.RemoveItem(item, false);

            Mocker.GetMock<ISeedrProxy>()
                  .Verify(v => v.DeleteTransfer(1, It.IsAny<SeedrSettings>()), Times.Once());
        }

        [Test]
        public void RemoveItem_should_delete_file_when_file_id_exists()
        {
            GivenSuccessfulTorrentDownload();

            var remoteMovie = CreateRemoteMovie();

            Subject.Download(remoteMovie, CreateIndexer()).Wait();

            _folderContents.Transfers.Clear();
            GivenFile(200, _title, 1000);
            GivenLocalFileExists(_title);

            var item = Subject.GetItems().Single();

            Subject.RemoveItem(item, false);

            Mocker.GetMock<ISeedrProxy>()
                  .Verify(v => v.DeleteFile(200, It.IsAny<SeedrSettings>()), Times.Once());
        }

        [Test]
        public void RemoveItem_should_not_throw_when_cloud_delete_fails()
        {
            GivenSuccessfulMagnetDownload();

            var remoteMovie = CreateRemoteMovie();
            remoteMovie.Release.DownloadUrl = "magnet:?xt=urn:btih:CBC2F069FE8BB2F544EAE707D75BCD3DE9DCF951&tr=udp";

            Subject.Download(remoteMovie, CreateIndexer()).Wait();

            _folderContents.Transfers.Clear();
            GivenFolder(100, _title, 1000);
            GivenLocalFolderExists(_title);

            Mocker.GetMock<ISeedrProxy>()
                  .Setup(s => s.DeleteFolder(It.IsAny<long>(), It.IsAny<SeedrSettings>()))
                  .Throws(new DownloadClientException("API error"));

            var item = Subject.GetItems().Single();

            Subject.Invoking(s => s.RemoveItem(item, false))
                   .Should().NotThrow();

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void MarkItemAsImported_should_delete_from_cloud_when_enabled()
        {
            GivenSuccessfulMagnetDownload();

            var remoteMovie = CreateRemoteMovie();
            remoteMovie.Release.DownloadUrl = "magnet:?xt=urn:btih:CBC2F069FE8BB2F544EAE707D75BCD3DE9DCF951&tr=udp";

            Subject.Download(remoteMovie, CreateIndexer()).Wait();

            _folderContents.Transfers.Clear();
            GivenFolder(100, _title, 1000);
            GivenLocalFolderExists(_title);

            var item = Subject.GetItems().Single();

            Subject.MarkItemAsImported(item);

            Mocker.GetMock<ISeedrProxy>()
                  .Verify(v => v.DeleteFolder(100, It.IsAny<SeedrSettings>()), Times.Once());
        }

        [Test]
        public void MarkItemAsImported_should_delete_file_from_cloud_when_enabled()
        {
            GivenSuccessfulTorrentDownload();

            var remoteMovie = CreateRemoteMovie();

            Subject.Download(remoteMovie, CreateIndexer()).Wait();

            _folderContents.Transfers.Clear();
            GivenFile(200, _title, 1000);
            GivenLocalFileExists(_title);

            var item = Subject.GetItems().Single();

            Subject.MarkItemAsImported(item);

            Mocker.GetMock<ISeedrProxy>()
                  .Verify(v => v.DeleteFile(200, It.IsAny<SeedrSettings>()), Times.Once());
        }

        [Test]
        public void MarkItemAsImported_should_not_delete_from_cloud_when_disabled()
        {
            Subject.Definition.Settings.As<SeedrSettings>().DeleteFromCloud = false;

            GivenSuccessfulMagnetDownload();

            var remoteMovie = CreateRemoteMovie();
            remoteMovie.Release.DownloadUrl = "magnet:?xt=urn:btih:CBC2F069FE8BB2F544EAE707D75BCD3DE9DCF951&tr=udp";

            Subject.Download(remoteMovie, CreateIndexer()).Wait();

            _folderContents.Transfers.Clear();
            GivenFolder(100, _title, 1000);
            GivenLocalFolderExists(_title);

            var item = Subject.GetItems().Single();

            Subject.MarkItemAsImported(item);

            Mocker.GetMock<ISeedrProxy>()
                  .Verify(v => v.DeleteFolder(It.IsAny<long>(), It.IsAny<SeedrSettings>()), Times.Never());
        }

        [Test]
        public void MarkItemAsImported_should_not_throw_when_cloud_delete_fails()
        {
            GivenSuccessfulMagnetDownload();

            var remoteMovie = CreateRemoteMovie();
            remoteMovie.Release.DownloadUrl = "magnet:?xt=urn:btih:CBC2F069FE8BB2F544EAE707D75BCD3DE9DCF951&tr=udp";

            Subject.Download(remoteMovie, CreateIndexer()).Wait();

            _folderContents.Transfers.Clear();
            GivenFolder(100, _title, 1000);
            GivenLocalFolderExists(_title);

            Mocker.GetMock<ISeedrProxy>()
                  .Setup(s => s.DeleteFolder(It.IsAny<long>(), It.IsAny<SeedrSettings>()))
                  .Throws(new DownloadClientException("API error"));

            var item = Subject.GetItems().Single();

            Subject.Invoking(s => s.MarkItemAsImported(item))
                   .Should().NotThrow();

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void MarkItemAsImported_should_delete_transfer_when_no_folder_id()
        {
            GivenSuccessfulMagnetDownload();

            var remoteMovie = CreateRemoteMovie();
            remoteMovie.Release.DownloadUrl = "magnet:?xt=urn:btih:CBC2F069FE8BB2F544EAE707D75BCD3DE9DCF951&tr=udp";

            Subject.Download(remoteMovie, CreateIndexer()).Wait();

            GivenTransfer(1, _title, 50, 1000, "CBC2F069FE8BB2F544EAE707D75BCD3DE9DCF951");

            var item = Subject.GetItems().Single();

            Subject.MarkItemAsImported(item);

            Mocker.GetMock<ISeedrProxy>()
                  .Verify(v => v.DeleteTransfer(1, It.IsAny<SeedrSettings>()), Times.Once());
        }

        [Test]
        public void GetStatus_should_return_download_directory()
        {
            var result = Subject.GetStatus();

            result.IsLocalhost.Should().BeFalse();
            result.OutputRootFolders.Should().NotBeNull();
            result.OutputRootFolders.First().Should().Be(@"/downloads".AsOsAgnostic());
        }

        [Test]
        public void Test_should_fail_on_authentication_error()
        {
            Mocker.GetMock<ISeedrProxy>()
                  .Setup(s => s.GetUser(It.IsAny<SeedrSettings>()))
                  .Throws(new DownloadClientAuthenticationException("Failed to authenticate"));

            var result = Subject.Test();

            result.IsValid.Should().BeFalse();
        }

        [Test]
        public void Test_should_pass_with_valid_user()
        {
            Mocker.GetMock<ISeedrProxy>()
                  .Setup(s => s.GetUser(It.IsAny<SeedrSettings>()))
                  .Returns(new SeedrUser { Email = "test@test.com", SpaceUsed = 100, SpaceMax = 1000 });

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FolderExists(@"/downloads".AsOsAgnostic()))
                  .Returns(true);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FolderWritable(It.IsAny<string>()))
                  .Returns(true);

            var result = Subject.Test();

            result.IsValid.Should().BeTrue();
        }

        [Test]
        public void Test_should_warn_when_cloud_storage_nearly_full()
        {
            Mocker.GetMock<ISeedrProxy>()
                  .Setup(s => s.GetUser(It.IsAny<SeedrSettings>()))
                  .Returns(new SeedrUser { Email = "test@test.com", SpaceUsed = 950, SpaceMax = 1000 });

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FolderExists(@"/downloads".AsOsAgnostic()))
                  .Returns(true);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FolderWritable(It.IsAny<string>()))
                  .Returns(true);

            var result = Subject.Test();

            result.Errors.Should().ContainSingle(e => e.GetType() == typeof(NzbDrone.Core.Validation.NzbDroneValidationFailure) && ((NzbDrone.Core.Validation.NzbDroneValidationFailure)e).IsWarning);
        }

        [Test]
        public void multiple_active_transfers_should_return_all_items()
        {
            GivenTransfer(1, "Movie.2024.1080p", 30, 1000, "HASH1");
            GivenTransfer(2, "Movie.2025.720p", 60, 2000, "HASH2");

            var items = Subject.GetItems().ToList();

            items.Should().HaveCount(2);
            items[0].Title.Should().Be("Movie.2024.1080p");
            items[1].Title.Should().Be("Movie.2025.720p");
        }

        [Test]
        public void transfer_without_hash_should_use_seedr_prefix_id()
        {
            GivenTransfer(42, _title, 50, 1000);

            var item = Subject.GetItems().Single();

            item.DownloadId.Should().Be("seedr-42");
        }

        [Test]
        public void GetItems_should_return_empty_when_api_returns_null()
        {
            Mocker.GetMock<ISeedrProxy>()
                  .Setup(s => s.GetFolderContents(null, It.IsAny<SeedrSettings>()))
                  .Returns((SeedrFolderContents)null);

            var items = Subject.GetItems().ToList();

            items.Should().BeEmpty();

            ExceptionVerification.ExpectedWarns(1);
        }
    }
}
