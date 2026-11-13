using System.Security.Cryptography;
using AbpPlugin.Hosting;

namespace AbpPlugin.Tests;

/// <summary>A plugin's file is hashed as it's read into storage, so recording it doesn't read it twice.</summary>
public class HashingStreamTests
{
    [Fact]
    public async Task What_is_read_through_is_counted_and_hashed_and_the_stream_under_it_stays_open()
    {
        byte[] content = RandomNumberGenerator.GetBytes(200_000);
        MemoryStream source = new(content);
        await using (HashingStream hashed = new(source))
        {
            await hashed.CopyToAsync(Stream.Null, 4096);
            Assert.Equal(content.Length, hashed.BytesRead);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), hashed.Sha256());
        }

        Assert.True(source.CanRead);
    }

    [Fact]
    public void Reading_a_span_at_a_time_hashes_the_same()
    {
        byte[] content = RandomNumberGenerator.GetBytes(10_000);
        using HashingStream hashed = new(new MemoryStream(content));
        Span<byte> buffer = stackalloc byte[333];
        while (hashed.Read(buffer) > 0)
        {
        }

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), hashed.Sha256());
    }
}
