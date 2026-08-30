using System.Reflection;
using Dapper;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class BuildTimeoutTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    private const int MaxConcurrentBuilds = 5;

    [Fact]
    public Task BuildTimeoutStartsAfterDockerResourcesAreCreated()
    {
        return AssertDockerResourcesAreCleaned(containerCreateFails: false);
    }

    [Fact]
    public Task ContainerCreateFailureRemovesBuildVolume()
    {
        return AssertDockerResourcesAreCleaned(containerCreateFails: true);
    }

    private async Task AssertDockerResourcesAreCleaned(bool containerCreateFails)
    {
        if (OperatingSystem.IsWindows())
            return;

        var tempDirectory = Path.Combine(Path.GetTempPath(), $"plugin-builder-fake-docker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var dockerPath = Path.Combine(tempDirectory, "docker");
        File.WriteAllText(dockerPath, """
            #!/bin/sh
            set -eu
            state="${PB_FAKE_DOCKER_STATE:?}"
            printf '%s\n' "$*" >> "$state/commands"

            case "$1:$2" in
                volume:create)
                    for argument in "$@"; do volume="$argument"; done
                    case "$volume" in plugin-builder-volume-*) ;; *) exit 2 ;; esac
                    printf '%s' "$volume" > "$state/volume"
                    printf '%s\n' "$volume"
                    ;;
                container:create)
                    previous=""
                    name=""
                    for argument in "$@"; do
                        if [ "$previous" = "--name" ]; then name="$argument"; break; fi
                        previous="$argument"
                    done
                    [ -n "$name" ]
                    if [ "${PB_FAKE_DOCKER_CREATE_FAIL:-false}" = "true" ]; then
                        printf '%s\n' 'container create failed' >&2
                        exit 1
                    fi
                    sleep 2
                    printf '%s' "$name" > "$state/container"
                    printf '%s\n' fake-container-id
                    ;;
                container:start)
                    sleep 30
                    ;;
                container:rm)
                    if [ ! -f "$state/container" ]; then
                        printf '%s\n' "No such container: $4" >&2
                        exit 1
                    fi
                    [ "$4" = "$(cat "$state/container")" ]
                    rm -f "$state/container"
                    ;;
                volume:rm)
                    [ -f "$state/volume" ]
                    [ "$3" = "$(cat "$state/volume")" ]
                    rm -f "$state/volume"
                    ;;
                *)
                    exit 2
                    ;;
            esac
            """);
        File.SetUnixFileMode(
            dockerPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalSkipBuild = Environment.GetEnvironmentVariable("DOCKER_STARTUP_SKIP_BUILD");
        var originalFakeState = Environment.GetEnvironmentVariable("PB_FAKE_DOCKER_STATE");
        var originalCreateFailure = Environment.GetEnvironmentVariable("PB_FAKE_DOCKER_CREATE_FAIL");
        try
        {
            Environment.SetEnvironmentVariable("DOCKER_STARTUP_SKIP_BUILD", "true");

            await using var tester = Create(containerCreateFails ? "FailingDockerCreate" : "DelayedDockerCreate");
            tester.ReuseDatabase = false;
            tester.BuildTimeoutSeconds = 1;
            await tester.Start();

            Environment.SetEnvironmentVariable("PATH", tempDirectory + Path.PathSeparator + originalPath);
            Environment.SetEnvironmentVariable("PB_FAKE_DOCKER_STATE", tempDirectory);
            Environment.SetEnvironmentVariable("PB_FAKE_DOCKER_CREATE_FAIL", containerCreateFails ? "true" : null);

            var ownerId = await tester.CreateFakeUserAsync();
            var pluginSlug = new PluginSlug("delayed-" + Guid.NewGuid().ToString("N")[..8]);
            await using var connection = await tester.GetService<DBConnectionFactory>().Open();
            Assert.True(await connection.NewPlugin(pluginSlug, ownerId));
            var buildId = await connection.NewBuild(
                pluginSlug,
                new PluginBuildParameters("https://example.invalid/plugin.git"));

            var exception = await Assert.ThrowsAsync<BuildServiceException>(() =>
                tester.GetService<BuildService>()
                    .Build(new FullBuildId(pluginSlug, buildId))
                    .WaitAsync(TimeSpan.FromSeconds(15)));

            Assert.Contains(
                containerCreateFails ? "container create failed" : "timed out",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(tempDirectory, "container")));
            Assert.False(File.Exists(Path.Combine(tempDirectory, "volume")));
            var commands = await File.ReadAllLinesAsync(Path.Combine(tempDirectory, "commands"));
            if (containerCreateFails)
            {
                Assert.Collection(
                    commands,
                    command => Assert.StartsWith("volume create --label ", command, StringComparison.Ordinal),
                    command => Assert.StartsWith("container create --name ", command, StringComparison.Ordinal),
                    command => Assert.StartsWith("container rm --force ", command, StringComparison.Ordinal),
                    command => Assert.StartsWith("volume rm plugin-builder-volume-", command, StringComparison.Ordinal));
            }
            else
            {
                Assert.Collection(
                    commands,
                    command => Assert.StartsWith("volume create --label ", command, StringComparison.Ordinal),
                    command => Assert.StartsWith("container create --name ", command, StringComparison.Ordinal),
                    command => Assert.StartsWith("container start --attach ", command, StringComparison.Ordinal),
                    command => Assert.StartsWith("container rm --force ", command, StringComparison.Ordinal),
                    command => Assert.StartsWith("volume rm plugin-builder-volume-", command, StringComparison.Ordinal));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("DOCKER_STARTUP_SKIP_BUILD", originalSkipBuild);
            Environment.SetEnvironmentVariable("PB_FAKE_DOCKER_STATE", originalFakeState);
            Environment.SetEnvironmentVariable("PB_FAKE_DOCKER_CREATE_FAIL", originalCreateFailure);
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task HangingBuildsTimeOutCleanResourcesAndReleaseSlots()
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        tester.BuildTimeoutSeconds = 3;
        await tester.Start();

        var processRunner = tester.GetService<ProcessRunner>();
        var fixtureName = $"plugin-builder-timeout-fixture-{Guid.NewGuid():N}";
        List<FullBuildId> buildIds = [];
        try
        {
            var repoUrl = await StartHangingGitRepository(processRunner, fixtureName);
            var ownerId = await tester.CreateFakeUserAsync();
            var pluginSlug = new PluginSlug($"timeout-{Guid.NewGuid():N}"[..16]);
            await using var connection = await tester.GetService<DBConnectionFactory>().Open();
            Assert.True(await connection.NewPlugin(pluginSlug, ownerId));

            for (var i = 0; i < MaxConcurrentBuilds + 1; i++)
            {
                var buildId = await connection.NewBuild(pluginSlug, new PluginBuildParameters(repoUrl));
                buildIds.Add(new FullBuildId(pluginSlug, buildId));
            }

            var buildService = tester.GetService<BuildService>();
            var semaphore = (SemaphoreSlim)typeof(BuildService)
                .GetField("_semaphore", BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null)!;
            Assert.Equal(MaxConcurrentBuilds, semaphore.CurrentCount);

            var builds = buildIds.Select(buildService.Build).ToArray();
            foreach (var build in builds)
            {
                var exception = await Assert.ThrowsAsync<BuildServiceException>(async () =>
                    await build.WaitAsync(TimeSpan.FromSeconds(30)));
                Assert.Contains("timed out", exception.Message, StringComparison.OrdinalIgnoreCase);
            }

            Assert.Equal(MaxConcurrentBuilds, semaphore.CurrentCount);
            foreach (var buildId in buildIds)
            {
                var row = await connection.QuerySingleAsync<(string state, string error)>(
                    "SELECT state, build_info->>'error' AS error FROM builds WHERE plugin_slug=@pluginSlug AND id=@buildId",
                    new { pluginSlug = pluginSlug.ToString(), buildId = buildId.BuildId });
                Assert.Equal(BuildStates.Failed.ToEventName(), row.state);
                Assert.Contains("timed out", row.error, StringComparison.OrdinalIgnoreCase);

                Assert.Empty(await ListDockerResources(processRunner,
                    ["container", "ls", "--all", "--quiet", "--filter", $"label=BTCPAY_PLUGIN_BUILD={buildId}"]));
                Assert.Empty(await ListDockerResources(processRunner,
                    ["volume", "ls", "--quiet", "--filter", $"label=BTCPAY_PLUGIN_BUILD={buildId}"]));
            }
        }
        finally
        {
            foreach (var buildId in buildIds)
            {
                foreach (var container in await ListDockerResources(processRunner,
                             ["container", "ls", "--all", "--quiet", "--filter", $"label=BTCPAY_PLUGIN_BUILD={buildId}"]))
                {
                    await RunDocker(processRunner, ["container", "rm", "--force", container]);
                }

                foreach (var volume in await ListDockerResources(processRunner,
                             ["volume", "ls", "--quiet", "--filter", $"label=BTCPAY_PLUGIN_BUILD={buildId}"]))
                {
                    await RunDocker(processRunner, ["volume", "rm", "--force", volume]);
                }
            }

            await RunDocker(processRunner, ["container", "rm", "--force", fixtureName]);
        }
    }

    private static async Task<string> StartHangingGitRepository(ProcessRunner processRunner, string fixtureName)
    {
        const string fixtureScript = """
            set -e
            mkdir -p /tmp/timeout-repo
            cd /tmp/timeout-repo
            git init --initial-branch=main
            git config user.email timeout@example.com
            git config user.name timeout
            cat > TimeoutPlugin.csproj <<'EOF'
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <AssemblyName>TimeoutPlugin</AssemblyName>
              </PropertyGroup>
              <Target Name="HangBuild" BeforeTargets="Publish">
                <Exec Command="sleep 300" />
              </Target>
            </Project>
            EOF
            git add TimeoutPlugin.csproj
            git commit -m fixture
            exec git daemon --reuseaddr --export-all --base-path=/tmp --listen=0.0.0.0 /tmp
            """;

        Assert.Equal(0, await RunDocker(processRunner,
        [
            "run", "--detach", "--name", fixtureName, "--network", "bridge",
            "--entrypoint", "/bin/bash", "plugin-builder", "-c", fixtureScript
        ]));

        OutputCapture address = new();
        Assert.Equal(0, await RunDocker(processRunner,
            ["container", "inspect", "--format", "{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}", fixtureName], address));
        var repoUrl = $"git://{address.ToString().Trim()}/timeout-repo";

        using var readinessTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            if (await RunDocker(processRunner,
                    ["run", "--rm", "--network", "bridge", "--entrypoint", "git", "plugin-builder", "ls-remote", repoUrl]) == 0)
                return repoUrl;

            await Task.Delay(100, readinessTimeout.Token);
        }
    }

    private static async Task<IReadOnlyCollection<string>> ListDockerResources(ProcessRunner processRunner, string[] arguments)
    {
        OutputCapture output = new();
        Assert.Equal(0, await RunDocker(processRunner, arguments, output));
        return output.Lines.ToArray();
    }

    private static async Task<int> RunDocker(ProcessRunner processRunner, string[] arguments, IOutputCapture? output = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            return await processRunner.RunAsync(new ProcessSpec
            {
                Executable = "docker",
                Arguments = arguments,
                OutputCapture = output
            }, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return -1;
        }
    }
}
