﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using Tgstation.Server.Api.Models;
using Tgstation.Server.Api.Models.Internal;
using Tgstation.Server.Api.Models.Request;
using Tgstation.Server.Api.Models.Response;
using Tgstation.Server.Client;
using Tgstation.Server.Client.Components;
using Tgstation.Server.Common.Extensions;
using Tgstation.Server.Host.Components.Engine;
using Tgstation.Server.Host.Configuration;
using Tgstation.Server.Host.IO;
using Tgstation.Server.Host.System;

namespace Tgstation.Server.Tests.Live.Instance
{
	sealed class ByondTest(IByondClient byondClient, IJobsClient jobsClient, IFileDownloader fileDownloader, Api.Models.Instance metadata, EngineType engineType) : JobsRequiredTest(jobsClient)
	{
		readonly IByondClient byondClient = byondClient ?? throw new ArgumentNullException(nameof(byondClient));
		readonly IFileDownloader fileDownloader = fileDownloader ?? throw new ArgumentNullException(nameof(fileDownloader));

		readonly Api.Models.Instance metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));

		static readonly Dictionary<EngineType, EngineVersion> edgeVersions = new ()
		{
			{ EngineType.Byond, null },
			{ EngineType.OpenDream, null }
		};

		EngineVersion testVersion;
		readonly EngineType testEngine = engineType;

		public Task Run(CancellationToken cancellationToken, out Task firstInstall)
		{
			firstInstall = RunPartOne(cancellationToken);
			return RunContinued(firstInstall, cancellationToken);
		}

		public static async ValueTask<EngineVersion> GetEdgeVersion(EngineType engineType, IFileDownloader fileDownloader, CancellationToken cancellationToken)
		{
			var edgeVersion = edgeVersions[engineType];

			if (edgeVersion != null)
				return edgeVersion;

			EngineVersion engineVersion;
			if (engineType == EngineType.Byond)
			{
				await using var provider = fileDownloader.DownloadFile(new Uri("https://www.byond.com/download/version.txt"), null);
				var stream = await provider.GetResult(cancellationToken);
				using var reader = new StreamReader(stream, Encoding.UTF8, false, -1, true);
				var text = await reader.ReadToEndAsync(cancellationToken);
				var splits = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

				var targetVersion = splits.Last();

				var badVersionMap = new PlatformIdentifier().IsWindows
					? []
					// linux map also needs updating in CI
					: new Dictionary<string, string>()
					{
						{ "515.1612", "515.1611" }
					};

				badVersionMap.Add("515.1617", "515.1616");

				if (badVersionMap.TryGetValue(targetVersion, out var remappedVersion))
					targetVersion = remappedVersion;

				Assert.IsTrue(EngineVersion.TryParse(targetVersion, out engineVersion), $"Bad version: {targetVersion}");
			}
			else if (engineType == EngineType.OpenDream)
			{
				var masterBranch = await TestingGitHubService.RealTestClient.Repository.Branch.Get("OpenDreamProject", "OpenDream", "master");

				engineVersion = new EngineVersion
				{
					Engine = EngineType.OpenDream,
					SourceSHA = masterBranch.Commit.Sha,
				};
			}
			else
			{
				Assert.Fail($"Unimplemented edge retrieval for engine type: {engineType}");
				return null;
			}

			global::System.Console.WriteLine($"Edge {engineType} version evalutated to {engineVersion}");
			return edgeVersions[engineType] = engineVersion;
		}

		async Task RunPartOne(CancellationToken cancellationToken)
		{
			testVersion = await GetEdgeVersion(testEngine, fileDownloader, cancellationToken);
			await TestNoVersion(cancellationToken);
			await TestInstallNullVersion(cancellationToken);
			await TestInstallStable(cancellationToken);
		}

		ValueTask TestInstallNullVersion(CancellationToken cancellationToken)
			=> ApiAssert.ThrowsException<ApiConflictException, ByondInstallResponse>(
				() => byondClient.SetActiveVersion(
					new ByondVersionRequest
					{
						Engine = testEngine,
					},
					null,
					cancellationToken),
				ErrorCode.ModelValidationFailure);

		async Task RunContinued(Task firstInstall, CancellationToken cancellationToken)
		{
			await firstInstall;
			await TestInstallFakeVersion(cancellationToken);
			await TestCustomInstalls(cancellationToken);
			await TestDeletes(cancellationToken);
		}

		async Task TestDeletes(CancellationToken cancellationToken)
		{
			var deleteThisOneBecauseItWasntPartOfTheOriginalTest = await byondClient.DeleteVersion(new ByondVersionDeleteRequest
			{
				Engine = testEngine,
				Version = testVersion.Version,
				CustomIteration = 2,
			}, cancellationToken);
			await WaitForJob(deleteThisOneBecauseItWasntPartOfTheOriginalTest, 30, false, null, cancellationToken);

			var nonExistentUninstallResponseTask = ApiAssert.ThrowsException<ConflictException, JobResponse>(() => byondClient.DeleteVersion(
				new ByondVersionDeleteRequest
				{
					Version = new(509, 1000),
					Engine = testEngine,
				},
				cancellationToken), ErrorCode.ResourceNotPresent);

			var uninstallResponseTask = byondClient.DeleteVersion(
				new ByondVersionDeleteRequest
				{
					Version = testVersion.Version,
					Engine = testVersion.Engine,
					SourceSHA = testVersion.SourceSHA,
				},
				cancellationToken);

			var badBecauseActiveResponseTask = ApiAssert.ThrowsException<ConflictException, JobResponse>(() => byondClient.DeleteVersion(
				new ByondVersionDeleteRequest
				{
					Version = testVersion.Version,
					Engine = testVersion.Engine,
					SourceSHA = testVersion.SourceSHA,
					CustomIteration = 1,
				},
				cancellationToken), ErrorCode.EngineCannotDeleteActiveVersion);

			await badBecauseActiveResponseTask;

			var uninstallJob = await uninstallResponseTask;
			Assert.IsNotNull(uninstallJob);

			// Has to wait on deployment test possibly
			var uninstallTask = WaitForJob(uninstallJob, 120, false, null, cancellationToken);

			await nonExistentUninstallResponseTask;

			await uninstallTask;
			var byondDir = Path.Combine(metadata.Path, "Byond", testVersion.ToString());
			Assert.IsFalse(Directory.Exists(byondDir));

			var newVersions = await byondClient.InstalledVersions(null, cancellationToken);
			Assert.IsNotNull(newVersions);
			Assert.AreEqual(1, newVersions.Count);
			Assert.AreEqual(testVersion.Version.Semver(), newVersions[0].Version.Version.Semver());
			Assert.AreEqual(1, newVersions[0].Version.CustomIteration);
		}

		async Task TestInstallFakeVersion(CancellationToken cancellationToken)
		{
			var newModel = new ByondVersionRequest
			{
				Version = new Version(5011, 1385)
			};

			await ApiAssert.ThrowsException<ApiConflictException, ByondInstallResponse>(() => byondClient.SetActiveVersion(newModel, null, cancellationToken), ErrorCode.ModelValidationFailure);

			newModel.Engine = testEngine;

			var test = await byondClient.SetActiveVersion(newModel, null, cancellationToken);
			Assert.IsNotNull(test.InstallJob);
			await WaitForJob(test.InstallJob, 60, true, ErrorCode.EngineDownloadFail, cancellationToken);
		}

		async Task TestInstallStable(CancellationToken cancellationToken)
		{
			var newModel = new ByondVersionRequest
			{
				Version = testVersion.Version,
				Engine = testVersion.Engine,
				SourceSHA = testVersion.SourceSHA,
			};
			var test = await byondClient.SetActiveVersion(newModel, null, cancellationToken);
			Assert.IsNotNull(test.InstallJob);
			await WaitForJob(test.InstallJob, 180, false, null, cancellationToken);
			var currentShit = await byondClient.ActiveVersion(cancellationToken);
			Assert.AreEqual(newModel, currentShit.Version);
			Assert.IsFalse(currentShit.Version.CustomIteration.HasValue);

			var dreamMaker = "DreamMaker";
			if (new PlatformIdentifier().IsWindows)
				dreamMaker += ".exe";

			var dreamMakerDir = Path.Combine(metadata.Path, "Byond", newModel.Version.ToString(), "byond", "bin");

			Assert.IsTrue(Directory.Exists(dreamMakerDir), $"Directory {dreamMakerDir} does not exist!");
			Assert.IsTrue(
				File.Exists(
					Path.Combine(dreamMakerDir, dreamMaker)),
				$"Missing DreamMaker executable! Dir contents: {string.Join(", ", Directory.GetFileSystemEntries(dreamMakerDir))}");
		}

		async Task TestNoVersion(CancellationToken cancellationToken)
		{
			var allVersionsTask = byondClient.InstalledVersions(null, cancellationToken);
			var currentShit = await byondClient.ActiveVersion(cancellationToken);
			Assert.IsNotNull(currentShit);
			Assert.IsNull(currentShit.Version);
			var otherShit = await allVersionsTask;
			Assert.IsNotNull(otherShit);
			Assert.AreEqual(0, otherShit.Count);
		}

		async Task TestCustomInstalls(CancellationToken cancellationToken)
		{
			var generalConfigOptionsMock = new Mock<IOptions<GeneralConfiguration>>();
			generalConfigOptionsMock.SetupGet(x => x.Value).Returns(new GeneralConfiguration());
			var sessionConfigOptionsMock = new Mock<IOptions<SessionConfiguration>>();
			sessionConfigOptionsMock.SetupGet(x => x.Value).Returns(new SessionConfiguration());

			var assemblyInformationProvider = new AssemblyInformationProvider();

			IEngineInstaller byondInstaller = new PlatformIdentifier().IsWindows
				? new WindowsByondInstaller(
					Mock.Of<IProcessExecutor>(),
					Mock.Of<IIOManager>(),
					fileDownloader,
					generalConfigOptionsMock.Object,
					Mock.Of<ILogger<WindowsByondInstaller>>())
				: new PosixByondInstaller(
					Mock.Of<IPostWriteHandler>(),
					Mock.Of<IIOManager>(),
					fileDownloader,
					Mock.Of<ILogger<PosixByondInstaller>>());

			using var windowsByondInstaller = byondInstaller as WindowsByondInstaller;

			// get the bytes for stable
			await using var stableBytesMs = await TestingUtils.ExtractMemoryStreamFromInstallationData(await byondInstaller.DownloadVersion(testVersion, null, cancellationToken), cancellationToken);

			var test = await byondClient.SetActiveVersion(
				new ByondVersionRequest
				{
					Engine = testVersion.Engine,
					Version = testVersion.Version,
					SourceSHA = testVersion.SourceSHA,
					UploadCustomZip = true
				},
				stableBytesMs,
				cancellationToken);

			Assert.IsNotNull(test.InstallJob);
			await WaitForJob(test.InstallJob, 30, false, null, cancellationToken);

			// do it again. #1501
			stableBytesMs.Seek(0, SeekOrigin.Begin);
			var test2 = await byondClient.SetActiveVersion(
				new ByondVersionRequest
				{
					Version = testVersion.Version,
					SourceSHA = testVersion.SourceSHA,
					Engine = testVersion.Engine,
					UploadCustomZip = true
				},
				stableBytesMs,
				cancellationToken);

			Assert.IsNotNull(test2.InstallJob);
			await WaitForJob(test2.InstallJob, 30, false, null, cancellationToken);

			var newSettings = await byondClient.ActiveVersion(cancellationToken);
			Assert.AreEqual(new Version(testVersion.Version.Major, testVersion.Version.Minor, 0), newSettings.Version.Version);
			Assert.AreEqual(2, newSettings.Version.CustomIteration);

			// test a few switches
			var installResponse = await byondClient.SetActiveVersion(new ByondVersionRequest
			{
				Version = testVersion.Version,
				SourceSHA = testVersion.SourceSHA,
				Engine = testVersion.Engine,
			}, null, cancellationToken);
			Assert.IsNull(installResponse.InstallJob);
			await ApiAssert.ThrowsException<ApiConflictException, ByondInstallResponse>(() => byondClient.SetActiveVersion(new ByondVersionRequest
			{
				Version = testVersion.Version,
				Engine = testEngine,
				CustomIteration = 3,
			}, null, cancellationToken), ErrorCode.EngineNonExistentCustomVersion);

			installResponse = await byondClient.SetActiveVersion(new ByondVersionRequest
			{
				Version = new Version(testVersion.Version.Major, testVersion.Version.Minor),
				Engine = testEngine,
				CustomIteration = 1,
			}, null, cancellationToken);
			Assert.IsNull(installResponse.InstallJob);
		}
	}
}
