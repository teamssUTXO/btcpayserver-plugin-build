using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using PluginBuilder.Services;
using Xunit;

namespace PluginBuilder.Tests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class BuildMetadataReadSecurityTests
{
    private const int MaxBuildMetadataBytes = 1024 * 1024;

    public enum MetadataSource
    {
        Valid,
        AtLimit,
        Oversized,
        EndlessDevice
    }

    [Theory]
    [InlineData(MetadataSource.Valid)]
    [InlineData(MetadataSource.AtLimit)]
    [InlineData(MetadataSource.Oversized)]
    [InlineData(MetadataSource.EndlessDevice)]
    public async Task BuildMetadataReadIsBounded(MetadataSource sourceKind)
    {
        if (OperatingSystem.IsWindows())
            return;

        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            $"plugin-builder-metadata-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        var sourcePath = Path.Combine(tempDirectory, "metadata source");
        var expectedContent = sourceKind switch
        {
            MetadataSource.Valid => """{"assemblyName":"Fixture"}""",
            MetadataSource.AtLimit =>
                "{\"padding\":\"" + new string('A', MaxBuildMetadataBytes - "{\"padding\":\"\"}".Length) + "\"}",
            _ => null
        };

        switch (sourceKind)
        {
            case MetadataSource.Valid:
            case MetadataSource.AtLimit:
                await File.WriteAllTextAsync(sourcePath, expectedContent!);
                break;
            case MetadataSource.Oversized:
                await File.WriteAllBytesAsync(sourcePath, new byte[MaxBuildMetadataBytes + 1]);
                break;
            case MetadataSource.EndlessDevice:
                File.CreateSymbolicLink(sourcePath, "/dev/zero");
                break;
        }

        var dockerPath = Path.Combine(tempDirectory, "docker");
        await File.WriteAllTextAsync(dockerPath, """
            #!/bin/sh
            set -eu
            state="${PB_FAKE_DOCKER_STATE:?}"
            printf '%s:%s\n' "$1" "$2" >> "$state/commands"

            case "$1:$2" in
                container:create)
                    previous=""
                    name=""
                    auto_remove=false
                    readonly_mount=false
                    for argument in "$@"; do
                        if [ "$argument" = "plugin-builder" ]; then break; fi
                        if [ "$previous" = "--name" ]; then
                            name="$argument"
                        fi
                        case "$argument" in
                            --rm) auto_remove=true ;;
                            *:/out:ro) readonly_mount=true ;;
                        esac
                        previous="$argument"
                    done
                    [ -n "$name" ]
                    [ "$auto_remove" = true ]
                    [ "$readonly_mount" = true ]

                    while [ "$#" -gt 0 ] && [ "$1" != "plugin-builder" ]; do
                        shift
                    done
                    [ "$#" -ge 7 ]
                    shift
                    [ "$1" = "/bin/sh" ]
                    [ "$2" = "-c" ]
                    [ "$4" = "read-build-metadata" ]
                    [ "$5" = "/out/build-env.json" ]

                    printf '%s' "$3" > "$state/read-script"
                    printf '%s' "$6" > "$state/read-limit"
                    printf '%s' "$name" > "$state/container"
                    printf '%s\n' fake-container-id
                    ;;
                container:start)
                    [ "$3" = "--attach" ]
                    [ "$4" = "$(cat "$state/container")" ]
                    set +e
                    /bin/sh -c "$(cat "$state/read-script")" \
                        read-build-metadata \
                        "$state/metadata source" \
                        "$(cat "$state/read-limit")"
                    status=$?
                    set -e
                    if [ "$status" -eq 0 ]; then
                        rm -f "$state/container"
                    fi
                    exit "$status"
                    ;;
                container:rm)
                    [ -f "$state/container" ]
                    [ "$4" = "$(cat "$state/container")" ]
                    rm -f "$state/container"
                    ;;
                *)
                    exit 2
                    ;;
            esac
            """);

        File.SetUnixFileMode(
            dockerPath,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalState = Environment.GetEnvironmentVariable("PB_FAKE_DOCKER_STATE");

        try
        {
            Environment.SetEnvironmentVariable(
                "PATH",
                tempDirectory + Path.PathSeparator + originalPath);
            Environment.SetEnvironmentVariable("PB_FAKE_DOCKER_STATE", tempDirectory);

            var service = new BuildService(
                NullLogger<BuildService>.Instance,
                null!,
                new ProcessRunner(NullLogger<ProcessRunner>.Instance),
                null!,
                null!,
                null!,
                null!);

            var method = typeof(BuildService).GetMethod(
                "ReadFileInVolume",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var readTask = Assert.IsAssignableFrom<Task<string>>(
                method!.Invoke(service, ["attacker-volume", "build-env.json"]));

            if (expectedContent != null)
            {
                Assert.Equal(expectedContent, await readTask.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.False(File.Exists(Path.Combine(tempDirectory, "container")));
                Assert.Equal(
                    ["container:create", "container:start"],
                    await File.ReadAllLinesAsync(Path.Combine(tempDirectory, "commands")));
            }
            else
            {
                var exception = await Assert.ThrowsAsync<BuildServiceException>(
                    async () => await readTask.WaitAsync(TimeSpan.FromSeconds(5)));

                if (sourceKind == MetadataSource.Oversized)
                    Assert.Contains("exceeds", exception.Message, StringComparison.OrdinalIgnoreCase);
                Assert.False(File.Exists(Path.Combine(tempDirectory, "container")));
                Assert.Equal(
                    ["container:create", "container:start", "container:rm"],
                    await File.ReadAllLinesAsync(Path.Combine(tempDirectory, "commands")));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("PB_FAKE_DOCKER_STATE", originalState);
            Directory.Delete(tempDirectory, recursive: true);
        }
    }
}
