using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class PostgresOnlyStorageContractTests
{
    [Fact]
    public void Runtime_source_has_no_raw_blob_dependency()
    {
        var root = FindRepositoryRoot();
        var sourceRoot = Path.Combine(root, "src", "Lsc.Inventory.Api");
        var source = string.Join('\n', Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText));

        Assert.DoesNotContain("Azure.Storage.Blobs", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BlobAuditOptions", source, StringComparison.Ordinal);
        Assert.DoesNotContain("UploadRawPayloadAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("raw_blob_name", source, StringComparison.Ordinal);
        Assert.DoesNotContain("audit_blob_name", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Lsc.Inventory.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
