using RunAsLauncher.Verification;

namespace RunAsLauncher.Tests;

public sealed class FileHasherTests
{
    // SHA-256("abc") em maiúsculo.
    private const string AbcSha256 =
        "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD";

    [Fact]
    public void Sha256_KnownContent_ReturnsUppercaseHex()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ral-hash-{Guid.NewGuid():N}.bin");
        File.WriteAllText(path, "abc");
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            string hash = FileHasher.Sha256(fs);
            Assert.Equal(AbcSha256, hash);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Sha256_RewindsStream_HashesFromStart()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ral-hash-{Guid.NewGuid():N}.bin");
        File.WriteAllText(path, "abc");
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            fs.ReadByte(); // avança a posição; o hasher deve rebobinar
            Assert.Equal(AbcSha256, FileHasher.Sha256(fs));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
