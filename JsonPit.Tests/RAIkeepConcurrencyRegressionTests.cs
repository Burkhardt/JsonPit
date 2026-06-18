using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using OsLib;
using Xunit;

namespace JsonPit.Tests
{
    public class RAIkeepConcurrencyRegressionTests
    {
        private static RaiPath CreateRegressionRoot(string label) =>
            RAIkeepTestEnvironment.CloudPath("ConcurrencyRegression", label, Guid.NewGuid().ToString("N"));

        private static PitItem CreateSnapshot(string id, object payload = null)
        {
            var json = payload is null ? new JObject() : JObject.FromObject(payload);
            json[nameof(PitItem.Id)] = id;
            json[nameof(PitItem.Modified)] = DateTimeOffset.UtcNow;
            json[nameof(PitItem.Deleted)] = false;
            return new PitItem(json);
        }

        private static PitItem CreateHistoricalSnapshot(string id, DateTimeOffset modified, object payload = null)
        {
            var json = payload is null ? new JObject() : JObject.FromObject(payload);
            json[nameof(PitItem.Id)] = id;
            json[nameof(PitItem.Modified)] = modified;
            json[nameof(PitItem.Deleted)] = false;
            return new PitItem(json);
        }

        private static string ConfiguredCloudRootOrNull()
        {
            var cloud = (Os.Config as JObject)?["Cloud"] as JObject;
            foreach (var provider in new[] { "GoogleDrive", "OneDrive", "Dropbox" })
            {
                var root = cloud?[provider]?.ToString();
                if (!string.IsNullOrWhiteSpace(root))
                    return root;
            }

            return null;
        }

        [Fact]
        public async Task Pit_And_OsConfig_AreSafe_UnderConcurrentCrossDirectoryUse()
        {
            // CRITICAL GUARD: this verifies thread safety without rewriting Os.Config values.
            Assert.True(
                Os.IsConfigLoaded,
                $"Os.Config must be populated once at startup before parallel tasks run. Config: {Os.ConfigFileFullName}");

            const int concurrencyDegree = 10;
            var cancellationToken = TestContext.Current.CancellationToken;
            var configSnapshot = (object)Os.Config;
            var tempRoot = Os.TempDir.FullPath;
            var testRoot = CreateRegressionRoot(nameof(Pit_And_OsConfig_AreSafe_UnderConcurrentCrossDirectoryUse));
            var tasks = new List<Task>();

            try
            {
                testRoot.mkdir();

                for (var i = 0; i < concurrencyDegree; i++)
                {
                    var workerId = i;
                    tasks.Add(Task.Run(() =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        Assert.Same(configSnapshot, (object)Os.Config);
                        Assert.Equal(tempRoot, Os.TempDir.FullPath);

                        var workerScratchPath = testRoot / $"WorkerRoot_{workerId}";
                        var isCloudPath = workerScratchPath.Cloud;
                        var resolvedPathString = workerScratchPath.FullPath;

                        Assert.NotNull(resolvedPathString);
                        Assert.StartsWith(testRoot.FullPath, resolvedPathString);

                        var pit = new Pit(
                            workerScratchPath,
                            readOnly: false,
                            autoload: false,
                            backup: false,
                            unflagged: true);

                        var item = CreateSnapshot($"Worker-{workerId}", new
                        {
                            WorkerId = workerId,
                            Path = resolvedPathString,
                            IsCloudPath = isCloudPath
                        });

                        pit.Add(item);
                        pit.Save(force: true);

                        var reloaded = new Pit(
                            workerScratchPath,
                            readOnly: true,
                            autoload: true,
                            backup: false,
                            unflagged: true);

                        var reloadedItem = reloaded[$"Worker-{workerId}"];

                        Assert.NotNull(reloadedItem);
                        Assert.Equal(workerId, reloadedItem!["WorkerId"]!.Value<int>());
                        Assert.Equal(resolvedPathString, reloadedItem["Path"]!.Value<string>());
                        Assert.Equal(isCloudPath, reloadedItem["IsCloudPath"]!.Value<bool>());
                    }, cancellationToken));
                }

                await Task.WhenAll(tasks);
            }
            finally
            {
                testRoot.rmdir(depth: 10, deleteFiles: true);
            }
        }

        [Fact]
        public async Task PathResolution_IsStable_UnderParallelFlood()
        {
            Assert.True(
                Os.IsConfigLoaded,
                $"Os.Config must be populated once at startup before parallel tasks run. Config: {Os.ConfigFileFullName}");

            const int concurrencyDegree = 24;
            const int iterationsPerWorker = 200;
            var cancellationToken = TestContext.Current.CancellationToken;
            var configSnapshot = (object)Os.Config;
            var tempRoot = Os.TempDir.FullPath;
            var tempCloud = Os.TempDir.Cloud;
            var localBackupRoot = Os.LocalBackupDir?.FullPath;
            var localBackupCloud = Os.LocalBackupDir?.Cloud;
            var configuredCloudRoot = ConfiguredCloudRootOrNull();

            if (!string.IsNullOrWhiteSpace(configuredCloudRoot))
                Assert.True(new RaiPath(configuredCloudRoot).Cloud);

            var tasks = Enumerable.Range(0, concurrencyDegree)
                .Select(workerId => Task.Run(() =>
                {
                    for (var i = 0; i < iterationsPerWorker; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        Assert.Same(configSnapshot, (object)Os.Config);
                        Assert.Equal(tempRoot, Os.TempDir.FullPath);

                        var localPath = Os.TempDir / $"PathFlood_{workerId}_{i}";
                        var canonicalFile = new CanonicalFile(localPath, $"Canonical_{workerId}_{i}", "tmp");

                        Assert.Equal(tempCloud, localPath.Cloud);
                        Assert.Equal(tempCloud, canonicalFile.Path.Cloud);

                        if (localBackupRoot is not null && localBackupCloud is not null)
                        {
                            var backupPath = Os.LocalBackupDir / $"BackupFlood_{workerId}_{i}";

                            Assert.Equal(localBackupRoot, Os.LocalBackupDir.FullPath);
                            Assert.Equal(localBackupCloud.Value, backupPath.Cloud);
                        }

                        if (!string.IsNullOrWhiteSpace(configuredCloudRoot))
                        {
                            var cloudPath = new RaiPath(configuredCloudRoot) / $"CloudFlood_{workerId}_{i}";

                            Assert.True(cloudPath.Cloud);
                        }
                    }
                }, cancellationToken))
                .ToArray();

            await Task.WhenAll(tasks);
        }

        [Fact]
        public async Task SinglePit_ConcurrentAddDeleteAndEnumeration_DoesNotCorruptIndexes()
        {
            var testRoot = CreateRegressionRoot(nameof(SinglePit_ConcurrentAddDeleteAndEnumeration_DoesNotCorruptIndexes));

            try
            {
                var pit = new Pit(
                    testRoot,
                    readOnly: false,
                    autoload: false,
                    backup: false,
                    unflagged: true);

                const int concurrencyDegree = 8;
                const int iterationsPerWorker = 120;
                var cancellationToken = TestContext.Current.CancellationToken;
                var tasks = Enumerable.Range(0, concurrencyDegree)
                    .Select(workerId => Task.Run(() =>
                    {
                        for (var i = 0; i < iterationsPerWorker; i++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            var sharedId = $"Shared-{i % 25}";
                            var item = CreateSnapshot(sharedId, new { WorkerId = workerId, Iteration = i });

                            pit.Add(item);

                            if (i % 11 == 0)
                                pit.Delete(sharedId, by: $"worker-{workerId}", backDate: false);

                            _ = pit.Contains(sharedId, withDeleted: true);
                            _ = pit.Keys.ToList();

                            foreach (var history in pit)
                            {
                                _ = history.Count;
                                _ = history.Peek();
                            }
                        }
                    }, cancellationToken))
                    .ToArray();

                await Task.WhenAll(tasks);

                Assert.NotEmpty(pit.HistoricItems);
                foreach (var historyById in pit.HistoricItems)
                {
                    Assert.Equal(historyById.Key, historyById.Value.Key);
                    Assert.All(historyById.Value.History, fragment => Assert.Equal(historyById.Key, fragment.Id));
                }
            }
            finally
            {
                testRoot.rmdir(depth: 10, deleteFiles: true);
            }
        }

        [Fact]
        public async Task ChangeFileIngestion_InterleavedWithLocalAdds_MergesBothHistories()
        {
            var testRoot = CreateRegressionRoot(nameof(ChangeFileIngestion_InterleavedWithLocalAdds_MergesBothHistories));

            try
            {
                var pit = new Pit(
                    testRoot,
                    readOnly: false,
                    autoload: false,
                    backup: false,
                    unflagged: true);

                pit.Add(CreateSnapshot("Seed"));
                pit.Save(force: true);

                const int itemCount = 60;
                var cancellationToken = TestContext.Current.CancellationToken;
                var localIds = new ConcurrentBag<string>();
                var externalIds = new ConcurrentBag<string>();
                var baseTime = DateTimeOffset.UtcNow.AddMinutes(-5);

                var localAddTask = Task.Run(() =>
                {
                    for (var i = 0; i < itemCount; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var id = $"Local-{i}";
                        var item = CreateSnapshot(id, new { Source = "local", Seq = i });

                        if (pit.Add(item))
                            localIds.Add(id);
                    }
                }, cancellationToken);

                var externalIngestionTask = Task.Run(() =>
                {
                    for (var i = 0; i < itemCount; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var id = $"External-{i}";
                        var item = CreateHistoricalSnapshot(id, baseTime.AddTicks(i), new { Source = "external", Seq = i });

                        pit.CreateChangeFile(item, $"ExternalServer-{i}");
                        externalIds.Add(id);
                    }
                }, cancellationToken);

                await Task.WhenAll(localAddTask, externalIngestionTask);

                pit.MergeChanges();
                pit.Save(force: true);

                var reloaded = new Pit(
                    testRoot,
                    readOnly: true,
                    autoload: true,
                    backup: false,
                    unflagged: true);

                foreach (var id in localIds)
                    Assert.NotNull(reloaded[id]);

                foreach (var id in externalIds)
                    Assert.NotNull(reloaded[id]);
            }
            finally
            {
                testRoot.rmdir(depth: 10, deleteFiles: true);
            }
        }

        [Fact]
        public async Task SaveInterleavedWithAdds_SubsequentSavePersistsEveryAcceptedItem()
        {
            var testRoot = CreateRegressionRoot(nameof(SaveInterleavedWithAdds_SubsequentSavePersistsEveryAcceptedItem));

            try
            {
                var pit = new Pit(
                    testRoot,
                    readOnly: false,
                    autoload: false,
                    backup: false,
                    unflagged: true);

                var payload = new string('x', 512);
                for (var i = 0; i < 100; i++)
                {
                    var seed = CreateSnapshot($"Seed-{i}", new { Payload = payload, Seq = i });
                    pit.Add(seed);
                }

                pit.Save(force: true);

                const int writerCount = 3;
                const int itemsPerWriter = 40;
                var acceptedIds = new ConcurrentBag<string>();
                var cancellationToken = TestContext.Current.CancellationToken;
                using var startGate = new ManualResetEventSlim(false);

                var saveTask = Task.Run(() =>
                {
                    startGate.Set();
                    for (var i = 0; i < 8; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        pit.Save(force: true);
                        Thread.Yield();
                    }
                }, cancellationToken);

                var writerTasks = Enumerable.Range(0, writerCount)
                    .Select(writerId => Task.Run(() =>
                    {
                        startGate.Wait(cancellationToken);

                        for (var i = 0; i < itemsPerWriter; i++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            var id = $"Concurrent-{writerId}-{i}";
                            var item = CreateSnapshot(id, new { WriterId = writerId, Seq = i, Payload = payload });

                            if (pit.Add(item))
                                acceptedIds.Add(id);
                        }
                    }, cancellationToken))
                    .ToArray();

                await Task.WhenAll(writerTasks.Concat([saveTask]));

                // This intentionally is not forced. If a mutation was accepted during a
                // save but had its dirty flag cleared by that save, this call will not
                // write it and the reload assertions below will expose the loss.
                pit.Save();

                var reloaded = new Pit(
                    testRoot,
                    readOnly: true,
                    autoload: true,
                    backup: false,
                    unflagged: true);

                foreach (var id in acceptedIds)
                    Assert.NotNull(reloaded[id]);
            }
            finally
            {
                testRoot.rmdir(depth: 10, deleteFiles: true);
            }
        }
    }
}
